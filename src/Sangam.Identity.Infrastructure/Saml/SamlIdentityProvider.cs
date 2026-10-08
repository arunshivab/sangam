using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Admin;
using Sangam.Identity.Application.Saml;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Saml;

/// <summary>
/// The SAML identity provider's state (PR-22): service providers, the requests being answered, and the console's
/// management of service providers. The protocol itself is in <see cref="SamlProtocol"/>; the endpoints are on the
/// identity server.
/// </summary>
public sealed class SamlIdentityProvider : ISamlAdminService
{
    /// <summary>How long a person has to sign in and consent.</summary>
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(15);

    /// <summary>How far a request's IssueInstant may be from now.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private readonly SangamDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    /// <summary>Initialises the provider.</summary>
    /// <param name="db">Database.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="clock">Clock.</param>
    public SamlIdentityProvider(SangamDbContext db, IAuditWriter audit, IClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The active service provider with this entity id, with its application.</summary>
    /// <param name="entityId">Entity id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<(SamlServiceProvider Provider, App App)?> FindAsync(string entityId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        var found = await _db.SamlServiceProviders.AsNoTracking()
            .Where(p => p.EntityId == entityId)
            .Join(_db.Apps.AsNoTracking(), p => p.AppId, a => a.Id, (p, a) => new { p, a })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return found is null || found.a.Status != AppStatus.Active ? null : (found.p, found.a);
    }

