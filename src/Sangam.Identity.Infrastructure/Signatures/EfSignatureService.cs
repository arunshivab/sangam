using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Signatures;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Signatures;

/// <summary>
/// <see cref="ISignatureService"/> on the database (PR-17, SGM-207 §5). A request lives fifteen minutes, is signed
/// or declined once, and records the authentication behind the signature; the identity audit records the
/// ceremony, the application's own audit records the signed record (SGM-208).
/// </summary>
public sealed partial class EfSignatureService : ISignatureService
{
    /// <summary>How long a request may wait for the person.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly SangamDbContext _db;
    private readonly ISignatureTokenIssuer _issuer;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="issuer">Signs the signature token.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    public EfSignatureService(SangamDbContext db, ISignatureTokenIssuer issuer, IAuditWriter audit, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<(SignatureRequestCreated? Created, string? Error)> CreateAsync(Guid appId, SignatureRequestInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        string? problem = Validate(input);
        if (problem is not null)
        {
            return (null, problem);
        }

        if (input.Signer is Guid signer && !await _db.Users.AnyAsync(u => u.Id == signer && u.Status == UserStatus.Active, cancellationToken).ConfigureAwait(false))
        {
            return (null, "The named signer is not an active Sangam account.");
        }

        DateTimeOffset now = _clock.UtcNow;
        SignatureRequest request = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            RecordId = input.RecordId.Trim(),
            RecordHash = input.RecordHash.Trim(),
            Meaning = input.Meaning.Trim(),
            DisplayText = input.DisplayText.Trim(),
            SignerUserId = input.Signer,
            ReturnUrl = input.ReturnUrl,
            Status = SignatureStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        };
        _db.SignatureRequests.Add(request);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (new SignatureRequestCreated(request.Id, "/sign/" + request.Id.ToString("D"), request.ExpiresAt), null);
    }

    /// <inheritdoc />
    public async Task<SignatureCeremony?> GetAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var row = await _db.SignatureRequests.AsNoTracking()
            .Where(r => r.Id == requestId)
            .Join(_db.Apps, r => r.AppId, a => a.Id, (r, a) => new { Request = r, a.DisplayName, a.ClientId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        SignatureRequest r = row.Request;
        return new SignatureCeremony(r.Id, r.AppId, row.DisplayName, row.ClientId, r.RecordId, r.RecordHash, r.Meaning, r.DisplayText, r.SignerUserId, r.Status,
            r.Status == SignatureStatus.Pending && r.ExpiresAt <= _clock.UtcNow, r.ReturnUrl);
    }

    /// <inheritdoc />
    public async Task<SignatureResult> SignAsync(Guid requestId, Guid signer, string signerName, string issuer, string acr, IReadOnlyList<string> methods, DateTimeOffset authenticatedAt, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signerName);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(acr);
        ArgumentNullException.ThrowIfNull(methods);
        DateTimeOffset now = _clock.UtcNow;
        if (AuthenticationAssurance.Level(acr) < 2 || now - authenticatedAt > AuthenticationAssurance.SignatureFreshness)
        {
            return new SignatureResult(false, "Signing needs a two-step sign-in within the last five minutes.");
        }

        SignatureRequest? request = await _db.SignatureRequests.FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        string? refusal = Refusal(request, signer, now);
        if (refusal is not null)
        {
            return new SignatureResult(false, refusal);
        }

        string clientId = await _db.Apps.Where(a => a.Id == request!.AppId).Select(a => a.ClientId).SingleAsync(cancellationToken).ConfigureAwait(false);
        string token = _issuer.Issue(new SignatureClaims(issuer, clientId, request!.Id, signer, signerName, request.RecordId, request.RecordHash, request.Meaning, now, AuthenticationAssurance.AcrSign, methods, authenticatedAt));

        // Only one decision ever: a second tab or a replayed post finds the request no longer pending.
        int updated = await _db.SignatureRequests
            .Where(r => r.Id == requestId && r.Status == SignatureStatus.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, SignatureStatus.Signed)
                      .SetProperty(r => r.DecidedByUserId, signer)
                      .SetProperty(r => r.DecidedAt, now)
                      .SetProperty(r => r.Acr, acr)
                      .SetProperty(r => r.Amr, string.Join(' ', methods))
                      .SetProperty(r => r.Token, token),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated == 0)
        {
            return new SignatureResult(false, "This request has already been answered.");
        }

