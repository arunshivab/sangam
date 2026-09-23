# Sangam — V0 Sprint Plan
## 90-Day Execution Plan from Day 1 to Beta Launch

**Version:** v0.1
**Date:** May 2026
**Scope:** First 90 days, from legal incorporation to App #1 beta integration

---

## Assumptions Driving This Plan

1. **Founders work part-time on Sangam.** Each founder runs their own company and contributes ~10-15 hours/week to Sangam. Plan accommodates this — no all-nighters, no heroic sprints.
2. **No external hires for v0.** Three founders, no contractors. If pace slows, scope contracts, not deadlines.
3. **Legal setup happens in parallel with technical setup.** Don't sequence them; you'll lose a month.
4. **First-app integration uses the least complex partner app.** Pick the one with the simplest user model, lowest current production traffic, and most willing founder. Recommended: whichever app has *fewest existing users to migrate*.
5. **Definition of "Beta launch."** App #1 is live on Sangam identity, with 10-50 invited real users using it for production work. NOT a public launch.
6. **Stack confirmed.** ASP.NET Core 8, Blazor Server, OpenIddict, ASP.NET Core Identity, PostgreSQL, Docker Compose on Oracle Cloud Free Tier (Mumbai).

---

## Role Assignments (Recommended)

These can rotate, but Day 1 needs clear ownership. Suggested split:

| Role | Responsibility | Suggested holder |
|---|---|---|
| **Tech Lead (Year 1)** | Code review, merge approvals, architectural calls, weekly sync | Founder with most identity/auth experience |
| **Infra Lead** | Cloud, Docker, backups, monitoring, secrets | Founder with most DevOps comfort |
| **Frontend Lead** | Blazor admin UI, self-service UI, design system | Founder with most UI/UX inclination |
| **Legal/Ops Lead** | LLP registration, lawyer coordination, T&C, DPO setup | Whichever founder has bandwidth for paperwork |

Each founder picks up two roles. Tech Lead rotates every 6 months per the founder agreement.

---

## High-Level 90-Day Roadmap

```
Month 1 (Days 1-30)         Month 2 (Days 31-60)         Month 3 (Days 61-90)
─────────────────────       ─────────────────────        ─────────────────────
Foundation & Setup          Build Core Features          Integration & Beta

Week 1-2: Legal + Repo      Week 5-6: Org/App registry   Week 9-10: App #1 integration
Week 3-4: Auth core works   Week 7-8: Admin + Self UI    Week 11-12: Beta with users
```

---

## MONTH 1 — Foundation (Days 1-30)

**Goal:** By Day 30, basic OIDC server works locally and is deployed to staging. Joint Entity is incorporated or close to.

### Sprint 1 (Days 1-14): Legal + Repo Setup

**Legal track (Legal/Ops Lead):**
- Day 1-3: Engage Indian lawyer with tech-and-health expertise. Share the legal scaffolding document.
- Day 4-10: Lawyer drafts final Founder Agreement, User T&C, Privacy Policy.
- Day 7-14: File LLP incorporation paperwork. (LLP registration in India typically takes 15-30 days.)
- Day 10-14: Open bank account in LLP name (once incorporated; preliminary work in parallel).
- Day 14: All three founders sign Founder Agreement.

**Technical track (Tech Lead + Infra Lead):**
- Day 1-2: Choose final platform name. Register `.in` and `.org` domains immediately (cost ~₹2,000).
- Day 3-4: Create GitHub organisation. Set up repositories:
  - `sangam-platform` (the OIDC server + admin + self-service)
  - `sangam-client-dotnet` (the NuGet SDK)
  - `sangam-docs` (integration documentation)
- Day 4-5: Add Apache 2.0 LICENSE, README, CONTRIBUTING.md, CODE_OF_CONDUCT.md to all repos. Keep repos private until v0.1 ships.
- Day 5-7: Set up GitHub Actions CI: dotnet build, dotnet test, container build. Branch protection on main.
- Day 8-10: Create solution skeleton:
  ```
  src/
  ├── Sangam.Identity.Server/       (ASP.NET Core host, OpenIddict)
  ├── Sangam.Identity.Domain/       (entities, business logic)
  ├── Sangam.Identity.Infrastructure/ (EF Core, Postgres)
  ├── Sangam.Admin.Web/             (Blazor Server admin UI)
  ├── Sangam.SelfService.Web/       (Blazor Server user-facing UI)
  └── Sangam.Client/                (NuGet SDK package)
  tests/
  └── (one mirror per src/ project)
  ```
