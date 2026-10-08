using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sangam.Identity.Infrastructure.Tests.Passkeys;

/// <summary>
/// A WebAuthn authenticator in software: an ES256 key pair, "none" attestation, real CBOR and real
/// signatures over real challenges. It produces the same JSON a browser's PublicKeyCredential.toJSON()
/// does, so the server code under test cannot tell it from a phone.
/// </summary>
internal sealed class SoftwareAuthenticator : IDisposable
{
    private static readonly string[] Internal = ["internal"];
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    public uint Counter { get; set; }

    /// <summary>The user handle the server gave at registration (options.user.id), returned at sign-in as a browser does.</summary>
    public string? UserHandle { get; private set; }

    public void Dispose() => _key.Dispose();

    /// <summary>Answers a registration (navigator.credentials.create).</summary>
    public string Create(string optionsJson, string origin, string rpId, bool userVerified = true)
    {
        JsonNode options = JsonNode.Parse(optionsJson)!;
        string challenge = options["challenge"]!.GetValue<string>();
        UserHandle = options["user"]?["id"]?.GetValue<string>();
        byte[] clientData = ClientData("webauthn.create", challenge, origin);
        ECParameters p = _key.ExportParameters(false);
        CborWriter cose = new(CborConformanceMode.Lax);
        cose.WriteStartMap(5);
        cose.WriteInt32(1);
        cose.WriteInt32(2);
        cose.WriteInt32(3);
        cose.WriteInt32(-7);
        cose.WriteInt32(-1);
        cose.WriteInt32(1);
        cose.WriteInt32(-2);
        cose.WriteByteString(p.Q.X!);
        cose.WriteInt32(-3);
        cose.WriteByteString(p.Q.Y!);
        cose.WriteEndMap();

        byte[] lengths = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lengths, (ushort)CredentialId.Length);
        byte[] authData = [.. AuthenticatorData(rpId, userVerified, attested: true), .. new byte[16], .. lengths, .. CredentialId, .. cose.Encode()];

        CborWriter attestation = new(CborConformanceMode.Lax);
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt");
        attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt");
        attestation.WriteStartMap(0);
        attestation.WriteEndMap();
        attestation.WriteTextString("authData");
        attestation.WriteByteString(authData);
        attestation.WriteEndMap();

        return JsonSerializer.Serialize(new
        {
            id = B64(CredentialId),
            rawId = B64(CredentialId),
            type = "public-key",
            response = new { attestationObject = B64(attestation.Encode()), clientDataJSON = B64(clientData), transports = Internal },
            clientExtensionResults = new { },
        });
    }

    /// <summary>Answers a sign-in (navigator.credentials.get).</summary>
    public string Get(string optionsJson, string origin, string rpId, Guid userId, bool userVerified = true)
    {
        string challenge = JsonNode.Parse(optionsJson)!["challenge"]!.GetValue<string>();
        byte[] clientData = ClientData("webauthn.get", challenge, origin);
        byte[] authData = AuthenticatorData(rpId, userVerified, attested: false);
        byte[] signature = _key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return JsonSerializer.Serialize(new
        {
            id = B64(CredentialId),
            rawId = B64(CredentialId),
            type = "public-key",
            response = new { authenticatorData = B64(authData), clientDataJSON = B64(clientData), signature = B64(signature), userHandle = UserHandle ?? B64(userId.ToByteArray()) },
            clientExtensionResults = new { },
        });
    }

    private static byte[] ClientData(string type, string challenge, string origin)
    {
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin, crossOrigin = false }));
    }

    private byte[] AuthenticatorData(string rpId, bool userVerified, bool attested)
    {
        byte flags = (byte)(0x01 | (userVerified ? 0x04 : 0) | (attested ? 0x40 : 0));
        byte[] counter = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(counter, Counter);
        return [.. SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), flags, .. counter];
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
