using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tenancy;

namespace Sangam.Identity.Server.Tests.Oidc;

/// <summary>
/// rc.12: an application adds staff for one of its admins, who signed in to it with Sangam; the person joins on the e-mailed
/// link with the admin's code, creating their account on the way.
/// </summary>
[Collection("server")]
public sealed partial class StaffApiTests
{
    private static readonly string[] WardRead = ["ward.read"];
    private static readonly string[] WardNurse = ["ward_nurse"];
    private readonly SangamServerFactory _factory;

    public StaffApiTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task AnApplication_AddsStaff_ForItsSignedInAdmin_AndThePersonJoinsWithTheCode()
    {
        // The admin signs in to the application with Sangam, which gives the application their access token.
        using BrowserSession admin = new(_factory);
        string adminEmail = await RegisterAndVerifyAsync(admin, "Anita", "Rao", $"staff-admin-{Guid.NewGuid():N}@example.in");
        (string verifier, string challenge) = Pkce();
        (_, string consentPath, _) = await admin.FollowAsync(Authorize(challenge));
        string adminToken = (await ExchangeAsync(admin, consentPath, verifier)).GetProperty("access_token").GetString()!;

        using HttpClient client = _factory.CreateClient();
        string appToken = await ClientTokenAsync(client);
        Guid orgId = Guid.NewGuid();
        using (HttpResponseMessage org = await SendAsync(client, appToken, HttpMethod.Put, $"/api/v1/orgs/{orgId:D}", new { name = "Staff Clinic", type = "hospital", parentId = (Guid?)null, metadata = (string?)null }, null))
        {
            Assert.Equal(HttpStatusCode.OK, org.StatusCode);
        }

        using (HttpResponseMessage role = await SendAsync(client, appToken, HttpMethod.Put, "/api/v1/roles/ward_nurse", new { displayName = "Ward nurse", description = "rc.12", permissions = WardRead, orgId = (Guid?)null }, null))
        {
            Assert.Equal(HttpStatusCode.OK, role.StatusCode);
        }

        await MakeAdminAsync(adminEmail, orgId);
        // A last name of its own, so an earlier run's Kiran in the shared test database is not "similar".
        string lastName = "Das" + Guid.NewGuid().ToString("N")[..6];
        object person = new { firstName = "Kiran", lastName, email = $"kiran-{Guid.NewGuid():N}@example.in", mobile = (string?)null, dateOfBirth = "1993-02-14" };

        // Without the admin's own token, or with the application's token in its place, nothing happens.
        using (HttpResponseMessage none = await SendAsync(client, appToken, HttpMethod.Post, $"/api/v1/orgs/{orgId:D}/staff/check", person, null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        }

        using (HttpResponseMessage machine = await SendAsync(client, appToken, HttpMethod.Post, $"/api/v1/orgs/{orgId:D}/staff/check", person, appToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, machine.StatusCode);
        }

        using (HttpResponseMessage check = await SendAsync(client, appToken, HttpMethod.Post, $"/api/v1/orgs/{orgId:D}/staff/check", person, adminToken))
        {
            JsonElement body = await check.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.OK, check.StatusCode);
            Assert.Equal("none", body.GetProperty("outcome").GetString());
        }

        string code;
        using (HttpResponseMessage added = await SendAsync(client, appToken, HttpMethod.Post, $"/api/v1/orgs/{orgId:D}/staff", new { person, roles = WardNurse }, adminToken))
        {
            JsonElement body = await added.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            Assert.Equal("waiting", body.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("userId").ValueKind);
            code = body.GetProperty("activationCode").GetString()!;
        }

        // The e-mail-only invitation is gone.
        using (HttpResponseMessage old = await SendAsync(client, appToken, HttpMethod.Post, $"/api/v1/orgs/{orgId:D}/invitations", new { email = "x@example.in", role = "ward_nurse", appliesToDescendants = false }, null))
        {
            Assert.Equal(HttpStatusCode.Gone, old.StatusCode);
        }

        // The person opens the link, enters the code, creates their account, and joins.
        string email = JsonSerializer.SerializeToElement(person).GetProperty("email").GetString()!;
        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();
        string link = LinkPath().Match(outbox.LatestFor(email)!.Message.TextBody).Value;
        using BrowserSession kiran = new(_factory);
        (HttpStatusCode opened, string page) = await kiran.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, opened);
        Assert.Contains("Staff Clinic", page, StringComparison.Ordinal);

