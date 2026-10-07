using Imagiqa.Web.Records;
using Sangam.Client;

namespace Imagiqa.Web.Tests;

/// <summary>imagiQa's rules: who may do what, decided only from the roles Sangam reported.</summary>
[Collection("imagiqa-db")]
public sealed class PatientRecordsTests : IAsyncLifetime
{
    private readonly Guid _apulki = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private PatientRecords _records = null!;

    public async Task InitializeAsync()
    {
        if (ImagiqaFactory.HasDatabase)
        {
            await ImagiqaFactory.MigrateAsync();
            _records = new PatientRecords(ImagiqaFactory.ContextFactory(), TimeProvider.System);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task DoctorsAndNurses_Register_ButSomeoneWithoutARoleCannot()
    {
        SangamUser nurse = People.Person("Nisha", People.At(_apulki, ImagiqaRoles.Nurse));
        SangamUser clerk = People.Person("Ravi", People.At(_apulki, "org_admin"));

        RecordResult ok = await _records.RegisterAsync(nurse, _apulki, Patient("Meena"));
        Assert.True(ok.Succeeded, ok.Message);
        Assert.StartsWith("Registered Meena as IQ-", ok.Message, StringComparison.Ordinal);

        Assert.False((await _records.RegisterAsync(clerk, _apulki, Patient("Kiran"))).Succeeded);
        Assert.Empty(await _records.SearchAsync(clerk, _apulki, null));
    }

    [PostgresFact]
    public async Task APatient_IsSeenOnlyAtTheHospitalThatRegisteredThem()
    {
        SangamUser both = People.Person("Dr Arun", People.At(_apulki, ImagiqaRoles.Doctor), People.At(_other, ImagiqaRoles.Doctor, "Other Hospital"));
        Guid id = (await _records.RegisterAsync(both, _apulki, Patient("Lakshmi"))).Id!.Value;

        Assert.NotNull(await _records.GetAsync(both, _apulki, id));
        Assert.Null(await _records.GetAsync(both, _other, id));
        Assert.Empty(await _records.SearchAsync(both, _other, "Lakshmi"));
        Assert.False((await _records.AddNoteAsync(both, _other, id, "Seen elsewhere")).Succeeded);
    }

    [PostgresFact]
    public async Task ARoleAtOneHospital_GivesNothingAtAnother()
    {
        SangamUser doctor = People.Person("Dr Arun", People.At(_apulki, ImagiqaRoles.Doctor));
        Assert.False((await _records.RegisterAsync(doctor, _other, Patient("Zara"))).Succeeded);
    }

    [PostgresFact]
    public async Task OnlyNurses_RecordVitals_AndOnlyDoctors_WriteNotes()
    {
        SangamUser nurse = People.Person("Nisha", People.At(_apulki, ImagiqaRoles.Nurse));
        SangamUser doctor = People.Person("Dr Arun", People.At(_apulki, ImagiqaRoles.Doctor));
        Guid id = (await _records.RegisterAsync(nurse, _apulki, Patient("Farhan"))).Id!.Value;
        VitalsInput vitals = new(88, 124, 82, 37.1m, 98, 16);

        Assert.False((await _records.RecordVitalsAsync(doctor, _apulki, id, vitals)).Succeeded);
        Assert.True((await _records.RecordVitalsAsync(nurse, _apulki, id, vitals)).Succeeded);
        Assert.False((await _records.AddNoteAsync(nurse, _apulki, id, "Looks well")).Succeeded);
        Assert.True((await _records.AddNoteAsync(doctor, _apulki, id, "Fever 3 days. Review in 48 h.")).Succeeded);

        PatientDetail detail = (await _records.GetAsync(nurse, _apulki, id))!;
        VitalSigns taken = Assert.Single(detail.Vitals);
        Assert.Equal("Nisha", taken.RecordedByName);
        Assert.Equal("Dr Arun", Assert.Single(detail.Notes).WrittenByName);
    }

    [PostgresFact]
    public async Task ImplausibleInput_IsRefusedWithAReason()
    {
        SangamUser nurse = People.Person("Nisha", People.At(_apulki, ImagiqaRoles.Nurse));
        Assert.False((await _records.RegisterAsync(nurse, _apulki, Patient("Future") with { DateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2) })).Succeeded);
        Assert.False((await _records.RegisterAsync(nurse, _apulki, Patient(" "))).Succeeded);
        Assert.False((await _records.RegisterAsync(nurse, _apulki, Patient("Sex") with { Sex = "unknown" })).Succeeded);
        Assert.False((await _records.RegisterAsync(nurse, _apulki, Patient("Short") with { Mobile = "12345" })).Succeeded);

        Guid id = (await _records.RegisterAsync(nurse, _apulki, Patient("Anil"))).Id!.Value;
        Assert.Equal("Enter at least one reading.", (await _records.RecordVitalsAsync(nurse, _apulki, id, new VitalsInput(null, null, null, null, null, null))).Message);
        Assert.False((await _records.RecordVitalsAsync(nurse, _apulki, id, new VitalsInput(300, null, null, null, null, null))).Succeeded);
        Assert.False((await _records.RecordVitalsAsync(nurse, _apulki, id, new VitalsInput(null, 90, 95, null, null, null))).Succeeded);
    }

    [PostgresFact]
    public async Task Search_FindsByName_IqNumber_AndMobile()
    {
        SangamUser nurse = People.Person("Nisha", People.At(_apulki, ImagiqaRoles.Nurse));
        RecordResult registered = await _records.RegisterAsync(nurse, _apulki, Patient("Savitri") with { FamilyName = "Deshpande", Mobile = "98765 43210" });
        string mrn = registered.Message.Split(' ')[^1].TrimEnd('.');

        Assert.Single(await _records.SearchAsync(nurse, _apulki, "savitri desh"));
        Assert.Single(await _records.SearchAsync(nurse, _apulki, mrn));
        Assert.Single(await _records.SearchAsync(nurse, _apulki, "43210"));
        Assert.Equal("9876543210", (await _records.SearchAsync(nurse, _apulki, "Savitri"))[0].Mobile);
    }

    private static PatientInput Patient(string given) => new(given, "Patil", new DateOnly(1986, 6, 12), "female", null);
}
