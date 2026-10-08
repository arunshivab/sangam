# Dependency and container vulnerability scanning (R7, SGM-503)

Every dependency Sangam ships, and every container image, is checked for known vulnerabilities on every pull request
by the `security` CI job. It fails on anything high or critical.

| What | Tool | Fails on |
|---|---|---|
| NuGet packages of every project in `Sangam.sln`, direct and transitive | `dotnet list package --vulnerable --include-transitive`, read by `tools/security/dotnet-vulnerable.sh` | any vulnerable package |
| npm packages of the JavaScript SDK (`sdk/js`) | `npm audit --audit-level=high` | high, critical |
| Python SDK (`sdk/python`) with every extra | `pip-audit --skip-editable` | any known vulnerability |
| Every lockfile and `pom.xml` in the repository, secrets, the `Dockerfile` | Trivy `fs` (also the second source for npm and Maven) | high, critical |
| The identity image (Ubuntu packages, .NET runtime, the published `.deps.json`) | Trivy `image --ignore-unfixed` | high, critical with a fix |
| The `caddy:2` and `postgres:18` images | Trivy `image --ignore-unfixed` | reported, not failing (upstream's fixes) |
| The Kubernetes manifests (`deploy/kubernetes`) | `kubectl kustomize` and kubeconform (schemas); Trivy `misconfig` (above) | invalid manifests; high, critical |

Run them yourself:

```sh
bash tools/security/dotnet-vulnerable.sh Sangam.sln
(cd sdk/js && npm ci && npm audit --audit-level=high)
(cd sdk/python && pip install pip-audit -e ".[test,fastapi,flask]" && pip-audit --skip-editable)
(cd sdk/java && mvn -B -q dependency:resolve)
docker run --rm -v "$PWD:/src:ro" -v "$HOME/.m2/repository:/root/.m2/repository:ro" \
  ghcr.io/aquasecurity/trivy:0.75.0 fs --severity HIGH,CRITICAL --scanners vuln,secret,misconfig --offline-scan /src
docker build --target app -t sangam/identity:local .
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock ghcr.io/aquasecurity/trivy:0.75.0 image sangam/identity:local
```

## Results at R7 (7 October 2026)

- NuGet: no vulnerable package in any of the 23 projects, transitive included. The image scans agree (57 packages in
  the identity server's `.deps.json`, .NET runtime 10.0.12: 0 findings).
- npm (`sdk/js`): 0. Python (`sdk/python`, 66 installed distributions): 0.
- Java (`sdk/java`): **4 critical and 5 high fixed.** On Spring Boot 3.5.16 the SDK pulled in Tomcat 10.1.55 (three
  authentication or constraint bypasses), Spring MVC 6.2.19 (remote code execution, fixed only in Spring Framework 7)
  and Jackson 2.21.4 (denial of service). The SDK now builds on Spring Boot 4.1.1, with Tomcat 11.0.26, Jackson 2.22.3
  and Jackson 3.1.7 pinned ahead of Spring Boot's own versions in `sdk/java/pom.xml`. After: 0.
- Images (identity, portal, admin, partner, migrator, demo; Ubuntu 24.04 base): no high or critical. Each has 9 medium
  and 5 low findings in Ubuntu packages (glibc, PCRE2, tar, zlib, systemd libraries, shadow, ICU), all marked
  *affected* with no Ubuntu fix published yet. The runtime stage now runs `apt-get upgrade`, so each release build
  takes Ubuntu's fixes as they appear.
- The `Dockerfile` had no `HEALTHCHECK` (Trivy DS-0026, low). The app images now report healthy or unhealthy from
  `/health/live`.

### At the release gate (images built from the release tree)

- All six images (identity, portal, admin, partner, migrator, demo) at `1.0.0-rc.1`: 0 high or critical. The identity
  image, at every severity: 9 medium and 5 low, all in Ubuntu packages with no fix published.
- The repository, including the Kubernetes manifests (`deploy/kubernetes/base`): 0 high or critical. One finding is
  a reviewed exception, recorded with its reason in `.trivyignore.yaml`: KSV-0109 matches setting *names* that
  contain "secrets", "passwords" or "passkeys", and their values are not secrets. The kind overlay's test-only
  services are not scanned.
- The third-party images the production stack runs. The CI job reports these and does not fail on them, because
  their fixes come from upstream:
  - `caddy:2`: 0 high or critical.
  - `postgres:18`: 0 in its Debian packages. There are 22 in `gosu`, a helper the entrypoint uses once to drop root,
    built with an older Go standard library. The TLS and URL-parsing issues are not reachable through it. Trivy
    also flags the image's sample "snake-oil" key, which PostgreSQL uses only if TLS is configured with it. Use the
    database service's own certificate (SGM-908, R-04).
- CycloneDX SBOMs of the repository and of each image are in the release evidence.

## When a scan fails

1. Read which package and which version fixes it.
2. Update the direct dependency, or pin the transitive one (`Directory.Packages.props` for NuGet, `overrides` in
   `sdk/js/package.json`, the dependency's minimum in `sdk/python/pyproject.toml`, `dependencyManagement` in
   `sdk/java/pom.xml`).
3. If there is no fix, record the finding, why it does not apply or how it is contained, and a review date in SGM-701;
   do not suppress it in CI without that record.
