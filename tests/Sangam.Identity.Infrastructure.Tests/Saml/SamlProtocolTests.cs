using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using Sangam.Identity.Infrastructure.Saml;

namespace Sangam.Identity.Infrastructure.Tests.Saml;

/// <summary>
/// PR-22: the SAML message handling Sangam writes itself — safe XML loading, the defence against signature wrapping,
/// encryption only the SP can open, redirect-binding compression, SP metadata import and the assurance mapping.
/// </summary>
public sealed class SamlProtocolTests : IDisposable
{
    private const string Idp = "https://id.sangamid.in/saml";
    private const string Acs = "https://crm.example.in/saml/acs";
    private static readonly string[] IdSpellings = ["ID", "Id", "id"];
    private readonly X509Certificate2 _signing = SelfSigned("CN=Sangam test IdP");
    private readonly RSA _spKey = RSA.Create(2048);

    public void Dispose()
    {
        _signing.Dispose();
        _spKey.Dispose();
    }

    [Fact]
    public void Load_RefusesDocumentTypeDefinitions()
    {
        const string Xxe = "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><r>&x;</r>";
        Assert.Throws<XmlException>(() => SamlProtocol.Load(Xxe));
    }

    [Fact]
    public void ASignedResponse_Verifies_AndAnyChangeBreaksIt()
    {
        XmlDocument response = SamlProtocol.Load(SamlProtocol.BuildResponse(Content(encryptFor: null), _signing));
        using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(_signing.Export(X509ContentType.Cert));
        Assert.True(SamlProtocol.VerifyEnvelopedSignature(response, publicOnly));

        XmlNode nameId = response.GetElementsByTagName("NameID", SamlProtocol.AssertionNs)[0]!;
        nameId.InnerText = "someone-else";
        Assert.False(SamlProtocol.VerifyEnvelopedSignature(response, publicOnly));

        using X509Certificate2 other = SelfSigned("CN=Not Sangam");
        Assert.False(SamlProtocol.VerifyEnvelopedSignature(SamlProtocol.Load(SamlProtocol.BuildResponse(Content(encryptFor: null), _signing)), other));
    }

    [Fact]
    public void SignatureWrapping_IsRefused()
    {
        // A signed response moved inside an attacker's envelope: the signature is valid, but not over the root.
        XmlDocument genuine = SamlProtocol.Load(SamlProtocol.BuildResponse(Content(encryptFor: null), _signing));
        XmlDocument wrapped = SamlProtocol.Load("<samlp:Response xmlns:samlp=\"" + SamlProtocol.ProtocolNs + "\" ID=\"_evil\" Version=\"2.0\"><x>" + genuine.DocumentElement!.OuterXml + "</x></samlp:Response>");
        Assert.False(SamlProtocol.VerifyEnvelopedSignature(wrapped, _signing));

        // Two signatures: refused outright, whatever they cover.
        XmlDocument doubled = SamlProtocol.Load(genuine.OuterXml);
        XmlNode signature = doubled.GetElementsByTagName("Signature", SamlProtocol.DsigNs)[0]!;
        doubled.DocumentElement!.AppendChild(signature.CloneNode(deep: true));
        Assert.False(SamlProtocol.VerifyEnvelopedSignature(doubled, _signing));
    }

    [Fact]
    public void TheRootId_MustBeAValidXmlName_AndBelongToNoOtherElement()
    {
        // rc.4 (CodeQL cs/xml/xpath-injection): the check no longer strips characters before comparing, so an ID with a
        // quote cannot be mistaken for another element's ID; it is refused as not an xs:ID at all.
        Assert.False(SamlProtocol.IsWellFormedId("_a'b"));
        Assert.False(SamlProtocol.IsWellFormedId("_a\"b"));
        Assert.False(SamlProtocol.IsWellFormedId(string.Empty));
        Assert.False(SamlProtocol.IsWellFormedId("1starts-with-a-digit"));
        Assert.True(SamlProtocol.IsWellFormedId("_9f8c1d2e-1a2b-4c3d-8e9f-0a1b2c3d4e5f"));

        XmlDocument document = SamlProtocol.Load("<r ID=\"_x'\"><a ID=\"_x\"/><b Id=\"_y\"/><c id=\"_y\"/></r>");
        Assert.Equal(1, SamlProtocol.ElementsWithId(document, "_x'"));
        Assert.Equal(1, SamlProtocol.ElementsWithId(document, "_x"));
        Assert.Equal(2, SamlProtocol.ElementsWithId(document, "_y"));
    }