        (HttpStatusCode wrongStatus, _, string wrongPage) = await kiran.PostFormAsync(link, new Dictionary<string, string> { ["Code"] = "AAAA-AAAA" }, handler: "Code");
        Assert.Equal(HttpStatusCode.OK, wrongStatus);
        Assert.Contains("4 more tries", WebUtility.HtmlDecode(wrongPage), StringComparison.Ordinal);

        (HttpStatusCode right, string? ticketed, _) = await kiran.PostFormAsync(link, new Dictionary<string, string> { ["Code"] = code }, handler: "Code");
        Assert.Equal(HttpStatusCode.Found, right);
        Assert.Contains("ticket=", ticketed, StringComparison.Ordinal);

        (_, string register) = await kiran.GetAsync("/register?returnUrl=" + Uri.EscapeDataString(ticketed!));
        Assert.Contains("Kiran", register, StringComparison.Ordinal);
        await RegisterAndVerifyAsync(kiran, "Kiran", lastName, email, ticketed);

        (HttpStatusCode joinStatus, _, string joined) = await kiran.PostFormAsync(ticketed!, [], handler: "Join");
        Assert.Equal(HttpStatusCode.OK, joinStatus);
        Assert.Contains("You have joined", WebUtility.HtmlDecode(joined), StringComparison.Ordinal);

        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid kiranId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        Assert.Equal("ward_nurse", await db.OrgMemberships.Where(m => m.UserId == kiranId && m.OrgId == orgId && m.RevokedAt == null).Select(m => m.Role!.Code).SingleAsync());

        // rc.14: someone already in the customer is added elsewhere in it by the application's own id for them; their
        // Sangam id names nobody the application knows.
        Guid wardId = Guid.NewGuid();
        using (HttpResponseMessage ward = await SendAsync(client, appToken, HttpMethod.Put, $"/api/v1/orgs/{wardId:D}", new { name = "Staff Clinic Ward 2", type = "department", parentId = (Guid?)orgId, metadata = (string?)null }, null))
        {
            Assert.Equal(HttpStatusCode.OK, ward.StatusCode);
        }

        using (HttpResponseMessage bySangamId = await SendAsync(client, appToken, HttpMethod.Post, $"/api/v1/orgs/{wardId:D}/staff", new { userId = kiranId, roles = WardNurse }, adminToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, bySangamId.StatusCode);
        }

