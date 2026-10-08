namespace Sangam.Shared.Constants;

/// <summary>
/// The assurance levels an application may ask for with <c>acr_values</c> and find in a token's <c>acr</c>
/// (PR-17, SGM-207 §3). Shared by the identity server and the client library so they never drift apart.
/// </summary>
public static class SangamAcr
{
    /// <summary>Single factor: a password, or an e-mailed or texted code.</summary>
    public const string SingleFactor = "urn:sangam:acr:1";

    /// <summary>Two factors: a password with a code, an authenticator or an SMS.</summary>
    public const string TwoFactor = "urn:sangam:acr:2";

    /// <summary>Phishing-resistant: a passkey with user verification.</summary>
    public const string PhishingResistant = "urn:sangam:acr:3";

    /// <summary>Signature-grade: two factors or a passkey within the last five minutes.</summary>
    public const string Signature = "urn:sangam:acr:sign";
}
