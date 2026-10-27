#!/usr/bin/env bash
# Sangam - deploy one release on the E2E VM (rc.16, ASVS V1.14.4 and V14.1.1). Replaces the hand-typed runbook steps
# of deploy/production/README.md sections 3 and 4 with one script that checks before it changes anything:
#
#   sudo /opt/sangam/scripts/deploy.sh v1.0.0-rc.16            # the release tag to run
#   sudo /opt/sangam/scripts/deploy.sh v1.0.0-rc.16 --saml     # also docker-compose.saml.yml
#   sudo /opt/sangam/scripts/deploy.sh v1.0.0-rc.16 --digilocker
#   sudo /opt/sangam/scripts/deploy.sh v1.0.0-rc.16 --logs       # also docker-compose.logs.yml (logs off the server)
#   sudo /opt/sangam/scripts/deploy.sh --check                 # the checks only, on what is checked out now
#
# In order, stopping at the first problem:
#   1. Checks, on the tag's own files: run as root; the tag exists; the secrets folder is root's alone (700) and holds
#      every file the stacks name, none empty and none writable but by root; no "SET-" placeholder is left in the
#      settings; the owner's connection string file is root's alone (600); the database is healthy. If a check fails,
#      the previous checkout is put back and nothing else is touched.
#   2. A backup (scripts/backup-db.sh), so a release whose migration goes wrong can be restored.
#   3. The images are built from the tag, then the migrator applies its migrations as the schema owner, and the
#      database is asked whether each host's role has the rights it should (rc.16, postgres/roles.sql).
#   4. The stack starts, and each host must be healthy and answer /health/ready (it reaches the database) within five
#      minutes.
#   5. The result is written to /srv/sangam/status/deploy.json for the monitoring page.
# Rolling back is the same command with the previous tag, for releases from rc.16 on; migrations only move forward, so a
# release that needs its database back is restored from the backup taken in step 2 (scripts/restore-drill.sh shows how
# a dump is restored). A release before rc.16 used one shared connection string and the "postgres" administrator: go
# back to one by restoring that backup and following that release's own README.
set -euo pipefail

REPO="${REPO:-/opt/sangam}"
COMPOSE_DIR="$REPO/deploy/production"
SECRETS_DIR="$COMPOSE_DIR/secrets"
# The schema owner's connection string (sangam_owner, postgres/roles.sql), one line; never in the secrets folder,
# because no container may have it.
OWNER_CONNECTION_FILE="${OWNER_CONNECTION_FILE:-/etc/sangam/owner-connection}"
STATUS_DIR="${STATUS_DIR:-/srv/sangam/status}"
DB_CONTAINER="${DB_CONTAINER:-sangam-db-postgres-1}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-300}"

tag=""
check_only=false
files=(-f docker-compose.yml)
for arg in "$@"; do
  case "$arg" in
    --check) check_only=true ;;
    --saml) files+=(-f docker-compose.saml.yml) ;;
    --digilocker) files+=(-f docker-compose.digilocker.yml) ;;
    --logs) files+=(-f docker-compose.logs.yml) ;;
    v*) tag="$arg" ;;
    *) echo "Unknown argument: $arg"; exit 2 ;;
  esac
done

problems=()
problem() { problems+=("$1"); echo "PROBLEM: $1"; }
status() {
  mkdir -p "$STATUS_DIR"
  printf '{"at":"%s","ok":%s,"tag":"%s","commit":"%s","detail":"%s"}\n' "$(date -u +%FT%TZ)" "$1" "$tag" "$(git -C "$REPO" rev-parse --short HEAD 2>/dev/null || echo unknown)" "$2" \
    > "$STATUS_DIR/deploy.json.tmp" && mv "$STATUS_DIR/deploy.json.tmp" "$STATUS_DIR/deploy.json"
}
compose() { (cd "$COMPOSE_DIR" && docker compose "${files[@]}" "$@"); }

# ---------------------------------------------------------------------------------------------------- 1. checks
[ "$(id -u)" -eq 0 ] || { echo "Run as root (sudo): the secrets folder is root's alone."; exit 1; }

if [ -n "$tag" ]; then
  git -C "$REPO" fetch --tags --quiet
  git -C "$REPO" rev-parse --verify --quiet "refs/tags/$tag" > /dev/null || { echo "No such tag: $tag"; exit 1; }
elif ! $check_only; then
  echo "Name the release tag to deploy, for example v1.0.0-rc.16 (or --check)."; exit 2
fi

# The checks read the tag's own files, so the tag is checked out first; if a check fails, the previous checkout is put
# back, so a refused deployment leaves the server exactly as it was.
previous=""
if [ -n "$tag" ] && ! $check_only; then
  if [ -n "$(git -C "$REPO" status --porcelain --untracked-files=no)" ]; then
    problem "the repository at $REPO has local changes; the server runs only what a tag says"
  else
    previous="$(git -C "$REPO" rev-parse HEAD)"
    git -C "$REPO" checkout --quiet "refs/tags/$tag"
  fi
fi

# The secrets folder: root's, 700, so no other user on the server can reach a secret (ASVS V2.10.3). The files inside
# are read by the containers' own non-root users through Docker's bind mounts, so they are root's and readable
# (644 or 444) but never writable by anyone but root.
if [ ! -d "$SECRETS_DIR" ]; then
  problem "no secrets folder at $SECRETS_DIR"
