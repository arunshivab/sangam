using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Security;

/// <summary>
/// rc.8 (A6 workaround, SGM-911; vault brief section 10): before the identity server starts serving, every key it holds as
/// a file or secret — the token signing and encryption certificates, the key-ring certificates, the SAML certificates, the
/// password pepper, the DigiLocker subject key and the SAML pairwise key — is compared with the fingerprint recorded the
/// last time. A key that has changed stops the start, writes <c>security.key.mismatch</c> to the audit log and alerts the
/// founder through Anjal. A key seen for the first time (a new rotation file) is recorded and audited.
/// </summary>
/// <remarks>
/// <para>The fingerprints live in the database (<c>host_reports</c>, host <c>keys</c>), not beside the keys, so swapping a
/// key file is not enough to pass the check. Certificate files are fingerprinted by the SHA-256 of their bytes; secrets
/// by a keyed hash cut to 128 bits, which says nothing about the secret itself.</para>
/// <para>A planned change (rotation, re-export) is accepted by starting once with <c>Sangam:Keys:AcceptChanged=true</c>;
/// that start records the new fingerprints, audits and alerts, and the setting is then removed. On by default outside
/// Development and Testing (<c>Sangam:Keys:FingerprintCheck</c>).</para>
/// </remarks>
public sealed partial class KeyFingerprintCheck : IHostedLifecycleService
{
    /// <summary>The setting that turns the check on or off.</summary>
    public const string EnabledKey = "Sangam:Keys:FingerprintCheck";

    /// <summary>The setting that accepts changed keys for one start.</summary>
    public const string AcceptChangedKey = "Sangam:Keys:AcceptChanged";

    /// <summary>The <c>host_reports</c> host under which fingerprints are kept.</summary>
    public const string ReportHost = "keys";

    private static readonly byte[] SecretLabel = Encoding.UTF8.GetBytes("sangam-key-fingerprint-v1");

    private static readonly (string Name, string Section)[] CertificateSections =
    [
        ("cert.signing", "Sangam:Certificates:Signing"),
        ("cert.encryption", "Sangam:Certificates:Encryption"),
        ("cert.key_ring", "Sangam:DataProtection:Certificates"),
        ("cert.saml", "Sangam:Saml:Certificates"),
    ];

    private static readonly (string Name, string Key)[] Secrets =
    [
        ("secret.pepper", Argon2idOptions.PepperKey),
        ("secret.digilocker_subject", "Sangam:DigiLocker:SubjectKey"),
        ("secret.saml_pairwise", "Sangam:Saml:PairwiseKey"),
    ];

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly ILogger<KeyFingerprintCheck> _logger;
    private readonly bool _enabled;

