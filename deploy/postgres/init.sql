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

REVOKE CONNECT ON DATABASE sangam_identity FROM PUBLIC;
GRANT CONNECT ON DATABASE sangam_identity TO sangam_identity;