else
  [ "$(stat -c '%U %a' "$SECRETS_DIR")" = "root 700" ] || problem "$SECRETS_DIR must be owned by root with mode 700 (chmod 700)"
  while IFS= read -r -d '' f; do
    name="$(basename "$f")"
    [ "$name" = "README.md" ] && continue
    perms="$(stat -c '%U %a' "$f")"
    [ "$perms" = "root 644" ] || [ "$perms" = "root 444" ] || problem "secret $name must be owned by root with mode 644 or 444 (is: $perms); the folder's 700 keeps it from other users"
    [ -s "$f" ] || problem "secret $name is empty"
  done < <(find "$SECRETS_DIR" -maxdepth 1 -type f -print0)

  needed="$(compose config --format json | python3 -c 'import json,sys; [print(v["file"].rsplit("/",1)[-1]) for v in (json.load(sys.stdin).get("secrets") or {}).values() if v.get("file")]')"
  for name in $needed; do
    [ -f "$SECRETS_DIR/$name" ] || problem "missing secret file: $name (deploy/production/secrets/README.md says what it holds)"
  done
fi

# Placeholders the founder fills in before go-live (SET-ANJAL-API-URL and the like).
placeholders="$(compose config 2>/dev/null | grep -oE 'SET-[A-Z0-9-]+' | sort -u | tr '\n' ' ' || true)"
[ -z "$placeholders" ] || problem "settings still to fill in, in docker-compose.yml: $placeholders"

if ! $check_only; then
  if [ ! -f "$OWNER_CONNECTION_FILE" ]; then
    problem "no owner connection string at $OWNER_CONNECTION_FILE (sangam_owner; see deploy/production/README.md)"
  else
    [ "$(stat -c '%U %a' "$OWNER_CONNECTION_FILE")" = "root 600" ] || problem "$OWNER_CONNECTION_FILE must be owned by root with mode 600"
  fi
fi

health="$(docker inspect -f '{{.State.Health.Status}}' "$DB_CONTAINER" 2>/dev/null)" || health="missing"
[ "$health" = "healthy" ] || problem "the database container $DB_CONTAINER is $health (start it: docker compose -f postgres/docker-compose.yml up -d)"

if [ "${#problems[@]}" -gt 0 ]; then
  [ -n "$previous" ] && git -C "$REPO" checkout --quiet "$previous"
  echo "Nothing was changed: ${#problems[@]} problem(s) above."
  $check_only || status false "refused before starting: ${#problems[@]} problem(s)"
  exit 1
fi
echo "Checks passed."
$check_only && exit 0

trap 'status false "deploy.sh failed at line $LINENO"' ERR

# ---------------------------------------------------------------------------------------------------- 2. backup
"$REPO/scripts/backup-db.sh"

# ---------------------------------------------------------------------------------------------------- 3. build, migrate
compose build --pull
docker build --target migrator -t "sangam/migrator:$tag" "$REPO"
# The owner's connection string goes in by environment (SANGAM_CONNECTION, read by the migrations' factory), never on a
# command line, where any user of the server could see it in the process list.
envfile="$(umask 077 && mktemp)"
trap 'rm -f "$envfile"; status false "deploy.sh failed at line $LINENO"' ERR
printf 'SANGAM_CONNECTION=%s\n' "$(head -n 1 "$OWNER_CONNECTION_FILE")" > "$envfile"
docker run --rm --network sangam-db --env-file "$envfile" "sangam/migrator:$tag"
rm -f "$envfile"

# Each host's role has the rights it should (the migration Rc16HostRoles, or postgres/grants.sql).
rights="$(docker exec "$DB_CONTAINER" psql -U "${DB_ADMIN:-sangam_dba}" -d sangam_identity -tAc "SELECT
  has_table_privilege('sangam_portal_app', 'users', 'INSERT')
  AND has_table_privilege('sangam_admin_app', 'audit_events', 'INSERT')
  AND NOT has_table_privilege('sangam_partner_app', 'audit_events', 'DELETE')
  AND has_table_privilege('sangam_identity_app', 'audit_events', 'DELETE')
  AND has_table_privilege('sangam_identity_app', 'client_assertion_uses', 'INSERT')
  AND NOT has_table_privilege('sangam_portal_app', 'client_assertion_uses', 'SELECT')" 2>&1)" || true
if [ "$rights" != "t" ]; then
  echo "$rights"
  echo "The hosts' database roles do not have their rights. Run postgres/grants.sql as sangam_owner (deploy/production/README.md, \"Roles\")."
  status false "host roles without their rights"
  exit 1
fi
echo "Host roles have their rights."

# ---------------------------------------------------------------------------------------------------- 4. start, health
compose up -d --remove-orphans
# Docker's own health check asks /health/live (the process is up); a host is ready only when /health/ready answers too
# (it reaches the database), asked from inside the container the same way, with bash and no curl in the image.
ready='exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /health/ready HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3 && head -n 1 <&3 | grep -q " 200 "'
deadline=$(( $(date +%s) + HEALTH_TIMEOUT ))
for service in identity portal admin partner; do
  until container="$(compose ps -q "$service")" \
      && [ "$(docker inspect -f '{{.State.Health.Status}}' "$container" 2>/dev/null)" = "healthy" ] \
      && docker exec "$container" bash -c "$ready" 2>/dev/null; do
    if [ "$(date +%s)" -gt "$deadline" ]; then
      echo "$service did not become ready in ${HEALTH_TIMEOUT}s; its last log lines:"
      compose logs --tail 40 "$service" || true
      status false "$service not ready after ${HEALTH_TIMEOUT}s"
      exit 1
    fi
    sleep 5
  done
  echo "$service is ready."
done

# ---------------------------------------------------------------------------------------------------- 5. record
status true "deployed"
echo "Deployed $tag ($(git -C "$REPO" rev-parse --short HEAD))."
