using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Tenancy;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;

namespace Sangam.Identity.Server.Tests;

/// <summary>
/// PR-16 over HTTP: an organisation's policy changes how its people sign in to the application — the code step,
/// a new password, a passkey, a second factor — while everyone else is untouched.
/// </summary>
[Collection("server")]
public sealed partial class PolicyScreenTests
{
    private const string Password = "Correct-Horse-2026!";
    private readonly SangamServerFactory _factory;

    public PolicyScreenTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task AnOrganisationRequiringTwoStep_AddsTheCodeStep_ForItsPeopleOnly()
    {
        using BrowserSession s = new(_factory);
        (Guid userId, string email) = await RegisterAsync(s);
        await PlaceInOrganisationAsync(userId, org => org.SignInPolicy = SignInPolicy.PasswordAndOtp);
        await s.PostFormAsync("/logout", []);

        (_, string? loc, _) = await s.PostFormAsync(LoginPath(), new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        Assert.Equal("/login/verify", Path(loc));

        // The same person signing in to Sangam itself (no application) is not asked for a code.
        using BrowserSession direct = new(_factory);
        (_, string? directLoc, _) = await direct.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        Assert.Equal("/account", directLoc);
    }

    [PostgresFact]
    public async Task APasswordShorterThanThePolicy_MustBeReplaced_BeforeTheSignInGoesOn()
    {
        using BrowserSession s = new(_factory);
        (Guid userId, string email) = await RegisterAsync(s);
        await PlaceInOrganisationAsync(userId, org => org.MinPasswordLength = 24);
        await s.PostFormAsync("/logout", []);

        (_, string? loc, _) = await s.PostFormAsync(LoginPath(), new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        Assert.Equal("/login/new-password", Path(loc));
        Assert.Contains("reason=too_short", loc, StringComparison.Ordinal);

        (_, string page) = await s.GetAsync(loc!);
        Assert.Contains("at least 24 characters", page, StringComparison.Ordinal);

        (HttpStatusCode shortSt, _, string shortHtml) = await s.PostFormAsync(loc!, new Dictionary<string, string> { ["NewPassword"] = "Still-Too-Short-26!", ["ConfirmPassword"] = "Still-Too-Short-26!" });
        Assert.Equal(HttpStatusCode.OK, shortSt);
        Assert.Contains("at least 24 characters", WebUtility.HtmlDecode(shortHtml), StringComparison.Ordinal);

        const string Longer = "Kaveri-River-Flows-East-2026!";
        (_, string? done, _) = await s.PostFormAsync(loc!, new Dictionary<string, string> { ["NewPassword"] = Longer, ["ConfirmPassword"] = Longer });
        Assert.StartsWith("/connect/authorize", done, StringComparison.Ordinal);

        using BrowserSession again = new(_factory);
        (_, string? oldLoc, _) = await again.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        Assert.Null(oldLoc);
        (_, string? newLoc, _) = await again.PostFormAsync("/login", new Dictionary<string, string> { ["Email"] = email, ["Password"] = Longer });
        Assert.Equal("/account", newLoc);
    }

    [PostgresFact]
    public async Task TheApplicationsOwnMinimum_IsEnforced_OnAFirstSignIn_BeforeThePersonIsLinkedToIt()
    {
        using BrowserSession s = new(_factory);
        (_, string email) = await RegisterAsync(s);
        await s.PostFormAsync("/logout", []);
        string clientId = "policy-" + Guid.NewGuid().ToString("N")[..8];
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            db.Apps.Add(new App { Id = Guid.NewGuid(), ClientId = clientId, Slug = clientId, DisplayName = "Long Passwords HIS", OwnerCompanyName = "imagiQa", MinPasswordLength = 24, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        string returnUrl = "/connect/authorize?client_id=" + clientId + "&response_type=code";
        (_, string? loc, _) = await s.PostFormAsync("/login?returnUrl=" + Uri.EscapeDataString(returnUrl), new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        Assert.Equal("/login/new-password", Path(loc));
        (_, string page) = await s.GetAsync(loc!);
        Assert.Contains("at least 24 characters", page, StringComparison.Ordinal);

        (HttpStatusCode st, _, string html) = await s.PostFormAsync(loc!, new Dictionary<string, string> { ["NewPassword"] = "Kaveri-River-26!", ["ConfirmPassword"] = "Kaveri-River-26!" });
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Contains("Use at least 24 characters", html, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task APasskeyOnlyOrganisation_RefusesThePassword_AndSendsAPasswordSessionBackToSignIn()
    {
        using BrowserSession s = new(_factory);
        (Guid userId, string email) = await RegisterAsync(s);
        await PlaceInOrganisationAsync(userId, org => org.SignInPolicy = SignInPolicy.PasskeyOnly);

        (_, string afterAuthorize, _) = await s.FollowAsync(Authorize());
        Assert.Equal("/login", Path(afterAuthorize));

        (HttpStatusCode st, string? loc, string html) = await s.PostFormAsync(LoginPath(), new Dictionary<string, string> { ["Email"] = email, ["Password"] = Password });
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Null(loc);
        Assert.Contains("requires you to sign in with a passkey", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ARequiredSecondFactor_StopsAPasswordOnlySession_AndSaysWhatToSetUp()
    {
        using BrowserSession s = new(_factory);
        (Guid userId, _) = await RegisterAsync(s);
        await PlaceInOrganisationAsync(userId, org => org.MfaRequirement = MfaRequirement.Required);

        (_, string location, string html) = await s.FollowAsync(Authorize());
        Assert.Equal("/login/two-step-required", Path(location));
        Assert.Contains("Two-step sign-in required", html, StringComparison.Ordinal);
        Assert.Contains("/profile", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/account/passkeys\"", html, StringComparison.Ordinal);

        // prompt=none is answered with an error, never a page.
        (_, string silent, _) = await s.FollowAsync(Authorize() + "&prompt=none");
        Assert.StartsWith(DevelopmentSeeder.SampleRedirectUri, silent, StringComparison.Ordinal);
        Assert.Contains("error=login_required", silent, StringComparison.Ordinal);
    }

    private static string Authorize()
    {
        string verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "/connect/authorize?client_id=" + DevelopmentSeeder.SampleClientId
            + "&redirect_uri=" + Uri.EscapeDataString(DevelopmentSeeder.SampleRedirectUri)
            + "&response_type=code&scope=" + Uri.EscapeDataString("openid profile email")
            + "&state=xyz&code_challenge=" + challenge + "&code_challenge_method=S256";
    }

    private static string LoginPath() => "/login?returnUrl=" + Uri.EscapeDataString(Authorize());

    private static string? Path(string? location) => location?.Split('?')[0];

    private async Task PlaceInOrganisationAsync(Guid userId, Action<Organisation> policy)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid appId = await db.Apps.Where(a => a.ClientId == DevelopmentSeeder.SampleClientId).Select(a => a.Id).SingleAsync();
        IManagementService management = scope.ServiceProvider.GetRequiredService<IManagementService>();
        ManagementResult<RoleDto> role = await management.UpsertRoleAsync(appId, "policy_staff", new RoleUpsert("Staff", null, ["sample:read"], null), ManagementActor.Api);
        Assert.True(role.Status == ManagementStatus.Ok, role.Message);
        Guid orgId = Guid.NewGuid();
        ManagementResult<OrganisationDto> created = await management.UpsertOrganisationAsync(appId, orgId, new OrganisationUpsert("Policy test " + orgId.ToString("N")[..6], "hospital", null, null), ManagementActor.Api);
        Assert.True(created.Status == ManagementStatus.Ok, created.Message);
        // rc.5: the person joins as their own act (consent first); through the API alone only users of the app can be added.
        ManagementResult<MembershipDto> member = await management.UpsertMembershipAsync(appId, orgId, userId, new MembershipUpsert("policy_staff", false), ManagementActor.Person(userId));
        Assert.True(member.Status == ManagementStatus.Ok, member.Message);

        Organisation org = await db.Organisations.SingleAsync(o => o.Id == orgId);
        policy(org);
        await db.SaveChangesAsync();
    }

    private async Task<(Guid UserId, string Email)> RegisterAsync(BrowserSession s)
    {
        string email = $"policy-{Guid.NewGuid():N}@example.in";
        string national = Random.Shared.NextInt64(7000000000, 9999999999).ToString(CultureInfo.InvariantCulture);
        (HttpStatusCode st, string? loc, string html) = await s.PostFormAsync("/register", new Dictionary<string, string>
        {
            ["FirstName"] = "Lakshmi",
            ["LastName"] = "Rao",
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = national,
            ["BirthDay"] = "3",
            ["BirthMonth"] = "4",
            ["BirthYear"] = "1987",
            ["Gender"] = "female",
            ["Password"] = Password,
            ["AcceptTerms"] = "true",
        });
        Assert.True(st == HttpStatusCode.Found && loc == "/verify", html);
        string code = CodeRegex().Match(_factory.Services.GetRequiredService<InMemoryEmailOutbox>().LatestFor(email)!.Message.TextBody).Value;
        (_, string? okLoc, _) = await s.PostFormAsync("/verify", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal("/verified", okLoc);

        using IServiceScope scope = _factory.Services.CreateScope();
        Guid id = await scope.ServiceProvider.GetRequiredService<SangamDbContext>().Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return (id, email);
    }

    [GeneratedRegex("[0-9]{6}")]
    private static partial Regex CodeRegex();
}
