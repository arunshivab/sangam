using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sangam.Identity.Infrastructure.Apps;

/// <summary>
/// rc.15 (ASVS V2.10.1): reads a public key an application's owner pastes on the partner console — a PEM public key
/// (<c>-----BEGIN PUBLIC KEY-----</c>) or a JSON Web Key — and turns it into the JSON Web Key Sangam keeps. RSA keys need at
/// least 2048 bits; elliptic-curve keys use P-256, P-384 or P-521. A private key is refused with a warning, never kept.
/// The key id is the key's RFC 7638 thumbprint, so the same key always gets the same id.
/// </summary>
public static class ClientKeyFormat
{
    /// <summary>The smallest RSA key accepted, in bits.</summary>
    public const int MinRsaBits = 2048;

    /// <summary>The largest RSA key accepted, in bits.</summary>
    public const int MaxRsaBits = 8192;

    /// <summary>The longest text accepted.</summary>
    public const int MaxLength = 8000;

    /// <summary>Reads <paramref name="text"/>.</summary>
    /// <param name="text">A PEM public key or a JSON Web Key (alone, or the only key of a key set).</param>
    /// <returns>The key, or why it was refused.</returns>
    public static ClientKeyParse Parse(string? text)
    {
        string input = (text ?? string.Empty).Trim();
        if (input.Length == 0)
        {
            return ClientKeyParse.Refused("Paste the public key: a PEM block that starts with -----BEGIN PUBLIC KEY-----, or a JSON Web Key.");
        }

        if (input.Length > MaxLength)
        {
            return ClientKeyParse.Refused("That is too long to be a public key.");
        }

        if (input.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            return ClientKeyParse.Refused("That is a private key. Never share it, not even with Sangam: keep it on your servers, and paste only the public key.");
        }

        return input.StartsWith('{') ? FromJwk(input) : FromPem(input);
    }

    private static ClientKeyParse FromPem(string pem)
    {
        if (!pem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) && !pem.Contains("-----BEGIN RSA PUBLIC KEY-----", StringComparison.Ordinal))
        {
            return ClientKeyParse.Refused("Paste the public key: a PEM block that starts with -----BEGIN PUBLIC KEY-----, or a JSON Web Key.");
        }

        try
        {
            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return FromRsa(rsa.ExportParameters(false));
        }
        catch (CryptographicException)
        {
            // Not RSA: try an elliptic-curve key.
        }
        catch (ArgumentException)
        {
            return ClientKeyParse.Refused("Sangam could not read that key. Paste the whole PEM block, including the BEGIN and END lines.");
        }

