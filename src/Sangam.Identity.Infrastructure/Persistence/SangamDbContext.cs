using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence;

/// <summary>
/// The single identity database. ASP.NET Core Identity user tables (no Identity roles —
/// platform operators and app admins have their own tables), Sangam's tenancy tables, and
/// OpenIddict's application / authorization / scope / token tables. All names snake_case.
/// </summary>
public sealed class SangamDbContext : IdentityUserContext<SangamUser, Guid>
{
    /// <summary>Initialises the context.</summary>
    /// <param name="options">EF Core options.</param>
    public SangamDbContext(DbContextOptions<SangamDbContext> options)
        : base(options)
    {
    }

    /// <summary>Organisation types (reference data).</summary>
    public DbSet<OrgType> OrgTypes => Set<OrgType>();

    /// <summary>Organisations.</summary>
    public DbSet<Organisation> Organisations => Set<Organisation>();

    /// <summary>Partner apps.</summary>
    public DbSet<App> Apps => Set<App>();

    /// <summary>App-defined roles.</summary>
    public DbSet<Role> Roles => Set<Role>();

    /// <summary>Organisation memberships.</summary>
    public DbSet<OrgMembership> OrgMemberships => Set<OrgMembership>();

    /// <summary>App grants.</summary>
    public DbSet<AppGrant> AppGrants => Set<AppGrant>();

    /// <summary>Consents.</summary>
    public DbSet<Consent> Consents => Set<Consent>();

    /// <summary>Platform operators.</summary>
    public DbSet<PlatformOperator> PlatformOperators => Set<PlatformOperator>();

    /// <summary>App admins.</summary>
    public DbSet<AppAdmin> AppAdmins => Set<AppAdmin>();

    /// <summary>Audit events (append-only).</summary>
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    /// <summary>One-time codes (email verification, password reset, sign-in).</summary>
    public DbSet<OneTimeCode> OneTimeCodes => Set<OneTimeCode>();

    /// <summary>Browser sessions.</summary>
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(AssemblyReference.Assembly);
        builder.UseOpenIddict();

        // OpenIddict names its tables explicitly, which bypasses the snake_case convention.
        builder.Entity<OpenIddictEntityFrameworkCoreApplication>().ToTable("openiddict_applications");
        builder.Entity<OpenIddictEntityFrameworkCoreAuthorization>().ToTable("openiddict_authorizations");
        builder.Entity<OpenIddictEntityFrameworkCoreScope>().ToTable("openiddict_scopes");
        builder.Entity<OpenIddictEntityFrameworkCoreToken>().ToTable("openiddict_tokens");
    }
}
