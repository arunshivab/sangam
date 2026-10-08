# Sangam production deployment (PR-10)

One E2E Networks VM in India (D-023), shared with Anjal. Three Docker Compose stacks, each separate
(OI-013): PostgreSQL (`postgres/`), Sangam (this folder), and Anjal (its own repository). Caddy here
terminates TLS for the four Sangam hosts. Decisions still open are marked **FOUNDER**.

## 1. Prepare

1. DNS: `id`, `account`, `admin` and `partners.sangamid.in` point at the VM.
2. Mail: `scripts/check-mail-dns.ps1 -Domain sangamid.in -DkimSelector <Anjal's selector>` passes. **FOUNDER (OI-027)**
3. Secrets: create every file listed in `secrets/README.md`.

## 2. Database

    docker compose -f postgres/docker-compose.yml up -d

Create two roles: the **owner** (runs migrations, owns the tables) and the **application** role (used by
the hosts; can read and write, cannot alter `audit_events`). The application role's connection string
goes in `secrets/ConnectionStrings__Sangam` (go-live checklist, "Added by R0").

## 3. Migrations — once per release, as the owner

    docker build --target migrator -t sangam/migrator ../..
    docker run --rm --network sangam-db -e CONNECTION="<owner connection string>" sangam/migrator

The bundle applies only migrations not yet applied; running it again does nothing.

## 4. Start

    docker compose up -d --build
    curl -fsS https://id.sangamid.in/health/ready

## 5. Operate

- Backups: `scripts/backup-db.sh` nightly from cron; `scripts/restore-drill.sh` monthly (SGM-603).
  The off-site copy target is **FOUNDER** (vendor choice).
- Telemetry: set `Sangam__Telemetry__OtlpEndpoint` to a collector. The collector and dashboards are
  **FOUNDER** (vendor choice).
- Audit retention: `Sangam__Audit__RetentionDays` stays unset until the periods are decided. **FOUNDER**
- PostgreSQL version: 18 is pinned in `postgres/docker-compose.yml`. **FOUNDER (OI-014)**

Not verified in Claude's environment (no Docker there): building these images, `docker compose up`,
and the Caddyfile under Caddy. The application side — certificates, secret files, health checks, the
migration bundle against an empty database — is covered by tests.
