using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace Sangam.Identity.Infrastructure.Saml;

/// <summary>
/// The SAML 2.0 messages Sangam's identity provider reads and writes (PR-22, SGM-215), with Microsoft's own XML
/// signature code and no SAML library. Reading is defensive: no DTDs, no external entities, a size cap, one root of
/// the expected kind, and a signature accepted only when it is the root's own, covers the root by its ID and is the
/// only one. Writing signs the assertion and the response (RSA-SHA256, exclusive canonicalisation) and, when the SP
/// gave an encryption certificate, encrypts the assertion (AES-256-GCM, key wrapped with RSA-OAEP).
/// </summary>
public static class SamlProtocol
{
    /// <summary>SAML 2.0 assertion namespace.</summary>
    public const string AssertionNs = "urn:oasis:names:tc:SAML:2.0:assertion";

    /// <summary>SAML 2.0 protocol namespace.</summary>
    public const string ProtocolNs = "urn:oasis:names:tc:SAML:2.0:protocol";

    /// <summary>SAML 2.0 metadata namespace.</summary>
    public const string MetadataNs = "urn:oasis:names:tc:SAML:2.0:metadata";

    /// <summary>XML Signature namespace.</summary>
    public const string DsigNs = "http://www.w3.org/2000/09/xmldsig#";

    /// <summary>XML Encryption namespace.</summary>
    public const string XencNs = "http://www.w3.org/2001/04/xmlenc#";

    /// <summary>HTTP-Redirect binding.</summary>
    public const string RedirectBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-Redirect";

    /// <summary>HTTP-POST binding.</summary>
    public const string PostBinding = "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST";

    /// <summary>Persistent (pairwise) NameID format.</summary>
    public const string PersistentNameId = "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent";

    /// <summary>E-mail NameID format.</summary>
    public const string EmailNameId = "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress";

    /// <summary>Status: success.</summary>
    public const string Success = "urn:oasis:names:tc:SAML:2.0:status:Success";

    /// <summary>Status: the requester was at fault.</summary>
    public const string Requester = "urn:oasis:names:tc:SAML:2.0:status:Requester";

    /// <summary>Status (second level): the authentication context asked for cannot be met.</summary>
    public const string NoAuthnContext = "urn:oasis:names:tc:SAML:2.0:status:NoAuthnContext";

    /// <summary>Status (second level): the person refused.</summary>
    public const string RequestDenied = "urn:oasis:names:tc:SAML:2.0:status:RequestDenied";

    /// <summary>Authentication context: password over a protected transport (one factor).</summary>
    public const string PasswordProtectedTransport = "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport";

    /// <summary>Authentication context: multi-factor (REFEDS MFA profile).</summary>
    public const string RefedsMfa = "https://refeds.org/profile/mfa";

    private const int MaxMessageBytes = 64 * 1024;
    private const string AesGcm = "http://www.w3.org/2009/xmlenc11#aes256-gcm";
    private const string RsaOaep = "http://www.w3.org/2001/04/xmlenc#rsa-oaep-mgf1p";

