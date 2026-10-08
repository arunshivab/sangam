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

- **Backups:** `scripts/backup-db.sh` nightly from cron; `scripts/restore-drill.sh` monthly (SGM-603). Both write
  their result to `/srv/sangam/status` (`backup.json`, `restore-drill.json`), which the hosts read for the monitoring
  page. D-E (R4): encrypted before upload, to E2E Object Storage in another region with object lock; a second
  provider before hospital go-live.
- **Monitoring (D-H):** no third party. Every host records its metrics into the database; the operator console's
  *Monitoring* page shows them; the identity server (`Sangam__Monitoring__Evaluate=true`) alerts the founder by
  e-mail and SMS through Anjal. Anjal's server runs the outside watchdog against `/health/ready`; Sangam watches
  Anjal through `Sangam__Monitoring__AnjalHealthUrl`. When Anjal itself is down, Sangam cannot send through it: the
  page shows it, and Anjal's own watchdog is the alert path.
- **Logs:** the `local` logging driver, rotated (50 MB × 20 per container). They stay on the server and go into the
  encrypted backups, kept 180 days (D-H; backup job in R4).
- **Breached-password list (D-J):** download the Pwned Passwords SHA-1 list with the official downloader, then

      mkdir -p /srv/sangam/pwned
      docker compose run --rm -v /srv/sangam/pwned:/var/lib/sangam/pwned -v /path/to/download:/download:ro \
        identity breach-list import --source /download/pwnedpasswords.txt \
        --output /var/lib/sangam/pwned/pwned-passwords.bin --date 2026-10-01

  The running hosts pick the new list up within a minute; the monitoring page shows its date. Switch the check on
  with `Sangam__Passwords__BreachCheck__Enabled=true` only after the import. Refresh every few months.
- **Audit retention:** `Sangam__Audit__RetentionDays` stays unset until D-A's archive (1 year live, then an
  encrypted archive in India, purged at 7 years) is built in R4.
- **PostgreSQL:** 18 everywhere (D-F).

Not verified in Claude's environment: the Caddyfile under Caddy, and Anjal's real API. The images build and the
identity server starts in Production in Docker (release evidence); certificates, secret files, health checks and the
migration bundle against an empty database are covered by tests.
