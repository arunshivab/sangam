# Changelog

All notable changes to Sangam are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added — PR-03 Auth screens 1–6
- Screens on `id.sangamid.in`, all working with JavaScript disabled: `/login`, `/login/code`
  (passwordless), `/login/verify` (code step), `/register`, `/verify` (code + server-rendered
  resend countdown), `/verified`, `/forgot`, `/reset`, a minimal signed-in `/account` with the
  sign-in preference, `/logout`, `/Error`, and Development-only `/dev/outbox`.
- `IAccountService` (register, find, issue/verify codes, password check with lockout and
  audit, passwordless start, reset with security-stamp rotation, sign-in preference) on
  ASP.NET Core Identity; `OneTimeCodeService`; `PasswordStrength` (Anjal's policy: 8+,
  all four classes, blocklist); `InMemoryEmailOutbox`.
- Session cookie (`sangam.session`, 12 h sliding, security stamp re-validated every 5 min)
  and pending-flow cookie (`sangam.pending`, 15 min); per-IP rate limiting on auth POSTs.
- Migration `ProfileFieldsSignInModesAndOneTimeCodes`: `first_name`, `last_name`,
  `date_of_birth`, `gender`, `sign_in_preference` on users (replacing `name`),
  `sign_in_policy` on apps, and the `one_time_codes` table.
- Tests: sign-in mode resolution, password strength, one-time codes (cooldown, attempts,
  expiry, rate window), account flows through the real DI graph, and HTTP journeys with a
  cookie-keeping no-JS browser session (register → verify → account → sign out → forgot →
  reset → sign in; two-step and passwordless; lockout; rate limiter; enumeration safety).
- ADR-0002.

### Changed — PR-03
- `.editorconfig`: CA2007 off for the three web hosts (no SynchronizationContext).
- Development logging: EF command and migration noise reduced to Warning.

### Added — PR-02 Domain + Infrastructure + first OIDC endpoint
- Domain entities for the tenancy model (`SangamUser`, `OrgType`, `Organisation` tree with
  materialised path, `App`, `Role` with optional org scope, `OrgMembership` with
  `applies_to_descendants`, `AppGrant`, `Consent`, `PlatformOperator`, `AppAdmin`,
  append-only `AuditEvent`) and the audit action taxonomy — see ADR-0001.
- `SangamDbContext` (ASP.NET Core Identity user tables, Sangam tables, OpenIddict tables; all
  snake_case) and the `InitialSchema` migration, with PostgreSQL rules that make
  `audit_events` append-only and partial unique indexes that allow re-grants after revocation.
- Argon2id password hashing (`Argon2idPasswordHasher`, PHC format, rehash-on-upgrade).
- `IClock`, `IEmailSender` (logging implementation until Anjal), `ISmsSender` (reserved),
  `IAuditWriter` (dedicated-context EF implementation).
- OpenIddict server in `Sangam.Identity.Server`: discovery document, JWKS, and
  `POST /connect/token` for the client-credentials grant; development certificates in
  Development/Testing, refuses to start elsewhere until PR-08 configures real ones.
- Development seeding of scopes and the `sangam-dev-sample` partner app (client credentials).
- PostgreSQL-backed tests (`[PostgresFact]`) driven by `SANGAM_TEST_CONNECTION`: run against
  a native PostgreSQL locally and a service container on the Ubuntu CI job; skipped elsewhere.
- `dotnet-ef` tool manifest; `sangam_identity_test` and `sangam_identity_test_server`
  databases in `init.sql` (the server tests use their own so parallel test projects never
  truncate under the in-process host); bootstrap script enables the PostgreSQL-backed tests
  when a server is reachable.

### Changed — PR-02
- `.editorconfig`: interface members need no accessibility modifier; EF migrations are exempt
  from style analysis.
- CI: explicit Ubuntu (with PostgreSQL service) and Windows jobs instead of a matrix; check
  names unchanged.

### Added — PR-01 Foundation
- Solution with nine `src/` projects (Identity Domain / Application / Infrastructure / Server,
  SelfService.Web, Admin.Web, Shared, Web.Shared, Client) and a mirrored test project for each.
- Clean-architecture layering tests that fail the build if dependencies point outward.
- Central package management, `global.json` pinned to the .NET 10 SDK, `.editorconfig` with
  style enforced in the build, warnings as errors.
- GitHub Actions CI: build + test on Ubuntu and Windows, plus a `dotnet format` check.
- Design system: `sangam-tokens.css`, `sangam-brand.css`, `SangamMark` and `SangamLockup`
  components, favicon set (16 / 32 / 180 / 512, `.ico`, `.svg`).
- LiPi Sans self-hosted (`Sangam.Web.Shared/wwwroot/fonts/lipi/`, SIL OFL) and LiPicons
  (`LiPicons.Blazor` 1.1.0 via `localpackages/`); no external font, icon or script loads,
  enforced by a test in every host.
- Foundation pages in all three hosts rendering the tokens, lockups and palette.
- `scripts/bootstrap-dev.ps1` (native PostgreSQL by default, `-InitDatabase`, `-WithDocker`),
  idempotent `deploy/postgres/init.sql`, and the server Compose stack
  (`deploy/docker-compose.dev.yml`: PostgreSQL 16 + Caddy).
- Apache 2.0 licence, README, CONTRIBUTING, CODE_OF_CONDUCT, SECURITY, issue and PR templates.
- Planning documents filed under `docs/planning/`; design handoff under `docs/design/`.
