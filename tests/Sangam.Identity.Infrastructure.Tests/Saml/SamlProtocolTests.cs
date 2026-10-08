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
        Assert.Throws<InvalidDataException>(() => SamlProtocol.ReadSpMetadata("<x/>"));
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
