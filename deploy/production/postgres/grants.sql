-- Sangam - each host's database rights (rc.16, ASVS V1.2.2). The migration Rc16HostRoles applies exactly this, as the
-- schema owner; run it by hand only if the roles (roles.sql) were made after that migration ran:
--
--   docker exec -i sangam-db-postgres-1 psql -v ON_ERROR_STOP=1 -U sangam_owner -d sangam_identity < grants.sql
--
-- Kept identical to HostRoles.GrantsSql by a test (HostRolesTests). Safe to run again.

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
