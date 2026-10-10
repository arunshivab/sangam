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
        builder.UseSetting("Sangam:DataProtection:PersistKeys", "false");
        // rc.5: tests never call the Pwned Passwords service; the built-in list is the check.
        builder.UseSetting("Sangam:Passwords:BreachCheck:Endpoint", "");
        // OI-015: TestServer has no HTTPS port; the redirection middleware’s warning is expected here.
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.HttpsPolicy", "Error");
        builder.UseSetting("ConnectionStrings:Sangam", HasDatabase ? ServerTestConnection : SangamDbContextFactory.DevelopmentConnectionString);
        builder.UseSetting("Sangam:Issuer", string.Empty);
        builder.UseSetting("Sangam:Database:MigrateOnStartup", HasDatabase ? "true" : "false");
        builder.UseSetting("Sangam:Seed:DevelopmentSample", HasDatabase ? "true" : "false");
        builder.UseSetting("Sangam:Email:UseOutbox", "true");
        builder.UseSetting("Sangam:Sms:Enabled", "true");
        builder.UseSetting("Sangam:Sms:Provider", "outbox");
        builder.UseSetting("Sangam:RateLimit:PostsPerMinute", "1000");
        builder.UseSetting("Sangam:RateLimit:TokenPerMinute", "1000");
        builder.UseSetting("Sangam:RateLimit:UserInfoPerMinute", "1000");
        builder.UseSetting("Sangam:RateLimit:ApiPerMinute", "1000");
        builder.UseSetting("Sangam:Antibot:MinimumSeconds", "0");
        builder.UseSetting("Sangam:Otp:ResendCooldown", "00:00:00");
        builder.UseSetting("Sangam:Logout:DeliverInBackground", "false");
        builder.UseSetting("Sangam:Provisioning:DeliverInBackground", "false");
        builder.UseSetting("Sangam:PasswordHashing:MemoryKiB", "8192");
        builder.UseSetting("Sangam:PasswordHashing:Iterations", "2");
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Warning");
        builder.UseSetting("Logging:LogLevel:OpenIddict", "Warning");
        // PR-21: a spent refresh token reused at once is theft, here; no leeway to wait out in a test.
        builder.UseSetting("Sangam:Tokens:RefreshReuseLeewaySeconds", "0");
        if (HasDatabase)
        {
            // PR-21: registered from settings like a deployment's own (R4): a native app, a device app, an application
            // allowed to exchange tokens for imagiQa, and one that must use pushed authorization requests.
            builder.UseSetting("Sangam:Clients:native:ClientId", NativeClientId);
            builder.UseSetting("Sangam:Clients:native:Kind", "native");
            builder.UseSetting("Sangam:Clients:native:RedirectUris", NativeRedirectUri + ",http://127.0.0.1/callback,https://app.example.in/native/callback");
            builder.UseSetting("Sangam:Clients:device:ClientId", DeviceClientId);
            builder.UseSetting("Sangam:Clients:device:Kind", "device");
            builder.UseSetting("Sangam:Clients:exchanger:ClientId", ExchangerClientId);
            builder.UseSetting("Sangam:Clients:exchanger:BaseUrl", "https://exchanger.example.in/");
            builder.UseSetting("Sangam:Clients:exchanger:Secret", ExchangerSecret);
            builder.UseSetting("Sangam:Clients:exchanger:ExchangeAudiences", "imagiqa");
            builder.UseSetting("Sangam:Clients:paronly:ClientId", ParOnlyClientId);
            builder.UseSetting("Sangam:Clients:paronly:BaseUrl", "https://par.example.in/");
            builder.UseSetting("Sangam:Clients:paronly:Secret", ExchangerSecret);
            builder.UseSetting("Sangam:Clients:paronly:RequirePushedAuthorization", "true");
        }
    }

    /// <summary>PR-21 test native application.</summary>
    public const string NativeClientId = "test-native";

    /// <summary>Its private-use redirect address.</summary>
    public const string NativeRedirectUri = "in.sangamid.test:/callback";

    /// <summary>PR-21 test device application.</summary>
    public const string DeviceClientId = "test-device";

    /// <summary>PR-21 test application that may exchange tokens for imagiQa.</summary>
    public const string ExchangerClientId = "test-exchanger";

    /// <summary>Its secret (and the PAR-only application's).</summary>
    public const string ExchangerSecret = "exchanger-secret-0123456789abcdefghijklmn";

    /// <summary>PR-21 test application that must use pushed authorization requests.</summary>
    public const string ParOnlyClientId = "test-par-only";
}

/// <summary>Serialises the server test classes on one shared host.</summary>
[CollectionDefinition("server")]
public sealed class ServerTestGroup : ICollectionFixture<SangamServerFactory>
{
}
