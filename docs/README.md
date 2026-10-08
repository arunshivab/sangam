# Sangam documentation

| Folder | What it holds | Status |
|---|---|---|
| `design/` | The approved design handoff: brand system, `sangam-tokens.css`, twelve product screens, screenshots. `design/README.md` is the **authoritative** visual/UX specification. | Current |
| `decisions/` | Architecture decision records made during the build. Each supersedes the planning documents where they differ. | Current |
| `planning/` | The strategic, technical, legal and UX planning documents written before the build started. | Historical reference — superseded where noted below |
| `security/` | R7: the ASVS Level 2 self-assessment, the ZAP scans, dependency and image scanning, and the penetration-test scope and rules of engagement. `security/README.md` is the index. | Current |

## Precedence when documents disagree

1. `design/README.md` wins on anything visual or UX.
2. This file (the corrections below) wins on current decisions.
3. `planning/*` is the historical record; where a later decision changed it, the later decision stands.

## Corrections to the planning documents

- **Ownership (D-C, 7 October 2026).** The planning documents assume a joint LLP of three founders. Sangam is
  **solo-built and owned personally by Dr. Arun Shiva Balasubramanian**. It started under imagiQa Healthcare
  Services Pvt Ltd, but all of its resources are his; a new company will be formed later and Sangam
  transferred to it. Jurisdiction: Ahmedabad. The Founder Agreement (legal scaffolding, Part A) and the
  cost-sharing sections no longer apply; each partner company gets a short service MoU instead. The end-user
  Terms and the Privacy Policy name Dr. Arun Shiva Balasubramanian as owner, operator and data fiduciary
  (`Sangam:Operator`, `Sangam:Jurisdiction`; the texts are with counsel).
- **PostgreSQL (D-F, 7 October 2026).** The documents say PostgreSQL 16. Sangam runs **PostgreSQL 18
  everywhere**: production (18.6 at the time of the decision), CI (`postgres:18`) and local development
  (update local installs to the current 18.x).
- **Target framework.** The planning documents say .NET 8 LTS. The build targets **.NET 10 (LTS)**
  — .NET 8 support ends in November 2026 — in line with the rest of the imagiQa portfolio.
- **Hosting.** Oracle Cloud Free Tier was the v0 recommendation. Phase 1 is a single E2E Networks
  VM (2 vCPU / 6 GB) shared with Anjal, with separate PostgreSQL databases and roles; Phase 2
  moves Sangam to its own VM once the revenue applications are commercial.
- **Timeline.** The 90-day sprint plan assumed three part-time founders. The solo estimate is
  four to seven months across eight pull requests (see below).
- **Visual design.** `planning/sangam-mockups.html` and `planning/sangam-what-it-holds.html`
  show an earlier, provisional look. They are superseded by `design/` (palm-leaf mark,
  teal / cream / ochre palette, Newsreader / Mukta / JetBrains Mono).
- **Domains.** The planning documents use `sangam.in` as a placeholder. The registered domains
  are `sangamid.in` (canonical), `sangamid.com` and `sangamid.co.in` (both 301 → `.in`).
- **UI component library.** MudBlazor was pencilled in for the Blazor hosts. That decision is
  deferred to PR-05; the design system is hand-rolled on `sangam-tokens.css` and may not need it.
- **Local development.** The planning documents assume Docker Compose on the developer
  machine. Local development uses a native PostgreSQL 18 install; Compose (PostgreSQL + Caddy)
  is the server stack for the E2E Networks VM and an opt-in alternative locally
  (`scripts/bootstrap-dev.ps1 -WithDocker`). Caddy is a server concern — TLS termination with
  automatic certificates, one port 443 shared by the three hosts and by Anjal — and has no role
  on a laptop.
