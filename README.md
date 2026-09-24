# Sangam

**One identity. Many homes.**

Sangam is an open-source identity and tenancy platform for Indian healthcare software,
built and operated by [imagiQa Healthcare Services Pvt Ltd](https://imagiqa.com). Users
register once at `id.sangamid.in` and sign in across partner applications with the same
OpenID Connect / OAuth 2.0 handshake that "Sign in with Google" uses — credentials stay
with Sangam, partner applications receive only the claims the user consented to.

| Host | Purpose | Stack |
|---|---|---|
| `id.sangamid.in` | Authentication server and the eight auth screens | ASP.NET Core Razor Pages + OpenIddict + ASP.NET Core Identity (works with JavaScript disabled) |
| `account.sangamid.in` | Self-service portal: linked apps, devices, audit log, personal data | Blazor Server |
| `admin.sangamid.in` | Operator console (MFA mandatory) | Blazor Server |

Status: **PR-03 — accounts and sign-in**. People can register, verify their email by code,
sign in (password, password + emailed code, or passwordless), recover a password and sign out —
all without JavaScript. Apps cannot yet initiate a sign-in (authorization code flow and the
consent screen arrive in PR-04). See [`docs/README.md`](docs/README.md) for the
eight-PR delivery plan.

## Quick start

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0),
[PostgreSQL 16 or later](https://www.postgresql.org/download/) installed natively (needed from PR-02),
PowerShell 5.1 or 7.

```powershell
git clone https://github.com/arunshivab/sangam.git
cd sangam
.\scripts\bootstrap-dev.ps1                 # checks tools, finds Postgres, builds, tests
.\scripts\bootstrap-dev.ps1 -InitDatabase   # once: creates the sangam_identity role + database
dotnet run --project src/Sangam.Identity.Server   # http://localhost:5100/register
```

In Development the server captures outgoing email instead of sending it; open
`http://localhost:5100/dev/outbox` to read verification and reset codes.

Docker is not required for local development. `deploy/docker-compose.dev.yml` (PostgreSQL 16 +
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
  Sangam.Shared/                   DTOs and constants shared with the SDK (plain class library)
  Sangam.Web.Shared/               tokens, brand CSS, LiPi Sans, favicons, mark + lockup components
  Sangam.Client/                   NuGet SDK partner applications install (PR-07)
tests/                             one test project per src/ project
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

Local development uses a native PostgreSQL 16+ with the `sangam_identity` database created by
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

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) and
[SECURITY.md](SECURITY.md). Report vulnerabilities privately to security@sangamid.in.

## Licence

Apache License 2.0 — see [LICENSE](LICENSE). Copyright © 2026 imagiQa Healthcare Services Pvt Ltd.
