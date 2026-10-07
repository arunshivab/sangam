using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sangam.Client;

namespace Imagiqa.Web.Records;

/// <summary>
/// Every rule imagiQa has, in one place. Each call names the person (as Sangam signed them in) and
/// the hospital they are working in; what they may do there comes only from the roles Sangam
/// reported for that hospital. Pages decide what to show, never what is allowed.
/// </summary>
public sealed class PatientRecords
{
    private static readonly string[] Sexes = ["female", "male", "other"];
    private readonly IDbContextFactory<ImagiqaDbContext> _db;
    private readonly TimeProvider _clock;

    /// <summary>Initialises the service.</summary>
    /// <param name="db">Database.</param>
    /// <param name="clock">Clock.</param>
    public PatientRecords(IDbContextFactory<ImagiqaDbContext> db, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Displayed form of a medical record number.</summary>
    /// <param name="mrn">The number.</param>
    /// <returns>For example <c>IQ-000042</c>.</returns>
    public static string FormatMrn(long mrn) => "IQ-" + mrn.ToString("000000", CultureInfo.InvariantCulture);

    /// <summary>Whether the person holds <paramref name="role"/> at this hospital.</summary>
    /// <param name="user">The person.</param>
    /// <param name="hospitalId">The hospital.</param>
    /// <param name="role">The role code.</param>
    /// <returns><see langword="true"/> when they do.</returns>
    public static bool Holds(SangamUser user, Guid hospitalId, string role)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(role);
        return user.PathOf(hospitalId) is { } path && user.HasRole(role, path);
    }

    /// <summary>Whether the person may work with patients at this hospital at all.</summary>
    /// <param name="user">The person.</param>
    /// <param name="hospitalId">The hospital.</param>
    /// <returns><see langword="true"/> for a doctor or a nurse there.</returns>
    public static bool IsClinician(SangamUser user, Guid hospitalId)
        => Holds(user, hospitalId, ImagiqaRoles.Doctor) || Holds(user, hospitalId, ImagiqaRoles.Nurse);

    /// <summary>Finds patients of this hospital by name, record number or mobile; newest first.</summary>
    /// <param name="user">The person.</param>
    /// <param name="hospitalId">The hospital.</param>
    /// <param name="query">What to look for; empty lists the most recent.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Up to 50 patients; none if they are not a clinician here.</returns>
    public async Task<IReadOnlyList<PatientSummary>> SearchAsync(SangamUser user, Guid hospitalId, string? query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!IsClinician(user, hospitalId))
        {
            return [];
        }

        using ImagiqaDbContext db = _db.CreateDbContext();
        IQueryable<Patient> patients = db.Patients.AsNoTracking().Where(p => p.OrganisationId == hospitalId);
        if (!string.IsNullOrWhiteSpace(query))
        {
            string term = query.Trim();
            string digits = new([.. term.Where(char.IsAsciiDigit)]);
            long? mrn = term.StartsWith("IQ", StringComparison.OrdinalIgnoreCase) && long.TryParse(digits, CultureInfo.InvariantCulture, out long n) ? n : null;
            patients = patients.Where(p =>
                EF.Functions.ILike(p.GivenName + " " + p.FamilyName, $"%{term}%")
                || (mrn != null && p.Mrn == mrn)
                || (digits.Length >= 4 && p.Mobile != null && p.Mobile.Contains(digits)));
        }