    [Fact]
    public void AnotherElementUsingTheRootsId_InAnySpelling_IsRefused()
    {
        XmlDocument genuine = SamlProtocol.Load(SamlProtocol.BuildResponse(Content(encryptFor: null), _signing));
        string id = genuine.DocumentElement!.GetAttribute("ID");
        Assert.Equal(1, SamlProtocol.ElementsWithId(genuine, id));

        foreach (string attribute in IdSpellings)
        {
            XmlDocument copy = SamlProtocol.Load(genuine.OuterXml);
            XmlElement decoy = copy.CreateElement("decoy");
            decoy.SetAttribute(attribute, id);
            copy.DocumentElement!.AppendChild(decoy);
            Assert.Equal(2, SamlProtocol.ElementsWithId(copy, id));
            Assert.False(SamlProtocol.VerifyEnvelopedSignature(copy, _signing));
        }
    }

    [Fact]
    public void AnEncryptedAssertion_OpensOnlyWithTheServiceProvidersKey_AndIsSignedInside()
    {
        using X509Certificate2 spCertificate = new CertificateRequest("CN=SP encryption", _spKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using X509Certificate2 spPublic = X509CertificateLoader.LoadCertificate(spCertificate.Export(X509ContentType.Cert));
        XmlDocument response = SamlProtocol.Load(SamlProtocol.BuildResponse(Content(spPublic), _signing));
        Assert.True(SamlProtocol.VerifyEnvelopedSignature(response, _signing));
        Assert.DoesNotContain("pairwise-subject", response.OuterXml, StringComparison.Ordinal);

        XmlElement encrypted = (XmlElement)response.GetElementsByTagName("EncryptedAssertion", SamlProtocol.AssertionNs)[0]!;
        XmlDocument assertion = SamlProtocol.Load(SamlProtocol.DecryptAssertion(encrypted, _spKey));
        Assert.True(SamlProtocol.VerifyEnvelopedSignature(assertion, _signing));
        Assert.Equal("pairwise-subject", assertion.GetElementsByTagName("NameID", SamlProtocol.AssertionNs)[0]!.InnerText);

        using RSA stranger = RSA.Create(2048);
        Assert.ThrowsAny<CryptographicException>(() => SamlProtocol.DecryptAssertion(encrypted, stranger));
    }

    [Theory]
    [InlineData(true, "http://www.w3.org/2009/xmlenc11#rsa-oaep")]
    [InlineData(false, "http://www.w3.org/2001/04/xmlenc#rsa-oaep-mgf1p")]
    public void TheKey_IsWrappedWithSha256OaepByDefault_OrSha1ForAnOlderServiceProvider(bool sha256, string algorithm)
    {
        // rc.15 (ASVS V6.2.5).
        using X509Certificate2 spCertificate = new CertificateRequest("CN=SP encryption", _spKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using X509Certificate2 spPublic = X509CertificateLoader.LoadCertificate(spCertificate.Export(X509ContentType.Cert));
        XmlDocument response = SamlProtocol.Load(SamlProtocol.BuildResponse(Content(spPublic) with { OaepSha256 = sha256 }, _signing));
        XmlNamespaceManager ns = new(response.NameTable);
        ns.AddNamespace("xenc", SamlProtocol.XencNs);
        ns.AddNamespace("ds", SamlProtocol.DsigNs);
        XmlElement method = (XmlElement)response.SelectSingleNode("//xenc:EncryptedKey/xenc:EncryptionMethod", ns)!;
        Assert.Equal(algorithm, method.GetAttribute("Algorithm"));
        Assert.Equal(sha256 ? "http://www.w3.org/2001/04/xmlenc#sha256" : "http://www.w3.org/2000/09/xmldsig#sha1", ((XmlElement)method.SelectSingleNode("ds:DigestMethod", ns)!).GetAttribute("Algorithm"));
        Assert.Equal(sha256, method.InnerXml.Contains("mgf1sha256", StringComparison.Ordinal));

        XmlElement encrypted = (XmlElement)response.GetElementsByTagName("EncryptedAssertion", SamlProtocol.AssertionNs)[0]!;
        Assert.Contains("pairwise-subject", SamlProtocol.DecryptAssertion(encrypted, _spKey), StringComparison.Ordinal);

        // A raw RSA-OAEP-SHA256 unwrap of the key works only when Sangam used SHA-256 (so the label is honest).
        byte[] wrapped = Convert.FromBase64String(response.SelectSingleNode("//xenc:EncryptedKey/xenc:CipherData/xenc:CipherValue", ns)!.InnerText);
        if (sha256)
        {
            Assert.Equal(32, _spKey.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256).Length);
        }
        else
        {
            Assert.ThrowsAny<CryptographicException>(() => _spKey.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256));
        }
    }

