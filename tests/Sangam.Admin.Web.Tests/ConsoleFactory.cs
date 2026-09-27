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
                new Claim("name", "Test Operator"),
            ], SchemeName, "name", "role");

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
