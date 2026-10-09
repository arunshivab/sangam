#!/usr/bin/env bash
# rc.4 (SGM-503): the once-per-release code-quality review. The CodeQL gate on pull requests (codeql.yml) runs the
# security-extended suite only; this script runs the wider security-and-quality suite over every language, by hand,
# before each release. Each finding is read and either fixed in that release or recorded in docs/security/README.md
# as not worth changing, with the reason.
#
# Needs the CodeQL CLI (https://github.com/github/codeql-action/releases, the "codeql-bundle" for your platform), the
# .NET 10 SDK, Node 22, Python 3 and a JDK 21 with Maven. Run from the repository root:
#
#   CODEQL=/path/to/codeql/codeql bash tools/security/codeql-quality.sh artifacts/codeql
#
# One SARIF file per language lands in $OUT, plus summary.txt: every finding outside generated code (obj/) and the
# design prototypes (docs/design/), grouped by rule. The script never fails on findings; it fails only when an
# analysis cannot run. CodeQL's default Java heap is too small for the C# quality queries on an 8 GB machine; give it
# more with CODEQL_HEAP (CODEQL_HEAP=4500m CODEQL_THREADS=1 works there).
set -euo pipefail

OUT="$(mkdir -p "${1:-artifacts/codeql}" && realpath "${1:-artifacts/codeql}")"
CODEQL="${CODEQL:-codeql}"
ROOT="$(pwd)"
DB="$OUT/db"
rm -rf "$DB"
mkdir -p "$DB"

analyse() {   # language, suite, [build command]
  local lang="$1" suite="$2" build="${3:-}"
  echo "== $lang"
  if [[ -n "$build" ]]; then
    "$CODEQL" database create "$DB/$lang" --language="$lang" --source-root="$ROOT" --overwrite --command="$build" \
      > "$OUT/$lang-create.log" 2>&1
  else
    "$CODEQL" database create "$DB/$lang" --language="$lang" --source-root="$ROOT" --overwrite --build-mode=none \
      > "$OUT/$lang-create.log" 2>&1
  fi
  "$CODEQL" database analyze "$DB/$lang" "codeql/$suite" --format=sarif-latest --output="$OUT/$lang.sarif" \
    --threads="${CODEQL_THREADS:-0}" ${CODEQL_HEAP:+-J-Xmx"$CODEQL_HEAP"} > "$OUT/$lang-analyze.log" 2>&1
}

# A clean tree, so nothing a previous build left in obj/ is analysed twice.
find src tests samples -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true

analyse csharp     csharp-queries:codeql-suites/csharp-security-and-quality.qls \
  "dotnet build Sangam.sln -c Release -p:UseSharedCompilation=false"
analyse javascript javascript-queries:codeql-suites/javascript-security-and-quality.qls
analyse python     python-queries:codeql-suites/python-security-and-quality.qls
analyse java       java-queries:codeql-suites/java-security-and-quality.qls \
  "mvn -q -f sdk/java/pom.xml -DskipTests package"
analyse actions    actions-queries:codeql-suites/actions-security-and-quality.qls

python3 - "$OUT" <<'PY'
import json, pathlib, sys, collections
out = pathlib.Path(sys.argv[1])
rows = collections.defaultdict(list)
for sarif in sorted(out.glob("*.sarif")):
    for run in json.loads(sarif.read_text())["runs"]:
        for r in run.get("results", []):
            loc = r["locations"][0]["physicalLocation"]
            path = loc["artifactLocation"]["uri"]
            if "/obj/" in f"/{path}" or path.startswith("docs/design/"):
                continue
            rows[r["ruleId"]].append(f'{path}:{loc.get("region", {}).get("startLine", "?")}  {r["message"]["text"][:140]}')
lines = [f"{sum(len(v) for v in rows.values())} findings in {len(rows)} rules (generated code and docs/design left out)", ""]
for rule in sorted(rows, key=lambda k: (-len(rows[k]), k)):
    lines.append(f"{rule} ({len(rows[rule])})")
    lines += [f"  {x}" for x in sorted(rows[rule])]
(out / "summary.txt").write_text("\n".join(lines) + "\n")
print(lines[0])
PY
echo "Summary: $OUT/summary.txt"