- Day 10-14: Wire OpenIddict basics. Spin up a minimal OIDC server that accepts authorization code flow with PKCE for a hardcoded test client. Use SQLite for now (Postgres in next sprint).

**Sprint 1 Definition of Done:**
- [ ] All three founders have signed founder agreement (or signing scheduled)
- [ ] LLP filing submitted
- [ ] Domain registered
- [ ] GitHub org created, repos initialized with licence
- [ ] CI passing on main
- [ ] OIDC `/authorize` and `/token` endpoints respond correctly for a test client
- [ ] First weekly founder sync held

### Sprint 2 (Days 15-30): Authentication Core

**Infra track:**
- Day 15-16: Provision Oracle Cloud Free Tier account. Set up 1× Ampere A1 VM (4 vCPU, 12 GB RAM) in Mumbai region. Install Docker, Docker Compose, Caddy.
- Day 17-18: Set up PostgreSQL 16 in Docker on the VM. Create separate databases (`sangam_identity`, `sangam_audit`, plus placeholders for partner apps).
- Day 19-20: Set up automated `pg_dump` backup to Oracle Object Storage (within free tier).
- Day 21-22: Set up Caddy reverse proxy with auto-SSL via Let's Encrypt. Configure domain DNS.

**Backend track (Tech Lead):**
- Day 15-18: Replace SQLite with Postgres. EF Core migrations working. Schema for Users, Sessions, OIDC tokens.
- Day 19-22: Integrate ASP.NET Core Identity. User registration endpoint (email + password). Argon2id hashing configured. Rate limiting on login/register.
- Day 23-26: Email verification flow. Sign up with Brevo. Wire MailKit. Test deliverability to Gmail, Outlook, three test corporate domains.
- Day 27-28: Password reset flow.
- Day 29-30: Login with MFA-ready scaffolding (MFA itself is v1 — keep the hooks ready). Session management with sliding expiry.

**Frontend track (Frontend Lead):**
- Day 15-22: Set up MudBlazor in `Sangam.SelfService.Web`. Build registration page, login page, email verification confirmation page, password reset request and reset pages.
- Day 22-28: Build first version of consent screen (will be wired to real consent logic in Month 2).
- Day 28-30: Polish: error states, loading states, mobile responsive layout.

**Sprint 2 Definition of Done:**
- [ ] Staging environment live on `staging.sangam.in` (or chosen name)
- [ ] User can register, verify email, log in, log out
- [ ] User can reset password
- [ ] OIDC `/authorize → /token → /userinfo` flow works end-to-end with a real test client app
- [ ] PostgreSQL backups running nightly, restore test successful at least once
- [ ] Caddy auto-SSL working, HTTPS only
- [ ] LLP incorporation in progress or completed
- [ ] Lawyer's revised Founder Agreement returned for review

---

## MONTH 2 — Core Features (Days 31-60)

**Goal:** By Day 60, Sangam has full org/app/consent/role model, admin UI, and is feature-complete for App #1 integration.

### Sprint 3 (Days 31-45): Organisations, Apps, Consent

**Backend track (Tech Lead):**
- Day 31-34: Implement Organisation entity. Endpoints: create org, list orgs, view org, edit org metadata.
- Day 35-37: Implement OrgMembership. Endpoint to add/remove users from orgs with a role.
- Day 38-40: Implement App registry. Apps are OIDC clients. Endpoint to register a new app (admin only), generate client secrets, manage redirect URIs.
- Day 41-43: Implement AppGrant — the per-user-per-app access record. Implement Consent — captured at first link.
- Day 44-45: Token claims now include `orgs[]`, `app_id`, `role` (per the data model doc). Tested via real OIDC flow.

**Frontend track (Frontend Lead):**
- Day 31-38: Build the consent screen — fully functional. Shows: which Partner App is requesting access, what data will be shared, current list of all partner apps, opt-in toggle for future partners notification.
- Day 39-45: User self-service UI:
  - View profile, edit name/phone
  - View orgs you belong to, your role in each
  - View apps you have access to
  - Revoke access to an app
  - View active sessions, "log out everywhere"
  - Initiate data export (DPDPA right of access)
  - Initiate account deletion (DPDPA right of erasure)

**Infra track (Infra Lead):**
- Day 31-35: Set up Seq for log aggregation. Wire Serilog in all .NET projects.
- Day 36-40: Set up UptimeRobot for synthetic health checks. Set up Grafana Cloud free tier for metrics dashboards.
- Day 41-45: Document operational runbook: how to deploy, how to roll back, how to handle outage, where the secrets are, who has access.

