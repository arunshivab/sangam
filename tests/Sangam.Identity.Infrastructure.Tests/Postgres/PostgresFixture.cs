using Microsoft.EntityFrameworkCore;
using Sangam.Identity.Infrastructure.Persistence;

namespace Sangam.Identity.Infrastructure.Tests.Postgres;

/// <summary>
/// One migrated database per test run, shared by every class in the "postgres" collection
/// (which serialises them). <see cref="ResetAsync"/> truncates the mutable tables between tests.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly string[] MutableTables =
    [
        "audit_events",
        "data_protection_keys",
        "email_change_requests",
        "invitations",
        "passkey_credentials",
        "passkey_challenges",
        "user_passkeys",
        "mfa_reset_requests",
        "metric_points",
        "monitoring_alerts",
        "host_reports",
        "unknown_address_attempts",
        "dev_outbox",
        "grievance_entries",
        "grievances",
        "identity_verifications",
        "user_attribute_values",
        "user_attribute_definitions",
        "app_claim_mappings",
        "webhook_deliveries",
        "webhook_endpoints",
        "scim_deliveries",
        "scim_group_members",
        "scim_group_links",
        "scim_user_links",
        "scim_targets",
        "app_events",
        "saml_requests",
        "saml_service_providers",
        "sms_messages",
        "customisations",
        "branding_assets",
        "message_templates",
        "logout_notifications",
        "session_apps",
        "signature_requests",
        "pending_memberships",
        "staff_invites",
        "join_codes",
        "staff_checks",
        "shared_device_people",
        "shared_devices",
        "user_pins",
        "emergency_accesses",
        "org_admins",
        "org_memberships",
        "app_subjects",
        "app_grants",
        "consents",
        "client_secrets",
        "client_keys",
        "client_assertion_uses",
        "access_review_decisions",
        "app_admins",
        "platform_operators",
        "roles",
        "organisations",
        "apps",
        "user_tokens",
        "user_logins",
        "user_claims",
        "users",
        "login_id_releases",
        "openiddict_tokens",
        "openiddict_authorizations",
        "openiddict_applications",
        "openiddict_scopes",
    ];

    public string ConnectionString { get; } = PostgresFactAttribute.ConnectionString ?? string.Empty;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(ConnectionString);

    public SangamDbContext CreateContext()
    {
        DbContextOptionsBuilder<SangamDbContext> builder = new();
        SangamDbContextOptions.Configure(builder, ConnectionString);
        return new SangamDbContext(builder.Options);
    }

    /// <summary>rc.14: the id the application knows the person by (given out now if it has none).</summary>
    /// <param name="appId">The application.</param>
    /// <param name="userId">The person's Sangam id.</param>
    public async Task<Guid> SubjectAsync(Guid appId, Guid userId)
    {
        await using SangamDbContext db = CreateContext();
        return await Sangam.Identity.Infrastructure.Apps.PairwiseSubjects.ForAsync(db, appId, userId);
    }

    public async Task InitializeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        await using SangamDbContext db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Empties every non-reference table. audit_events refuses TRUNCATE unless the session sets
    /// sangam.audit_maintenance, which this test-only reset does inside one transaction (OI-039).
    /// </summary>
    public async Task ResetAsync()
    {
        await using SangamDbContext db = CreateContext();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT set_config('sangam.audit_maintenance', 'on', true);");
        string sql = "TRUNCATE TABLE " + string.Join(", ", MutableTables) + " RESTART IDENTITY CASCADE;";
        await db.Database.ExecuteSqlRawAsync(sql);
        await tx.CommitAsync();
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
}
