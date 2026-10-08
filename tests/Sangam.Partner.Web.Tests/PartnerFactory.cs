using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Partner.Web.Tests;

/// <summary>
/// Hosts the partner console in-process with the OpenID Connect hop replaced by a stub scheme, so the
/// gate and the pages can be exercised without a running identity server. The stub signs in as
/// whichever user a test chooses through the <c>X-Test-User</c> header.
/// </summary>
public sealed class PartnerFactory : WebApplicationFactory<Program>
{
    public const string ConnectionEnvironmentVariable = "SANGAM_TEST_CONNECTION";
    public const string SchemeName = "TestPartner";
    public const string UserHeader = "X-Test-User";

    public static string? TestConnection => Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);

    public static bool HasDatabase => !string.IsNullOrWhiteSpace(TestConnection);

    /// <summary>The partner console tests' own database, so they never race the other suites.</summary>
    public static string PartnerTestConnection
    {
        get
        {
            Npgsql.NpgsqlConnectionStringBuilder builder = new(TestConnection);
            builder.Database += "_partner";
            return builder.ConnectionString;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.UseSetting("Sangam:DataProtection:PersistKeys", "false");
        // OI-015: TestServer has no HTTPS port; the redirection middleware’s warning is expected here.
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.HttpsPolicy", "Error");
        builder.UseSetting("ConnectionStrings:Sangam", HasDatabase ? PartnerTestConnection : SangamDbContextFactory.DevelopmentConnectionString);
        builder.UseSetting("Sangam:Authority", "https://id.example.invalid");
        builder.UseSetting("Sangam:PortalUrl", "https://account.example.invalid");
        builder.UseSetting("Sangam:Maintenance:Enabled", "false");
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Warning");

        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(SchemeName).AddScheme<AuthenticationSchemeOptions, StubHandler>(SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = SchemeName;
                o.DefaultAuthenticateScheme = SchemeName;
                o.DefaultChallengeScheme = SchemeName;
            });
        });
    }

    /// <summary>Creates the schema if needed and a verified user, optionally with an authenticator.</summary>
    public async Task<Guid> SeedUserAsync(bool mfa, string firstName = "Test")
    {
        ArgumentNullException.ThrowIfNull(firstName);
        using IServiceScope scope = Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        await db.Database.MigrateAsync();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string email = $"{firstName.ToLowerInvariant()}-{Guid.NewGuid():N}@example.in";
        SangamUser user = new()
        {
            Id = Guid.NewGuid(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            TwoFactorEnabled = mfa,
            FirstName = firstName,
            LastName = "Partner",
            DateOfBirth = new DateOnly(1985, 1, 1),
            Gender = Gender.PreferNotToSay,
            PhoneNumber = "+9198" + Random.Shared.Next(10000000, 99999999).ToString(System.Globalization.CultureInfo.InvariantCulture),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now,
            LastPasswordChangeAt = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>Registers an application with a unique client id, under the given name and sign-in rule.</summary>
    public async Task<Guid> SeedAppAsync(string displayName, SignInPolicy policy = SignInPolicy.Default)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        using IServiceScope scope = Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        await db.Database.MigrateAsync();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string clientId = $"app-{Guid.NewGuid():N}";
        App app = new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Slug = clientId,
            DisplayName = displayName,
            OwnerCompanyName = "Partner Pvt Ltd",
            SignInPolicy = policy,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        return app.Id;
    }

    /// <summary>Makes <paramref name="userId"/> an administrator of <paramref name="appId"/>, and links them to it.</summary>
    public async Task MakeAdminAsync(Guid appId, Guid userId, AppAdminRole role)
    {
        using IServiceScope scope = Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), AppId = appId, UserId = userId, GrantedAt = now });
        db.AppAdmins.Add(new AppAdmin { Id = Guid.NewGuid(), AppId = appId, UserId = userId, Role = role, GrantedAt = now });
        await db.SaveChangesAsync();
    }

    /// <summary>A client that is signed in as <paramref name="userId"/>.</summary>
    public HttpClient ClientFor(Guid userId)
    {
        HttpClient client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(UserHeader, userId.ToString("D"));
        return client;
    }

    private sealed class StubHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public StubHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out Microsoft.Extensions.Primitives.StringValues value))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            ClaimsIdentity identity = new(
            [
                new Claim("sub", value.ToString()),
                new Claim("name", "Test Partner"),
            ], SchemeName, "name", "role");

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
