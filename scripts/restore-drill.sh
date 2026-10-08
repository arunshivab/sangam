#!/usr/bin/env bash
# Sangam - restore drill (SGM-603, D-E): restore a backup into a scratch database, count rows in key tables,
# report the time taken. A backup that has never been restored is a hope, not a backup.
#   restore-drill.sh                           the newest local backup
#   restore-drill.sh --offsite <target-name>   the newest daily copy in that off-site bucket (the other region),
#                                              decrypted with the founder's age key, brought to the drill:
#                                              AGE_IDENTITY=/path/to/key.txt (removed from the server afterwards)
set -euo pipefail
BACKUP_DIR="${BACKUP_DIR:-/var/backups/sangam}"
CONTAINER="${CONTAINER:-sangam-db-postgres-1}"
OFFSITE_TARGETS_DIR="${OFFSITE_TARGETS_DIR:-/etc/sangam/backup-targets}"
SCRATCH="sangam_restore_drill"
# D-H: the result goes to the monitoring page.
STATUS_DIR="${STATUS_DIR:-/srv/sangam/status}"
mkdir -p "$STATUS_DIR"
status() { printf '{"at":"%s","ok":%s,"detail":"%s"}\n' "$(date -u +%FT%TZ)" "$1" "$2" > "$STATUS_DIR/restore-drill.json.tmp" && mv "$STATUS_DIR/restore-drill.json.tmp" "$STATUS_DIR/restore-drill.json"; }
trap 'status false "restore-drill.sh failed at line $LINENO"' ERR
start="$(date +%s)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
if [ "${1:-}" = "--offsite" ]; then
  target="$OFFSITE_TARGETS_DIR/${2:?which off-site target}.env"
  [ -n "${AGE_IDENTITY:-}" ] && [ -s "$AGE_IDENTITY" ] || { echo "AGE_IDENTITY must point at the founder's age key for the drill."; false; }
  key="$( set -a; . "$target"; set +a
    aws s3api list-objects-v2 --endpoint-url "$ENDPOINT" --region "${REGION:-auto}" --bucket "$BUCKET" --prefix daily/ \
      --query 'sort_by(Contents, &LastModified)[-1].Key' --output text )"
  ( set -a; . "$target"; set +a
    aws s3api get-object --endpoint-url "$ENDPOINT" --region "${REGION:-auto}" --bucket "$BUCKET" --key "$key" "$work/backup.dump.age" > /dev/null )
  age --decrypt -i "$AGE_IDENTITY" -o "$work/backup.dump" "$work/backup.dump.age"
  latest="$work/backup.dump"
  source="off-site $(basename "$target" .env):$key"
else
  latest="$(ls -1t "$BACKUP_DIR"/*.dump | head -n 1)"
  sha256sum -c "$latest.sha256"
  source="$(basename "$latest")"
fi
docker exec "$CONTAINER" dropdb -U postgres --if-exists "$SCRATCH"
docker exec "$CONTAINER" createdb -U postgres "$SCRATCH"
docker exec -i "$CONTAINER" pg_restore -U postgres -d "$SCRATCH" --no-owner < "$latest"
counts=""
for t in users audit_events org_memberships; do
  n="$(docker exec "$CONTAINER" psql -U postgres -d "$SCRATCH" -tAc "select count(*) from $t")"
  echo "$t: $n"; counts="$counts $t=$n"
done
docker exec "$CONTAINER" dropdb -U postgres "$SCRATCH"
echo "Restored $source in $(( $(date +%s) - start )) s. Record this in SGM-603."
status true "$source restored in $(( $(date +%s) - start )) s;$counts"
