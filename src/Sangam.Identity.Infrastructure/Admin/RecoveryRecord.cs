using System.Text.Json;
using System.Text.Json.Serialization;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Admin;

/// <summary>
/// rc.6 (SGM-914): what DigiLocker said for a recovery, kept on the request only until it is applied, refused or
/// cancelled — the name, date of birth and gender on the record, the keyed hash of the DigiLocker id (never the id), and
/// which parts did not match the account.
/// </summary>
/// <param name="Method">The method (<c>digilocker</c>).</param>
/// <param name="SubjectHash">The keyed hash of the DigiLocker id.</param>
/// <param name="Name">The record's name.</param>
/// <param name="DateOfBirth">The record's date of birth.</param>
/// <param name="Gender">The record's gender.</param>
/// <param name="Mismatches">The parts that did not match.</param>
internal sealed record RecoveryRecord(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("subject_hash")] string SubjectHash,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dob")] DateOnly DateOfBirth,
    [property: JsonPropertyName("gender")] Gender Gender,
    [property: JsonPropertyName("mismatches")] IReadOnlyList<string> Mismatches)
{
    /// <summary>As JSON for the request's record column.</summary>
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Reads a request's record, or <see langword="null"/>.</summary>
    /// <param name="json">The column.</param>
    public static RecoveryRecord? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RecoveryRecord>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
