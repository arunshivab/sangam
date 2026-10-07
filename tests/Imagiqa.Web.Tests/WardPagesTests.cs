using System.Net;
using Imagiqa.Web.Records;
using Sangam.Client;

namespace Imagiqa.Web.Tests;

/// <summary>What each person is shown, rendered by the real host from a Sangam-shaped sign-in.</summary>
[Collection("imagiqa-db")]
public sealed class WardPagesTests : IClassFixture<ImagiqaFactory>, IAsyncLifetime
{
    private readonly ImagiqaFactory _factory;
    private readonly Guid _apulki = Guid.NewGuid();

    public WardPagesTests(ImagiqaFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        if (ImagiqaFactory.HasDatabase)
        {
            await ImagiqaFactory.MigrateAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Anonymous_IsSentToSignIn()
    {
        using HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Found, $"got {(int)response.StatusCode}");
    }

    [PostgresFact]
    public async Task SomeoneWithNoClinicalRole_IsToldPlainly()
    {
        string html = await GetAsync(People.Person("Ravi", People.At(_apulki, "org_admin")), "/");
        Assert.Contains("You have no role at a hospital on imagiQa yet", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Register a patient", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ANurse_GetsTheVitalsForm_ADoctor_GetsTheNoteForm()
    {
        SangamUser nurse = People.Person("Nisha", People.At(_apulki, ImagiqaRoles.Nurse));
        SangamUser doctor = People.Person("Dr Arun", People.At(_apulki, ImagiqaRoles.Doctor));
        Guid id = (await new PatientRecords(ImagiqaFactory.ContextFactory(), TimeProvider.System)
            .RegisterAsync(nurse, _apulki, new PatientInput("Meena", "Patil", new DateOnly(1990, 1, 1), "female", null))).Id!.Value;

        string asNurse = await GetAsync(nurse, $"/patients/{id:D}");
        Assert.Contains("Meena Patil", asNurse, StringComparison.Ordinal);
        Assert.Contains("Record vital signs", asNurse, StringComparison.Ordinal);
        Assert.DoesNotContain("Save note", asNurse, StringComparison.Ordinal);
        Assert.Contains(">Nurse<", asNurse, StringComparison.Ordinal);

        string asDoctor = await GetAsync(doctor, $"/patients/{id:D}");
        Assert.Contains("Save note", asDoctor, StringComparison.Ordinal);
        Assert.DoesNotContain("Record vital signs", asDoctor, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task APatientFromAnotherHospital_IsNotShown()
    {
        Guid other = Guid.NewGuid();
        SangamUser there = People.Person("Dr Other", People.At(other, ImagiqaRoles.Doctor, "Other Hospital"));
        Guid id = (await new PatientRecords(ImagiqaFactory.ContextFactory(), TimeProvider.System)
            .RegisterAsync(there, other, new PatientInput("Hidden", "Person", new DateOnly(1990, 1, 1), "male", null))).Id!.Value;

        string html = await GetAsync(People.Person("Dr Arun", People.At(_apulki, ImagiqaRoles.Doctor)), $"/patients/{id:D}");
        Assert.Contains("No such patient at Apulki Medical Center", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden Person", html, StringComparison.Ordinal);
    }

    /// <summary>The PR-05 lesson: without an interactive render mode no button ever works.</summary>
    [PostgresFact]
    public async Task PagesAreInteractive_SoButtonsActuallyWork()
    {
        SangamUser nurse = People.Person("Nisha", People.At(_apulki, ImagiqaRoles.Nurse));
        foreach (string path in new[] { "/", "/patients/new" })
        {
            string html = await GetAsync(nurse, path);
            string body = html[html.IndexOf("<body", StringComparison.Ordinal)..];
            Assert.Contains("<!--Blazor:{\"type\":\"server\"", body, StringComparison.Ordinal);
        }
    }

    private async Task<string> GetAsync(SangamUser user, string path)
    {
        using HttpClient client = _factory.ClientFor(user);
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));
        string html = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {html[..Math.Min(300, html.Length)]}");
        return html;
    }
}
