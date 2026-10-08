#!/usr/bin/env bash
# rc.2: starts the four Sangam hosts from a Release build, in Development, for the nightly accessibility and ZAP jobs
# (and for a local run of either). Each host listens on its usual port and logs to $LOGS/<host>.log.
#
#   dotnet build Sangam.sln -c Release
#   bash tools/ci/start-hosts.sh            # waits until all four answer /health/live
#   bash tools/ci/start-hosts.sh stop
#
# Needs PostgreSQL with the development database (deploy/postgres/init.sql). In Development the identity server
# applies the migrations and registers the sample application (sangam-dev-sample) on start.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
LOGS="${LOGS:-$ROOT/artifacts/hosts}"
CONFIGURATION="${CONFIGURATION:-Release}"
HOSTS=("Identity.Server:5100" "SelfService.Web:5200" "Admin.Web:5300" "Partner.Web:5400")
mkdir -p "$LOGS"

if [[ "${1:-start}" == "stop" ]]; then
  for pid_file in "$LOGS"/*.pid; do
    [[ -f "$pid_file" ]] && kill "$(cat "$pid_file")" 2>/dev/null || true
    rm -f "$pid_file"
  done
  exit 0
fi

# The identity server first: it migrates the database the others then read.
start() {
  local name="${1%%:*}" port="${1##*:}"
  local dir="$ROOT/src/Sangam.$name"
  local dll="$dir/bin/$CONFIGURATION/net10.0/Sangam.$name.dll"
  [[ -f "$dll" ]] || { echo "Not built: $dll (run: dotnet build Sangam.sln -c $CONFIGURATION)" >&2; exit 1; }
  # Run from the project folder, as dotnet run does, so Development serves the static files from the source tree.
  (cd "$dir" && ASPNETCORE_ENVIRONMENT=Development exec dotnet "$dll" --urls "http://localhost:$port" > "$LOGS/$name.log" 2>&1) &
  echo $! > "$LOGS/$name.pid"
}

wait_for() {
  local name="${1%%:*}" port="${1##*:}"
  for _ in $(seq 1 120); do
    if curl -fs -o /dev/null "http://localhost:$port/health/live"; then
      echo "$name is up on $port"
      return 0
    fi
    sleep 1
  done
  echo "$name did not start on $port; its log:" >&2
  tail -n 60 "$LOGS/$name.log" >&2
  exit 1
}

start "${HOSTS[0]}"
wait_for "${HOSTS[0]}"
for host in "${HOSTS[@]:1}"; do
  start "$host"
done
for host in "${HOSTS[@]:1}"; do
  wait_for "$host"
done
