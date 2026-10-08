using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Imagiqa.Web.Records;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Sangam.Client;

namespace Imagiqa.Web.Tests;

/// <summary>
/// Hosts imagiQa in-process against its own test database, with a stub sign-in that hands the
/// application exactly what a Sangam sign-in would: a subject, a name, and the <c>sangam_orgs</c>
/// claim.
/// </summary>
public class ImagiqaFactory : WebApplicationFactory<Program>
{
    /// <summary>Environment variable holding the shared PostgreSQL test connection.</summary>
    public const string ConnectionEnvironmentVariable = "SANGAM_TEST_CONNECTION";

    private const string SchemeName = "TestSangam";
    private const string UserHeader = "X-Test-User";
    private const string OrgsHeader = "X-Test-Orgs";
    private static readonly SemaphoreSlim MigrateGate = new(1, 1);
    private static bool _migrated;

    /// <summary>Whether a PostgreSQL test database is configured.</summary>
    public static bool HasDatabase => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable));

    /// <summary>imagiQa's own test database, derived from the shared connection.</summary>
    public static string TestConnection
    {
        get
        {
            NpgsqlConnectionStringBuilder builder = new(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable));
            builder.Database += "_imagiqa";
            return builder.ConnectionString;
        }
    }

    /// <summary>A context factory over the test database, for tests that use the service directly.</summary>
    public static IDbContextFactory<ImagiqaDbContext> ContextFactory()
        => new PooledDbContextFactory<ImagiqaDbContext>(new DbContextOptionsBuilder<ImagiqaDbContext>()
            .UseNpgsql(TestConnection).UseSnakeCaseNamingConvention().Options);

    /// <summary>Creates the schema once per test run.</summary>
    public static async Task MigrateAsync()
    {
        await MigrateGate.WaitAsync();
        try
        {
            if (!_migrated)
            {
                using ImagiqaDbContext db = ContextFactory().CreateDbContext();
                await db.Database.MigrateAsync();
                _migrated = true;
            }
        }
        finally
        {
            MigrateGate.Release();
        }
    }

    /// <summary>A client signed in as the given person with the given memberships.</summary>
    public HttpClient ClientFor(SangamUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        HttpClient client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(UserHeader, $"{user.Id:D}|{user.Name}");
        client.DefaultRequestHeaders.Add(OrgsHeader, JsonSerializer.Serialize(user.Memberships.Select(m => new Dictionary<string, object>
        {
            ["id"] = m.OrganisationId.ToString("D"),
            ["name"] = m.OrganisationName,
            ["type"] = m.OrganisationType,
            ["path"] = m.Path,
            ["role"] = m.Role,
            ["permissions"] = m.Permissions,
            ["inherits"] = m.AppliesToDescendants,
        })));
        return client;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Imagiqa", HasDatabase ? TestConnection : "Host=localhost;Database=unused");
        builder.UseSetting("Sangam:Authority", "https://id.example.invalid");
        builder.UseSetting("Sangam:ClientId", "imagiqa");
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

    private sealed class StubHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public StubHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out Microsoft.Extensions.Primitives.StringValues user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            string[] parts = user.ToString().Split('|', 2);
            List<Claim> claims = [new(SangamUser.SubjectClaim, parts[0]), new(SangamUser.NameClaim, parts[1])];
            if (Request.Headers.TryGetValue(OrgsHeader, out Microsoft.Extensions.Primitives.StringValues orgs))
            {
                // One claim holding the whole array: the second shape the SDK must accept.
                claims.Add(new Claim(Sangam.Shared.Constants.SangamClaims.Orgs, orgs.ToString()));
            }

            ClaimsIdentity identity = new(claims, SchemeName, SangamUser.NameClaim, "role");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}

/// <summary>A fact that needs PostgreSQL; skipped when <c>SANGAM_TEST_CONNECTION</c> is unset.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresFactAttribute : FactAttribute
{
    /// <summary>Initialises the attribute.</summary>
    public PostgresFactAttribute()
    {
        if (!ImagiqaFactory.HasDatabase)
        {
            Skip = $"{ImagiqaFactory.ConnectionEnvironmentVariable} is not set; PostgreSQL-backed test skipped.";
        }
    }
}

/// <summary>Both test classes share one database; run them one at a time.</summary>
[CollectionDefinition("imagiqa-db")]
public sealed class ImagiqaDatabaseGroup
{
}

/// <summary>Builds people for tests.</summary>
internal static class People
{
    public static SangamMembership At(Guid hospital, string role, string name = "Apulki Medical Center")
        => new(hospital, name, "hospital", $"/{hospital:D}/", role, [], false);

    public static SangamUser Person(string name, params SangamMembership[] memberships)
        => new(Guid.NewGuid(), name, null, memberships);
}
