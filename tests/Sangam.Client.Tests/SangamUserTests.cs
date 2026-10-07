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

        SangamMembership only = Assert.Single(user.Memberships);
        Assert.Equal(Hospital, only.OrganisationId);
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
}
