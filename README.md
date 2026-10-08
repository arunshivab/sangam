# Sangam

**One identity. Many homes.**

Sangam is an open-source identity and tenancy platform for Indian healthcare software,
owned and operated by Dr. Arun Shiva Balasubramanian (it started under imagiQa Healthcare Services; a company
will be formed to hold it). Users
register once at `id.sangamid.in` and sign in across partner applications with the same
OpenID Connect / OAuth 2.0 handshake that "Sign in with Google" uses — credentials stay
with Sangam, partner applications receive only the claims the user consented to.

| Host | Purpose | Stack |
|---|---|---|
| `id.sangamid.in` | Authentication server and the eight auth screens | ASP.NET Core Razor Pages + OpenIddict + ASP.NET Core Identity (works with JavaScript disabled) |
| `account.sangamid.in` | Self-service portal: linked apps, devices, audit log, personal data | Blazor Server |
| `admin.sangamid.in` | Operator console (MFA mandatory) | Blazor Server |
| `partners.sangamid.in` | Partner console: an application's own staff manage it (MFA mandatory) | Blazor Server |

Status: **PR-08 — applications can use Sangam in a few lines**. The `Sangam.Client` package adds
"Sign in with Sangam" to an ASP.NET Core or Blazor application in one call and tells it which roles
each person holds in which organisation. imagiQa, a small sample hospital application, uses it end
to end: its doctors and nurses get their roles from the partner console. Next: Anjal email delivery
(PR-09).

Previously: **PR-07 — partners run their own applications**. A partner's own staff manage their
application's roles, organisations and people's roles on a separate partner console, as Owners or
Admins, seeing only people who have linked that application. imagiQa assigns each application's
first owner; everything a partner does is recorded in their name, and people read it in their own
log. Next: the `Sangam.Client` SDK (PR-08).

Previously: **PR-06 — Sangam can be operated**. imagiQa staff run the platform from an operator console
with four ranks, a mandatory authenticator app, and every look at a user's record recorded in that
user's own log. Anyone can now add an authenticator app to their account. Next: application
administrators (PR-07).

Previously: **PR-05 — accounts are self-serviceable**. Alongside the identity provider, people can
now see every application they have allowed and revoke it, see where they are signed in and end a
single session or all of them, read their own audit trail, download their data and delete their
account. Next: the admin console (PR-06).

Previously: **PR-04 — Sangam is an identity provider**. Partner apps sign users in with
Authorization Code + PKCE, users consent to exactly what is shared, tokens and `userinfo`
carry the user's organisations and roles, apps administer their own tenancy through
`/api/v1`, and app-initiated sign-out works. The self-service portal is PR-05. See [`docs/README.md`](docs/README.md) for the
eight-PR delivery plan.

