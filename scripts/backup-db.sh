#!/usr/bin/env bash
# Sangam - nightly backup (PR-10, SGM-603; D-E, D-H). Run from cron on the VM:
#   15 2 * * * /opt/sangam/scripts/backup-db.sh >> /var/log/sangam-backup.log 2>&1
#
# 1. pg_dump of the database, kept on the server for KEEP_DAYS.
# 2. The day's container logs (D-H: kept 180 days, inside the backups).
# 3. D-E: everything leaving the server is encrypted first with `age` for the founder's public key
#    (BACKUP_AGE_RECIPIENTS: a file of age public keys; the private key stays offline with the founder),
#    then uploaded to E2E Object Storage in another region, with S3 Object Lock (COMPLIANCE) so that nobody —
#    not even someone holding Sangam's storage keys — can delete or overwrite a copy before its time:
#      daily/    every night            locked 14 days
#      weekly/   Sundays                locked 8 weeks
#      monthly/  the 1st of each month  locked 12 months
#      logs/     every night            locked 180 days
#      audit-archive/  each new D-A archive file (already encrypted for the founder)  locked 6 years — its events
#                      are a year old when archived, so the copy goes when they turn seven, as D-A says
#    The bucket's lifecycle rules (deploy/production/backup/lifecycle.json) delete each copy once its lock has run out.
# Each off-site target is one file /etc/sangam/backup-targets/<name>.env (OFFSITE_TARGETS_DIR) with
#   ENDPOINT=https://...  BUCKET=...  REGION=...  AWS_ACCESS_KEY_ID=...  AWS_SECRET_ACCESS_KEY=...
# A second provider (D-E, before hospital go-live) is a second file there; nothing else changes.
set -euo pipefail
BACKUP_DIR="${BACKUP_DIR:-/var/backups/sangam}"
# D-H: the result goes to the monitoring page (mounted read-only into the hosts as /var/lib/sangam/status).
STATUS_DIR="${STATUS_DIR:-/srv/sangam/status}"
mkdir -p "$STATUS_DIR"
status() { printf '{"at":"%s","ok":%s,"detail":"%s"}\n' "$(date -u +%FT%TZ)" "$1" "$2" > "$STATUS_DIR/backup.json.tmp" && mv "$STATUS_DIR/backup.json.tmp" "$STATUS_DIR/backup.json"; }
trap 'status false "backup-db.sh failed at line $LINENO"' ERR
KEEP_DAYS="${KEEP_DAYS:-14}"
CONTAINER="${CONTAINER:-sangam-db-postgres-1}"
DATABASE="${DATABASE:-sangam_identity}"
COMPOSE_DIR="${COMPOSE_DIR:-/opt/sangam/deploy/production}"
AUDIT_ARCHIVE_DIR="${AUDIT_ARCHIVE_DIR:-/srv/sangam/audit-archive}"
OFFSITE_TARGETS_DIR="${OFFSITE_TARGETS_DIR:-/etc/sangam/backup-targets}"
BACKUP_AGE_RECIPIENTS="${BACKUP_AGE_RECIPIENTS:-/etc/sangam/backup-recipients.txt}"
NOW="${BACKUP_NOW:-$(date -u +%FT%TZ)}"   # tests pin the date
mkdir -p "$BACKUP_DIR"
stamp="$(date -u -d "$NOW" +%Y%m%dT%H%M%SZ)"

# 1. The database.
file="$BACKUP_DIR/${DATABASE}_${stamp}.dump"
docker exec "$CONTAINER" pg_dump -U postgres -Fc "$DATABASE" > "$file"
sha256sum "$file" > "$file.sha256"

# 2. The day's logs from every Sangam container.
logs="$BACKUP_DIR/logs_${stamp}.txt.gz"
(cd "$COMPOSE_DIR" && docker compose logs --no-color --timestamps --since 25h) | gzip -9 > "$logs"

find "$BACKUP_DIR" \( -name "${DATABASE}_*.dump*" -o -name "logs_*.txt.gz" \) -mtime +"$KEEP_DAYS" -delete
echo "$(date -u +%FT%TZ) backup written: $file ($(stat -c %s "$file") bytes)"

# 3. Off-site, encrypted, locked.
offsite="no off-site target configured"
if compgen -G "$OFFSITE_TARGETS_DIR/*.env" > /dev/null; then
  [ -s "$BACKUP_AGE_RECIPIENTS" ] || { echo "No age recipients in $BACKUP_AGE_RECIPIENTS: refusing to upload unencrypted."; false; }
  enc_dump="$file.age"; enc_logs="$logs.age"
  age --encrypt -R "$BACKUP_AGE_RECIPIENTS" -o "$enc_dump" "$file"
  age --encrypt -R "$BACKUP_AGE_RECIPIENTS" -o "$enc_logs" "$logs"
  day="$(date -u -d "$NOW" +%F)"
  until_date() { date -u -d "$NOW + $1" +%FT%TZ; }
  put() { # target-env key file lock-period
    local md5; md5="$(openssl dgst -md5 -binary "$3" | base64)"
    ( set -a; . "$1"; set +a
      aws s3api put-object --endpoint-url "$ENDPOINT" --region "${REGION:-auto}" --bucket "$BUCKET" --key "$2" --body "$3" \
        --content-md5 "$md5" --object-lock-mode COMPLIANCE --object-lock-retain-until-date "$(until_date "$4")" > /dev/null )
  }
  names=()
  for target in "$OFFSITE_TARGETS_DIR"/*.env; do
    name="$(basename "$target" .env)"
    put "$target" "daily/$day/$(basename "$enc_dump")" "$enc_dump" "14 days"
    put "$target" "logs/$day/$(basename "$enc_logs")" "$enc_logs" "180 days"
    if [ "$(date -u -d "$NOW" +%u)" = "7" ]; then put "$target" "weekly/$day/$(basename "$enc_dump")" "$enc_dump" "8 weeks"; fi
    if [ "$(date -u -d "$NOW" +%d)" = "01" ]; then put "$target" "monthly/$day/$(basename "$enc_dump")" "$enc_dump" "12 months"; fi
    # D-A's archive files, each uploaded once (a marker per target remembers which); their events are a year old
    # already, so six more years makes seven.
    if [ -d "$AUDIT_ARCHIVE_DIR" ]; then
      mkdir -p "$BACKUP_DIR/uploaded-$name"
      for archive in "$AUDIT_ARCHIVE_DIR"/*.sgmaud; do
        [ -e "$archive" ] || continue
        marker="$BACKUP_DIR/uploaded-$name/$(basename "$archive")"
        [ -e "$marker" ] && continue
        put "$target" "audit-archive/$(basename "$archive")" "$archive" "6 years" && touch "$marker"
      done
    fi
    names+=("$name")
  done
  rm -f "$enc_dump" "$enc_logs"
  offsite="encrypted copy locked off-site at ${names[*]}"
fi
status true "$(basename "$file"), $(stat -c %s "$file") bytes; $offsite"