        Guid kiranSubject = await _factory.SubjectAsync(DevelopmentSeeder.SampleClientId, kiranId);
        using (HttpResponseMessage again = await SendAsync(client, appToken, HttpMethod.Post, $"/api/v1/orgs/{wardId:D}/staff", new { userId = kiranSubject, roles = WardNurse }, adminToken))
        {
            string body = await again.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal("added", JsonDocument.Parse(body).RootElement.GetProperty("status").GetString());
            Assert.Equal(kiranSubject.ToString("D"), JsonDocument.Parse(body).RootElement.GetProperty("userId").GetString());
        }
    }

    [PostgresFact]
    public async Task ChangingAPlace_ThroughTheApi_SaysWhichAdminAsked_AndThatPersonMustBeAnAdminThere()
    {
        // rc.13 (decision of 14 Oct 2026): PUT and DELETE /orgs/{id}/members/{user} name the admin, as adding staff does.
        using BrowserSession admin = new(_factory);
        string adminEmail = await RegisterAndVerifyAsync(admin, "Asha", "Menon", $"members-admin-{Guid.NewGuid():N}@example.in");
        (string adminVerifier, string adminChallenge) = Pkce();
        (_, string adminConsent, _) = await admin.FollowAsync(Authorize(adminChallenge));
        string adminToken = (await ExchangeAsync(admin, adminConsent, adminVerifier)).GetProperty("access_token").GetString()!;

        // Someone who uses the application, but administers nothing.
        using BrowserSession nurse = new(_factory);
        string nurseEmail = await RegisterAndVerifyAsync(nurse, "Devi", "Nair", $"members-nurse-{Guid.NewGuid():N}@example.in");
        (string nurseVerifier, string nurseChallenge) = Pkce();
        (_, string nurseConsent, _) = await nurse.FollowAsync(Authorize(nurseChallenge));
        string nurseToken = (await ExchangeAsync(nurse, nurseConsent, nurseVerifier)).GetProperty("access_token").GetString()!;

        using HttpClient client = _factory.CreateClient();
        string appToken = await ClientTokenAsync(client);
        Guid orgId = Guid.NewGuid();
        using (HttpResponseMessage org = await SendAsync(client, appToken, HttpMethod.Put, $"/api/v1/orgs/{orgId:D}", new { name = "Members Clinic", type = "hospital", parentId = (Guid?)null, metadata = (string?)null }, null))
        {
            Assert.Equal(HttpStatusCode.OK, org.StatusCode);
        }

        using (HttpResponseMessage role = await SendAsync(client, appToken, HttpMethod.Put, "/api/v1/roles/ward_nurse", new { displayName = "Ward nurse", description = "rc.13", permissions = WardRead, orgId = (Guid?)null }, null))
        {
            Assert.Equal(HttpStatusCode.OK, role.StatusCode);
        }

        await MakeAdminAsync(adminEmail, orgId);
        Guid adminId;
        Guid nurseId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            adminId = await db.Users.Where(u => u.Email == adminEmail).Select(u => u.Id).SingleAsync();
            nurseId = await db.Users.Where(u => u.Email == nurseEmail).Select(u => u.Id).SingleAsync();
        }

        // rc.14: the application names the nurse by its own id for her; her Sangam id means nothing to it.
        Guid nurseSubject = await _factory.SubjectAsync(DevelopmentSeeder.SampleClientId, nurseId);
        Assert.NotEqual(nurseId, nurseSubject);
        string path = $"/api/v1/orgs/{orgId:D}/members/{nurseSubject:D}";
        object body = new { roles = WardNurse };
        using (HttpResponseMessage bySangamId = await SendAsync(client, appToken, HttpMethod.Put, $"/api/v1/orgs/{orgId:D}/members/{nurseId:D}", body, adminToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, bySangamId.StatusCode);
        }

        // No admin named, the application's own token in its place, or someone who is not an admin there: nothing changes.
        using (HttpResponseMessage none = await SendAsync(client, appToken, HttpMethod.Put, path, body, null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        }

        using (HttpResponseMessage machine = await SendAsync(client, appToken, HttpMethod.Put, path, body, appToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, machine.StatusCode);
        }

        using (HttpResponseMessage notAdmin = await SendAsync(client, appToken, HttpMethod.Put, path, body, nurseToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, notAdmin.StatusCode);
        }

        // The admin asks: the place is given, and both it and the audit trail say who asked.
        using (HttpResponseMessage given = await SendAsync(client, appToken, HttpMethod.Put, path, body, adminToken))
        {
            Assert.Equal(HttpStatusCode.OK, given.StatusCode);
            Assert.Equal(nurseSubject.ToString("D"), JsonDocument.Parse(await given.Content.ReadAsStringAsync()).RootElement.GetProperty("userId").GetString());
        }

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            Assert.Equal(adminId, await db.OrgMemberships.Where(m => m.UserId == nurseId && m.OrgId == orgId && m.RevokedAt == null).Select(m => m.GrantedByUserId).SingleAsync());
            Assert.True(await db.AuditEvents.AnyAsync(a => a.Action == AuditActions.OrgMembershipGrant && a.ActorUserId == adminId && a.TargetId == nurseId));
        }

        using (HttpResponseMessage noneToEnd = await SendAsync(client, appToken, HttpMethod.Delete, path, null, null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, noneToEnd.StatusCode);
        }

        using (HttpResponseMessage ended = await SendAsync(client, appToken, HttpMethod.Delete, path, null, adminToken))
        {
            Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        }

        // rc.13: the hospital takes only people verified with DigiLocker now; the nurse is not, so no new place is given.
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            await db.Organisations.Where(o => o.Id == orgId).ExecuteUpdateAsync(u => u.SetProperty(o => o.VerifiedStaffOnly, true));
        }

        using (HttpResponseMessage refused = await SendAsync(client, appToken, HttpMethod.Put, path, body, adminToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("verified with DigiLocker", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [PostgresFact]
    public async Task AnOrganisationTakingOnlyVerifiedPeople_AsksThePersonToVerify_BeforeTheyJoin()
    {
        // rc.13 (tenants' request): the activation page says so, and joining waits for DigiLocker.
        using BrowserSession admin = new(_factory);
        string adminEmail = await RegisterAndVerifyAsync(admin, "Leela", "Pillai", $"verified-admin-{Guid.NewGuid():N}@example.in");
        (string verifier, string challenge) = Pkce();
        (_, string consentPath, _) = await admin.FollowAsync(Authorize(challenge));
        string adminToken = (await ExchangeAsync(admin, consentPath, verifier)).GetProperty("access_token").GetString()!;

        using HttpClient client = _factory.CreateClient();
        string appToken = await ClientTokenAsync(client);
        Guid orgId = Guid.NewGuid();
        using (HttpResponseMessage org = await SendAsync(client, appToken, HttpMethod.Put, $"/api/v1/orgs/{orgId:D}", new { name = "Verified Clinic", type = "hospital", parentId = (Guid?)null, metadata = (string?)null }, null))
        {
            Assert.Equal(HttpStatusCode.OK, org.StatusCode);
        }

        await MakeAdminAsync(adminEmail, orgId);
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            await db.Organisations.Where(o => o.Id == orgId).ExecuteUpdateAsync(u => u.SetProperty(o => o.VerifiedStaffOnly, true));
        }

        string lastName = "Joseph" + Guid.NewGuid().ToString("N")[..6];
        string email = $"rani-{Guid.NewGuid():N}@example.in";
        object person = new { firstName = "Rani", lastName, email, mobile = (string?)null, dateOfBirth = "1993-02-14" };
        string code;
        using (HttpResponseMessage added = await SendAsync(client, appToken, HttpMethod.Post, $"/api/v1/orgs/{orgId:D}/staff", new { person }, adminToken))
        {
            JsonElement body = await added.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            code = body.GetProperty("activationCode").GetString()!;
        }

        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();
        string link = LinkPath().Match(outbox.LatestFor(email)!.Message.TextBody).Value;
        using BrowserSession rani = new(_factory);
        (_, string page) = await rani.GetAsync(link);
        Assert.Contains("data-panel=\"verified-only-note\"", page, StringComparison.Ordinal);

        (_, string? ticketed, _) = await rani.PostFormAsync(link, new Dictionary<string, string> { ["Code"] = code }, handler: "Code");
        await RegisterAndVerifyAsync(rani, "Rani", lastName, email, ticketed);

        // Signed in but not verified with DigiLocker: the page asks for it, and offers to join now and verify later.
        (_, string asked) = await rani.GetAsync(ticketed!);
        Assert.Contains("data-panel=\"verify-to-join\"", asked, StringComparison.Ordinal);
        Assert.Contains("This organisation wants you to verify with DigiLocker to join.", WebUtility.HtmlDecode(asked), StringComparison.Ordinal);
        Assert.Contains("/identity/required?join=true", WebUtility.HtmlDecode(asked), StringComparison.Ordinal);
        (_, _, string joinedFirst) = await rani.PostFormAsync(ticketed!, [], handler: "Join");
        Assert.Contains("You have joined", WebUtility.HtmlDecode(joinedFirst), StringComparison.Ordinal);
        Assert.Contains("data-panel=\"verify-to-join\"", joinedFirst, StringComparison.Ordinal);

        Guid raniId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            raniId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            Assert.False(await db.OrgMemberships.AnyAsync(m => m.UserId == raniId && m.OrgId == orgId));

            // DigiLocker verifies her (as its callback would); the sweep then gives the place.
            await db.Users.Where(u => u.Id == raniId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IdentityVerifiedAt, DateTimeOffset.UtcNow));
        }

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<EfStaffService>().ApplyDueAsync();
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            Assert.True(await db.OrgMemberships.AnyAsync(m => m.UserId == raniId && m.OrgId == orgId && m.RevokedAt == null));
        }
    }

    [PostgresFact]
    public async Task ATester_JoinsADemoOrganisation_Themselves_OnlyWhereAnApplicationAdminAllowedIt()
    {
        // rc.13 (decision of 19 Oct 2026): V-15's one-click demo join, under "someone must ask".
        using BrowserSession tester = new(_factory);
        string email = await RegisterAndVerifyAsync(tester, "Tara", "Iyer", $"tester-{Guid.NewGuid():N}@example.in");
        (string verifier, string challenge) = Pkce();
        (_, string consent, _) = await tester.FollowAsync(Authorize(challenge));
        string testerToken = (await ExchangeAsync(tester, consent, verifier)).GetProperty("access_token").GetString()!;

        using HttpClient client = _factory.CreateClient();
        string appToken = await ClientTokenAsync(client);
        Guid demo = Guid.NewGuid();
        using (HttpResponseMessage org = await SendAsync(client, appToken, HttpMethod.Put, $"/api/v1/orgs/{demo:D}", new { name = "Demo Hospital (made up)", type = "hospital", parentId = (Guid?)null, metadata = "{\"demo\":true}" }, null))
        {
            Assert.Equal(HttpStatusCode.OK, org.StatusCode);
        }

        Guid testerId;
        Guid otherId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            testerId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
            otherId = await db.Users.Where(u => u.Id != testerId).Select(u => u.Id).FirstAsync();
        }

        string path = $"/api/v1/orgs/{demo:D}/members/{await _factory.SubjectAsync(DevelopmentSeeder.SampleClientId, testerId):D}";
        using (HttpResponseMessage notYet = await SendAsync(client, appToken, HttpMethod.Put, path, new { }, testerToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, notYet.StatusCode);
        }

        // An administrator of the application switches it on for this organisation only.
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            Guid appId = await db.Apps.Where(a => a.ClientId == DevelopmentSeeder.SampleClientId).Select(a => a.Id).SingleAsync();
            Guid owner = otherId;
            if (!await db.AppAdmins.AnyAsync(a => a.AppId == appId && a.UserId == owner && a.RevokedAt == null))
            {
                db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = appId, UserId = owner, Role = Domain.Enums.AppAdminRole.Owner, GrantedAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }

            Sangam.Identity.Application.Tenancy.IOrgConsoleService console = scope.ServiceProvider.GetRequiredService<Sangam.Identity.Application.Tenancy.IOrgConsoleService>();
            Assert.False((await console.SetTestersMayJoinAsync(testerId, appId, demo, true)).Succeeded);
            Assert.True((await console.SetTestersMayJoinAsync(owner, appId, demo, true)).Succeeded);
        }

        using (HttpResponseMessage joined = await SendAsync(client, appToken, HttpMethod.Put, path, new { }, testerToken))
        {
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        }

        // Only themselves: the same token cannot place anyone else there.
        using (HttpResponseMessage someoneElse = await SendAsync(client, appToken, HttpMethod.Put, $"/api/v1/orgs/{demo:D}/members/{await _factory.SubjectAsync(DevelopmentSeeder.SampleClientId, otherId):D}", new { }, testerToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, someoneElse.StatusCode);
        }

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
            Assert.Equal(testerId, await db.OrgMemberships.Where(m => m.UserId == testerId && m.OrgId == demo && m.RevokedAt == null).Select(m => m.GrantedByUserId).SingleAsync());
        }
    }

    private async Task MakeAdminAsync(string email, Guid orgId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid app = await db.Apps.Where(a => a.ClientId == DevelopmentSeeder.SampleClientId).Select(a => a.Id).SingleAsync();
        SangamUser user = await db.Users.SingleAsync(u => u.Email == email);
        user.IdentityVerifiedAt = DateTimeOffset.UtcNow;
        db.OrgAdmins.Add(new OrgAdmin { Id = Guid.NewGuid(), AppId = app, OrgId = orgId, UserId = user.Id, Right = OrgAdminRight.Admin, GrantedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<string> RegisterAndVerifyAsync(BrowserSession s, string first, string last, string email, string? returnUrl = null)
    {
        string mobile = Random.Shared.NextInt64(7000000000, 9999999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        InMemoryEmailOutbox outbox = _factory.Services.GetRequiredService<InMemoryEmailOutbox>();
        string path = returnUrl is null ? "/register" : "/register?returnUrl=" + Uri.EscapeDataString(returnUrl);
        (HttpStatusCode status, string? location, string html) = await s.PostFormAsync(path, new Dictionary<string, string>
        {
            ["FirstName"] = first,
            ["LastName"] = last,
            ["Email"] = email,
            ["Country"] = "IN",
            ["MobileNumber"] = mobile,
            ["DateOfBirth"] = "1993-02-14",
            ["Gender"] = "female",
            ["Password"] = "Kaveri-River-2026!",
            ["AcceptTerms"] = "true",
        });
        Assert.True(status == HttpStatusCode.Found && location!.StartsWith("/verify", StringComparison.Ordinal), html);

        // The newest e-mail to the address (Recent is newest first) that is not the activation link (whose token may hold six digits in a row).
        string body = outbox.Recent.First(m => string.Equals(m.Message.ToEmail, email, StringComparison.OrdinalIgnoreCase) && !m.Message.TextBody.Contains("/staff/", StringComparison.Ordinal)).Message.TextBody;
        string code = Regex.Match(body, @"\b[0-9]{6}\b").Value;
        (_, string? verified, _) = await s.PostFormAsync(location, new Dictionary<string, string> { ["Code"] = code });
        Assert.StartsWith("/verified", verified, StringComparison.Ordinal);
        return email;
    }

    private async Task<JsonElement> ExchangeAsync(BrowserSession s, string consentPath, string verifier)
    {
        string redirect = consentPath;
        if (consentPath.StartsWith("/consent", StringComparison.Ordinal))
        {
            (HttpStatusCode allowStatus, string? allowLocation, _) = await s.PostFormAsync(consentPath, [], handler: "Allow");
            Assert.Equal(HttpStatusCode.Found, allowStatus);
            redirect = allowLocation!;
        }

        if (redirect.StartsWith('/'))
        {
            (_, redirect, _) = await s.FollowAsync(redirect);
        }

        string code = HttpUtility.ParseQueryString(new Uri(redirect).Query)["code"]!;
        using HttpClient api = _factory.CreateClient();
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = DevelopmentSeeder.SampleRedirectUri,
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
            ["code_verifier"] = verifier,
        });
        using HttpResponseMessage response = await api.PostAsync(new Uri("/connect/token", UriKind.Relative), form);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<string> ClientTokenAsync(HttpClient client)
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = DevelopmentSeeder.SampleClientId,
            ["client_secret"] = DevelopmentSeeder.SampleClientSecret,
            ["scope"] = "sangam.manage",
        });
        using HttpResponseMessage response = await client.PostAsync(new Uri("/connect/token", UriKind.Relative), form);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body, string? actingAdmin)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (actingAdmin is not null)
        {
            request.Headers.Add("Sangam-Acting-Admin", actingAdmin);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static string Authorize(string challenge)
        => "/connect/authorize?client_id=" + DevelopmentSeeder.SampleClientId
        + "&redirect_uri=" + Uri.EscapeDataString(DevelopmentSeeder.SampleRedirectUri)
        + "&response_type=code&scope=" + Uri.EscapeDataString("openid profile email")
        + "&state=xyz&code_challenge=" + challenge + "&code_challenge_method=S256";

    private static (string Verifier, string Challenge) Pkce()
    {
        string verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    [GeneratedRegex(@"/staff/[A-Za-z0-9_-]+")]
    private static partial Regex LinkPath();
}