    /// <summary>Decodes an HTTP-Redirect message (base64 of raw DEFLATE).</summary>
    /// <param name="value">The SAMLRequest or SAMLResponse value, already URL-decoded.</param>
    public static string InflateRedirect(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] compressed = Convert.FromBase64String(value);
        using DeflateStream deflate = new(new MemoryStream(compressed), CompressionMode.Decompress);
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > MaxMessageBytes)
            {
                throw new InvalidDataException("The SAML message is too large.");
            }
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>Encodes a message for the HTTP-Redirect binding (raw DEFLATE, then base64).</summary>
    /// <param name="xml">The message.</param>
    public static string DeflateRedirect(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using MemoryStream output = new();
        using (DeflateStream deflate = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(xml);
            deflate.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>Loads untrusted XML safely: no DTD, no resolver, a size cap, whitespace kept for signatures.</summary>
    /// <param name="xml">The XML.</param>
    public static XmlDocument Load(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (Encoding.UTF8.GetByteCount(xml) > MaxMessageBytes)
        {
            throw new InvalidDataException("The SAML message is too large.");
        }

        XmlDocument document = new() { PreserveWhitespace = true, XmlResolver = null };
        using StringReader text = new(xml);
        using XmlReader reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxMessageBytes });
        document.Load(reader);
        return document;
    }

    /// <summary>Reads an AuthnRequest.</summary>
    /// <param name="document">The message.</param>
    public static AuthnRequestMessage ReadAuthnRequest(XmlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        XmlElement root = Root(document, "AuthnRequest");
        XmlNamespaceManager ns = Namespaces(document);
        List<string> classes = [.. root.SelectNodes("samlp:RequestedAuthnContext/saml:AuthnContextClassRef", ns)!.Cast<XmlNode>().Select(n => n.InnerText.Trim())];
        string comparison = (root.SelectSingleNode("samlp:RequestedAuthnContext", ns) as XmlElement)?.GetAttribute("Comparison") is { Length: > 0 } c ? c : "exact";
        return new AuthnRequestMessage(
            Required(root, "ID"),
            root.SelectSingleNode("saml:Issuer", ns)?.InnerText.Trim() ?? throw new InvalidDataException("The request names no issuer."),
            ParseInstant(Required(root, "IssueInstant")),
            Attr(root, "Destination"),
            Attr(root, "AssertionConsumerServiceURL"),
            Attr(root, "ProtocolBinding"),
            string.Equals(Attr(root, "ForceAuthn"), "true", StringComparison.OrdinalIgnoreCase),
            string.Equals(Attr(root, "IsPassive"), "true", StringComparison.OrdinalIgnoreCase),
            (root.SelectSingleNode("samlp:NameIDPolicy", ns) as XmlElement)?.GetAttribute("Format") is { Length: > 0 } f ? f : null,
            classes,
            comparison);
    }

    /// <summary>Reads a LogoutRequest.</summary>
    /// <param name="document">The message.</param>
    public static LogoutRequestMessage ReadLogoutRequest(XmlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        XmlElement root = Root(document, "LogoutRequest");
        XmlNamespaceManager ns = Namespaces(document);
        return new LogoutRequestMessage(
            Required(root, "ID"),
            root.SelectSingleNode("saml:Issuer", ns)?.InnerText.Trim() ?? throw new InvalidDataException("The request names no issuer."),
            ParseInstant(Required(root, "IssueInstant")),
            Attr(root, "Destination"),
            root.SelectSingleNode("saml:NameID", ns)?.InnerText.Trim(),
            [.. root.SelectNodes("samlp:SessionIndex", ns)!.Cast<XmlNode>().Select(n => n.InnerText.Trim())]);
    }

    /// <summary>
    /// Checks an HTTP-Redirect binding signature over the raw query exactly as sent: <c>SAMLRequest=…&amp;RelayState=…&amp;SigAlg=…</c>
    /// (values still URL-encoded), RSA-SHA256 only.
    /// </summary>
    /// <param name="rawQuery">The query string as received, without the leading '?'.</param>
    /// <param name="certificate">The SP's signing certificate.</param>
    public static bool VerifyRedirectSignature(string rawQuery, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(rawQuery);
        ArgumentNullException.ThrowIfNull(certificate);
        Dictionary<string, string> raw = new(StringComparer.Ordinal);
        foreach (string pair in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                raw[pair[..eq]] = pair[(eq + 1)..];
            }
        }

        string messageKey = raw.ContainsKey("SAMLRequest") ? "SAMLRequest" : "SAMLResponse";
        if (!raw.TryGetValue(messageKey, out string? message) || !raw.TryGetValue("SigAlg", out string? sigAlg) || !raw.TryGetValue("Signature", out string? signature)
            || Uri.UnescapeDataString(sigAlg) != SignedXml.XmlDsigRSASHA256Url)
        {
            return false;
        }

        string signed = messageKey + "=" + message + (raw.TryGetValue("RelayState", out string? relay) ? "&RelayState=" + relay : string.Empty) + "&SigAlg=" + sigAlg;
        using RSA? rsa = certificate.GetRSAPublicKey();
        try
        {
            return rsa is not null && rsa.VerifyData(Encoding.UTF8.GetBytes(signed), Convert.FromBase64String(Uri.UnescapeDataString(signature.Replace("+", "%2B", StringComparison.Ordinal))), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks an enveloped signature (HTTP-POST binding): exactly one signature that is a direct child of the root,
    /// whose one reference is the root's own ID — the defence against signature wrapping — valid for the certificate.
    /// A signature deeper down (an assertion's own, inside a signed response) signs only its own element and is
    /// checked separately on that element.
    /// </summary>
    /// <param name="document">The message.</param>
    /// <param name="certificate">The signer's certificate.</param>
    public static bool VerifyEnvelopedSignature(XmlDocument document, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(certificate);
        XmlElement root = document.DocumentElement!;
        List<XmlElement> own = [.. root.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Signature" && e.NamespaceURI == DsigNs)];
        if (own.Count != 1 || !root.HasAttribute("ID") || document.SelectNodes("//*[@ID='" + root.GetAttribute("ID").Replace("'", string.Empty, StringComparison.Ordinal) + "']")!.Count != 1)
        {
            return false;
        }

        SignedXml signed = new(root);
        signed.LoadXml(own[0]);
        if (signed.SignedInfo!.References.Count != 1 || ((Reference)signed.SignedInfo.References[0]!).Uri != "#" + root.GetAttribute("ID"))
        {
            return false;
        }

        return signed.CheckSignature(certificate, verifySignatureOnly: true);
    }

    /// <summary>Builds the signed (and, for an SP with an encryption certificate, encrypted) response to a sign-in.</summary>
    /// <param name="response">What to say.</param>
    /// <param name="signing">Sangam's SAML signing certificate (with its private key).</param>
    public static string BuildResponse(SamlResponseContent response, X509Certificate2 signing)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(signing);
        XmlDocument document = new() { PreserveWhitespace = false };
        string responseId = NewId();
        XmlElement root = document.CreateElement("samlp", "Response", ProtocolNs);
        root.SetAttribute("xmlns:saml", AssertionNs);
        root.SetAttribute("ID", responseId);
        root.SetAttribute("Version", "2.0");
        root.SetAttribute("IssueInstant", Instant(response.IssuedAt));
        root.SetAttribute("Destination", response.Destination);
        if (!string.IsNullOrEmpty(response.InResponseTo))
        {
            root.SetAttribute("InResponseTo", response.InResponseTo);
        }

        document.AppendChild(root);
        XmlElement issuer = Saml(document, "Issuer", response.IdpEntityId);
        root.AppendChild(issuer);
        root.AppendChild(Status(document, response.StatusCode, response.SubStatusCode));

        if (response.Subject is not null)
        {
            XmlElement assertion = Assertion(document, response, response.Subject);
            root.AppendChild(assertion);
            Sign(assertion, (XmlElement)assertion.FirstChild!, signing);
            if (response.EncryptFor is X509Certificate2 encryptFor)
            {
                XmlElement encrypted = Encrypt(document, assertion, encryptFor);
                root.ReplaceChild(encrypted, assertion);
            }
        }

        Sign(root, issuer, signing);
        return document.OuterXml;
    }

    /// <summary>Builds a signed LogoutResponse.</summary>
    /// <param name="idpEntityId">Sangam's entity id.</param>
    /// <param name="destination">The SP's logout address.</param>
    /// <param name="inResponseTo">The LogoutRequest's ID.</param>
    /// <param name="issuedAt">Now.</param>
    /// <param name="signing">Signing certificate.</param>
    public static string BuildLogoutResponse(string idpEntityId, string destination, string inResponseTo, DateTimeOffset issuedAt, X509Certificate2 signing)
    {
        ArgumentNullException.ThrowIfNull(signing);
        XmlDocument document = new();
        XmlElement root = document.CreateElement("samlp", "LogoutResponse", ProtocolNs);
        root.SetAttribute("xmlns:saml", AssertionNs);
        root.SetAttribute("ID", NewId());
        root.SetAttribute("Version", "2.0");
        root.SetAttribute("IssueInstant", Instant(issuedAt));
        root.SetAttribute("Destination", destination);
        root.SetAttribute("InResponseTo", inResponseTo);
        document.AppendChild(root);
        XmlElement issuer = Saml(document, "Issuer", idpEntityId);
        root.AppendChild(issuer);
        root.AppendChild(Status(document, Success, null));
        Sign(root, issuer, signing);
        return document.OuterXml;
    }

    /// <summary>Sangam's IdP metadata: entity id, signing key, single sign-on and logout addresses, NameID formats.</summary>
    /// <param name="entityId">Entity id.</param>
    /// <param name="ssoUrl">Single sign-on address.</param>
    /// <param name="sloUrl">Single logout address.</param>
    /// <param name="signing">The signing certificates (current first; a previous one stays published during a rotation).</param>
    public static string Metadata(string entityId, string ssoUrl, string sloUrl, IReadOnlyList<X509Certificate2> signing)
    {
        ArgumentNullException.ThrowIfNull(signing);
        XmlDocument document = new();
        XmlElement root = document.CreateElement("md", "EntityDescriptor", MetadataNs);
        root.SetAttribute("entityID", entityId);
        document.AppendChild(root);
        XmlElement idp = document.CreateElement("md", "IDPSSODescriptor", MetadataNs);
        idp.SetAttribute("WantAuthnRequestsSigned", "false");
        idp.SetAttribute("protocolSupportEnumeration", ProtocolNs);
        root.AppendChild(idp);
        foreach (X509Certificate2 certificate in signing)
        {
            XmlElement key = document.CreateElement("md", "KeyDescriptor", MetadataNs);
            key.SetAttribute("use", "signing");
            XmlElement info = document.CreateElement("ds", "KeyInfo", DsigNs);
            XmlElement data = document.CreateElement("ds", "X509Data", DsigNs);
            XmlElement cert = document.CreateElement("ds", "X509Certificate", DsigNs);
            cert.InnerText = Convert.ToBase64String(certificate.RawData);
            data.AppendChild(cert);
            info.AppendChild(data);
            key.AppendChild(info);
            idp.AppendChild(key);
        }

        idp.AppendChild(Endpoint(document, "SingleLogoutService", RedirectBinding, sloUrl));
        foreach (string format in new[] { PersistentNameId, EmailNameId })
        {
            XmlElement nameId = document.CreateElement("md", "NameIDFormat", MetadataNs);
            nameId.InnerText = format;
            idp.AppendChild(nameId);
        }

        idp.AppendChild(Endpoint(document, "SingleSignOnService", RedirectBinding, ssoUrl));
        idp.AppendChild(Endpoint(document, "SingleSignOnService", PostBinding, ssoUrl));
        return document.OuterXml;
    }

    /// <summary>Reads an SP's metadata: entity id, ACS addresses (HTTP-POST), logout address, certificates.</summary>
    /// <param name="xml">The SP metadata.</param>
    public static SpMetadata ReadSpMetadata(string xml)
    {
        XmlDocument document = Load(xml);
        XmlNamespaceManager ns = Namespaces(document);
        XmlElement entity = document.DocumentElement is { LocalName: "EntityDescriptor", NamespaceURI: MetadataNs } e ? e : throw new InvalidDataException("This is not SAML metadata (no EntityDescriptor).");
        XmlElement sp = entity.SelectSingleNode("md:SPSSODescriptor", ns) as XmlElement ?? throw new InvalidDataException("The metadata describes no service provider (SPSSODescriptor).");
        List<string> acs = [.. sp.SelectNodes("md:AssertionConsumerService", ns)!.Cast<XmlElement>()
            .Where(a => a.GetAttribute("Binding") == PostBinding)
            .OrderBy(a => a.GetAttribute("isDefault") == "true" ? 0 : 1)
            .ThenBy(a => int.TryParse(a.GetAttribute("index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : int.MaxValue)
            .Select(a => a.GetAttribute("Location"))];
        string? slo = sp.SelectNodes("md:SingleLogoutService", ns)!.Cast<XmlElement>().FirstOrDefault(s => s.GetAttribute("Binding") == RedirectBinding)?.GetAttribute("Location");
        string? Cert(string use) => sp.SelectNodes("md:KeyDescriptor", ns)!.Cast<XmlElement>()
            .Where(k => k.GetAttribute("use") is var u && (u == use || u.Length == 0))
            .Select(k => k.SelectSingleNode("ds:KeyInfo/ds:X509Data/ds:X509Certificate", ns)?.InnerText)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) is string b64
                ? "-----BEGIN CERTIFICATE-----\n" + string.Join('\n', b64.Where(ch => !char.IsWhiteSpace(ch)).Chunk(64).Select(chunk => new string(chunk))) + "\n-----END CERTIFICATE-----"
                : null;
        return new SpMetadata(entity.GetAttribute("entityID"), acs, slo, Cert("signing"), Cert("encryption"), sp.GetAttribute("AuthnRequestsSigned") == "true");
    }

    /// <summary>The assurance level a SAML authentication context asks for: Sangam's own, REFEDS MFA, or password.</summary>
    /// <param name="classRef">The class reference.</param>
    public static int LevelOf(string classRef) => classRef switch
    {
        PasswordProtectedTransport => 1,
        "urn:oasis:names:tc:SAML:2.0:ac:classes:Password" => 1,
        "urn:oasis:names:tc:SAML:2.0:ac:classes:unspecified" => 0,
        RefedsMfa => 2,
        "urn:sangam:acr:1" => 1,
        "urn:sangam:acr:2" => 2,
        "urn:sangam:acr:3" => 3,
        _ => -1,
    };

    /// <summary>The authentication context to state for the level reached.</summary>
    /// <param name="level">Level 1, 2 or 3.</param>
    public static string ClassFor(int level) => level >= 2 ? RefedsMfa : PasswordProtectedTransport;

    private static XmlElement Assertion(XmlDocument document, SamlResponseContent response, SamlSubjectContent subject)
    {
        XmlElement assertion = document.CreateElement("saml", "Assertion", AssertionNs);
        assertion.SetAttribute("xmlns:saml", AssertionNs);
        assertion.SetAttribute("ID", NewId());
        assertion.SetAttribute("Version", "2.0");
        assertion.SetAttribute("IssueInstant", Instant(response.IssuedAt));
        assertion.AppendChild(Saml(document, "Issuer", response.IdpEntityId));

        XmlElement subjectElement = document.CreateElement("saml", "Subject", AssertionNs);
        XmlElement nameId = Saml(document, "NameID", subject.NameId);
        nameId.SetAttribute("Format", subject.NameIdFormat);
        if (subject.NameIdFormat == PersistentNameId)
        {
            nameId.SetAttribute("NameQualifier", response.IdpEntityId);
            nameId.SetAttribute("SPNameQualifier", response.Audience);
        }

        subjectElement.AppendChild(nameId);
        XmlElement confirmation = document.CreateElement("saml", "SubjectConfirmation", AssertionNs);
        confirmation.SetAttribute("Method", "urn:oasis:names:tc:SAML:2.0:cm:bearer");
        XmlElement data = document.CreateElement("saml", "SubjectConfirmationData", AssertionNs);
        if (!string.IsNullOrEmpty(response.InResponseTo))
        {
            data.SetAttribute("InResponseTo", response.InResponseTo);
        }

        data.SetAttribute("NotOnOrAfter", Instant(response.IssuedAt + response.Lifetime));
        data.SetAttribute("Recipient", response.Destination);
        confirmation.AppendChild(data);
        subjectElement.AppendChild(confirmation);
        assertion.AppendChild(subjectElement);

        XmlElement conditions = document.CreateElement("saml", "Conditions", AssertionNs);
        conditions.SetAttribute("NotBefore", Instant(response.IssuedAt.AddMinutes(-1)));
        conditions.SetAttribute("NotOnOrAfter", Instant(response.IssuedAt + response.Lifetime));
        XmlElement restriction = document.CreateElement("saml", "AudienceRestriction", AssertionNs);
        restriction.AppendChild(Saml(document, "Audience", response.Audience));
        conditions.AppendChild(restriction);
        assertion.AppendChild(conditions);

        XmlElement authn = document.CreateElement("saml", "AuthnStatement", AssertionNs);
        authn.SetAttribute("AuthnInstant", Instant(subject.AuthnInstant));
        authn.SetAttribute("SessionIndex", subject.SessionIndex);
        authn.SetAttribute("SessionNotOnOrAfter", Instant(response.IssuedAt.AddHours(8)));
        XmlElement context = document.CreateElement("saml", "AuthnContext", AssertionNs);
        context.AppendChild(Saml(document, "AuthnContextClassRef", subject.AuthnContextClass));
        authn.AppendChild(context);
        assertion.AppendChild(authn);

        if (subject.Attributes.Count > 0)
        {
            XmlElement statement = document.CreateElement("saml", "AttributeStatement", AssertionNs);
            foreach (SamlClaim attribute in subject.Attributes)
            {
                XmlElement element = document.CreateElement("saml", "Attribute", AssertionNs);
                element.SetAttribute("Name", attribute.Name);
                element.SetAttribute("NameFormat", "urn:oasis:names:tc:SAML:2.0:attrname-format:uri");
                element.SetAttribute("FriendlyName", attribute.FriendlyName);
                foreach (string value in attribute.Values)
                {
                    element.AppendChild(Saml(document, "AttributeValue", value));
                }

                statement.AppendChild(element);
            }

            assertion.AppendChild(statement);
        }

        return assertion;
    }

    private static void Sign(XmlElement element, XmlElement after, X509Certificate2 certificate)
    {
        using RSA rsa = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("The SAML signing certificate has no RSA private key.");
        SignedXml signed = new(element) { SigningKey = rsa };
        signed.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signed.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        Reference reference = new("#" + element.GetAttribute("ID")) { DigestMethod = SignedXml.XmlDsigSHA256Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signed.AddReference(reference);
        KeyInfo keyInfo = new();
        keyInfo.AddClause(new KeyInfoX509Data(certificate));
        signed.KeyInfo = keyInfo;
        signed.ComputeSignature();
        element.InsertAfter(element.OwnerDocument.ImportNode(signed.GetXml(), deep: true), after);
    }

    private static XmlElement Encrypt(XmlDocument document, XmlElement assertion, X509Certificate2 recipient)
    {
        byte[] plain = Encoding.UTF8.GetBytes(assertion.OuterXml);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] cipher = new byte[plain.Length];
        using (System.Security.Cryptography.AesGcm aes = new(key, tag.Length))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        using RSA rsa = recipient.GetRSAPublicKey() ?? throw new InvalidOperationException("The SP's encryption certificate has no RSA key.");
        byte[] wrapped = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA1);
        CryptographicOperations.ZeroMemory(key);

        XmlElement encryptedAssertion = document.CreateElement("saml", "EncryptedAssertion", AssertionNs);
        XmlElement data = document.CreateElement("xenc", "EncryptedData", XencNs);
        data.SetAttribute("Type", "http://www.w3.org/2001/04/xmlenc#Element");
        XmlElement method = document.CreateElement("xenc", "EncryptionMethod", XencNs);
        method.SetAttribute("Algorithm", AesGcm);
        data.AppendChild(method);
        XmlElement keyInfo = document.CreateElement("ds", "KeyInfo", DsigNs);
        XmlElement encryptedKey = document.CreateElement("xenc", "EncryptedKey", XencNs);
        XmlElement keyMethod = document.CreateElement("xenc", "EncryptionMethod", XencNs);
        keyMethod.SetAttribute("Algorithm", RsaOaep);
        XmlElement digest = document.CreateElement("ds", "DigestMethod", DsigNs);
        digest.SetAttribute("Algorithm", SignedXml.XmlDsigSHA1Url);
        keyMethod.AppendChild(digest);
        encryptedKey.AppendChild(keyMethod);
        encryptedKey.AppendChild(CipherData(document, wrapped));
        keyInfo.AppendChild(encryptedKey);
        data.AppendChild(keyInfo);
        data.AppendChild(CipherData(document, [.. nonce, .. cipher, .. tag]));
        encryptedAssertion.AppendChild(data);
        return encryptedAssertion;
    }

    /// <summary>Decrypts an EncryptedAssertion Sangam wrote (for tests and SP integrators checking their setup).</summary>
    /// <param name="encryptedAssertion">The element.</param>
    /// <param name="recipientKey">The SP's private key.</param>
    public static string DecryptAssertion(XmlElement encryptedAssertion, RSA recipientKey)
    {
        ArgumentNullException.ThrowIfNull(encryptedAssertion);
        ArgumentNullException.ThrowIfNull(recipientKey);
        XmlNamespaceManager ns = Namespaces(encryptedAssertion.OwnerDocument);
        byte[] wrapped = Convert.FromBase64String(encryptedAssertion.SelectSingleNode("xenc:EncryptedData/ds:KeyInfo/xenc:EncryptedKey/xenc:CipherData/xenc:CipherValue", ns)!.InnerText);
        byte[] all = Convert.FromBase64String(encryptedAssertion.SelectSingleNode("xenc:EncryptedData/xenc:CipherData/xenc:CipherValue", ns)!.InnerText);
        byte[] key = recipientKey.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA1);
        byte[] plain = new byte[all.Length - 28];
        using (System.Security.Cryptography.AesGcm aes = new(key, 16))
        {
            aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(12, plain.Length), all.AsSpan(12 + plain.Length, 16), plain);
        }

        return Encoding.UTF8.GetString(plain);
    }

    private static XmlElement CipherData(XmlDocument document, byte[] value)
    {
        XmlElement data = document.CreateElement("xenc", "CipherData", XencNs);
        XmlElement cipher = document.CreateElement("xenc", "CipherValue", XencNs);
        cipher.InnerText = Convert.ToBase64String(value);
        data.AppendChild(cipher);
        return data;
    }

    private static XmlElement Status(XmlDocument document, string code, string? subCode)
    {
        XmlElement status = document.CreateElement("samlp", "Status", ProtocolNs);
        XmlElement statusCode = document.CreateElement("samlp", "StatusCode", ProtocolNs);
        statusCode.SetAttribute("Value", code);
        if (subCode is not null)
        {
            XmlElement inner = document.CreateElement("samlp", "StatusCode", ProtocolNs);
            inner.SetAttribute("Value", subCode);
            statusCode.AppendChild(inner);
        }

        status.AppendChild(statusCode);
        return status;
    }

    private static XmlElement Endpoint(XmlDocument document, string name, string binding, string location)
    {
        XmlElement endpoint = document.CreateElement("md", name, MetadataNs);
        endpoint.SetAttribute("Binding", binding);
        endpoint.SetAttribute("Location", location);
        return endpoint;
    }

    private static XmlElement Saml(XmlDocument document, string name, string text)
    {
        XmlElement element = document.CreateElement("saml", name, AssertionNs);
        element.InnerText = text;
        return element;
    }

    private static XmlElement Root(XmlDocument document, string localName)
        => document.DocumentElement is { NamespaceURI: ProtocolNs } root && root.LocalName == localName
            ? root
            : throw new InvalidDataException($"Expected a SAML {localName}.");

    private static XmlNamespaceManager Namespaces(XmlDocument document)
    {
        XmlNamespaceManager ns = new(document.NameTable);
        ns.AddNamespace("saml", AssertionNs);
        ns.AddNamespace("samlp", ProtocolNs);
        ns.AddNamespace("md", MetadataNs);
        ns.AddNamespace("ds", DsigNs);
        ns.AddNamespace("xenc", XencNs);
        return ns;
    }

    private static string Required(XmlElement element, string attribute)
        => element.GetAttribute(attribute) is { Length: > 0 } value ? value : throw new InvalidDataException($"The message has no {attribute}.");

    private static string? Attr(XmlElement element, string attribute) => element.GetAttribute(attribute) is { Length: > 0 } value ? value : null;

    private static DateTimeOffset ParseInstant(string value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at)
            ? at
            : throw new InvalidDataException("The message has an unreadable IssueInstant.");

    private static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string NewId() => "_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
}

