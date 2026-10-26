-- Sangam - the database, its owner and one role per host (rc.16, ASVS V1.2.2). Run ONCE, as the database
-- administrator, before the first migration:
--
--   docker exec -i sangam-db-postgres-1 psql -v ON_ERROR_STOP=1 -U sangam_dba -d postgres < roles.sql
--
-- Then give each login role its password (never in a file in the repository), in the same psql as sangam_dba:
--
--   \password sangam_owner           -- runs the migrations (the migrator image), owns the tables
--   \password sangam_identity_app    -- identity server  -> secrets/ConnectionStrings__Sangam__identity
--   \password sangam_portal_app      -- account portal   -> secrets/ConnectionStrings__Sangam__portal
--   \password sangam_admin_app       -- operator console -> secrets/ConnectionStrings__Sangam__admin
--   \password sangam_partner_app     -- partner console  -> secrets/ConnectionStrings__Sangam__partner
--
-- The rights themselves are applied by the migration Rc16HostRoles (as sangam_owner). If the roles are made after that
-- migration has run, apply them once with grants.sql (same folder). Safe to run again: nothing is made twice.

DO $roles$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sangam_owner') THEN
        CREATE ROLE sangam_owner LOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sangam_app') THEN
        CREATE ROLE sangam_app NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sangam_identity_app') THEN
        CREATE ROLE sangam_identity_app LOGIN IN ROLE sangam_app;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sangam_portal_app') THEN
        CREATE ROLE sangam_portal_app LOGIN IN ROLE sangam_app;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sangam_admin_app') THEN
        CREATE ROLE sangam_admin_app LOGIN IN ROLE sangam_app;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sangam_partner_app') THEN
        CREATE ROLE sangam_partner_app LOGIN IN ROLE sangam_app;
    END IF;
END
$roles$;

-- Each host is named in pg_stat_activity and the server log, has its own connection limit, and cannot sit in an open
-- transaction for long.
ALTER ROLE sangam_identity_app CONNECTION LIMIT 60;
ALTER ROLE sangam_portal_app CONNECTION LIMIT 30;
ALTER ROLE sangam_admin_app CONNECTION LIMIT 15;
ALTER ROLE sangam_partner_app CONNECTION LIMIT 30;
ALTER ROLE sangam_identity_app SET application_name = 'sangam-identity';
ALTER ROLE sangam_portal_app SET application_name = 'sangam-portal';
ALTER ROLE sangam_admin_app SET application_name = 'sangam-admin';
ALTER ROLE sangam_partner_app SET application_name = 'sangam-partner';
ALTER ROLE sangam_app SET idle_in_transaction_session_timeout = '60s';
ALTER ROLE sangam_identity_app SET idle_in_transaction_session_timeout = '60s';
ALTER ROLE sangam_portal_app SET idle_in_transaction_session_timeout = '60s';
ALTER ROLE sangam_admin_app SET idle_in_transaction_session_timeout = '60s';
ALTER ROLE sangam_partner_app SET idle_in_transaction_session_timeout = '60s';

SELECT 'CREATE DATABASE sangam_identity WITH OWNER = sangam_owner ENCODING = ''UTF8'''
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'sangam_identity')
\gexec

REVOKE ALL ON DATABASE sangam_identity FROM PUBLIC;
GRANT CONNECT ON DATABASE sangam_identity TO sangam_owner, sangam_app;

\connect sangam_identity
-- Only the owner creates anything in the schema.
ALTER SCHEMA public OWNER TO sangam_owner;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO sangam_app;
