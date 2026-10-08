#!/usr/bin/env bash
# rc.2 (SGM-503): the OWASP ZAP baseline scan of the four hosts, as R7 ran it by hand (docs/security/zap.md), for the
# nightly job. Passive rules only, two minutes of spidering per host, one host at a time. A FAIL from any host fails
# the run; WARN results are reported, not failed on (R7 reviewed them). Reports land in $OUT (HTML and JSON per host).
#
#   bash tools/ci/start-hosts.sh && bash tools/security/zap-baseline.sh artifacts/zap
set -uo pipefail

OUT="${1:-artifacts/zap}"
IMAGE="${ZAP_IMAGE:-ghcr.io/zaproxy/zaproxy:stable}"
mkdir -p "$OUT"
chmod 777 "$OUT"   # the scanner runs as its own user inside the container and writes its reports here

failed=0
for port in 5100 5200 5300 5400; do
  docker run --rm --network host -e JAVA_OPTS=-Xmx1536m -v "$(realpath "$OUT"):/zap/wrk:rw" "$IMAGE" \
    zap-baseline.py -t "http://localhost:$port/" -m 2 -J "baseline-$port.json" -r "baseline-$port.html" \
    -z "-config start.checkForUpdates=false" > "$OUT/baseline-$port.log" 2>&1
  code=$?
  # zap-baseline.py: 0 clean, 2 warnings only, 1 at least one FAIL, 3 the scan itself failed.
  summary="$(grep -E '^FAIL-NEW|^WARN-NEW|^PASS' "$OUT/baseline-$port.log" | tail -n 1)"
  case "$code" in
    0|2) echo "port $port: passed (exit $code) $summary" ;;
    *)   echo "port $port: FAILED (exit $code) $summary"; grep -E '^FAIL-NEW|^FAIL-INPROG' "$OUT/baseline-$port.log" || tail -n 20 "$OUT/baseline-$port.log"; failed=1 ;;
  esac
done
exit "$failed"