/// <summary>An AuthnRequest as read.</summary>
public sealed record AuthnRequestMessage(string Id, string Issuer, DateTimeOffset IssueInstant, string? Destination, string? AcsUrl, string? ProtocolBinding, bool ForceAuthn, bool IsPassive, string? NameIdFormat, IReadOnlyList<string> AuthnContextClasses, string Comparison);

/// <summary>A LogoutRequest as read.</summary>
public sealed record LogoutRequestMessage(string Id, string Issuer, DateTimeOffset IssueInstant, string? Destination, string? NameId, IReadOnlyList<string> SessionIndexes);

/// <summary>An SP's metadata as read.</summary>
public sealed record SpMetadata(string EntityId, IReadOnlyList<string> AcsUrls, string? SloUrl, string? SigningCertificatePem, string? EncryptionCertificatePem, bool WantsSignedRequests);

/// <summary>What a response says.</summary>
public sealed record SamlResponseContent(string IdpEntityId, string Destination, string Audience, string? InResponseTo, DateTimeOffset IssuedAt, TimeSpan Lifetime, string StatusCode, string? SubStatusCode, SamlSubjectContent? Subject, X509Certificate2? EncryptFor);

/// <summary>Who signed in, and what is released about them.</summary>
public sealed record SamlSubjectContent(string NameId, string NameIdFormat, DateTimeOffset AuthnInstant, string SessionIndex, string AuthnContextClass, IReadOnlyList<SamlClaim> Attributes);

/// <summary>One released attribute.</summary>
public sealed record SamlClaim(string Name, string FriendlyName, IReadOnlyList<string> Values);