        try
        {
            using ECDsa ec = ECDsa.Create();
            ec.ImportFromPem(pem);
            return FromEc(ec.ExportParameters(false));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return ClientKeyParse.Refused("Sangam could not read that key. Paste the whole PEM block, including the BEGIN and END lines.");
        }
    }

    private static ClientKeyParse FromJwk(string json)
    {
        JsonObject? key;
        try
        {
            key = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return ClientKeyParse.Refused("Sangam could not read that JSON Web Key.");
        }

        if (key?["keys"] is JsonArray keys)
        {
            if (keys.Count != 1)
            {
                return ClientKeyParse.Refused("Add one key at a time.");
            }

            key = keys[0] as JsonObject;
        }

        if (key is null)
        {
            return ClientKeyParse.Refused("Sangam could not read that JSON Web Key.");
        }

        string[] privateParts = ["d", "p", "q", "dp", "dq", "qi", "k"];
        if (privateParts.Any(key.ContainsKey))
        {
            return ClientKeyParse.Refused("That is a private key. Never share it, not even with Sangam: keep it on your servers, and paste only the public key.");
        }

        if (key["use"] is JsonValue use && use.ToString() != "sig")
        {
            return ClientKeyParse.Refused("That key is not for signing (its \"use\" is not \"sig\").");
        }

        try
        {
            return Text(key, "kty") switch
            {
                "RSA" => FromRsa(new RSAParameters { Modulus = Bytes(key, "n"), Exponent = Bytes(key, "e") }),
                "EC" => FromEc(new ECParameters { Curve = Curve(Text(key, "crv")), Q = new ECPoint { X = Bytes(key, "x"), Y = Bytes(key, "y") } }),
                _ => ClientKeyParse.Refused("Use an RSA or elliptic-curve (EC) key."),
            };
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or InvalidOperationException)
        {
            return ClientKeyParse.Refused("Sangam could not read that JSON Web Key.");
        }
    }

    private static ClientKeyParse FromRsa(RSAParameters p)
    {
        if (p.Modulus is null || p.Exponent is null)
        {
            return ClientKeyParse.Refused("Sangam could not read that key.");
        }

        // Check it is a usable key, not just two numbers.
        using RSA rsa = RSA.Create();
        rsa.ImportParameters(p);
        int bits = rsa.KeySize;
        if (bits < MinRsaBits)
        {
            return ClientKeyParse.Refused($"That RSA key has {bits} bits; use at least {MinRsaBits}.");
        }

        if (bits > MaxRsaBits)
        {
            return ClientKeyParse.Refused($"That RSA key has {bits} bits; use at most {MaxRsaBits}.");
        }

        string n = Base64Url(p.Modulus);
        string e = Base64Url(p.Exponent);
        string kid = Thumbprint($"{{\"e\":\"{e}\",\"kty\":\"RSA\",\"n\":\"{n}\"}}");
        JsonObject jwk = new() { ["kty"] = "RSA", ["use"] = "sig", ["kid"] = kid, ["n"] = n, ["e"] = e };
        return ClientKeyParse.Ok(kid, jwk.ToJsonString(), $"RSA {bits}");
    }

    private static ClientKeyParse FromEc(ECParameters p)
    {
        string? crv = CurveName(p.Curve);
        if (crv is null || p.Q.X is null || p.Q.Y is null)
        {
            return ClientKeyParse.Refused("Use an elliptic-curve key on P-256, P-384 or P-521.");
        }

        // Check the point is on the curve.
        using ECDsa ec = ECDsa.Create();
        ec.ImportParameters(p);

        string x = Base64Url(p.Q.X);
        string y = Base64Url(p.Q.Y);
        string kid = Thumbprint($"{{\"crv\":\"{crv}\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}}");
        JsonObject jwk = new() { ["kty"] = "EC", ["use"] = "sig", ["kid"] = kid, ["crv"] = crv, ["x"] = x, ["y"] = y };
        return ClientKeyParse.Ok(kid, jwk.ToJsonString(), $"EC {crv}");
    }

    private static string? CurveName(ECCurve curve)
    {
        string? oid = curve.Oid?.Value;
        string? name = curve.Oid?.FriendlyName;
        return oid switch
        {
            "1.2.840.10045.3.1.7" => "P-256",
            "1.3.132.0.34" => "P-384",
            "1.3.132.0.35" => "P-521",
            _ => name switch
            {
                "nistP256" or "ECDSA_P256" => "P-256",
                "nistP384" or "ECDSA_P384" => "P-384",
                "nistP521" or "ECDSA_P521" => "P-521",
                _ => null,
            },
        };
    }

    private static ECCurve Curve(string crv) => crv switch
    {
        "P-256" => ECCurve.NamedCurves.nistP256,
        "P-384" => ECCurve.NamedCurves.nistP384,
        "P-521" => ECCurve.NamedCurves.nistP521,
        _ => throw new ArgumentException("Unsupported curve.", nameof(crv)),
    };

    private static string Text(JsonObject key, string name)
        => key[name] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text) ? text : throw new FormatException(name);

    private static byte[] Bytes(JsonObject key, string name)
    {
        string text = Text(key, name).Replace('-', '+').Replace('_', '/');
        text = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');
        return Convert.FromBase64String(text);
    }

    private static string Thumbprint(string canonical) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The outcome of reading a public key.</summary>
/// <param name="Succeeded">Whether it is usable.</param>
/// <param name="Message">Why it was refused, when it was.</param>
/// <param name="KeyId">Its id (RFC 7638 thumbprint).</param>
/// <param name="Jwk">The public JSON Web Key to keep.</param>
/// <param name="Kind">What kind of key it is, for people.</param>
public sealed record ClientKeyParse(bool Succeeded, string? Message, string? KeyId, string? Jwk, string? Kind)
{
    /// <summary>A usable key.</summary>
    /// <param name="keyId">Its id.</param>
    /// <param name="jwk">Its JSON Web Key.</param>
    /// <param name="kind">Its kind.</param>
    public static ClientKeyParse Ok(string keyId, string jwk, string kind) => new(true, null, keyId, jwk, kind);

    /// <summary>A refused key.</summary>
    /// <param name="message">Why.</param>
    public static ClientKeyParse Refused(string message) => new(false, message, null, null, null);
}
