using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Saml;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Saml;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests.Saml;

/// <summary>
/// PR-22 (SGM-215): Sangam as a SAML identity provider — metadata, SP-initiated sign-in with signed and encrypted
/// assertions, pairwise NameIDs, the same sign-in rules and consent as OpenID Connect, replay and address checks,
/// signed requests, IdP-initiated sign-in where allowed, and SP-initiated logout.
/// </summary>
[Collection("server")]
public sealed partial class SamlTests
{
    private const string Password = "Correct-Horse-2026!";
    private const string Sso = "http://localhost/saml/sso";
    private readonly SangamServerFactory _factory;

    public SamlTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Metadata_PublishesTheEntity_TheSigningKey_AndTheEndpoints()
    {
        using HttpClient client = _factory.CreateClient();
        XmlDocument metadata = new();
        metadata.LoadXml(await client.GetStringAsync(new Uri("/saml/metadata", UriKind.Relative)));
        Assert.Equal("http://localhost/saml", metadata.DocumentElement!.GetAttribute("entityID"));
        Assert.Contains(Sso, metadata.OuterXml, StringComparison.Ordinal);
        Assert.Contains("http://localhost/saml/slo", metadata.OuterXml, StringComparison.Ordinal);
        Assert.Single(metadata.GetElementsByTagName("X509Certificate", SamlProtocol.DsigNs).Cast<XmlNode>());
    }

    [Fact]
    public async Task WithAConfiguredIssuer_TheEntityIdAndAddresses_IgnoreTheHostHeader()
    {
        using WebApplicationFactory<Program> configured = _factory.WithWebHostBuilder(b => b.UseSetting("Sangam:Issuer", "https://id.example.in/"));
        using HttpClient client = configured.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri("/saml/metadata", UriKind.Relative));
        request.Headers.Host = "attacker.example.in";
        using HttpResponseMessage response = await client.SendAsync(request);
        string metadata = await response.Content.ReadAsStringAsync();
        Assert.Contains("entityID=\"https://id.example.in/saml\"", metadata, StringComparison.Ordinal);
        Assert.Contains("Location=\"https://id.example.in/saml/sso\"", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker.example.in", metadata, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AServiceProvider_GetsASignedEncryptedAssertion_AfterSignInAndConsent()
    {
        using Sp sp = await RegisterSpAsync(encrypt: true);
        using BrowserSession s = new(_factory);
        (Guid userId, string email) = await RegisterAsync(s);

        string requestId = NewId();
        (_, string location, _) = await s.FollowAsync("/saml/sso?" + RedirectQuery(AuthnRequest(sp.EntityId, requestId, sp.Acs), "back-to-ward-7"));
        Assert.StartsWith("/consent", location, StringComparison.Ordinal);
        (_, string? allowed, _) = await s.PostFormAsync(location, [], handler: "Allow");
        (HttpStatusCode status, _, string page) = await s.FollowAsync(allowed!);
        Assert.Equal(HttpStatusCode.OK, status);
        (XmlDocument response, string relay) = ReadPost(page, sp.Acs);
        Assert.Equal("back-to-ward-7", relay);

        X509Certificate2 idp = await IdpCertificateAsync();
        Assert.True(SamlProtocol.VerifyEnvelopedSignature(response, idp));
        Assert.Equal(requestId, response.DocumentElement!.GetAttribute("InResponseTo"));
        Assert.Contains(SamlProtocol.Success, response.OuterXml, StringComparison.Ordinal);
        Assert.Empty(response.GetElementsByTagName("Assertion", SamlProtocol.AssertionNs).Cast<XmlNode>());

        // Only the SP's own key opens the assertion; inside, it is signed by Sangam too.
        XmlElement encrypted = (XmlElement)response.GetElementsByTagName("EncryptedAssertion", SamlProtocol.AssertionNs)[0]!;
        XmlDocument assertion = SamlProtocol.Load(SamlProtocol.DecryptAssertion(encrypted, sp.EncryptionKey));
        Assert.True(SamlProtocol.VerifyEnvelopedSignature(assertion, idp));
        string nameId = Text(assertion, "NameID");
        Assert.Equal(sp.EntityId, Text(assertion, "Audience"));
        Assert.Equal(sp.Acs, ((XmlElement)assertion.GetElementsByTagName("SubjectConfirmationData", SamlProtocol.AssertionNs)[0]!).GetAttribute("Recipient"));
        Assert.Contains(email, assertion.OuterXml, StringComparison.Ordinal);
        Assert.Equal(SamlProtocol.PasswordProtectedTransport, Text(assertion, "AuthnContextClassRef"));
        Assert.DoesNotContain(userId.ToString("D"), nameId, StringComparison.Ordinal);
        SamlOptions options = _factory.Services.GetRequiredService<SamlOptions>();
        Assert.Equal(options.PairwiseNameId(userId, sp.EntityId), nameId);
        Assert.NotEqual(nameId, options.PairwiseNameId(userId, "https://other.example.in/sp"));

        // The same request again is a replay.
        (HttpStatusCode replay, _, string replayPage) = await s.FollowAsync("/saml/sso?" + RedirectQuery(AuthnRequest(sp.EntityId, requestId, sp.Acs), null));
        Assert.Equal(HttpStatusCode.BadRequest, replay);
        Assert.Contains("answered already", WebUtility.HtmlDecode(replayPage), StringComparison.Ordinal);

        using IServiceScope scope = _factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<SangamDbContext>().AuditEvents.AnyAsync(e => e.Action == AuditActions.SamlAssertion && e.ActorUserId == userId));
    }

