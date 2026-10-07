namespace Imagiqa.Web.Records;

/// <summary>A registered patient. Belongs to one hospital (a Sangam organisation) and is seen only there.</summary>
public sealed class Patient
{
    /// <summary>Record id.</summary>
    public Guid Id { get; set; }

    /// <summary>The Sangam organisation (hospital) that registered them.</summary>
    public Guid OrganisationId { get; set; }

    /// <summary>Medical record number, from a database sequence.</summary>
    public long Mrn { get; set; }

    /// <summary>Given name.</summary>
    public string GivenName { get; set; } = string.Empty;

    /// <summary>Family name; may be empty, as for many people in India.</summary>
    public string FamilyName { get; set; } = string.Empty;

    /// <summary>Date of birth.</summary>
    public DateOnly DateOfBirth { get; set; }

    /// <summary><c>female</c>, <c>male</c> or <c>other</c>.</summary>
    public string Sex { get; set; } = string.Empty;

    /// <summary>Mobile number, digits only.</summary>
    public string? Mobile { get; set; }

    /// <summary>When they were registered (UTC).</summary>
    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>The Sangam user id of whoever registered them.</summary>
    public Guid RegisteredById { get; set; }

    /// <summary>That person's name at the time.</summary>
    public string RegisteredByName { get; set; } = string.Empty;
}

/// <summary>One set of vital signs, recorded by a nurse. Any value may be left out.</summary>
public sealed class VitalSigns
{
    /// <summary>Record id.</summary>
    public Guid Id { get; set; }

    /// <summary>The patient.</summary>
    public Guid PatientId { get; set; }

    /// <summary>When they were taken (UTC).</summary>
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>The Sangam user id of the nurse.</summary>
    public Guid RecordedById { get; set; }

    /// <summary>The nurse's name at the time.</summary>
    public string RecordedByName { get; set; } = string.Empty;

    /// <summary>Pulse, beats per minute.</summary>
    public int? Pulse { get; set; }

    /// <summary>Systolic blood pressure, mmHg.</summary>
    public int? Systolic { get; set; }

    /// <summary>Diastolic blood pressure, mmHg.</summary>
    public int? Diastolic { get; set; }

    /// <summary>Temperature, °C.</summary>
    public decimal? TemperatureC { get; set; }

    /// <summary>Oxygen saturation, %.</summary>
    public int? SpO2 { get; set; }

    /// <summary>Breaths per minute.</summary>
    public int? RespiratoryRate { get; set; }
}

/// <summary>A consultation note, written by a doctor.</summary>
public sealed class ConsultationNote
{
    /// <summary>Record id.</summary>
    public Guid Id { get; set; }

    /// <summary>The patient.</summary>
    public Guid PatientId { get; set; }

    /// <summary>When it was written (UTC).</summary>
    public DateTimeOffset WrittenAt { get; set; }

    /// <summary>The Sangam user id of the doctor.</summary>
    public Guid WrittenById { get; set; }

    /// <summary>The doctor's name at the time.</summary>
    public string WrittenByName { get; set; } = string.Empty;

    /// <summary>The note.</summary>
    public string Text { get; set; } = string.Empty;
}