        List<Patient> rows = await patients.OrderByDescending(p => p.RegisteredAt).Take(50).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(Summary)];
    }

    /// <summary>Registers a patient at this hospital. Doctors and nurses.</summary>
    /// <param name="user">The person.</param>
    /// <param name="hospitalId">The hospital.</param>
    /// <param name="input">The patient.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome, with the new patient's id.</returns>
    public async Task<RecordResult> RegisterAsync(SangamUser user, Guid hospitalId, PatientInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(input);
        if (!IsClinician(user, hospitalId))
        {
            return RecordResult.Refused("Only a doctor or nurse of this hospital can register patients.");
        }

        string given = input.GivenName?.Trim() ?? string.Empty;
        string family = input.FamilyName?.Trim() ?? string.Empty;
        string? mobile = string.IsNullOrWhiteSpace(input.Mobile) ? null : new string([.. input.Mobile.Where(char.IsAsciiDigit)]);
        DateOnly today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        if (given.Length is 0 or > 100 || family.Length > 100)
        {
            return RecordResult.Refused("Enter the patient's given name (up to 100 characters).");
        }

        if (input.DateOfBirth > today || input.DateOfBirth < today.AddYears(-130))
        {
            return RecordResult.Refused("Enter a date of birth that is not in the future.");
        }

        if (!Sexes.Contains(input.Sex, StringComparer.Ordinal))
        {
            return RecordResult.Refused("Choose female, male or other.");
        }

        if (mobile is not null && mobile.Length is < 10 or > 15)
        {
            return RecordResult.Refused("A mobile number has 10 to 15 digits.");
        }

        Patient patient = new()
        {
            Id = Guid.NewGuid(),
            OrganisationId = hospitalId,
            GivenName = given,
            FamilyName = family,
            DateOfBirth = input.DateOfBirth,
            Sex = input.Sex,
            Mobile = mobile,
            RegisteredAt = _clock.GetUtcNow(),
            RegisteredById = user.Id,
            RegisteredByName = user.Name,
        };

        using ImagiqaDbContext db = _db.CreateDbContext();
        db.Patients.Add(patient);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new RecordResult(true, $"Registered {given} as {FormatMrn(patient.Mrn)}.", patient.Id);
    }

    /// <summary>A patient of this hospital, with vitals and notes; <see langword="null"/> from any other hospital.</summary>
    /// <param name="user">The person.</param>
    /// <param name="hospitalId">The hospital.</param>
    /// <param name="patientId">The patient.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The patient, or <see langword="null"/>.</returns>
    public async Task<PatientDetail?> GetAsync(SangamUser user, Guid hospitalId, Guid patientId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!IsClinician(user, hospitalId))
        {
            return null;
        }

        using ImagiqaDbContext db = _db.CreateDbContext();
        Patient? patient = await db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.Id == patientId && p.OrganisationId == hospitalId, cancellationToken).ConfigureAwait(false);
        if (patient is null)
        {
            return null;
        }

        List<VitalSigns> vitals = await db.Vitals.AsNoTracking().Where(v => v.PatientId == patientId).OrderByDescending(v => v.RecordedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<ConsultationNote> notes = await db.Notes.AsNoTracking().Where(n => n.PatientId == patientId).OrderByDescending(n => n.WrittenAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PatientDetail(Summary(patient), patient.RegisteredByName, vitals, notes);
    }

    /// <summary>Records vital signs. Nurses only.</summary>
    /// <param name="user">The person.</param>
    /// <param name="hospitalId">The hospital.</param>
    /// <param name="patientId">The patient.</param>
    /// <param name="input">The readings.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<RecordResult> RecordVitalsAsync(SangamUser user, Guid hospitalId, Guid patientId, VitalsInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(input);
        if (!Holds(user, hospitalId, ImagiqaRoles.Nurse))
        {
            return RecordResult.Refused("Only a nurse of this hospital can record vital signs.");
        }

        if (input is { Pulse: null, Systolic: null, Diastolic: null, TemperatureC: null, SpO2: null, RespiratoryRate: null })
        {
            return RecordResult.Refused("Enter at least one reading.");
        }

        if (OutOfRange(input) is { } problem)
        {
            return RecordResult.Refused(problem);
        }

        using ImagiqaDbContext db = _db.CreateDbContext();
        if (!await db.Patients.AnyAsync(p => p.Id == patientId && p.OrganisationId == hospitalId, cancellationToken).ConfigureAwait(false))
        {
            return RecordResult.Refused("That patient is not registered at this hospital.");
        }

        db.Vitals.Add(new VitalSigns
        {
            Id = Guid.NewGuid(),
            PatientId = patientId,
            RecordedAt = _clock.GetUtcNow(),
            RecordedById = user.Id,
            RecordedByName = user.Name,
            Pulse = input.Pulse,
            Systolic = input.Systolic,
            Diastolic = input.Diastolic,
            TemperatureC = input.TemperatureC,
            SpO2 = input.SpO2,
            RespiratoryRate = input.RespiratoryRate,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new RecordResult(true, "Vital signs recorded.");
    }

    /// <summary>Adds a consultation note. Doctors only.</summary>
    /// <param name="user">The person.</param>
    /// <param name="hospitalId">The hospital.</param>
    /// <param name="patientId">The patient.</param>
    /// <param name="text">The note.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    public async Task<RecordResult> AddNoteAsync(SangamUser user, Guid hospitalId, Guid patientId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(text);
        if (!Holds(user, hospitalId, ImagiqaRoles.Doctor))
        {
            return RecordResult.Refused("Only a doctor of this hospital can write consultation notes.");
        }

        string note = text.Trim();
        if (note.Length is 0 or > 4000)
        {
            return RecordResult.Refused("Write the note (up to 4,000 characters).");
        }

        using ImagiqaDbContext db = _db.CreateDbContext();
        if (!await db.Patients.AnyAsync(p => p.Id == patientId && p.OrganisationId == hospitalId, cancellationToken).ConfigureAwait(false))
        {
            return RecordResult.Refused("That patient is not registered at this hospital.");
        }

        db.Notes.Add(new ConsultationNote { Id = Guid.NewGuid(), PatientId = patientId, WrittenAt = _clock.GetUtcNow(), WrittenById = user.Id, WrittenByName = user.Name, Text = note });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new RecordResult(true, "Note saved.");
    }

    private static string? OutOfRange(VitalsInput v)
        => v.Pulse is < 20 or > 250 ? "Pulse must be between 20 and 250."
        : v.Systolic is < 50 or > 260 ? "Systolic pressure must be between 50 and 260."
        : v.Diastolic is < 30 or > 160 ? "Diastolic pressure must be between 30 and 160."
        : v.Systolic is { } s && v.Diastolic is { } d && d >= s ? "Diastolic pressure must be lower than systolic."
        : v.TemperatureC is < 30m or > 45m ? "Temperature must be between 30 and 45 °C."
        : v.SpO2 is < 50 or > 100 ? "Oxygen saturation must be between 50 and 100%."
        : v.RespiratoryRate is < 4 or > 60 ? "Breathing rate must be between 4 and 60."
        : null;

    private static PatientSummary Summary(Patient p)
        => new(p.Id, FormatMrn(p.Mrn), $"{p.GivenName} {p.FamilyName}".Trim(), p.DateOfBirth, p.Sex, p.Mobile, p.RegisteredAt);
}
