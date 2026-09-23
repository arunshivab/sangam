# Sangam documentation

| Folder | What it holds | Status |
|---|---|---|
| `design/` | The approved design handoff: brand system, `sangam-tokens.css`, twelve product screens, screenshots. `design/README.md` is the **authoritative** visual/UX specification. | Current |
| `planning/` | The strategic, technical, legal and UX planning documents written before the build started. | Historical reference — superseded where noted below |

## Precedence when documents disagree

1. `design/README.md` wins on anything visual or UX.
2. This file (the corrections below) wins on current decisions.
3. `planning/*` is the historical record; where a later decision changed it, the later decision stands.

## Corrections to the planning documents

- **Ownership.** The planning documents assume a joint LLP of three founders. Sangam is
  **solo-built and owned by imagiQa Healthcare Services Pvt Ltd**. The Founder Agreement
  (legal scaffolding, Part A) and the cost-sharing sections no longer apply; each partner
  company gets a short service MoU instead. The end-user Terms and the Privacy Policy name
  imagiQa Healthcare Services Pvt Ltd as operator and data fiduciary.
- **PostgreSQL.** The documents say PostgreSQL 16; any 16-or-later release works, and
  developers' local installs may be newer. The server image is pinned in PR-08.
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
  machine. Local development uses a native PostgreSQL 16 install; Compose (PostgreSQL + Caddy)
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
- **Partner facts.** The handoff's placeholder copy says LiPi is operated by "Lipi Systems Pvt Ltd,
  Bengaluru". LiPi is an imagiQa product (Ahmedabad). This is runtime data in the app registry,
  not a design change.

## Delivery plan

| PR | Title | Delivers |
|---|---|---|
| PR-01 | Foundation — repo skeleton + design tokens | Solution, project stubs, CI, licence, Docker Compose, tokens, logo, docs filed |
| PR-02 | Domain + Infrastructure + first OIDC endpoint | Entities, EF Core migrations, OpenIddict wired, discovery / JWKS respond |
| PR-03 | Auth screens 1–6 (Razor Pages, no-JS) | Login, register, verify, forgot / reset; Argon2id; rate limiting; Turnstile; Brevo |
| PR-04 | Consent + auth 7–8 + full OIDC flow | Consent screen, sign-out, Authorization Code + PKCE end to end |
| PR-05 | Self-service portal (Blazor Server) — screens 9–10 | Dashboard, linked apps, DPDPA data export, account deletion |
| PR-06 | Admin console (Blazor Server, dark scope) — screens 11–12 | Dashboard, users table with bulk actions, app registry, audit log, MFA mandatory |
| PR-07 | `Sangam.Client` NuGet SDK + integration sample | ~10-line partner integration, working sample |
| PR-08 | Production hardening + E2E deployment | Security headers, backups, runbooks, deployment guide, secret rotation |
