using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Shared.Constants;

namespace Sangam.Client.Tests;

/// <summary>What a partner application reads from a Sangam sign-in, and the organisation rule.</summary>
public sealed class SangamUserTests
{
    private static readonly Guid Hospital = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Ward = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Sibling = Guid.Parse("11111111-1111-1111-1111-111111111112");
    private static readonly string HospitalPath = $"/{Hospital:D}/";
    private static readonly string WardPath = $"/{Hospital:D}/{Ward:D}/";
    private static readonly string SiblingPath = $"/{Sibling:D}/";

    [Fact]
    public void OneClaimPerOrganisation_IsRead()
    {
        ClaimsPrincipal principal = Principal(
            Org(Hospital, HospitalPath, "doctor", ["patients:read"], inherits: true),
            Org(Ward, WardPath, "nurse", ["vitals:write"], inherits: false));

        SangamUser user = principal.GetSangamUser()!;
        Assert.Equal("Asha Rao", user.Name);
        Assert.Equal("asha@example.in", user.Email);
        Assert.Equal(2, user.Memberships.Count);
        Assert.Equal(["patients:read"], user.Memberships[0].Permissions);
    }

    [Fact]
    public void OneClaimHoldingTheWholeArray_IsReadTheSameWay()
    {
        string array = "[" + Org(Hospital, HospitalPath, "doctor", [], inherits: true) + "," + Org(Ward, WardPath, "nurse", [], inherits: false) + "]";
        SangamUser user = Principal(array).GetSangamUser()!;
        Assert.Equal(["doctor", "nurse"], user.Memberships.Select(m => m.Role));
    }

    [Fact]
    public void MalformedEntries_AreSkipped_NotFatal()
    {
        SangamUser user = Principal(
            "not json",
            "{\"id\":\"not-a-guid\",\"role\":\"doctor\",\"path\":\"/x/\"}",
            "{\"id\":\"" + Ward.ToString("D") + "\",\"path\":\"/x/\"}",
            Org(Hospital, HospitalPath, "doctor", [], inherits: false)).GetSangamUser()!;

        // rc.12: an entry with no role is a place without a role in Sangam, not a malformed one.
        Assert.Equal([Ward, Hospital], user.Memberships.Select(m => m.OrganisationId));
        Assert.Equal(string.Empty, user.Memberships[0].Role);
        Assert.Empty(user.RolesIn("/x/"));
        Assert.False(user.HasRole(string.Empty, "/x/"));
    }

    [Fact]
    public void SeveralRolesAtOneOrganisation_AreEachAMembership()
    {
        // rc.12: one entry carries every role held there; "role" (the first) is kept for older readers.
        string entry = "{\"id\":\"" + Hospital.ToString("D") + "\",\"name\":\"Apulki\",\"type\":\"hospital\",\"path\":\"" + HospitalPath
            + "\",\"role\":\"doctor\",\"roles\":[\"doctor\",\"administrator\",\"doctor\"],\"permissions\":[\"staff:manage\"],\"inherits\":false}";
        SangamUser user = Principal(entry).GetSangamUser()!;
        Assert.Equal(["doctor", "administrator"], user.Memberships.Select(m => m.Role));
        Assert.True(user.HasRole("administrator", HospitalPath));
        Assert.True(user.HasPermission("staff:manage", HospitalPath));
        Assert.Single(user.Organisations);
    }

    [Fact]
    public void AnInheritedRole_HoldsBelow_ButNotInASibling()
    {
        SangamUser user = Principal(Org(Hospital, HospitalPath, "doctor", ["notes:write"], inherits: true)).GetSangamUser()!;

        Assert.True(user.HasRole("doctor", HospitalPath));
        Assert.True(user.HasRole("doctor", WardPath));
        Assert.True(user.HasPermission("notes:write", WardPath));
        Assert.False(user.HasRole("doctor", SiblingPath));
        Assert.False(user.HasRole("nurse", HospitalPath));
    }

    [Fact]
    public void ARoleThatDoesNotInherit_HoldsOnlyWhereItWasGiven()
    {
        SangamUser user = Principal(Org(Hospital, HospitalPath, "nurse", [], inherits: false)).GetSangamUser()!;
        Assert.True(user.HasRole("nurse", HospitalPath));
        Assert.False(user.HasRole("nurse", WardPath));
        Assert.Empty(user.RolesIn(WardPath));
    }

    [Fact]
    public void Organisations_ListsEachOnce_AndPathOfFindsIt()
    {
        SangamUser user = Principal(
            Org(Hospital, HospitalPath, "doctor", [], inherits: false),
            Org(Hospital, HospitalPath, "nurse", [], inherits: false)).GetSangamUser()!;

        Assert.Single(user.Organisations);
        Assert.Equal(["doctor", "nurse"], user.RolesIn(HospitalPath));
        Assert.Equal(HospitalPath, user.PathOf(Hospital));
        Assert.Null(user.PathOf(Ward));
    }

