#!/usr/bin/env bash
# OI-058 (ASVS V13.1.1): Sangam's edge never changes a request path, so the identity server alone decides how a path
# is read — and EdgePathTests shows that every spelling of a management-API path, on real Kestrel, still needs the token.
#
#   bash tools/security/edge-path-check.sh caddy                 # CI: the production Caddy image and snippet, in front of
#                                                                # a server that echoes the path it receives
#   bash tools/security/edge-path-check.sh deployed https://id.sangamid.in
#                                                                # the deployed stack: no spelling reaches the API
#
# Both modes send each path byte for byte (curl --path-as-is) and fail on the first surprise.
set -euo pipefail

SPELLINGS=(
  "/api/v1/roles"
  "/api/v1/./roles"
  "/api/v1/x/../roles"
  "/./api/v1/roles"
  "/api/v1/%2e%2e/v1/roles"
  "/api/v1/roles%2F..%2F..%2Fconnect%2Ftoken"
  "/api//v1/roles"
  "/API/V1/Roles"
  "/%61pi/v1/roles"
  "/api/v1/%2561pi"
  "/api/v1/roles%00"
  "/api/v1/roles;x=1"
  "/api/v1/roles%20"
  "/api/v1/roles/"
  "/api/v1/roles?a=1&a=2"
)

mode="${1:-}"
case "$mode" in
caddy)
  root="$(cd "$(dirname "$0")/../.." && pwd)"
  work="$(mktemp -d)"
  trap 'docker rm -f sangam-edge-echo sangam-edge-caddy >/dev/null 2>&1 || true; docker network rm sangam-edge >/dev/null 2>&1 || true; rm -rf "$work"' EXIT
  cat > "$work/echo.py" <<'PY'
import http.server
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        body = self.path.encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *args):
        pass
http.server.ThreadingHTTPServer(("0.0.0.0", 8080), H).serve_forever()
PY
  # The production snippet exactly as deploy/production/Caddyfile has it; only the site is plain HTTP on a test name,
  # and the backend is the echo server.
  {
    printf '{\n  auto_https off\n  admin off\n}\n'
    sed -n '/^(sangam) {/,/^}/p' "$root/deploy/production/Caddyfile"
    printf 'http://id.test {\n  import sangam echo\n}\n'
  } > "$work/Caddyfile"
  docker build -q -t sangam/caddy:edge-check "$root/deploy/production/caddy" >/dev/null
  docker network create sangam-edge >/dev/null
  docker run -d --name sangam-edge-echo --network sangam-edge --network-alias echo \
    -v "$work/echo.py:/echo.py:ro" python:3.13-alpine python /echo.py >/dev/null
  docker run -d --name sangam-edge-caddy --network sangam-edge -p 127.0.0.1:8099:80 --user 1000:1000 \
    --sysctl net.ipv4.ip_unprivileged_port_start=0 --read-only --tmpfs /data --tmpfs /config --cap-drop ALL \
    --security-opt no-new-privileges:true -v "$work/Caddyfile:/etc/caddy/Caddyfile:ro" sangam/caddy:edge-check >/dev/null
  for _ in $(seq 1 30); do
    curl -fs -o /dev/null -H "Host: id.test" http://127.0.0.1:8099/ && break
    sleep 1
  done
  failed=0
  for path in "${SPELLINGS[@]}"; do
    got="$(curl -s --path-as-is -H "Host: id.test" "http://127.0.0.1:8099$path")"
    if [ "$got" = "$path" ]; then
      printf 'same      %s\n' "$path"
    else
      printf 'CHANGED   %s  ->  %s\n' "$path" "$got"
      failed=1
    fi
  done
  exit $failed
  ;;
deployed)
  base="${2:?usage: edge-path-check.sh deployed https://id.example}"
  failed=0
  for path in "${SPELLINGS[@]}"; do
    code="$(curl -s -o /dev/null -w '%{http_code}' --path-as-is "$base$path")"
    case "$code" in
      400|401|404) printf '%s  %s\n' "$code" "$path" ;;
      *) printf '%s  %s   <- reached past the token check or failed\n' "$code" "$path"; failed=1 ;;
    esac
  done
  exit $failed
  ;;
*)
  echo "usage: edge-path-check.sh caddy | deployed <https://id-host>" >&2
  exit 2
  ;;
esac