    [Fact]
    public void InboundMessages_MustFollowTheSamlSchema()
    {
        // rc.15 (ASVS V13.3.1).
        const string Good = "<samlp:AuthnRequest xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" xmlns:saml=\"urn:oasis:names:tc:SAML:2.0:assertion\" ID=\"_a1\" Version=\"2.0\" IssueInstant=\"2026-10-22T05:00:00Z\" AssertionConsumerServiceURL=\"https://crm.example.in/saml/acs\"><saml:Issuer>https://crm.example.in/saml</saml:Issuer><samlp:NameIDPolicy AllowCreate=\"true\" Format=\"urn:oasis:names:tc:SAML:2.0:nameid-format:persistent\"/></samlp:AuthnRequest>";
        Assert.Equal("_a1", SamlProtocol.LoadMessage(Good).DocumentElement!.GetAttribute("ID"));
        const string Logout = "<samlp:LogoutRequest xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" xmlns:saml=\"urn:oasis:names:tc:SAML:2.0:assertion\" ID=\"_l1\" Version=\"2.0\" IssueInstant=\"2026-10-22T05:00:00Z\"><saml:Issuer>https://crm.example.in/saml</saml:Issuer><saml:NameID>abc</saml:NameID><samlp:SessionIndex>_s1</samlp:SessionIndex></samlp:LogoutRequest>";
        SamlProtocol.LoadMessage(Logout);

        string[] bad =
        [
            Good.Replace(" Version=\"2.0\"", string.Empty, StringComparison.Ordinal),                       // a required attribute missing
            Good.Replace("2026-10-22T05:00:00Z", "yesterday", StringComparison.Ordinal),                      // not a dateTime
            Good.Replace("ID=\"_a1\"", "ID=\"1a\"", StringComparison.Ordinal),                                // not an XML ID
            Good.Replace("AllowCreate=\"true\"", "AllowCreate=\"maybe\"", StringComparison.Ordinal),          // not a boolean
            Good.Replace("<saml:Issuer>", "<samlp:Unknown/><saml:Issuer>", StringComparison.Ordinal),         // an element the schema does not have
            Good.Replace("<saml:Issuer>https://crm.example.in/saml</saml:Issuer><samlp:NameIDPolicy", "<samlp:NameIDPolicy", StringComparison.Ordinal).Replace("</samlp:AuthnRequest>", "<saml:Issuer>x</saml:Issuer></samlp:AuthnRequest>", StringComparison.Ordinal), // out of order
            "<samlp:Response xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" ID=\"_r\" Version=\"2.0\"/>",  // incomplete
            "<r/>",                                                                                          // not SAML at all
        ];
        foreach (string message in bad)
        {
            Assert.Throws<InvalidDataException>(() => SamlProtocol.LoadMessage(message));
        }
    }

    [Fact]
    public void RedirectBinding_RoundTrips_AndRefusesOversizedMessages()
    {
        const string Xml = "<samlp:AuthnRequest xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" ID=\"_a\"/>";
        Assert.Equal(Xml, SamlProtocol.InflateRedirect(SamlProtocol.DeflateRedirect(Xml)));
        string bomb = SamlProtocol.DeflateRedirect("<r>" + new string('a', 2_000_000) + "</r>");
        Assert.ThrowsAny<InvalidDataException>(() => SamlProtocol.InflateRedirect(bomb));
    }

