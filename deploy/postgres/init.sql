-- Sangam — create the identity database and its owning role.
-- Idempotent: safe to re-run. Works in psql on Windows/macOS/Linux and as the
-- docker-entrypoint-initdb.d script in deploy/docker-compose.dev.yml.
--
--   Local (native PostgreSQL):  psql -U postgres -f deploy/postgres/init.sql
--   Or:                          .\scripts\bootstrap-dev.ps1 -InitDatabase
--
-- Phase 1 layout: one PostgreSQL instance, one database and one role per product
-- (Sangam and Anjal side by side), no cross-database access.
-- The development password below is for local use only; production sets a real
-- password with ALTER ROLE after this script has run (PR-08 runbook).

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'sangam_identity') THEN
        CREATE ROLE sangam_identity WITH LOGIN PASSWORD 'sangam_dev';
    END IF;
END
$$;

SELECT 'CREATE DATABASE sangam_identity WITH OWNER = sangam_identity ENCODING = ''UTF8'''
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'sangam_identity')
\gexec

-- Throwaway databases for the PostgreSQL-backed tests ([PostgresFact]). Point
-- SANGAM_TEST_CONNECTION at sangam_identity_test; each test suite with its own host derives
-- its own database from it by suffix: "_server" and "_portal" (Identity.Server tests),
-- "_admin" (operator console) and "_partner" (partner console), so no in-process host is
-- truncated under by the Infrastructure tests running in parallel. This file runs only when
-- the data volume is first created; on an existing volume, create a new one by hand.
SELECT 'CREATE DATABASE sangam_identity_test WITH OWNER = sangam_identity ENCODING = ''UTF8'''
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'sangam_identity_test')
\gexec

SELECT 'CREATE DATABASE sangam_identity_test_server WITH OWNER = sangam_identity ENCODING = ''UTF8'''
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'sangam_identity_test_server')
\gexec

SELECT 'CREATE DATABASE sangam_identity_test_portal WITH OWNER = sangam_identity ENCODING = ''UTF8'''
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'sangam_identity_test_portal')
\gexec

SELECT 'CREATE DATABASE sangam_identity_test_admin WITH OWNER = sangam_identity ENCODING = ''UTF8'''
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'sangam_identity_test_admin')
\gexec

SELECT 'CREATE DATABASE sangam_identity_test_partner WITH OWNER = sangam_identity ENCODING = ''UTF8'''
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'sangam_identity_test_partner')
\gexec

REVOKE CONNECT ON DATABASE sangam_identity FROM PUBLIC;
GRANT CONNECT ON DATABASE sangam_identity TO sangam_identity;
REVOKE CONNECT ON DATABASE sangam_identity_test FROM PUBLIC;
GRANT CONNECT ON DATABASE sangam_identity_test TO sangam_identity;
REVOKE CONNECT ON DATABASE sangam_identity_test_server FROM PUBLIC;
GRANT CONNECT ON DATABASE sangam_identity_test_server TO sangam_identity;
REVOKE CONNECT ON DATABASE sangam_identity_test_portal FROM PUBLIC;
GRANT CONNECT ON DATABASE sangam_identity_test_portal TO sangam_identity;
REVOKE CONNECT ON DATABASE sangam_identity_test_admin FROM PUBLIC;
GRANT CONNECT ON DATABASE sangam_identity_test_admin TO sangam_identity;
REVOKE CONNECT ON DATABASE sangam_identity_test_partner FROM PUBLIC;
GRANT CONNECT ON DATABASE sangam_identity_test_partner TO sangam_identity;