    [PostgresFact]
    public async Task TheConsentScreen_NamesExactlyWhatTheAssertionCarries()
    {
        // V-14: the consent list used to be the OpenID Connect "profile" one (name, date of birth and gender).
        using Sp sp = await RegisterSpAsync(encrypt: false, attributes: ["given_name", "family_name", "email"]);
        using BrowserSession s = new(_factory);
        (_, string email) = await RegisterAsync(s);
        (_, string location, string consent) = await s.FollowAsync("/saml/sso?" + RedirectQuery(AuthnRequest(sp.EntityId, NewId(), sp.Acs), null));
        Assert.StartsWith("/consent", location, StringComparison.Ordinal);
        consent = WebUtility.HtmlDecode(consent);
        List<(string Label, string Value)> shown = [.. ShareRowRegex().Matches(consent).Select(m => (m.Groups[1].Value, m.Groups[2].Value))];
        Assert.DoesNotContain(shown, r => r.Label.Contains("birth", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["First name", "Last name", "Email address"], shown.Select(r => r.Label));

        (_, string? allowed, _) = await s.PostFormAsync(location, [], handler: "Allow");
        (_, _, string page) = await s.FollowAsync(allowed!);
        (XmlDocument response, _) = ReadPost(page, sp.Acs);
        List<string> carried = [.. response.GetElementsByTagName("AttributeValue", SamlProtocol.AssertionNs).Cast<XmlNode>().Select(n => n.InnerText)];
        Assert.Equal(carried.Order(StringComparer.Ordinal), shown.Select(r => r.Value).Order(StringComparer.Ordinal));
        Assert.Contains(email, carried);
    }

    [PostgresFact]
    public async Task Requests_ToAnUnregisteredAddress_Unsigned_OrStale_AreRefused()
    {
        using Sp sp = await RegisterSpAsync(encrypt: false, requireSigned: true);
        using BrowserSession s = new(_factory);
        await RegisterAsync(s);

        // Unsigned, where signatures are required.
        (HttpStatusCode unsigned, _, _) = await s.FollowAsync("/saml/sso?" + RedirectQuery(AuthnRequest(sp.EntityId, NewId(), sp.Acs), null));
        Assert.Equal(HttpStatusCode.BadRequest, unsigned);

        // Signed by the SP: accepted, and answered unencrypted.
        (_, string location, _) = await s.FollowAsync("/saml/sso?" + RedirectQuery(AuthnRequest(sp.EntityId, NewId(), sp.Acs), null, sp.SigningKey));
        Assert.StartsWith("/consent", location, StringComparison.Ordinal);
        (_, string? allowed, _) = await s.PostFormAsync(location, [], handler: "Allow");
        (_, _, string page) = await s.FollowAsync(allowed!);
        (XmlDocument response, _) = ReadPost(page, sp.Acs);
        Assert.Single(response.GetElementsByTagName("Assertion", SamlProtocol.AssertionNs).Cast<XmlNode>());
        Assert.True(SamlProtocol.VerifyEnvelopedSignature(response, await IdpCertificateAsync()));

        // Signed, but to an address the SP never registered; and a request from yesterday.
        (HttpStatusCode elsewhere, _, _) = await s.FollowAsync("/saml/sso?" + RedirectQuery(AuthnRequest(sp.EntityId, NewId(), "https://attacker.example.in/acs"), null, sp.SigningKey));
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere);
        (HttpStatusCode stale, _, _) = await s.FollowAsync("/saml/sso?" + RedirectQuery(AuthnRequest(sp.EntityId, NewId(), sp.Acs, DateTimeOffset.UtcNow.AddDays(-1)), null, sp.SigningKey));
        Assert.Equal(HttpStatusCode.BadRequest, stale);
    }

