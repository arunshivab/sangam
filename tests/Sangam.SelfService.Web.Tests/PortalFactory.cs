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
using Sangam.Shared.Constants;

namespace Sangam.SelfService.Web.Tests;

/// <summary>
/// Hosts account.sangamid.in in-process. The portal is normally an OpenID Connect client of
/// id.sangamid.in; these tests replace that one hop with a stub scheme so the screens and the
/// portal service can be exercised without a second host, exactly as the real cookie would.
/// </summary>
public sealed class PortalFactory : WebApplicationFactory<Program>
{
    public const string ConnectionEnvironmentVariable = "SANGAM_TEST_CONNECTION";
    public const string SchemeName = "TestPortalUser";

    /// <summary>The signed-in user the stub scheme issues; set by <see cref="SeedUserAsync"/>.</summary>
    public static Guid UserId { get; private set; }

    /// <summary>The Sangam session the stub cookie claims to come from.</summary>
    public static Guid SessionId { get; private set; }

    public static string? TestConnection => Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);

    public static bool HasDatabase => !string.IsNullOrWhiteSpace(TestConnection);

    /// <summary>The portal tests' own database, so they never race the other suites.</summary>
    public static string PortalTestConnection
    {
        get
        {
            Npgsql.NpgsqlConnectionStringBuilder builder = new(TestConnection);
            builder.Database += "_portal";
            return builder.ConnectionString;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Sangam", HasDatabase ? PortalTestConnection : SangamDbContextFactory.DevelopmentConnectionString);
        builder.UseSetting("Sangam:Authority", "https://id.example.invalid");
        builder.UseSetting("Sangam:Portal:ClientId", "sangam-portal");
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

    /// <summary>Creates the schema and a signed-in user with one linked app and two sessions.</summary>
    public async Task<(Guid AppId, Guid CurrentSession, Guid OtherSession)> SeedUserAsync()
    {
        using IServiceScope scope = Services.CreateScope();
        SangamDbContext db = scope.ServiceProvider.GetRequiredService<SangamDbContext>();
        await db.Database.MigrateAsync();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string email = $"portal-{Guid.NewGuid():N}@example.in";
        SangamUser user = new()
        {
            Id = Guid.NewGuid(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            FirstName = "Ravi",
            LastName = "Menon",
            DateOfBirth = new DateOnly(1986, 6, 12),
            Gender = Gender.Male,
            PhoneNumber = "+9198765" + Random.Shared.Next(10000, 99999).ToString(System.Globalization.CultureInfo.InvariantCulture),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now.AddDays(-40),
            UpdatedAt = now,
            LastPasswordChangeAt = now.AddDays(-40),
        };
        App app = new()
        {
            Id = Guid.NewGuid(),
            ClientId = "his-" + Guid.NewGuid().ToString("N")[..8],
            Slug = "his-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "LiPi HIS",
            OwnerCompanyName = "imagiQa Healthcare Services Pvt Ltd",
            Description = "Hospital information system",
            BrandColour = "#1D4E89",
            Glyph = "L",
            CreatedAt = now,
            UpdatedAt = now,
        };
        UserSession current = new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            AppId = app.Id,
            DeviceLabel = "First Floor Radiology",
            IpAddress = "10.4.2.37",
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36",
            SignInMode = SignInMode.Password,
            CreatedAt = now.AddHours(-2),
            LastSeenAt = now,
        };
        UserSession other = new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            IpAddress = "203.0.113.9",
            UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Version/17.0 Mobile Safari/604.1",
            SignInMode = SignInMode.PasswordAndOtp,
            CreatedAt = now.AddDays(-1),
            LastSeenAt = now.AddMinutes(-20),
        };

        db.Users.Add(user);
        db.Apps.Add(app);
        db.AppGrants.Add(new AppGrant { Id = Guid.NewGuid(), UserId = user.Id, AppId = app.Id, GrantedAt = now.AddDays(-10) });
        db.Consents.Add(new Sangam.Identity.Domain.Entities.Consent
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            AppId = app.Id,
            Scope = "openid profile email",
            ConsentVersion = "v1",
            GrantedAt = now.AddDays(-10),
        });
        db.UserSessions.AddRange(current, other);
        db.AuditEvents.Add(new AuditEvent
        {
            Action = Sangam.Identity.Domain.AuditActions.UserLoginSuccess,
            ActorType = AuditActorType.User,
            ActorUserId = user.Id,
            TargetType = "user",
            TargetId = user.Id,
            Metadata = "{\"mode\":\"password\"}",
            IpAddress = "10.4.2.37",
            OccurredAt = now.AddHours(-2),
        });
        await db.SaveChangesAsync();

        UserId = user.Id;
        SessionId = current.Id;
        return (app.Id, current.Id, other.Id);
    }

    private sealed class StubHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public StubHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (UserId == Guid.Empty || Request.Headers.ContainsKey("X-Test-Anonymous"))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            ClaimsIdentity identity = new(
            [
                new Claim("sub", UserId.ToString("D")),
                new Claim("name", "Ravi Menon"),
                new Claim("email", "ravi@example.in"),
                new Claim(SangamClaims.SessionId, SessionId.ToString("D")),
            ], SchemeName, "name", "role");

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