## Quick start

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0),
[PostgreSQL 18](https://www.postgresql.org/download/) installed natively (needed from PR-02),
PowerShell 5.1 or 7.

```powershell
git clone https://github.com/arunshivab/sangam.git
cd sangam
.\scripts\bootstrap-dev.ps1                 # checks tools, finds Postgres, builds, tests
.\scripts\bootstrap-dev.ps1 -InitDatabase   # once: creates the sangam_identity role + database
dotnet run --project src/Sangam.Identity.Server      # http://localhost:5100/register
dotnet run --project src/Sangam.SelfService.Web     # http://localhost:5200  (needs the server above)
dotnet run --project src/Sangam.Admin.Web           # http://localhost:5300  (operators only)
dotnet run --project src/Sangam.Partner.Web         # http://localhost:5400  (application administrators only)
dotnet run --project samples/Imagiqa.Web            # http://localhost:5500  (sample hospital application)
```

The console opens only for someone holding an operator rank **and** an authenticator app. To make
yourself the first owner, register and verify an account, then:

```powershell
dotnet run --project src/Sangam.Admin.Web -- create-operator you@example.in
```

It refuses once any operator exists. Then add an authenticator under Personal details in the portal.

The partner console opens for someone who administers at least one application **and** has an
authenticator app. An AppManager or above makes an application's first owner from the operator
console's Applications page (**Assign owner**); owners add the rest from the partner console.

In Development the server captures outgoing email instead of sending it; open
`http://localhost:5100/dev/outbox` to read verification and reset codes.

Docker is not required for local development. `deploy/docker-compose.dev.yml` (PostgreSQL 18, +
Caddy) is the server stack and an opt-in alternative: `.\scripts\bootstrap-dev.ps1 -WithDocker`.

## Repository layout

```
src/
  Sangam.Identity.Domain/          entities, value objects, domain events — no dependencies
  Sangam.Identity.Application/     use cases and the abstractions Infrastructure implements
  Sangam.Identity.Infrastructure/  EF Core + PostgreSQL, OpenIddict stores, email, CAPTCHA
  Sangam.Identity.Server/          id.sangamid.in — OIDC server + auth screens (Razor Pages)
  Sangam.SelfService.Web/          account.sangamid.in — portal (Blazor Server)
  Sangam.Admin.Web/                admin.sangamid.in — operator console (Blazor Server)
  Sangam.Partner.Web/              partners.sangamid.in — partner console (Blazor Server)
  Sangam.Shared/                   DTOs and constants shared with the SDK (plain class library)
  Sangam.Web.Shared/               tokens, brand CSS, LiPi Sans, favicons, mark + lockup components
  Sangam.Client/                   NuGet SDK partner applications install — see its README
samples/Imagiqa.Web/              imagiQa — sample hospital application using Sangam.Client
tests/                             one test project per src/ project, plus the sample's
deploy/                            server stack (Compose: Postgres + Caddy) and the Postgres init script
localpackages/                     local NuGet feed (LiPicons.Blazor — proprietary, see its README)
docs/design/                       the authoritative design handoff
docs/planning/                     strategic, technical, legal and UX planning documents
scripts/                           bootstrap-dev.ps1
```

Dependencies point inward: hosts → Application → Domain, with Infrastructure implementing
Application's interfaces. Layering tests in `tests/` fail the build if that direction is broken.

## Design system

The identity system — palm-leaf mark, teal `#0F3B38` / cream `#F2EFE8` / ochre `#8A6A2F` —
is specified in [`docs/design/README.md`](docs/design/README.md) and shipped from
`src/Sangam.Web.Shared/`: `sangam-tokens.css`, `sangam-brand.css`, the `SangamMark` and
`SangamLockup` components, **LiPi Sans** (self-hosted, one variable family covering Latin and
nine Indic scripts) and **LiPicons** (`LiPicons.Blazor`, from `localpackages/`). No fonts,
icons or scripts are loaded from external hosts — a test in every host enforces it. In the interface the wordmark is lowercase **sangam**;
in prose write "Sangam"; `SangamID` is the domain and package namespace only.

## Database and migrations

Local development uses a native PostgreSQL 18 with the `sangam_identity` database created by
`scripts/bootstrap-dev.ps1 -InitDatabase`. `Sangam.Identity.Server` applies migrations and
seeds the development sample app on startup in Development. To add a migration:

```powershell
dotnet ef migrations add <Name> --project src/Sangam.Identity.Infrastructure --startup-project src/Sangam.Identity.Infrastructure --output-dir Persistence/Migrations
```

PostgreSQL-backed tests run when `SANGAM_TEST_CONNECTION` points at the throwaway
`sangam_identity_test` database (the bootstrap script sets it for its own run). The
`Sangam.Identity.Server` tests derive `sangam_identity_test_server` from it — same server,
`_server` suffix — so test projects running in parallel never share a database with the
in-process host.

## Building

```powershell
Get-ChildItem -Recurse -Directory -Include bin,obj | Remove-Item -Recurse -Force
dotnet build Sangam.sln -c Release
dotnet test  Sangam.sln -c Release --no-build
dotnet format Sangam.sln --verify-no-changes
```

Warnings are errors, code style is enforced in the build, and every public member needs
XML documentation. CI runs the same three steps on Ubuntu and Windows.

### Building the container images behind a TLS-intercepting proxy

The images (`Dockerfile`, see `deploy/production/README.md`) restore NuGet packages and install
`libgssapi-krb5-2` during the build. On a network whose proxy re-signs HTTPS traffic — some corporate
networks and build sandboxes — those downloads fail certificate validation inside the build container.
That is the network, not a defect in Sangam: add the proxy's CA certificate to the build (or to the base
image's trust store) and build with `--network host` so the build uses the host's proxy settings. On an
ordinary network neither is needed.

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) and
[SECURITY.md](SECURITY.md). Report vulnerabilities privately to security@sangamid.in.

## Licence

Apache License 2.0 — see [LICENSE](LICENSE). Copyright © 2026 Dr. Arun Shiva Balasubramanian.
