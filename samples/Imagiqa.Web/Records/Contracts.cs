namespace Imagiqa.Web.Records;

/// <summary>imagiQa's role codes, as defined on Sangam's partner console.</summary>
public static class ImagiqaRoles
{
    /// <summary>Doctor: registers and finds patients, writes consultation notes.</summary>
    public const string Doctor = "doctor";

    /// <summary>Nurse: registers and finds patients, records vital signs.</summary>
    public const string Nurse = "nurse";
}

/// <summary>A patient in a list.</summary>
/// <param name="Id">Record id.</param>
/// <param name="Mrn">Displayed medical record number.</param>
/// <param name="Name">Full name.</param>
/// <param name="DateOfBirth">Date of birth.</param>
/// <param name="Sex">Sex.</param>
/// <param name="Mobile">Mobile number.</param>
/// <param name="RegisteredAt">When registered.</param>
public sealed record PatientSummary(Guid Id, string Mrn, string Name, DateOnly DateOfBirth, string Sex, string? Mobile, DateTimeOffset RegisteredAt);

/// <summary>A patient with their vitals and notes, newest first.</summary>
/// <param name="Patient">The patient.</param>
/// <param name="RegisteredByName">Who registered them.</param>
/// <param name="Vitals">Vital signs.</param>
/// <param name="Notes">Consultation notes.</param>
public sealed record PatientDetail(PatientSummary Patient, string RegisteredByName, IReadOnlyList<VitalSigns> Vitals, IReadOnlyList<ConsultationNote> Notes);

/// <summary>What registering a patient needs.</summary>
/// <param name="GivenName">Given name.</param>
/// <param name="FamilyName">Family name; may be empty.</param>
/// <param name="DateOfBirth">Date of birth.</param>
/// <param name="Sex"><c>female</c>, <c>male</c> or <c>other</c>.</param>
/// <param name="Mobile">Optional mobile number.</param>
public sealed record PatientInput(string GivenName, string? FamilyName, DateOnly DateOfBirth, string Sex, string? Mobile);

/// <summary>One set of vital signs; any value may be left out, but not all of them.</summary>
/// <param name="Pulse">Beats per minute.</param>
/// <param name="Systolic">Systolic, mmHg.</param>
/// <param name="Diastolic">Diastolic, mmHg.</param>
/// <param name="TemperatureC">Temperature, °C.</param>
/// <param name="SpO2">Oxygen saturation, %.</param>
/// <param name="RespiratoryRate">Breaths per minute.</param>
public sealed record VitalsInput(int? Pulse, int? Systolic, int? Diastolic, decimal? TemperatureC, int? SpO2, int? RespiratoryRate);

/// <summary>The outcome of a change.</summary>
/// <param name="Succeeded">Whether it was saved.</param>
/// <param name="Message">What to tell the person.</param>
/// <param name="Id">The new record's id, when one was created.</param>
public sealed record RecordResult(bool Succeeded, string Message, Guid? Id = null)
{
    /// <summary>A refusal.</summary>
    /// <param name="message">Why.</param>
    /// <returns>The result.</returns>
    public static RecordResult Refused(string message) => new(false, message);
}