**Sprint 3 Definition of Done:**
- [ ] User can register, choose to create an org or join one
- [ ] Admin can register a new app and get client credentials
- [ ] When user logs into App #1 (test client) for first time, consent screen appears and works
- [ ] User's token contains org and role claims
- [ ] User can view all their data via self-service UI
- [ ] Logging, monitoring, uptime checks all green

### Sprint 4 (Days 46-60): Admin UI and Audit

**Frontend track (Frontend Lead + Tech Lead):**
- Day 46-50: Admin UI (Blazor Server, separate project, MFA-protected):
  - Admin login with MFA mandatory (TOTP from Day 1 for admins, even though end-user MFA is v1)
  - User search, view, suspend, force-logout
  - Org search, view, edit
  - App registry: create, view, rotate secrets, set redirect URIs
  - Audit log viewer with search and filters
- Day 51-55: Operator dashboards: total users, daily logins, error rates, system health.
- Day 56-60: Polish, accessibility check, mobile-friendly check, internal dogfooding.

**Backend track (Tech Lead):**
- Day 46-50: Audit log fully wired. Every identity event recorded: login (success/fail), registration, password reset, app grant, app revoke, consent given/revoked, admin actions.
- Day 51-55: Data export endpoint (DPDPA). Generates JSON of all user data within 24 hours, emails download link.
- Day 56-60: Account deletion flow. Soft-delete first (30-day reversal window for user), then hard-delete with cascade. Audit log retains pseudonymised event references for legal retention.

**Infra track (Infra Lead):**
- Day 46-50: Set up secrets management properly. Move all secrets from environment variables to Oracle Vault or a dedicated KeePassXC + age-encrypted-file pattern. Document the secret rotation procedure.
- Day 51-55: First disaster recovery drill. Simulate full DB loss, restore from backup, validate user data integrity.
- Day 56-60: Write deployment script: one-command deploy from main branch to staging, with rollback.

**Legal/Ops track:**
- Day 46-50: Final user T&C and Privacy Policy delivered by lawyer. Reviewed by all three founders.
- Day 51-55: Sangam website (single landing page) describing the platform, listing partners (just the three founders' companies), linking T&C and Privacy Policy.
- Day 56-60: DPO appointed (can be one of the founders, or fractional external service). DPO email address set up. Breach response document drafted.

**Sprint 4 Definition of Done:**
- [ ] Admin UI is functional — operator can manage everything
- [ ] Audit log captures all identity events, searchable
- [ ] Data export works (user gets their data as JSON)
- [ ] Account deletion works (soft → hard delete with cascade)
- [ ] DR drill completed successfully
- [ ] All secrets in proper secrets management, not in code or environment files in git
- [ ] User T&C and Privacy Policy published on website
- [ ] DPO appointed, contact published

---

## MONTH 3 — Integration & Beta (Days 61-90)

**Goal:** By Day 90, App #1 is live on Sangam identity with 10-50 real beta users.

### Sprint 5 (Days 61-75): NuGet SDK + App #1 Integration

**Backend track (Tech Lead):**
- Day 61-65: Build `Sangam.Client` NuGet package. Thin wrapper over `Microsoft.AspNetCore.Authentication.OpenIdConnect` with Sangam-specific conventions: easy configuration, claim helpers, role helpers, org context helpers.
- Day 66-70: Write the integration guide: step-by-step how to add Sangam auth to an ASP.NET Core or Blazor app. Include code samples, test client app repo, troubleshooting.
- Day 71-75: Stand-by integration support for App #1.

**App #1 integration track (Owner of App #1, supported by Tech Lead):**
- Day 61-65: Code branch in App #1 to integrate Sangam. Replace existing auth with Sangam OIDC client. Read user_id, email, orgs, role from ID token. Map Sangam user_id to App #1's internal user table.
- Day 66-70: Migration logic: if App #1 already has users, write a migration script. Either (a) one-time bulk import of users into Sangam, or (b) shadow accounts — first time existing users log in, they re-authenticate against Sangam, get linked to their existing App #1 records.
- Day 71-75: End-to-end testing: registration, login, logout, password reset, role-based access, org switching (if applicable), session expiry.

**Infra track (Infra Lead):**
- Day 61-65: Set up production environment. Same VM size for now, just promoted from staging. Domain switched to `sangam.in` (apex domain). Production Postgres separate from staging.
- Day 66-70: Set up production monitoring alerts: pager on error rate spike, downtime, certificate near-expiry, disk space.
- Day 71-75: Soft launch readiness review. Checklist: backups verified, alerts firing correctly, runbook accessible to all three founders, on-call rotation defined.

**Sprint 5 Definition of Done:**
- [ ] `Sangam.Client` NuGet package version 0.1.0 published (internal feed for now)
- [ ] Integration guide complete with working code samples
- [ ] App #1 successfully integrated in dev/staging environment
- [ ] Production environment live
- [ ] On-call rotation between three founders documented and tested

### Sprint 6 (Days 76-90): Beta Launch

**All hands:**
- Day 76-80: Identify 10-50 beta users for App #1. Communicate the change (their login moves from "old App #1 auth" to "Sangam-powered login"). Set expectations: this is beta, expect bugs, please report issues to [beta@sangam.in].
- Day 81-83: Production cutover for App #1. Migrate users (with their re-consent for Sangam identity sharing). Monitor closely.
- Day 84-87: Daily standup among the three founders. Quick triage of any beta issues. Fix forward, don't roll back unless catastrophic.
- Day 88-89: Beta retrospective. What worked, what broke, what's needed for v0.2 (and what's deferred to v1).
- Day 90: Decide go/no-go for App #2 integration. If go, plan the next 30 days.

