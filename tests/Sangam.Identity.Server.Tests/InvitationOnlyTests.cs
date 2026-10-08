using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Seeding;
using Sangam.Identity.Server.Pages.Account;

namespace Sangam.Identity.Server.Tests;

/// <summary>D-I: for the private pilot, registration can be made invitation-only.</summary>
[Collection("server")]
public sealed class InvitationOnlyTests
{
    private const string Closed = "Sangam is open by invitation only for now.";
    private readonly SangamServerFactory _factory;

    public InvitationOnlyTests(SangamServerFactory factory)
    {
        _factory = factory;
    }

    [PostgresFact]
    public async Task WithoutAnInvitation_TheFormIsReplacedByANotice()
    {
        using WebApplicationFactory<Program> pilot = _factory.WithWebHostBuilder(b => b.UseSetting("Sangam:Registration:InvitationOnly", "true"));
        using BrowserSession s = new(pilot);
        (_, string page) = await s.GetAsync("/register");
        Assert.Contains(Closed, WebUtility.HtmlDecode(page), StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Password\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<form method=\"post\"", page, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task AnAllowedTester_CanRegister_AndOthersAreRefused()
    {
        using WebApplicationFactory<Program> pilot = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Sangam:Registration:InvitationOnly", "true");
            b.UseSetting("Sangam:Registration:AllowedEmails", "founder@sangamid.in, @testers.example.in");
        });
        string tester = $"t-{Guid.NewGuid():N}@testers.example.in";
        using (BrowserSession s = new(pilot))
        {
            (_, string? location, string html) = await s.PostFormAsync("/register", Form(tester));
            Assert.True(location == "/verify", html);
        }

        string stranger = $"s-{Guid.NewGuid():N}@example.in";
        using (BrowserSession s = new(pilot))
        {
            (_, string? location, string html) = await s.PostFormAsync("/register", Form(stranger));
            Assert.Null(location);
            Assert.Contains(Closed, WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        }

        Assert.Equal(1, await AccountsWithAsync(tester));
        Assert.Equal(0, await AccountsWithAsync(stranger));
    }

    [PostgresFact]
    public async Task AnOpenInvitation_LetsItsRecipientRegister_WithTheInvitedAddressOnly()
    {
        string email = $"invited-{Guid.NewGuid():N}@example.in";
        string token = await InviteAsync(email);
        string returnUrl = "/invite/" + token;
        using WebApplicationFactory<Program> pilot = _factory.WithWebHostBuilder(b => b.UseSetting("Sangam:Registration:InvitationOnly", "true"));

        using BrowserSession s = new(pilot);
        (_, string page) = await s.GetAsync("/register?returnUrl=" + Uri.EscapeDataString(returnUrl));
        Assert.DoesNotContain(Closed, WebUtility.HtmlDecode(page), StringComparison.Ordinal);
        Assert.Contains("value=\"" + email + "\"", page, StringComparison.Ordinal);

        (_, string? wrong, string wrongHtml) = await s.PostFormAsync("/register?returnUrl=" + Uri.EscapeDataString(returnUrl), Form($"other-{Guid.NewGuid():N}@example.in"));
        Assert.Null(wrong);
        Assert.Contains("Use the address your invitation was sent to.", WebUtility.HtmlDecode(wrongHtml), StringComparison.Ordinal);

        (_, string? ok, string html) = await s.PostFormAsync("/register?returnUrl=" + Uri.EscapeDataString(returnUrl), Form(email));
        Assert.True(ok is not null && ok.StartsWith("/verify", StringComparison.Ordinal), html);
        Assert.Equal(1, await AccountsWithAsync(email));
    }

    [Theory]
    [InlineData("/invite/abc-123", "abc-123")]
    [InlineData("/invite/abc?x=1", "abc")]
    [InlineData("/connect/authorize?client_id=x", null)]
    [InlineData("/invite/", null)]
    [InlineData("/invite/a b", null)]
    public void TheInvitationToken_IsReadFromTheReturnAddress(string returnUrl, string? token)
        => Assert.Equal(token, RegisterModel.InviteToken(returnUrl));

    private async Task<string> InviteAsync(string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        Guid appId = await db.Apps.Where(a => a.ClientId == DevelopmentSeeder.SampleClientId).Select(a => a.Id).SingleAsync();
        Guid admin = await db.Users.OrderBy(u => u.CreatedAt).Select(u => u.Id).FirstAsync();
        if (!await db.AppAdmins.AnyAsync(a => a.AppId == appId && a.UserId == admin && a.RevokedAt == null))
        {
            db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = appId, UserId = admin, Role = AppAdminRole.Owner, GrantedAt = DateTimeOffset.UtcNow });
        }

        Guid org = Guid.NewGuid();
        db.Organisations.Add(new Organisation { Id = org, Name = "Pilot Clinic", OrgTypeCode = "clinic", Path = OrganisationPath.ForRoot(org), RegisteredViaAppId = appId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        db.Invitations.Add(new Invitation
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            OrgId = org,
            RoleCode = "viewer",
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            InvitedByUserId = admin,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync();
        return token;
    }

    private async Task<int> AccountsWithAsync(string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SangamDbContext>().Users.CountAsync(u => u.Email == email);
    }

    private static Dictionary<string, string> Form(string email) => new(StringComparer.Ordinal)
    {
        ["FirstName"] = "Meera",
        ["LastName"] = "Pillai",
        ["Email"] = email,
        ["Country"] = "IN",
        ["MobileNumber"] = Random.Shared.NextInt64(7000000000, 9999999999).ToString(CultureInfo.InvariantCulture),
        ["BirthDay"] = "3",
        ["BirthMonth"] = "7",
        ["BirthYear"] = "1991",
        ["Gender"] = "female",
        ["Password"] = "Correct-Horse-2026!",
        ["AcceptTerms"] = "true",
    };
}
