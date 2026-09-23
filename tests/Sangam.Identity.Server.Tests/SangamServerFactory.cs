using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Server.Tests;

/// <summary>
/// Hosts id.sangamid.in in-process under the "Testing" environment. Shared by every test class
/// in the "server" collection so the schema is migrated and the sample app seeded exactly once.
/// Without <c>SANGAM_TEST_CONNECTION</c> the host starts but never touches a database
/// (discovery and JWKS need none). With it, the host uses a database derived from that
/// connection string by appending <c>_server</c> to the database name
/// (sangam_identity_test → sangam_identity_test_server), so the Infrastructure tests that
/// truncate sangam_identity_test in parallel can never pull the seeded client out from under
/// the token tests.
/// </summary>
public sealed class SangamServerFactory : WebApplicationFactory<Program>
{
    public const string ConnectionEnvironmentVariable = "SANGAM_TEST_CONNECTION";

    public static string? TestConnection => Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);

    public static bool HasDatabase => !string.IsNullOrWhiteSpace(TestConnection);

    /// <summary>The server tests' own database: the test connection with <c>_server</c> appended to the database name.</summary>
    public static string ServerTestConnection
    {
        get
        {
            NpgsqlConnectionStringBuilder builder = new(TestConnection);
            builder.Database += "_server";
            return builder.ConnectionString;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Sangam", HasDatabase ? ServerTestConnection : SangamDbContextFactory.DevelopmentConnectionString);
        builder.UseSetting("Sangam:Issuer", string.Empty);
        builder.UseSetting("Sangam:Database:MigrateOnStartup", HasDatabase ? "true" : "false");
        builder.UseSetting("Sangam:Seed:DevelopmentSample", HasDatabase ? "true" : "false");
    }
}

/// <summary>Serialises the server test classes on one shared host.</summary>
[CollectionDefinition("server")]
public sealed class ServerTestGroup : ICollectionFixture<SangamServerFactory>
{
}
