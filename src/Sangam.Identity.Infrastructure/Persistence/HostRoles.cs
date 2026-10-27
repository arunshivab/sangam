namespace Sangam.Identity.Infrastructure.Persistence;

/// <summary>
/// rc.16 (ASVS V1.2.2, founder decision of 26 Oct 2026): each host signs in to PostgreSQL as its own role, with only the
/// rights it needs. The four login roles share the group <see cref="Group"/>, which may read and write the application's
/// tables but may only add to the audit log and never write the migration history. The identity server alone may also
/// delete archived audit events (the maintenance sweep runs only there) and touch the tables only its sign-in endpoints
/// use (<see cref="IdentityOnlyTables"/>).
/// <para>
/// The roles are made once by the database administrator (<c>deploy/production/postgres/roles.sql</c>); the grants are
/// applied by migration <c>Rc16HostRoles</c> as the schema owner, and again by <c>grants.sql</c> if the roles are made
/// after the migration. Tables a later migration creates are granted to <see cref="Group"/> automatically (default
/// privileges of the owner). Where the roles do not exist (development, tests) the grants do nothing.
/// </para>
/// </summary>
public static class HostRoles
{
    /// <summary>The group every host's login role belongs to.</summary>
    public const string Group = "sangam_app";

    /// <summary>The identity server's login role.</summary>
    public const string Identity = "sangam_identity_app";

    /// <summary>The account portal's login role.</summary>
    public const string Portal = "sangam_portal_app";

    /// <summary>The operator console's login role.</summary>
    public const string Admin = "sangam_admin_app";

    /// <summary>The partner console's login role.</summary>
    public const string Partner = "sangam_partner_app";

    /// <summary>The role that owns the schema and runs the migrations.</summary>
    public const string Owner = "sangam_owner";

    /// <summary>
    /// Tables only the identity server's own endpoints use: single-use client assertions (private_key_jwt, rc.15) and
    /// pending SAML requests. The consoles and the portal have no right on them at all.
    /// </summary>
    public static readonly IReadOnlyList<string> IdentityOnlyTables = ["client_assertion_uses", "saml_requests"];

    /// <summary>
    /// The grants, as one idempotent statement. Run by the schema owner (or a superuser) in Sangam's database; does
    /// nothing when the roles have not been made.
    /// </summary>
    public const string GrantsSql = """
        DO $grants$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sangam_app')
                OR NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sangam_identity_app') THEN
                RAISE NOTICE 'Sangam host roles are not made here (roles.sql); no grants applied.';
                RETURN;
            END IF;

            -- Every host: read and write the application's tables, and use their sequences.
            GRANT USAGE ON SCHEMA public TO sangam_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO sangam_app;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO sangam_app;

            -- Tables later migrations make (as this same owner) are granted the same way.
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO sangam_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO sangam_app;

            -- No host writes the migration history; only the owner migrates.
            REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON "__EFMigrationsHistory" FROM sangam_app;

            -- The audit log: every host adds to it and reads it; none changes it. Only the identity server, where the
            -- maintenance sweep runs, deletes events once they are archived (a year old; D-A), and its trigger still
            -- refuses unless the archive switches maintenance on.
            REVOKE UPDATE, DELETE, TRUNCATE ON audit_events FROM sangam_app;
            GRANT DELETE ON audit_events TO sangam_identity_app;

            -- The identity server's own tables.
            REVOKE ALL ON client_assertion_uses, saml_requests FROM sangam_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON client_assertion_uses, saml_requests TO sangam_identity_app;
        END
        $grants$;
        """;
}