**Sprint 6 Definition of Done:**
- [ ] 10-50 real beta users using App #1 via Sangam identity
- [ ] No critical bugs open for > 48 hours
- [ ] Founder retrospective complete
- [ ] Plan for App #2 integration drafted

---

## Risks and Mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| LLP registration takes > 30 days | Medium | Start Day 1. Don't block code work on it — operate as informal partnership until LLP active. Sign founders' MoU early. |
| Lawyer engagement is slow | Medium | Engage Day 1. Provide the legal scaffolding doc as starting point — saves them weeks. |
| One founder has unexpected commitments to their own company | High | Build slack into plan. If a sprint slips by a week, the next sprint absorbs it; final beta date is the only hard target. |
| Oracle Cloud Free Tier limits or suspension | Low | Have E2E Networks account ready as backup. Docker Compose makes migration ~1 day. |
| Email deliverability issues with Brevo to Indian corporate domains | Medium | Test early (Sprint 2). If issues, fall back to Resend or Amazon SES. |
| App #1 integration is harder than expected | Medium | Pick simplest app first. If integration takes longer, defer Apps #2 and #3 — don't compress. |
| Real users find bugs we missed | Certain (it's beta) | Daily standup during beta. Triage rule: critical = fix today; major = fix this week; minor = backlog. |
| Founder disagreement on direction | Medium | Use the founder agreement decision framework. Don't relitigate decisions already made. |

---

## What's Explicitly NOT in v0 (Deferred to v1)

To prevent scope creep, these are explicitly **out of scope** for the 90-day v0:

- End-user MFA (TOTP, WebAuthn) — only admin MFA is in v0
- Magic link / passwordless login
- Social login (Google, etc.)
- Mobile SDKs
- Multi-language UI (English-only in v0)
- Org-level branding (logos, colors per app)
- Hierarchical orgs (parent/child)
- SAML support
- SCIM provisioning
- Org admin invite flow (admins add users via admin UI in v0; self-service invites come in v1)
- Apps #2 and #3 integration (after v0 beta succeeds with App #1)
- White-label per partner
- Webhooks for identity events
- Advanced audit log search and CSV export
- VAPT / security audit (scheduled for Month 9)

Anyone proposing to add any of these in v0 must propose another item to remove. **Scope discipline is the single biggest determinant of whether v0 ships on time.**

---

## Weekly Cadence

- **Monday morning:** 30-minute founder sync. Review last week, plan this week, surface blockers.
- **Wednesday midday:** Async progress check in shared channel (Slack/Discord). Each founder posts a 3-line update.
- **Friday end-of-day:** Demo and commit. Whatever was built this week gets demoed to other founders. Code merged or backlogged.

Don't meet more than this. Code, not meetings, is what ships.

---

## Definition of v0 Success

**Hard success criteria (must-have):**
- App #1 is in production on Sangam identity
- At least 10 real users have actively used it for at least 1 week
- No data loss or breach
- Cost is under ₹500/month total for first 90 days
- LLP is registered, agreements signed

**Soft success criteria (nice-to-have):**
- App #2 integration plan committed
- v0.2 backlog prioritised
- One independent contributor has submitted a PR (signals open-source health)

If hard criteria hit by Day 90, this is unambiguously a win. Celebrate, take a weekend off, then plan v1.

---

*End of v0 sprint plan.*

*Next document in the set: multi-tenancy and data model design.*