    [Fact]
    public void NobodySignedIn_GivesNoUser()
    {
        Assert.Null(new ClaimsPrincipal(new ClaimsIdentity()).GetSangamUser());
        Assert.Null(((ClaimsPrincipal?)null).GetSangamUser());
    }

    [Fact]
    public void Options_RefuseWhatCannotWork()
    {
        Assert.Throws<InvalidOperationException>(() => new SangamOptions { ClientId = "x" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new SangamOptions { Authority = "http://id.example.in", ClientId = "x" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new SangamOptions { Authority = "https://id.example.in" }.Validate());
        new SangamOptions { Authority = "http://localhost:5100", ClientId = "x", RequireHttpsMetadata = false }.Validate();
    }

    /// <summary>Found in the browser run: without the saved ID token, sign-out cannot return to the application.</summary>
    [Fact]
    public void TokensAreSavedByDefault_SoSignOutCanSendTheHint()
    {
        Assert.True(new SangamOptions().SaveTokens);
    }

    [Fact]
    public async Task AddSangam_RegistersTheCookieAndSangamSchemes_WithSangamAsTheChallenge()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSangam(o =>
        {
            o.Authority = "https://id.example.in";
            o.ClientId = "imagiqa";
        });

        await using ServiceProvider provider = services.BuildServiceProvider();
        IAuthenticationSchemeProvider schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.Equal(SangamDefaults.Scheme, (await schemes.GetDefaultChallengeSchemeAsync())!.Name);
        Assert.Equal(SangamDefaults.CookieScheme, (await schemes.GetDefaultAuthenticateSchemeAsync())!.Name);
    }

    private static ClaimsPrincipal Principal(params string[] orgClaims)
    {
        List<Claim> claims =
        [
            new(SangamUser.SubjectClaim, Guid.NewGuid().ToString("D")),
            new(SangamUser.NameClaim, "Asha Rao"),
            new(SangamUser.EmailClaim, "asha@example.in"),
        ];
        claims.AddRange(orgClaims.Select(c => new Claim(SangamClaims.Orgs, c)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static string Org(Guid id, string path, string role, string[] permissions, bool inherits)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [SangamOrgClaim.Id] = id.ToString("D"),
            [SangamOrgClaim.Name] = "Apulki",
            [SangamOrgClaim.Type] = "hospital",
            [SangamOrgClaim.Path] = path,
            [SangamOrgClaim.Role] = role,
            [SangamOrgClaim.Permissions] = permissions,
            [SangamOrgClaim.Inherits] = inherits,
        });

    [Fact]
    public void TheOrganisationChosenForThisSignIn_IsRead_WithEveryRoleThere()
    {
        // rc.12 (A6): sangam_org carries the one organisation the person is working at.
        SangamCurrentOrganisation? current = SangamClaimsPrincipalExtensions.ParseCurrentOrganisation(
            "{\"id\":\"" + Hospital.ToString("D") + "\",\"name\":\"Apulki\",\"type\":\"hospital\",\"path\":\"" + HospitalPath + "\",\"roles\":[\"doctor\",\"administrator\"],\"permissions\":[\"staff:manage\"]}");
        Assert.NotNull(current);
        Assert.Equal(Hospital, current.OrganisationId);
        Assert.Equal(["doctor", "administrator"], current.Roles);
        Assert.Null(SangamClaimsPrincipalExtensions.ParseCurrentOrganisation("not json"));
        Assert.Null(SangamClaimsPrincipalExtensions.ParseCurrentOrganisation(null));
    }

    [Fact]
    public void EmergencyAccess_IsRead_UntilItsEnd()
    {
        // rc.12 (M12): sangam_emergency carries the use, the application's case reference, the organisation and the end.
        SangamEmergency? e = SangamClaimsPrincipalExtensions.ParseEmergency(
            "{\"id\":\"0192a6b0-0000-7000-8000-0000000000e1\",\"case\":\"LIPI-48213\",\"org\":\"" + Hospital.ToString("D") + "\",\"until\":1792000000}");
        Assert.NotNull(e);
        Assert.Equal("LIPI-48213", e.CaseReference);
        Assert.Equal(Hospital, e.OrganisationId);
        Assert.True(e.IsActive(DateTimeOffset.FromUnixTimeSeconds(1791999999)));
        Assert.False(e.IsActive(DateTimeOffset.FromUnixTimeSeconds(1792000000)));
        Assert.Null(SangamClaimsPrincipalExtensions.ParseEmergency("{\"case\":\"x\"}"));
        Assert.Null(SangamClaimsPrincipalExtensions.ParseEmergency("not json"));
    }
}
