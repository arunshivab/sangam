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

namespace Sangam.Admin.Web.Tests;

/// <summary>
/// Hosts the console in-process with the OpenID Connect hop replaced by a stub scheme, so the
/// gate and the pages can be exercised without a running identity server. The stub signs in as
/// whichever user a test chooses through the <c>X-Test-User</c> header.
/// </summary>
public sealed class ConsoleFactory : WebApplicationFactory<Program>
{
    public const string ConnectionEnvironmentVariable = "SANGAM_TEST_CONNECTION";
    public const string SchemeName = "TestOperator";
    public const string UserHeader = "X-Test-User";

    /// <summary>rc.10: the sign-in level the stub claims (acr); two-step sign-in unless a test says otherwise.</summary>
    public const string LevelHeader = "X-Test-Acr";

    public static string? TestConnection => Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);

    public static bool HasDatabase => !string.IsNullOrWhiteSpace(TestConnection);

    /// <summary>The console tests' own database, so they never race the other suites.</summary>
    public static string ConsoleTestConnection
    {
        get
        {
            Npgsql.NpgsqlConnectionStringBuilder builder = new(TestConnection);
            builder.Database += "_admin";
            return builder.ConnectionString;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.UseSetting("Sangam:DataProtection:PersistKeys", "false");
        // rc.5: tests never call the Pwned Passwords service; the built-in list is the check.
        builder.UseSetting("Sangam:Passwords:BreachCheck:Endpoint", "");
        // OI-015: TestServer has no HTTPS port; the redirection middleware’s warning is expected here.
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.HttpsPolicy", "Error");
        builder.UseSetting("ConnectionStrings:Sangam", HasDatabase ? ConsoleTestConnection : SangamDbContextFactory.DevelopmentConnectionString);
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

            // rc.11: no identity server here; the signing keys page reads a key set made for the test.
            services.AddSingleton<Components.Common.ISigningKeySource, TestSigningKeys>();
        });
    }

    /// <summary>Creates the schema if needed and a verified user, optionally an operator, optionally with MFA.</summary>
    public async Task<Guid> SeedAsync(PlatformRole? role, bool mfa, string firstName = "Test")
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
            LastName = "Operator",
            DateOfBirth = new DateOnly(1985, 1, 1),
            Gender = Gender.PreferNotToSay,
            PhoneNumber = "+9198" + Random.Shared.Next(10000000, 99999999).ToString(System.Globalization.CultureInfo.InvariantCulture),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now,
            LastPasswordChangeAt = now,
        };
        db.Users.Add(user);
        if (role is PlatformRole r)
        {
            db.PlatformOperators.Add(new PlatformOperator { Id = Guid.NewGuid(), UserId = user.Id, Role = r, GrantedAt = now });
        }

        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>A client that is signed in as <paramref name="userId"/>, with two-step sign-in unless <paramref name="acr"/> says otherwise.</summary>
    public HttpClient ClientFor(Guid userId, string? acr = null)
    {
        HttpClient client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(UserHeader, userId.ToString("D"));
        if (acr is not null)
        {
            client.DefaultRequestHeaders.Add(LevelHeader, acr);
        }

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
                new Claim("acr", Request.Headers.TryGetValue(LevelHeader, out Microsoft.Extensions.Primitives.StringValues level) ? level.ToString() : "urn:sangam:acr:2"),
                new Claim("name", "Test Operator"),
            ], SchemeName, "name", "role");

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}

/// <summary>
/// The console test classes share one database; run them one at a time. Each class has its own host, and
/// two hosts migrating a fresh database at once collide (as on CI, where the database starts empty).
/// </summary>
[CollectionDefinition("admin-db")]
public sealed class ConsoleDatabaseGroup
{
}
