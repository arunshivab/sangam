#!/usr/bin/env bash
# rc.16 (ASVS V1.14.4, V14.1.1): the production deployment files are checked on every change, so a mistake in them is
# found in CI rather than on the server.
#
#   bash tools/deploy/check-deploy-config.sh            # CI: Caddy runs from its pinned image
#   CADDY=/path/to/caddy bash tools/deploy/check-deploy-config.sh   # locally, with a caddy binary
#
# 1. Every Compose file, alone and with its overlays, is valid (docker compose config, no daemon needed).
# 2. The Caddyfile is valid to Caddy itself (caddy validate).
# 3. Every secret the stacks name is documented in deploy/production/secrets/README.md, and none is committed.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
prod="$root/deploy/production"
fail=0

echo "== Compose files"
compose() { (cd "$prod" && docker compose "$@" config -q); }
compose -f docker-compose.yml
compose -f docker-compose.yml -f docker-compose.saml.yml
compose -f docker-compose.yml -f docker-compose.digilocker.yml
compose -f docker-compose.yml -f docker-compose.saml.yml -f docker-compose.digilocker.yml
compose -f docker-compose.yml -f docker-compose.logs.yml
compose -f postgres/docker-compose.yml
echo "valid"

echo "== Caddyfile"
if [ -n "${CADDY:-}" ]; then
  "$CADDY" validate --config "$prod/Caddyfile" --adapter caddyfile
else
  # The same pinned image the production Caddy is built from (deploy/production/caddy/Dockerfile).
  image="$(sed -n 's/^FROM \(caddy:[^ ]*\).*/\1/p' "$prod/caddy/Dockerfile" | head -n 1)"
  docker run --rm -v "$prod/Caddyfile:/etc/caddy/Caddyfile:ro" "$image" caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
fi

echo "== Secrets"
readme="$prod/secrets/README.md"
secret_files() { python3 -c 'import json,sys; [print(v.get("file","").rsplit("/",1)[-1]) for v in (json.load(sys.stdin).get("secrets") or {}).values()]'; }
names="$(cd "$prod" && {
  docker compose -f docker-compose.yml -f docker-compose.saml.yml -f docker-compose.digilocker.yml config --format json | secret_files
  docker compose -f postgres/docker-compose.yml config --format json | secret_files
} | sort -u)"
for name in $names; do
  if ! grep -q "\`$name\`" "$readme"; then
    echo "::error::secret file '$name' is used by a Compose file but not described in deploy/production/secrets/README.md"
    fail=1
  fi
done
committed="$(cd "$root" && git ls-files deploy/production/secrets | grep -v '/README.md$' || true)"
if [ -n "$committed" ]; then
  echo "::error::secret files are committed: $committed"
  fail=1
fi
[ "$fail" -eq 0 ] && echo "every secret is documented; none is committed"
exit "$fail"