    /// <summary>Initialises the check.</summary>
    /// <param name="scopes">Scope factory (database, audit, alerts).</param>
    /// <param name="configuration">Configuration.</param>
    /// <param name="environment">Host environment.</param>
    /// <param name="logger">Logger.</param>
    public KeyFingerprintCheck(IServiceScopeFactory scopes, IConfiguration configuration, IHostEnvironment environment, ILogger<KeyFingerprintCheck> logger)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        bool local = environment.EnvironmentName is "Development" or "Testing";
        _enabled = configuration.GetValue(EnabledKey, !local);
    }

    /// <summary>The fingerprint of every key this host holds, by name.</summary>
    /// <param name="configuration">Configuration.</param>
    /// <returns>Name to fingerprint (hex).</returns>
    public static IReadOnlyDictionary<string, string> Current(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        SortedDictionary<string, string> found = new(StringComparer.Ordinal);
        foreach ((string name, string section) in CertificateSections)
        {
            foreach (IConfigurationSection entry in configuration.GetSection(section).GetChildren())
            {
                string? path = entry["Path"];
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    found[$"{name}.{entry.Key}"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
                }
            }
        }

        foreach ((string name, string key) in Secrets)
        {
            if (configuration[key] is { Length: > 0 } value)
            {
                found[name] = SecretFingerprint(value);
            }
        }

        foreach (IConfigurationSection previous in configuration.GetSection(Argon2idOptions.SectionName + ":PreviousPeppers").GetChildren())
        {
            if (previous.Value is { Length: > 0 } value)
            {
                found[$"secret.pepper.v{previous.Key}"] = SecretFingerprint(value);
            }
        }

        return found;
    }

    /// <summary>A secret's fingerprint: HMAC-SHA-256 under a fixed label, cut to 128 bits.</summary>
    /// <param name="secret">The secret.</param>
    /// <returns>32 hex characters.</returns>
    public static string SecretFingerprint(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.ToHexStringLower(HMACSHA256.HashData(SecretLabel, Encoding.UTF8.GetBytes(secret)).AsSpan(0, 16));
    }

    /// <summary>Compares, records, and refuses the start on a changed key.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The names of keys that changed and were not accepted (empty when the start may go on).</returns>
    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, string> current = Current(_configuration);
        bool accept = _configuration.GetValue<bool>(AcceptChangedKey);

        using IServiceScope scope = _scopes.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        IAuditWriter audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        Dictionary<string, HostReport> recorded = await db.HostReports.Where(r => r.Host == ReportHost).ToDictionaryAsync(r => r.Subject, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);

        List<string> added = [];
        List<string> changed = [];
        foreach ((string name, string fingerprint) in current)
        {
            if (!recorded.TryGetValue(name, out HostReport? row))
            {
                db.HostReports.Add(new HostReport { Host = ReportHost, Subject = name, Payload = fingerprint, ReportedAt = DateTimeOffset.UtcNow });
                added.Add(name);
            }
            else if (!string.Equals(row.Payload, fingerprint, StringComparison.Ordinal))
            {
                changed.Add(name);
                if (accept)
                {
                    row.Payload = fingerprint;
                    row.ReportedAt = DateTimeOffset.UtcNow;
                }
            }
        }

        if (changed.Count > 0 && !accept)
        {
            // Nothing is recorded on a refused start, not even keys seen for the first time.
            db.ChangeTracker.Clear();
            await audit.WriteAsync(Entry(AuditActions.SecurityKeyMismatch, changed, accepted: false), cancellationToken).ConfigureAwait(false);
            await AlertAsync(scope, "Sangam key changed: start refused", $"The identity server refused to start: these keys differ from the fingerprints recorded at the last start: {string.Join(", ", changed)}. If you replaced them on purpose (rotation, SGM-911), start once with {AcceptChangedKey}=true. If you did not, treat it as a key compromise (SGM-802).", cancellationToken).ConfigureAwait(false);
            return changed;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (added.Count > 0)
        {
            await audit.WriteAsync(Entry(AuditActions.SecurityKeyRecorded, added, accepted: false), cancellationToken).ConfigureAwait(false);
        }

        if (changed.Count > 0)
        {
            await audit.WriteAsync(Entry(AuditActions.SecurityKeyMismatch, changed, accepted: true), cancellationToken).ConfigureAwait(false);
            await AlertAsync(scope, "Sangam key change accepted", $"The identity server started with changed keys, accepted by {AcceptChangedKey}: {string.Join(", ", changed)}. Remove the setting now.", cancellationToken).ConfigureAwait(false);
        }

        return [];
    }

    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            return;
        }

        IReadOnlyList<string> refused = await CheckAsync(cancellationToken).ConfigureAwait(false);
        if (refused.Count > 0)
        {
            throw new InvalidOperationException($"These keys differ from the fingerprints recorded at the last start: {string.Join(", ", refused)}. If they were replaced on purpose, start once with {AcceptChangedKey}=true. Refusing to start."); // i18n-ignore: a start-up log line for the operator
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static AuditEntry Entry(string action, List<string> names, bool accepted)
        => new(action, AuditActorType.System, TargetType: "key", Metadata: JsonSerializer.Serialize(new Dictionary<string, object> { ["keys"] = names, ["accepted"] = accepted }));

    private async Task AlertAsync(IServiceScope scope, string summary, string details, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            await scope.ServiceProvider.GetRequiredService<IPlatformAlerts>().SendAsync(summary, details, limit.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The alert is best effort; the audit row and the refused start stand without it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogAlertFailed(ex.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 1901, Level = LogLevel.Error, Message = "Could not send the key-fingerprint alert ({Error}); the audit log has the event.")]
    private partial void LogAlertFailed(string error);
}