    [PostgresFact]
    public async Task AskingForMultiFactor_SendsAOneFactorSessionBackToSignIn_AtThatLevel()
    {
        using Sp sp = await RegisterSpAsync(encrypt: false);
        using BrowserSession s = new(_factory);
        await RegisterAsync(s);
        (_, string location, _) = await s.FollowAsync("/saml/sso?" + RedirectQuery(AuthnRequest(sp.EntityId, NewId(), sp.Acs, context: SamlProtocol.RefedsMfa), null));
        Assert.StartsWith("/login", location, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("acr_values=" + Uri.EscapeDataString("urn:sangam:acr:2")), location, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task IdpInitiatedSignIn_IsOnlyForServiceProvidersThatAllowIt()
    {
        using Sp closed = await RegisterSpAsync(encrypt: false);
        using Sp open = await RegisterSpAsync(encrypt: false, idpInitiated: true);
        using BrowserSession s = new(_factory);
        await RegisterAsync(s);
        (HttpStatusCode refused, _, _) = await s.FollowAsync("/saml/launch/" + closed.Id.ToString("D"));
        Assert.Equal(HttpStatusCode.BadRequest, refused);

        (_, string location, _) = await s.FollowAsync("/saml/launch/" + open.Id.ToString("D"));
        (_, string? allowed, _) = await s.PostFormAsync(location, [], handler: "Allow");
        (_, _, string page) = await s.FollowAsync(allowed!);
        (XmlDocument response, string relay) = ReadPost(page, open.Acs);
        Assert.Equal(string.Empty, response.DocumentElement!.GetAttribute("InResponseTo"));
        Assert.Equal("/ward", relay);
    }

    [PostgresFact]
    public async Task ServiceProviderLogout_EndsTheSangamSession_AndAnswersSigned()
    {
        using Sp sp = await RegisterSpAsync(encrypt: false);
        using BrowserSession s = new(_factory);
        await RegisterAsync(s);
        string logout = "<samlp:LogoutRequest xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" xmlns:saml=\"urn:oasis:names:tc:SAML:2.0:assertion\" ID=\"_l1\" Version=\"2.0\" IssueInstant=\""
            + DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\"><saml:Issuer>" + sp.EntityId + "</saml:Issuer><saml:NameID>x</saml:NameID></samlp:LogoutRequest>";
        (HttpStatusCode status, string location, _) = await s.FollowAsync("/saml/slo?SAMLRequest=" + Uri.EscapeDataString(SamlProtocol.DeflateRedirect(logout)));
        Assert.Equal(HttpStatusCode.Redirect, status);
        Assert.StartsWith(sp.Slo + "?SAMLResponse=", location, StringComparison.Ordinal);
        Assert.True(SamlProtocol.VerifyRedirectSignature(new Uri(location).Query, await IdpCertificateAsync()));
        Assert.Contains("InResponseTo=\"_l1\"", SamlProtocol.InflateRedirect(HttpUtility.ParseQueryString(new Uri(location).Query)["SAMLResponse"]!), StringComparison.Ordinal);
        (_, string account, _) = await s.FollowAsync("/account");
        Assert.Contains("/login", account, StringComparison.Ordinal);
    }

    private static string NewId() => "_" + Guid.NewGuid().ToString("N");

    private static string AuthnRequest(string issuer, string id, string acs, DateTimeOffset? at = null, string? context = null)
        => "<samlp:AuthnRequest xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" xmlns:saml=\"urn:oasis:names:tc:SAML:2.0:assertion\" ID=\"" + id + "\" Version=\"2.0\" IssueInstant=\""
            + (at ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\" Destination=\"" + Sso + "\" AssertionConsumerServiceURL=\"" + acs
            + "\" ProtocolBinding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST\"><saml:Issuer>" + issuer + "</saml:Issuer>"
            + (context is null ? string.Empty : "<samlp:RequestedAuthnContext Comparison=\"minimum\"><saml:AuthnContextClassRef>" + context + "</saml:AuthnContextClassRef></samlp:RequestedAuthnContext>")
            + "</samlp:AuthnRequest>";

    private static string RedirectQuery(string xml, string? relayState, RSA? signWith = null)
    {
        string query = "SAMLRequest=" + Uri.EscapeDataString(SamlProtocol.DeflateRedirect(xml)) + (relayState is null ? string.Empty : "&RelayState=" + Uri.EscapeDataString(relayState));
        if (signWith is null)
        {
            return query;
        }

        query += "&SigAlg=" + Uri.EscapeDataString(SignedXml.XmlDsigRSASHA256Url);
        return query + "&Signature=" + Uri.EscapeDataString(Convert.ToBase64String(signWith.SignData(Encoding.UTF8.GetBytes(query), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
    }

    private static (XmlDocument Response, string RelayState) ReadPost(string page, string acs)
    {
        Assert.Contains("action=\"" + acs + "\"", page, StringComparison.Ordinal);
        string response = SamlResponseRegex().Match(page).Groups[1].Value;
        Match relay = RelayRegex().Match(page);
        return (SamlProtocol.Load(Encoding.UTF8.GetString(Convert.FromBase64String(response))), relay.Success ? WebUtility.HtmlDecode(relay.Groups[1].Value) : string.Empty);
    }

    private static string Text(XmlDocument document, string localName) => document.GetElementsByTagName(localName, SamlProtocol.AssertionNs)[0]!.InnerText;

    private async Task<X509Certificate2> IdpCertificateAsync()
    {
        using HttpClient client = _factory.CreateClient();
        XmlDocument metadata = new();
        metadata.LoadXml(await client.GetStringAsync(new Uri("/saml/metadata", UriKind.Relative)));
        return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(metadata.GetElementsByTagName("X509Certificate", SamlProtocol.DsigNs)[0]!.InnerText));
    }

    private async Task<Sp> RegisterSpAsync(bool encrypt, bool requireSigned = false, bool idpInitiated = false, string[]? attributes = null)
    {
        Sp sp = new();
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        SangamUser manager = await db.Users.OrderBy(u => u.CreatedAt).FirstAsync();
        if (!await db.PlatformOperators.AnyAsync(o => o.UserId == manager.Id && o.RevokedAt == null))
        {
            db.PlatformOperators.Add(new PlatformOperator { Id = Guid.NewGuid(), UserId = manager.Id, Role = PlatformRole.AppManager, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Application.Admin.AdminResult result = await scope.ServiceProvider.GetRequiredService<ISamlAdminService>().SaveAsync(manager.Id, new SamlSpInput(
            null, "Legacy CRM", "Example Systems", sp.EntityId, sp.Acs, sp.Slo, sp.SigningPem, encrypt ? sp.EncryptionPem : null, "persistent", attributes ?? ["name", "email", "roles"], requireSigned, idpInitiated, "/ward"), null);
        Assert.True(result.Succeeded, result.Message);
        sp.Id = await db.SamlServiceProviders.Where(p => p.EntityId == sp.EntityId).Select(p => p.Id).SingleAsync();
        return sp;
    }

    private async Task<(Guid UserId, string Email)> RegisterAsync(BrowserSession s)
    {
        string email = $"saml-{Guid.NewGuid():N}@example.in";
        (HttpStatusCode st, string? loc, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Anil",
            ["LastName"] = "Kumar",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = Random.Shared.NextInt64(7000000000, 9999999999).ToString(CultureInfo.InvariantCulture),
            ["BirthDay"] = "12",
            ["BirthMonth"] = "3",
            ["BirthYear"] = "1985",
            ["Gender"] = "male",
            ["Password"] = Password,
            ["AcceptTerms"] = "true",
        });
        Assert.True(st == HttpStatusCode.Found && loc == "/verify", html);
        string code = CodeRegex().Match(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody).Value;
        (_, string? ok, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal("/verified", ok);
        using IServiceScope scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<SangamDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(), email);
    }

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex CodeRegex();

    [GeneratedRegex("name=\"SAMLResponse\" value=\"([^\"]+)\"")]
    private static partial Regex SamlResponseRegex();

    [GeneratedRegex("sg-share-claim\">([^<]*)</span><span class=\"sg-share-value\">([^<]*)<")]
    private static partial Regex ShareRowRegex();

    [GeneratedRegex("name=\"RelayState\" value=\"([^\"]*)\"")]
    private static partial Regex RelayRegex();

    /// <summary>A test service provider with its own keys.</summary>
    private sealed class Sp : IDisposable
    {
        public Sp()
        {
            string host = "sp-" + Guid.NewGuid().ToString("N")[..8] + ".example.in";
            EntityId = "https://" + host + "/saml";
            Acs = "https://" + host + "/saml/acs";
            Slo = "https://" + host + "/saml/slo";
            (SigningKey, SigningPem) = MakeKey("signing");
            (EncryptionKey, EncryptionPem) = MakeKey("encryption");
        }

        public Guid Id { get; set; }

        public string EntityId { get; }

        public string Acs { get; }

        public string Slo { get; }

        public RSA SigningKey { get; }

        public string SigningPem { get; }

        public RSA EncryptionKey { get; }

        public string EncryptionPem { get; }

        public void Dispose()
        {
            SigningKey.Dispose();
            EncryptionKey.Dispose();
        }

        private static (RSA Key, string Pem) MakeKey(string use)
        {
            RSA rsa = RSA.Create(2048);
            using X509Certificate2 certificate = new CertificateRequest("CN=Test SP " + use, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            return (rsa, certificate.ExportCertificatePem());
        }
    }
}
