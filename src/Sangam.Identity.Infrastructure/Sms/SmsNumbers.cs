using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sangam.Identity.Infrastructure.Sms;

/// <summary>Masking and keyed hashing of phone numbers and IP addresses.</summary>
public static class SmsNumbers
{
    /// <summary>Masks a number for logs and screens: the dialling code and the last <paramref name="visible"/> digits.</summary>
    /// <param name="e164">The number in E.164 form.</param>
    /// <param name="visible">How many trailing digits stay visible.</param>
    public static string Mask(string? e164, int visible = 2)
    {
        if (string.IsNullOrEmpty(e164) || e164.Length <= visible + 3)
        {
            return "•••";
        }

        string code = e164.StartsWith("+91", StringComparison.Ordinal) ? "+91" : e164[..3];
        return string.Create(CultureInfo.InvariantCulture, $"{code} ••••••{e164[^visible..]}");
    }

    /// <summary>
    /// HMAC-SHA256 of <paramref name="value"/>, lowercase hex. A plain hash of a ten-digit number could be
    /// reversed by trying every number, so the hash is keyed.
    /// </summary>
    /// <param name="key">The hash key.</param>
    /// <param name="value">The number or address.</param>
    public static string Hash(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value)));
    }
}
