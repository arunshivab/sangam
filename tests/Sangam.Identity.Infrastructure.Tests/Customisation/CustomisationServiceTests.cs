using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sangam.Identity.Application.Abstractions;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;
using Sangam.Identity.Infrastructure.Services;
using Sangam.Identity.Infrastructure.Tests.Postgres;

namespace Sangam.Identity.Infrastructure.Tests.Customisation;

/// <summary>PR-19 (SGM-209): branding and templates by level, who may change them, and what people receive.</summary>
[Collection("postgres")]
public sealed class CustomisationServiceTests : IAsyncLifetime
{
    private static readonly Dictionary<string, string?> NoWelcome = [];
    private readonly List<IServiceScope> _scopes = [];
    private readonly PostgresFixture _pg;
    private ServiceProvider _provider = null!;
    private Guid _partner;
    private Guid _stranger;
    private Guid _operator;
    private Guid _appId;
    private Guid _otherAppId;
    private Guid _hospital;
    private Guid _ward;
    private Guid _otherOrg;

    public CustomisationServiceTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    public async Task InitializeAsync()
    {
        if (!_pg.IsAvailable)
        {
            return;
        }

        await _pg.ResetAsync();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataProtection();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sangam"] = _pg.ConnectionString,
            ["Sangam:Email:UseOutbox"] = "true",
            ["Sangam:PasswordHashing:MemoryKiB"] = "8192",
            ["Sangam:PasswordHashing:Iterations"] = "2",
            ["Sangam:Otp:ResendCooldown"] = "00:00:00",
        }).Build();
        services.AddSingleton(configuration);
        services.AddSangamInfrastructure(configuration);
        _provider = services.BuildServiceProvider();

        _partner = await NewUserAsync("partner@example.in", "+919876500101");
        _stranger = await NewUserAsync("stranger@example.in", "+919876500102");
        _operator = await NewUserAsync("operator@example.in", "+919876500103");

        await using SangamDbContext db = _pg.CreateContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        App app = new() { Id = Guid.NewGuid(), ClientId = "his", Slug = "his", DisplayName = "LiPi HIS", OwnerCompanyName = "LiPi", CreatedAt = now, UpdatedAt = now };
        App other = new() { Id = Guid.NewGuid(), ClientId = "lab", Slug = "lab", DisplayName = "Lab", OwnerCompanyName = "Lab", CreatedAt = now, UpdatedAt = now };
        db.Apps.AddRange(app, other);
        _appId = app.Id;
        _otherAppId = other.Id;
        _hospital = Guid.NewGuid();
        _ward = Guid.NewGuid();
        _otherOrg = Guid.NewGuid();
        db.Organisations.AddRange(
            new Organisation { Id = _hospital, Name = "City Hospital", OrgTypeCode = "hospital", Path = OrganisationPath.ForRoot(_hospital), RegisteredViaAppId = app.Id, CreatedAt = now, UpdatedAt = now },
            new Organisation { Id = _ward, Name = "Ward 3", OrgTypeCode = "department", ParentOrgId = _hospital, Path = OrganisationPath.ForChild(OrganisationPath.ForRoot(_hospital), _ward), Depth = 1, RegisteredViaAppId = app.Id, CreatedAt = now, UpdatedAt = now },
            new Organisation { Id = _otherOrg, Name = "Other Lab", OrgTypeCode = "lab", Path = OrganisationPath.ForRoot(_otherOrg), RegisteredViaAppId = other.Id, CreatedAt = now, UpdatedAt = now });
        db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = app.Id, UserId = _partner, Role = AppAdminRole.Admin, GrantedAt = now });
        db.PlatformOperators.Add(new PlatformOperator { Id = Guid.NewGuid(), UserId = _operator, Role = PlatformRole.AppManager, GrantedAt = now });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (IServiceScope scope in _scopes)
        {
            scope.Dispose();
        }

        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task OnlyTheApplicationsAdministrators_ChangeItsBranding_AndOnlyOperatorsThePlatforms()
    {
        ICustomisationService c = Service();
        BrandingInput input = new("#1D4E89", NoWelcome, null, null, null);
        Assert.False((await c.SaveBrandingAsync(_stranger, CustomisationScope.App, _appId, input, null)).Succeeded);
        Assert.False((await c.SaveBrandingAsync(_partner, CustomisationScope.App, _otherAppId, input, null)).Succeeded);
        Assert.False((await c.SaveBrandingAsync(_partner, CustomisationScope.Organisation, _otherOrg, input, null)).Succeeded);
        Assert.False((await c.SaveBrandingAsync(_partner, CustomisationScope.Platform, null, input, null)).Succeeded);
        Assert.True((await c.SaveBrandingAsync(_partner, CustomisationScope.App, _appId, input, null)).Succeeded);
        Assert.True((await c.SaveBrandingAsync(_partner, CustomisationScope.Organisation, _ward, input, null)).Succeeded);
        Assert.True((await c.SaveBrandingAsync(_operator, CustomisationScope.Platform, null, input, null)).Succeeded);
        Assert.Null(await c.GetBrandingAsync(_stranger, CustomisationScope.App, _appId));

        await using SangamDbContext db = _pg.CreateContext();
        Assert.Equal(3, await db.AuditEvents.CountAsync(e => e.Action == AuditActions.CustomisationBrandingUpdate));
    }

    [PostgresFact]
    public async Task AColourThatCannotCarryWhiteText_AndALinkThatIsNotHttps_AreRefused()
    {
        ICustomisationService c = Service();
        CustomisationResult light = await c.SaveBrandingAsync(_partner, CustomisationScope.App, _appId, new BrandingInput("#FFD700", NoWelcome, null, null, null), null);
        Assert.False(light.Succeeded);
        Assert.Contains("4.5:1", light.Message, StringComparison.Ordinal);
        Assert.False((await c.SaveBrandingAsync(_partner, CustomisationScope.App, _appId, new BrandingInput(null, NoWelcome, "http://help.example.in", null, null), null)).Succeeded);
        Assert.False((await c.SaveBrandingAsync(_partner, CustomisationScope.App, _appId, new BrandingInput(null, new Dictionary<string, string?> { ["en-IN"] = "two\nlines" }, null, null, null), null)).Succeeded);
    }

    [PostgresFact]
    public async Task TheSignInPage_TakesEachSetting_FromTheMostSpecificLevel_InTheReadersLanguage()
    {
        ICustomisationService c = Service();
        await c.SaveBrandingAsync(_operator, CustomisationScope.Platform, null, new BrandingInput(null, NoWelcome, "https://help.sangamid.in", null, null), null);
        await c.SaveBrandingAsync(_partner, CustomisationScope.App, _appId, new BrandingInput("#1D4E89", new Dictionary<string, string?> { ["en-IN"] = "Welcome to LiPi HIS", ["hi-IN"] = "LiPi HIS में आपका स्वागत है" }, null, "https://lipi.example.in/terms", null), null);
        await c.SaveBrandingAsync(_partner, CustomisationScope.Organisation, _hospital, new BrandingInput("#7A1F1F", new Dictionary<string, string?> { ["en-IN"] = "City Hospital staff sign-in" }, null, null, null), null);

        LoginBranding app = await c.ResolveLoginBrandingAsync(_appId, null, "hi-IN");
        Assert.Equal("#1D4E89", app.Accent);
        Assert.Equal("LiPi HIS में आपका स्वागत है", app.Welcome);
        Assert.Equal("https://help.sangamid.in", app.HelpUrl);
        Assert.Equal("https://lipi.example.in/terms", app.TermsUrl);

        // The ward inherits from its hospital; Malayalam falls back to the most specific English line.
        LoginBranding ward = await c.ResolveLoginBrandingAsync(_appId, _ward, "ml-IN");
        Assert.Equal("#7A1F1F", ward.Accent);
        Assert.Equal("City Hospital staff sign-in", ward.Welcome);
        Assert.Equal("https://lipi.example.in/terms", ward.TermsUrl);

        // An organisation of another application is ignored.
        LoginBranding foreign = await c.ResolveLoginBrandingAsync(_appId, _otherOrg, "en-IN");
        Assert.Equal("#1D4E89", foreign.Accent);
    }

    [PostgresFact]
    public async Task ANewLogo_ReplacesTheOldOne_AndIsServedUnderANewAddress()
    {
        ICustomisationService c = Service();
        byte[] first = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"4\" height=\"4\"/></svg>");
        byte[] second = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><circle r=\"4\"/></svg>");
        Assert.True((await c.SaveLogoAsync(_partner, CustomisationScope.App, _appId, "image/svg+xml", first, null)).Succeeded);
        string url1 = (await c.ResolveLoginBrandingAsync(_appId, null, "en-IN")).LogoUrl!;
        Assert.True((await c.SaveLogoAsync(_partner, CustomisationScope.App, _appId, "image/svg+xml", second, null)).Succeeded);
        string url2 = (await c.ResolveLoginBrandingAsync(_appId, null, "en-IN")).LogoUrl!;
        Assert.NotEqual(url1, url2);
        Assert.False((await c.SaveLogoAsync(_partner, CustomisationScope.App, _appId, "image/svg+xml", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"x()\"/>"), null)).Succeeded);

        await using SangamDbContext db = _pg.CreateContext();
        BrandingAsset only = await db.BrandingAssets.SingleAsync();
        Assert.Equal(second, (await c.GetLogoAsync(only.Id))!.Content);
        Assert.True((await c.RemoveLogoAsync(_partner, CustomisationScope.App, _appId, null)).Succeeded);
        Assert.Null((await c.ResolveLoginBrandingAsync(_appId, null, "en-IN")).LogoUrl);
    }

    [PostgresFact]
    public async Task AnApplicationsOwnWording_IsUsed_ButTheReadersLanguageComesFirst()
    {
        ICustomisationService c = Service();
        CustomisationResult saved = await c.SaveTemplateAsync(_partner, CustomisationScope.App, _appId,
            new TemplateInput(MessageTemplateKinds.SignInCode, "en-IN", "{{code}} — your LiPi HIS sign-in code", "Hello {{name}}, your LiPi HIS code is\n\n    {{code}}\n\nLiPi HIS", null), null);
        Assert.True(saved.Succeeded, saved.Message);

        IMessageTemplates t = Templates();
        Dictionary<string, string> values = new() { ["name"] = "Asha", ["code"] = "123456", ["minutes"] = "10" };
        EmailMessage english = await t.EmailAsync(MessageTemplateKinds.SignInCode, "en-IN", _appId, null, values, "a@x.in", "Asha");
        Assert.Equal("123456 — your LiPi HIS sign-in code", english.Subject);
        Assert.Contains("LiPi HIS", english.HtmlBody!, StringComparison.Ordinal);

        EmailMessage hindi = await t.EmailAsync(MessageTemplateKinds.SignInCode, "hi-IN", _appId, null, values, "a@x.in", "Asha");
        Assert.Contains("123456", hindi.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("your LiPi HIS code", hindi.TextBody, StringComparison.Ordinal);
        Assert.Contains("lang=\"hi-IN\"", hindi.HtmlBody!, StringComparison.Ordinal);

        // Another application keeps Sangam's text.
        EmailMessage other = await t.EmailAsync(MessageTemplateKinds.SignInCode, "en-IN", _otherAppId, null, values, "a@x.in", "Asha");
        Assert.Equal("123456 is your Sangam sign-in code", other.Subject);
    }

    [PostgresFact]
    public async Task AnOrganisationsWording_BeatsItsApplications_ForItsInvitations()
    {
        ICustomisationService c = Service();
        await c.SaveTemplateAsync(_partner, CustomisationScope.App, _appId, new TemplateInput(MessageTemplateKinds.Invitation, "en-IN", "Join {{application}}", "App text\n\n    {{link}}", null), null);
        await c.SaveTemplateAsync(_partner, CustomisationScope.Organisation, _hospital, new TemplateInput(MessageTemplateKinds.Invitation, "en-IN", "Join {{organisation}}", "Hospital text\n\n    {{link}}", null), null);
        Dictionary<string, string> values = new() { ["application"] = "LiPi HIS", ["organisation"] = "Ward 3", ["role"] = "Nurse", ["link"] = "https://id/invite/x", ["days"] = "7" };
        EmailMessage ward = await Templates().EmailAsync(MessageTemplateKinds.Invitation, "en-IN", _appId, _ward, values, "a@x.in", "a@x.in");
        Assert.Equal("Join Ward 3", ward.Subject);

        IReadOnlyList<TemplateRow> rows = (await c.ListTemplatesAsync(_partner, CustomisationScope.Organisation, _ward))!;
        TemplateRow inherited = rows.Single(r => r.Kind == MessageTemplateKinds.Invitation && r.Language == "en-IN");
        Assert.Equal("organisation", inherited.Source);
        Assert.Equal("default", rows.Single(r => r.Kind == MessageTemplateKinds.Invitation && r.Language == "hi-IN").Source);
        Assert.DoesNotContain(rows, r => r.Kind == MessageTemplateKinds.TwoStepResetNotice);

        Assert.True((await c.ResetTemplateAsync(_partner, CustomisationScope.Organisation, _hospital, MessageTemplateKinds.Invitation, "en-IN", null)).Succeeded);
        Assert.Equal("Join LiPi HIS", (await Templates().EmailAsync(MessageTemplateKinds.Invitation, "en-IN", _appId, _ward, values, "a@x.in", "a@x.in")).Subject);
    }

    [PostgresFact]
    public async Task SecurityNoticesAndTextMessages_AreThePlatforms_AndATextNeedsItsDltRegistration()
    {
        ICustomisationService c = Service();
        Assert.False((await c.SaveTemplateAsync(_partner, CustomisationScope.App, _appId, new TemplateInput(MessageTemplateKinds.TwoStepResetNotice, "en-IN", "Hi", "Hi {{name}}", null), null)).Succeeded);
        Assert.False((await c.SaveTemplateAsync(_operator, CustomisationScope.Platform, null, new TemplateInput(MessageTemplateKinds.SmsSignIn, "hi-IN", null, "{#var#} आपका SangamID साइन-इन कोड है। - SANGAM", null), null)).Succeeded);
        Assert.False((await c.SaveTemplateAsync(_operator, CustomisationScope.Platform, null, new TemplateInput(MessageTemplateKinds.SmsSignIn, "en-IN", null, "{#var#} code", "1107161234567890123"), null)).Succeeded);
        Assert.True((await c.SaveTemplateAsync(_operator, CustomisationScope.Platform, null, new TemplateInput(MessageTemplateKinds.SmsSignIn, "hi-IN", null, "{#var#} आपका SangamID साइन-इन कोड है। - SANGAM", "1107161234567890123"), null)).Succeeded);

        (string Text, string DltTemplateId)? sms = await Templates().SmsAsync(MessageTemplateKinds.SmsSignIn, "hi-IN");
        Assert.Equal("1107161234567890123", sms!.Value.DltTemplateId);
        Assert.Null(await Templates().SmsAsync(MessageTemplateKinds.SmsSignIn, "ml-IN"));
    }

    [PostgresFact]
    public async Task AnAccountsEmails_ComeInItsLanguage()
    {
        CultureInfo before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("ml-IN");
            Guid id = await NewUserAsync("mal@example.in", "+919876500104");
            await using SangamDbContext db = _pg.CreateContext();
            Assert.Equal("ml-IN", (await db.Users.SingleAsync(u => u.Id == id)).Locale);
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }

        using IServiceScope scope = _provider.CreateScope();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();
        EmailMessage sent = outbox.LatestFor("mal@example.in")!.Message;
        (string subject, _) = Infrastructure.Customisation.DefaultTemplates.Find(MessageTemplateKinds.EmailVerification, "ml-IN")!.Value;
        Assert.EndsWith(subject.Replace("{{code}}", string.Empty, StringComparison.Ordinal).Trim(), sent.Subject, StringComparison.Ordinal);
    }

    private ICustomisationService Service()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<ICustomisationService>();
    }

    private IMessageTemplates Templates()
    {
        IServiceScope scope = _provider.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IMessageTemplates>();
    }

    private async Task<Guid> NewUserAsync(string email, string mobile)
    {
        using IServiceScope scope = _provider.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        InMemoryEmailOutbox outbox = scope.ServiceProvider.GetRequiredService<InMemoryEmailOutbox>();
        Guid id = (await accounts.RegisterAsync(new RegisterUserCommand(
            "Test", "User", email, mobile, new DateOnly(1985, 1, 1), Gender.PreferNotToSay, "Kaveri-River-2026!", "v1", null, null))).UserId!.Value;
        string code = System.Text.RegularExpressions.Regex.Match(outbox.LatestFor(email)!.Message.TextBody, "[0-9]{6}").Value;
        await accounts.VerifyCodeAsync(id, OneTimeCodePurpose.EmailVerification, code, null);
        return id;
    }
}
