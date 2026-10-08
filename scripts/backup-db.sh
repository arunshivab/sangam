#!/usr/bin/env bash
# Sangam - nightly database backup (PR-10, SGM-603). Run from cron on the VM:
#   15 2 * * * /opt/sangam/scripts/backup-db.sh >> /var/log/sangam-backup.log 2>&1
set -euo pipefail
BACKUP_DIR="${BACKUP_DIR:-/var/backups/sangam}"
KEEP_DAYS="${KEEP_DAYS:-14}"
CONTAINER="${CONTAINER:-sangam-db-postgres-1}"
DATABASE="${DATABASE:-sangam_identity}"
mkdir -p "$BACKUP_DIR"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
file="$BACKUP_DIR/${DATABASE}_${stamp}.dump"
docker exec "$CONTAINER" pg_dump -U postgres -Fc "$DATABASE" > "$file"
sha256sum "$file" > "$file.sha256"
find "$BACKUP_DIR" -name "${DATABASE}_*.dump*" -mtime +"$KEEP_DAYS" -delete
echo "$(date -u +%FT%TZ) backup written: $file ($(stat -c %s "$file") bytes)"
# Off-site copy: the target is the founder's choice (vendor). Add it here, for example:
#   rclone copy "$file" remote:sangam-backups/
