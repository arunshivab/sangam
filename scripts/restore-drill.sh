#!/usr/bin/env bash
# Sangam - restore drill (SGM-603): restore the newest backup into a scratch database, count rows in
# key tables, report the time taken. A backup that has never been restored is a hope, not a backup.
set -euo pipefail
BACKUP_DIR="${BACKUP_DIR:-/var/backups/sangam}"
CONTAINER="${CONTAINER:-sangam-db-postgres-1}"
SCRATCH="sangam_restore_drill"
latest="$(ls -1t "$BACKUP_DIR"/*.dump | head -n 1)"
sha256sum -c "$latest.sha256"
start="$(date +%s)"
docker exec "$CONTAINER" dropdb -U postgres --if-exists "$SCRATCH"
docker exec "$CONTAINER" createdb -U postgres "$SCRATCH"
docker exec -i "$CONTAINER" pg_restore -U postgres -d "$SCRATCH" --no-owner < "$latest"
for t in users audit_events org_memberships; do
  echo "$t: $(docker exec "$CONTAINER" psql -U postgres -d "$SCRATCH" -tAc "select count(*) from $t")"
done
docker exec "$CONTAINER" dropdb -U postgres "$SCRATCH"
echo "Restored $latest in $(( $(date +%s) - start )) s. Record this in SGM-603."