    /// <summary>The active service provider by id, with its application.</summary>
    /// <param name="id">Row id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<(SamlServiceProvider Provider, App App)?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var found = await _db.SamlServiceProviders.AsNoTracking()
            .Where(p => p.Id == id)
            .Join(_db.Apps.AsNoTracking(), p => p.AppId, a => a.Id, (p, a) => new { p, a })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return found is null || found.a.Status != AppStatus.Active ? null : (found.p, found.a);
    }

    /// <summary>
    /// Checks an AuthnRequest against its service provider and keeps it for the person's sign-in: the issuer is
    /// registered, the request is fresh and addressed here, the ACS address is a registered one, a signature is there
    /// when required, the context asked for is one Sangam can meet, and the request id was never answered before.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="ssoUrl">This identity provider's sign-on address.</param>
    /// <param name="signatureValid">Whether a valid signature by the SP came with it.</param>
    /// <param name="relayState">The RelayState.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SamlAcceptance> AcceptAsync(AuthnRequestMessage request, string ssoUrl, bool signatureValid, string? relayState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ssoUrl);
        if (await FindAsync(request.Issuer, cancellationToken).ConfigureAwait(false) is not var (provider, app))
        {
            return SamlAcceptance.Refused("This service provider is not registered with Sangam, or is disabled.");
        }

        DateTimeOffset now = _clock.UtcNow;
        if (request.IssueInstant < now - ClockSkew || request.IssueInstant > now + ClockSkew)
        {
            return SamlAcceptance.Refused("The request is too old, or from the future: check the service provider's clock.");
        }

        if (request.Destination is not null && !string.Equals(request.Destination, ssoUrl, StringComparison.Ordinal))
        {
            return SamlAcceptance.Refused("The request is addressed to another identity provider.");
        }

        if (request.ProtocolBinding is not null && request.ProtocolBinding != SamlProtocol.PostBinding)
        {
            return SamlAcceptance.Refused("Sangam answers by HTTP-POST only.");
        }

        string[] acs = Lines(provider.AcsUrls);
        string? acsUrl = request.AcsUrl ?? acs.FirstOrDefault();
        if (acsUrl is null || !acs.Contains(acsUrl, StringComparer.Ordinal))
        {
            // Never send an assertion to an address the SP did not register.
            return SamlAcceptance.Refused("The assertion consumer address is not one registered for this service provider.");
        }

        if (provider.RequireSignedRequests && !signatureValid)
        {
            return SamlAcceptance.Refused("This service provider's requests must be signed, and this one is not, or not validly.");
        }

        int level = 0;
        if (request.AuthnContextClasses.Count > 0)
        {
            int[] levels = [.. request.AuthnContextClasses.Select(SamlProtocol.LevelOf)];
            if (levels.All(l => l < 0))
            {
                return SamlAcceptance.Refused("Sangam cannot provide the authentication context asked for.", SamlProtocol.NoAuthnContext, provider, acsUrl);
            }

            level = levels.Where(l => l >= 0).Min();
        }

        SamlRequest pending = new()
        {
            Handle = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)),
            ServiceProviderId = provider.Id,
            RequestId = request.Id.Length > 256 ? request.Id[..256] : request.Id,
            AcsUrl = acsUrl,
            RelayState = relayState is { Length: > 1000 } ? relayState[..1000] : relayState,
            RequiredLevel = level,
            ForceAuthn = request.ForceAuthn,
            CreatedAt = now,
            ExpiresAt = now + RequestLifetime,
        };
        _db.SamlRequests.Add(pending);
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            return SamlAcceptance.Refused("This request was answered already (a replay).");
        }

        await _db.SamlRequests.Where(r => r.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return new SamlAcceptance(pending, provider, app, null, null);
    }

    /// <summary>Starts an IdP-initiated sign-in, where the service provider allows it.</summary>
    /// <param name="providerId">The service provider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SamlAcceptance> LaunchAsync(Guid providerId, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(providerId, cancellationToken).ConfigureAwait(false) is not var (provider, app) || !provider.AllowIdpInitiated)
        {
            return SamlAcceptance.Refused("This application cannot be opened from Sangam.");
        }

        DateTimeOffset now = _clock.UtcNow;
        SamlRequest pending = new()
        {
            Handle = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)),
            ServiceProviderId = provider.Id,
            RequestId = string.Empty,
            AcsUrl = Lines(provider.AcsUrls)[0],
            RelayState = provider.DefaultRelayState,
            CreatedAt = now,
            ExpiresAt = now + RequestLifetime,
        };
        _db.SamlRequests.Add(pending);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SamlAcceptance(pending, provider, app, null, null);
    }

    /// <summary>A request still waiting for its answer, with its service provider and application.</summary>
    /// <param name="handle">The handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SamlAcceptance?> PendingAsync(string handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        DateTimeOffset now = _clock.UtcNow;
        SamlRequest? pending = await _db.SamlRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Handle == handle && r.AnsweredAt == null && r.ExpiresAt > now, cancellationToken).ConfigureAwait(false);
        return pending is null || await FindAsync(pending.ServiceProviderId, cancellationToken).ConfigureAwait(false) is not var (provider, app)
            ? null
            : new SamlAcceptance(pending, provider, app, null, null);
    }

    /// <summary>Marks a request answered — once: false when someone answered it first.</summary>
    /// <param name="handle">The handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> MarkAnsweredAsync(string handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        DateTimeOffset now = _clock.UtcNow;
        return await _db.SamlRequests.Where(r => r.Handle == handle && r.AnsweredAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.AnsweredAt, now), cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SamlSpView>?> ListAsync(Guid operatorUserId, CancellationToken cancellationToken = default)
    {
        if (await RoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var rows = await _db.SamlServiceProviders.AsNoTracking()
            .Join(_db.Apps.AsNoTracking(), p => p.AppId, a => a.Id, (p, a) => new { p, a })
            .OrderBy(x => x.a.DisplayName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(x => new SamlSpView(x.p.Id, x.a.Id, ToInput(x.p, x.a), x.a.Status == AppStatus.Active, !string.IsNullOrEmpty(x.p.EncryptionCertificate)))];
    }

    /// <inheritdoc />
    public async Task<AdminResult> SaveAsync(Guid operatorUserId, SamlSpInput input, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        PlatformRole? role = await RoleAsync(operatorUserId, cancellationToken).ConfigureAwait(false);
        if (role is null || role < PlatformRole.AppManager)
        {
            return AdminResult.Refused($"That needs {PlatformRanks.Label(PlatformRole.AppManager)} access.");
        }

        if (Check(input) is string problem)
        {
            return AdminResult.Refused(problem);
        }

        DateTimeOffset now = _clock.UtcNow;
        string entityId = input.EntityId.Trim();
        if (await _db.SamlServiceProviders.AnyAsync(p => p.EntityId == entityId && p.Id != input.Id, cancellationToken).ConfigureAwait(false))
        {
            return AdminResult.Refused("Another service provider already has that entity id.");
        }

        SamlServiceProvider? provider = input.Id is Guid id ? await _db.SamlServiceProviders.SingleOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false) : null;
        App? app;
        if (provider is null)
        {
            if (input.Id is not null)
            {
                return AdminResult.Refused("No such service provider.");
            }

            string suffix = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entityId)))[..10];
            app = new App
            {
                Id = Guid.NewGuid(),
                ClientId = "saml-" + suffix,
                Slug = "saml-" + suffix,
                CreatedAt = now,
                Status = AppStatus.Active,
                RequireConsent = true,
                ConsentVersion = "v1",
            };
            _db.Apps.Add(app);
            provider = new SamlServiceProvider { Id = Guid.NewGuid(), AppId = app.Id, CreatedAt = now };
            _db.SamlServiceProviders.Add(provider);
        }
        else
        {
            app = await _db.Apps.SingleAsync(a => a.Id == provider.AppId, cancellationToken).ConfigureAwait(false);
        }

        app.DisplayName = input.DisplayName.Trim();
        app.OwnerCompanyName = input.OwnerCompanyName.Trim();
        app.Description ??= "A SAML application that signs people in with Sangam.";
        app.HomepageUrl = new Uri(Lines(input.AcsUrls)[0]).GetLeftPart(UriPartial.Authority) + "/";
        app.UpdatedAt = now;
        provider.EntityId = entityId;
        provider.AcsUrls = string.Join('\n', Lines(input.AcsUrls));
        provider.SloUrl = Blank(input.SloUrl);
        provider.SigningCertificate = Blank(input.SigningCertificate);
        provider.EncryptionCertificate = Blank(input.EncryptionCertificate);
        provider.NameIdFormat = input.NameIdFormat;
        provider.Attributes = string.Join(',', SamlAttributes.All.Where(input.Attributes.Contains));
        provider.RequireSignedRequests = input.RequireSignedRequests;
        provider.AllowIdpInitiated = input.AllowIdpInitiated;
        provider.DefaultRelayState = Blank(input.DefaultRelayState);
        provider.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
            new AuditEntry(input.Id is null ? AuditActions.SamlProviderRegister : AuditActions.SamlProviderUpdate, AuditActorType.Admin, operatorUserId, app.Id, "app", app.Id,
                Metadata: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["entity_id"] = entityId,
                    ["acs"] = provider.AcsUrls.Split('\n'),
                    ["attributes"] = provider.Attributes,
                    ["name_id"] = provider.NameIdFormat,
                    ["encrypted"] = provider.EncryptionCertificate is not null,
                    ["idp_initiated"] = provider.AllowIdpInitiated,
                }),
                IpAddress: ipAddress),
            cancellationToken).ConfigureAwait(false);
        return AdminResult.Ok(input.Id is null ? $"{app.DisplayName} registered as a SAML application." : $"{app.DisplayName} saved.");
    }

    /// <inheritdoc />
    public (SamlSpInput? Input, string? Problem) FromMetadata(string metadataXml)
    {
        ArgumentNullException.ThrowIfNull(metadataXml);
        try
        {
            SpMetadata metadata = SamlProtocol.ReadSpMetadata(metadataXml);
            if (metadata.AcsUrls.Count == 0)
            {
                return (null, "The metadata has no HTTP-POST assertion consumer address.");
            }

            string name = Uri.TryCreate(metadata.EntityId, UriKind.Absolute, out Uri? entity) ? entity.Host : metadata.EntityId;
            return (new SamlSpInput(null, name, string.Empty, metadata.EntityId, string.Join('\n', metadata.AcsUrls), metadata.SloUrl, metadata.SigningCertificatePem, metadata.EncryptionCertificatePem,
                "persistent", ["name", "email"], metadata.WantsSignedRequests, false, null), null);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException)
        {
            return (null, "That is not readable SAML metadata.");
        }
    }

    /// <summary>The SP's certificate for one use, or <see langword="null"/>.</summary>
    /// <param name="pem">The PEM text.</param>
    public static X509Certificate2? Certificate(string? pem) => string.IsNullOrWhiteSpace(pem) ? null : X509Certificate2.CreateFromPem(pem);

    /// <summary>The lines of a multi-line setting.</summary>
    /// <param name="value">The setting.</param>
    public static string[] Lines(string value) => (value ?? string.Empty).Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Check(SamlSpInput input)
    {
        if (string.IsNullOrWhiteSpace(input.DisplayName) || string.IsNullOrWhiteSpace(input.OwnerCompanyName))
        {
            return "Give the application's name and who runs it.";
        }

        if (string.IsNullOrWhiteSpace(input.EntityId) || input.EntityId.Trim().Length > 500)
        {
            return "Give the service provider's entity id.";
        }

        string[] acs = Lines(input.AcsUrls);
        if (acs.Length == 0 || acs.Any(a => !IsHttps(a)) || (input.SloUrl is { Length: > 0 } slo && !IsHttps(slo)))
        {
            return "Assertion consumer and logout addresses must be https addresses.";
        }

        if (input.NameIdFormat is not ("persistent" or "email"))
        {
            return "Choose the NameID: persistent or e-mail address.";
        }

        foreach (string? pem in new[] { input.SigningCertificate, input.EncryptionCertificate })
        {
            if (string.IsNullOrWhiteSpace(pem))
            {
                continue;
            }

            try
            {
                using X509Certificate2 certificate = X509Certificate2.CreateFromPem(pem);
                using RSA? rsa = certificate.GetRSAPublicKey();
                if (rsa is null || rsa.KeySize < 2048)
                {
                    return "Certificates need an RSA key of at least 2048 bits.";
                }
            }
            catch (CryptographicException)
            {
                return "A certificate could not be read: paste it in PEM form.";
            }
        }

        if (input.RequireSignedRequests && string.IsNullOrWhiteSpace(input.SigningCertificate))
        {
            return "Signed requests need the service provider's signing certificate.";
        }

        return null;
    }

    private static bool IsHttps(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Fragment.Length == 0;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SamlSpInput ToInput(SamlServiceProvider p, App a) => new(p.Id, a.DisplayName, a.OwnerCompanyName, p.EntityId, p.AcsUrls, p.SloUrl, p.SigningCertificate, p.EncryptionCertificate, p.NameIdFormat,
        p.Attributes.Split(',', StringSplitOptions.RemoveEmptyEntries), p.RequireSignedRequests, p.AllowIdpInitiated, p.DefaultRelayState);

    private Task<PlatformRole?> RoleAsync(Guid operatorUserId, CancellationToken cancellationToken)
        => _db.PlatformOperators.AsNoTracking()
            .Where(o => o.UserId == operatorUserId && o.RevokedAt == null)
            .Select(o => (PlatformRole?)o.Role)
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>A request accepted for sign-in, or refused with the reason (and, when it can be told to the SP, the status).</summary>
/// <param name="Request">The kept request.</param>
/// <param name="Provider">Its service provider.</param>
/// <param name="App">Its application.</param>
/// <param name="Problem">Why it was refused.</param>
/// <param name="StatusCode">The SAML status to send back, when the refusal can go to the SP.</param>
public sealed record SamlAcceptance(SamlRequest? Request, SamlServiceProvider? Provider, App? App, string? Problem, string? StatusCode)
{
    /// <summary>Where a refusal that can be told may be sent.</summary>
    public string? RefusalAcs { get; init; }

    /// <summary>A refusal.</summary>
    /// <param name="problem">Why.</param>
    /// <param name="status">The SAML second-level status, when it can be told to the SP.</param>
    /// <param name="provider">The SP, when known.</param>
    /// <param name="acs">Its registered ACS.</param>
    public static SamlAcceptance Refused(string problem, string? status = null, SamlServiceProvider? provider = null, string? acs = null)
        => new(null, provider, null, problem, status) { RefusalAcs = acs };
}