        await AuditAsync(AuditActions.UserSignatureSign, request, signer, ipAddress, cancellationToken).ConfigureAwait(false);
        return new SignatureResult(true, $"Signed: {request.Meaning}.", Back(request, "signed"));
    }

    /// <inheritdoc />
    public async Task<SignatureResult> DeclineAsync(Guid requestId, Guid userId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;
        SignatureRequest? request = await _db.SignatureRequests.FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken).ConfigureAwait(false);
        string? refusal = Refusal(request, userId, now);
        if (refusal is not null)
        {
            return new SignatureResult(false, refusal);
        }

        int updated = await _db.SignatureRequests
            .Where(r => r.Id == requestId && r.Status == SignatureStatus.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, SignatureStatus.Declined)
                      .SetProperty(r => r.DecidedByUserId, userId)
                      .SetProperty(r => r.DecidedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated == 0)
        {
            return new SignatureResult(false, "This request has already been answered.");
        }

        await AuditAsync(AuditActions.UserSignatureDecline, request!, userId, ipAddress, cancellationToken).ConfigureAwait(false);
        return new SignatureResult(true, "You declined to sign.", Back(request!, "declined"));
    }

    /// <inheritdoc />
    public async Task<SignatureView?> GetForAppAsync(Guid appId, Guid requestId, CancellationToken cancellationToken = default)
    {
        SignatureRequest? r = await _db.SignatureRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == requestId && x.AppId == appId, cancellationToken).ConfigureAwait(false);
        if (r is null)
        {
            return null;
        }

        string status = r.Status switch
        {
            SignatureStatus.Signed => "signed",
            SignatureStatus.Declined => "declined",
            _ => r.ExpiresAt <= _clock.UtcNow ? "expired" : "pending",
        };
        return new SignatureView(r.Id, status, r.Token, r.DecidedAt, r.DecidedByUserId);
    }

    private static string? Validate(SignatureRequestInput input)
    {
        if (string.IsNullOrWhiteSpace(input.RecordId) || input.RecordId.Length > 200)
        {
            return "recordId is required, at most 200 characters.";
        }

        if (input.RecordHash is null || !HashRegex().IsMatch(input.RecordHash.Trim()))
        {
            return "recordHash must be sha256:, sha384: or sha512: followed by the lowercase hex digest.";
        }

        string hex = input.RecordHash.Trim()[(input.RecordHash.Trim().IndexOf(':', StringComparison.Ordinal) + 1)..];
        int expected = input.RecordHash.Trim()[..6] switch
        {
            "sha256" => 64,
            "sha384" => 96,
            _ => 128,
        };
        if (hex.Length != expected)
        {
            return "recordHash has the wrong length for its algorithm.";
        }

        if (string.IsNullOrWhiteSpace(input.Meaning) || input.Meaning.Length > 100)
        {
            return "meaning is required, at most 100 characters (for example \"Approved\").";
        }

        if (string.IsNullOrWhiteSpace(input.DisplayText) || input.DisplayText.Length > 1000)
        {
            return "displayText is required, at most 1000 characters.";
        }

        return string.IsNullOrWhiteSpace(input.ReturnUrl) || input.ReturnUrl.Length > 500 ? "returnUrl is required." : null;
    }

    private static string? Refusal(SignatureRequest? request, Guid userId, DateTimeOffset now)
    {
        if (request is null)
        {
            return "There is no such signature request.";
        }

        if (request.Status != SignatureStatus.Pending)
        {
            return "This request has already been answered.";
        }

        if (request.ExpiresAt <= now)
        {
            return "This request has expired. Ask the application for a new one.";
        }

        return request.SignerUserId is Guid named && named != userId ? "This request is for someone else to sign." : null;
    }

    private static string Back(SignatureRequest request, string status)
        => request.ReturnUrl + (request.ReturnUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?")
        + "signature_request=" + request.Id.ToString("D") + "&status=" + status;

    private Task AuditAsync(string action, SignatureRequest request, Guid userId, string? ipAddress, CancellationToken cancellationToken)
        => _audit.WriteAsync(
            new AuditEntry(action, AuditActorType.User, userId, request.AppId, "signature_request", request.Id,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["record_id"] = request.RecordId,
                    ["record_hash"] = request.RecordHash,
                    ["meaning"] = request.Meaning,
                }),
                IpAddress: ipAddress),
            cancellationToken);

    [GeneratedRegex("^(sha256|sha384|sha512):[0-9a-f]+$")]
    private static partial Regex HashRegex();
}
