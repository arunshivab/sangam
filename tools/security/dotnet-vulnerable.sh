#!/usr/bin/env bash
# R7 (SGM-503): fails when any project in Sangam.sln uses a NuGet package with a known vulnerability, direct or
# transitive. `dotnet list package --vulnerable` itself exits 0 either way, so its report is read here.
set -euo pipefail
report="$(dotnet list "${1:-Sangam.sln}" package --vulnerable --include-transitive 2>&1)"
printf '%s\n' "$report"
if grep -q 'has the following vulnerable packages' <<<"$report"; then
  echo "::error::Vulnerable NuGet packages found (see above)."
  exit 1
fi
echo "No vulnerable NuGet packages."