- **Typography and icons (decided 2026-09-23).** The handoff specifies Newsreader, Mukta,
  JetBrains Mono and Noto Sans Devanagari loaded from Google Fonts. Sangam instead ships
  **LiPi Sans** (imagiQa's unified variable family: Inter for Latin, Noto Sans for nine Indic
  scripts, SIL OFL) self-hosted from `Sangam.Web.Shared/wwwroot/fonts/lipi/`, and
  **LiPicons** (`LiPicons.Blazor`, imagiQa's icon system, restored from `localpackages/`).
  Nothing is fetched from an external host at runtime — a test in each host asserts it.
  `--sg-font-mono` falls back to the system monospace stack. The wordmark is LiPi Sans 600
  rather than Newsreader 500; everything else in the handoff still applies.
- **Tenancy and identity model (PR-02).** Users on ASP.NET Core Identity with Argon2id; email
  sign-in with email OTP, mobile stored unverified until 2027; three-level organisation tree
  with explicit memberships and an inheritance flag; roles owned by the app with optional
  org scoping; `platform_operators` and `app_admins` tables; `anonymous` audit actor. Full
  reasoning in [`decisions/0001-tenancy-and-identity-model.md`](decisions/0001-tenancy-and-identity-model.md).
- **Account flows (PR-03).** Emailed codes instead of links for verification and reset; no
  CAPTCHA in v0 (rate limiting + lockout + code limits instead of Turnstile); three sign-in modes
  with a per-app policy and a per-user preference; profile fields all required at registration.
  See [`decisions/0002-account-flows-and-sign-in-modes.md`](decisions/0002-account-flows-and-sign-in-modes.md).
- **OIDC flow, consent and the management API (PR-04).** Authorization Code + PKCE for every
  client; consent shown for every app with no first-party exemption; small ID tokens with the
  detail at `/connect/userinfo`; RP-initiated sign-out; an app-scoped management API at
  `/api/v1`. See [`decisions/0003-oidc-flow-consent-and-management-api.md`](decisions/0003-oidc-flow-consent-and-management-api.md).
- **Portal, sessions and deletion (PR-05).** The portal is a real OIDC client of the identity
  server; sessions are recorded rows so a single device can be signed out; device names come from
  the app (`sangam_device`) and never from geo-IP; deletion has a 30-day grace, an automatic purge
  and an operator hold. See
  [`decisions/0004-self-service-portal-sessions-and-deletion.md`](decisions/0004-self-service-portal-sessions-and-deletion.md).
- **Operator console (PR-06).** Four ordered ranks — Viewer, AppManager, Support, Owner —
  enforced in the service; mandatory authenticator for every operator; opening a record is
  audited and visible to the user; impersonation refused by design. See
  [`decisions/0005-operator-console-ranks-and-mfa.md`](decisions/0005-operator-console-ranks-and-mfa.md)
  and the plain-language [`authority-model.md`](authority-model.md).
- **Going live (PR-10).** Bootstrapping the first owner on production, the two-owner rule, and
  what must be true before the first boot: [`go-live-checklist.md`](go-live-checklist.md).
- **Partner facts.** The handoff's placeholder copy says LiPi is operated by "Lipi Systems Pvt Ltd,
  Bengaluru". LiPi is an imagiQa product (Ahmedabad). This is runtime data in the app registry,
  not a design change.

- **Step-up, signatures, languages, branding and logout (R3).** Assurance levels as `acr` values
  (`urn:sangam:acr:1`, `:2`, `:3`, `:sign`) with `amr` and `auth_time` in every token; electronic signatures as
  JWS tokens over a record hash after a fresh two-factor sign-in (PR-17). Screens in Hindi and Malayalam from one
  JSON text catalogue keyed by the English, with a build-time lint (PR-18; SGM-209 named resource files — the
  catalogue is the same idea in a form translators can review without tools). Branding and message templates by
  level — organisation, application, platform, built-in — with partner branding never changing structure
  (PR-19). Back- and front-channel logout, introspection and revocation (PR-20).
- **SDK family and the shared audit event (R6).** JavaScript/TypeScript (`@sangam/client`, `@sangam/node`,
  `@sangam/react`), Python (`sangam-client`, FastAPI and Flask) and Java (`sangam-client`, `sangam-spring`) SDKs next to
  .NET's `Sangam.Client`, with one sample each; the shared audit event schema 1.0 and its helper in every SDK; shared
  conformance vectors every SDK runs, and a live check of every sample ([`../sdk/README.md`](../sdk/README.md)).
- **Integrations and verified identity (R5).** SCIM 2.0 provisioning to applications that ask for it, kept in step by
  a state-based sync with retries and a reconcile (PR-23, [`provisioning.md`](provisioning.md)); signed webhooks on the
  Standard Webhooks scheme, with secret rotation and a delivery log (PR-24, [`webhooks.md`](webhooks.md)); custom
  attributes (never health data), custom claims under the `attributes` scope, and time-limited roles (PR-25,
  [`attributes-and-claims.md`](attributes-and-claims.md)); verification of name, date of birth and gender with
  DigiLocker, keeping a keyed hash of the DigiLocker id and never an Aadhaar number (PR-26,
  [`identity-verification.md`](identity-verification.md)). For pilot testers: [`pilot-guide.md`](pilot-guide.md).
- **Protocols, archive, backups, demo and grievances (R4).** Device authorization grant, PAR, token exchange and
  native apps (PR-21, `native-and-mobile.md`); Sangam as a SAML 2.0 identity provider (PR-22, ADR-0016, which
  replaces the proposed ADR-0016 of SGM-302); the encrypted audit archive (D-A); encrypted off-region backups under
  object lock (D-E); the demo at `demo.sangamid.in` and client registration from settings (D-I); the grievance log
  (D-D).
- **Founder decisions D-A to D-M (R3).** Anjal is the single messaging gateway, by its API
  (`anjal-messaging-contract.md`); passkeys on ASP.NET Core Identity's own WebAuthn support (Fido2NetLib removed);
  self-hosted monitoring in the operator console with alerts through Anjal (OpenTelemetry removed); an offline
  breached-password list; a cooling-off period on support resets of two-step sign-in; existing accounts concealed at
  registration; invitation-only registration for the pilot; PostgreSQL 18; ownership by the founder personally.
  Sangam → Anjal is machine-to-machine (an API key); Anjal → Sangam is an ordinary relying party (Anjal's users sign
  in with SangamID). Whether an Anjal mailbox maps to a SangamID e-mail address (OI-029) is decided later.

## Delivery plan

| PR | Title | Delivers |
|---|---|---|
| PR-01 | Foundation — repo skeleton + design tokens | Solution, project stubs, CI, licence, Docker Compose, tokens, logo, docs filed |
| PR-02 | Domain + Infrastructure + first OIDC endpoint | Entities, EF Core migrations, OpenIddict wired, discovery / JWKS / client-credentials token respond |
| PR-03 | Auth screens 1–6 (Razor Pages, no-JS) | Login (three modes), register, verify by code, forgot / reset by code; rate limiting; lockout; sessions; dev outbox |
| PR-04 | Consent + auth 7–8 + full OIDC flow | Consent screen, sign-out, Authorization Code + PKCE end to end, userinfo, management API |
| PR-05 | Self-service portal (Blazor Server) — screens 9–10 | Dashboard, linked apps, DPDPA data export, account deletion |
| PR-06 | Operator console (Blazor Server, dark scope) | Four ranks, mandatory authenticator, users, applications, operators, audited reads |
| PR-07 | Application administrators | The `app_admins` plane: a partner's own staff sign in and manage their roles, organisations and memberships |
| PR-08 | `Sangam.Client` NuGet SDK + imagiQa sample | Sign-in in one call; roles per organisation; imagiQa sample hospital app (ADR-0007) |
| PR-09 | Anjal email delivery | Verification, reset and sign-in codes sent through Anjal instead of the outbox; `sangamid.in` as a sending domain with SPF, DKIM and DMARC; a recipient allowlist outside production; first real send to a real inbox before go-live |
| PR-10 | Production hardening + deployment | Certificates, security headers, backups, runbooks, secret rotation, and the [go-live checklist](go-live-checklist.md) |