    [Fact]
    public void ServiceProviderMetadata_IsRead_PostBindingOnly_DefaultFirst()
    {
        string certificate = Convert.ToBase64String(_signing.Export(X509ContentType.Cert));
        string metadata = "<md:EntityDescriptor xmlns:md=\"" + SamlProtocol.MetadataNs + "\" xmlns:ds=\"" + SamlProtocol.DsigNs + "\" entityID=\"https://crm.example.in/saml\">"
            + "<md:SPSSODescriptor AuthnRequestsSigned=\"true\" protocolSupportEnumeration=\"urn:oasis:names:tc:SAML:2.0:protocol\">"
            + "<md:KeyDescriptor use=\"signing\"><ds:KeyInfo><ds:X509Data><ds:X509Certificate>" + certificate + "</ds:X509Certificate></ds:X509Data></ds:KeyInfo></md:KeyDescriptor>"
            + "<md:SingleLogoutService Binding=\"" + SamlProtocol.RedirectBinding + "\" Location=\"https://crm.example.in/saml/slo\"/>"
            + "<md:AssertionConsumerService Binding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Artifact\" Location=\"https://crm.example.in/artifact\" index=\"0\"/>"
            + "<md:AssertionConsumerService Binding=\"" + SamlProtocol.PostBinding + "\" Location=\"https://crm.example.in/saml/acs2\" index=\"1\"/>"
            + "<md:AssertionConsumerService Binding=\"" + SamlProtocol.PostBinding + "\" Location=\"" + Acs + "\" index=\"2\" isDefault=\"true\"/>"
            + "</md:SPSSODescriptor></md:EntityDescriptor>";
        SpMetadata sp = SamlProtocol.ReadSpMetadata(metadata);
        Assert.Equal("https://crm.example.in/saml", sp.EntityId);
        Assert.Equal([Acs, "https://crm.example.in/saml/acs2"], sp.AcsUrls);
        Assert.Equal("https://crm.example.in/saml/slo", sp.SloUrl);
        Assert.True(sp.WantsSignedRequests);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", sp.SigningCertificatePem, StringComparison.Ordinal);
        Assert.Null(sp.EncryptionCertificatePem);
        Assert.True(sp.OaepSha256);
        Assert.Throws<InvalidDataException>(() => SamlProtocol.ReadSpMetadata("<x/>"));

        // rc.15: an SP whose metadata lists only SHA-1 key transport gets SHA-1.
        string older = metadata.Replace("</md:KeyDescriptor>", "<md:EncryptionMethod Algorithm=\"http://www.w3.org/2001/04/xmlenc#rsa-oaep-mgf1p\"/></md:KeyDescriptor>", StringComparison.Ordinal);
        Assert.False(SamlProtocol.ReadSpMetadata(older).OaepSha256);
    }

    [Theory]
    [InlineData(SamlProtocol.PasswordProtectedTransport, 1)]
    [InlineData(SamlProtocol.RefedsMfa, 2)]
    [InlineData("urn:sangam:acr:3", 3)]
    [InlineData("urn:example:unknown", -1)]
    public void AuthenticationContexts_MapToSangamLevels(string classRef, int level)
    {
        Assert.Equal(level, SamlProtocol.LevelOf(classRef));
    }

    private static SamlResponseContent Content(X509Certificate2? encryptFor) => new(
        Idp,
        Acs,
        "https://crm.example.in/saml",
        "_req1",
        DateTimeOffset.UtcNow,
        TimeSpan.FromMinutes(5),
        SamlProtocol.Success,
        null,
        new SamlSubjectContent("pairwise-subject", SamlProtocol.PersistentNameId, DateTimeOffset.UtcNow, "_s1", SamlProtocol.PasswordProtectedTransport, [new SamlClaim("urn:oid:0.9.2342.19200300.100.1.3", "mail", ["anil@example.in"])]),
        encryptFor);

    private static X509Certificate2 SelfSigned(string subject)
    {
        using RSA rsa = RSA.Create(2048);
        return new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
