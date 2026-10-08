# Production go-live checklist

Written in PR-06, for PR-10 (production hardening and deployment). Nothing here can be done
earlier: the identity server refuses to start outside Development until production signing keys
exist, so there is no production database to act on yet.

## Before the first boot

- [ ] Production signing and encryption certificates configured (`Sangam:Certificates`), not the
      development keys. The server refuses to start without them — keep it that way.
- [ ] Real client secrets for `sangam-portal`, `sangam-admin` and `sangam-partner`, generated fresh and stored as
      secrets. The development values (`…-change-me`) must never reach production.
- [ ] `Sangam:Seed:DevelopmentSample` is **false**. The seeder creates the sample partner app and
      development redirect URIs; none of that belongs in production.
- [ ] `Sangam:Email:UseOutbox` is **false**, and Anjal's API (D-B) is configured with production
      credentials (`Sangam__Anjal__BaseUrl`, the `Sangam__Anjal__ApiKey` secret). `/dev/outbox` and `/dev/callback` return 404 outside Development —
      verify it on the live host.
- [ ] `sangamid.in` SPF, DKIM and DMARC published and passing. Send one verification code to a real
      inbox and read the headers: SPF, DKIM and DMARC must all say **pass**. Under a strict
      `p=reject` policy one misaligned header means every code is silently dropped.
- [ ] Redirect and post-logout URIs registered for the real hosts only
      (`account.sangamid.in`, `admin.sangamid.in`, `partners.sangamid.in`).
- [ ] `is_platform` is **true** on exactly Sangam's own three clients and nothing else. Migration
      `PlatformApps` sets it for the client ids `sangam-portal`, `sangam-admin` and `sangam-partner`;
      if production registers them under different ids, set it by hand. Without it the operator
      console would offer to disable its own client — and lock every operator out.

## Added by R0

- [ ] The application connects as a database role that does **not own** `audit_events` (and cannot
      alter it), separate from the role that runs migrations. The append-only triggers stop the
      application; only role separation stops someone with the application's credentials.
- [ ] `Sangam:ForwardedHeaders:KnownNetworks` (or `KnownProxies`) set to Caddy's network, so client
      addresses — in the audit trail and the rate limits — are the real ones.
- [ ] Monitoring points at `/health/live` and `/health/ready` on all four hosts.
- [ ] The data-protection key ring (`data_protection_keys`) is protected at rest with a certificate,
      and included in backups (SGM-603).
- [ ] `Sangam:Audit:RetentionDays` set — or deliberately left unset — once the founder has decided the
      retention periods. Unset keeps every audit event.
- [ ] The identity server starts: it refuses without a real e-mail sender or with the outbox on.

## Added by R1

- [ ] *(Superseded by D-B: Anjal's API, not SMTP — see "Founder decisions D-A to D-M" below.)*
- [ ] `scripts/check-mail-dns.ps1 -Domain sangamid.in -DkimSelector <selector> -AnjalSpf <Anjal's SPF include>` passes.
- [ ] Token certificates created, stored as secret files, and configured (`Sangam:Certificates`); the
      server starts.
- [ ] Two database roles: the owner runs the migrator; the hosts use the application role.
- [ ] Migrations applied with the `migrator` image before the hosts start.
- [ ] `scripts/backup-db.sh` in cron, with an off-site copy; `scripts/restore-drill.sh` run once and
      its time recorded in SGM-603.
- [ ] *(Superseded by D-H: no telemetry collector; Sangam monitors itself — see below.)*
- [ ] Terms and privacy notice reviewed by counsel (OI-002) and configured (`Sangam:Legal:TermsPath`,
      `PrivacyPath`); `/terms` and `/privacy` show them, not the pending notice.
- [ ] Grievance officer named and configured — D-D: Arun Shiva Balasubramanian, grievance@sangamid.in,
      acknowledged within 2 working days, resolved within 30 (set in `appsettings.json`); the
      `grievance@sangamid.in` mailbox exists on Anjal, and a grievance log is kept (SGM-108, R4).

## Added by R2

- [ ] A key-ring certificate created (separate from the token certificates), stored as
      `keyring_current.pfx` with its password secret; every host starts (they refuse without it).
      On rotation, the new certificate goes first and the old one stays listed until its keys expire.
- [ ] `Sangam__Passkeys__RpId` is the identity server's host (`id.sangamid.in`) and
      `Sangam__Passkeys__Origins` its exact origin (`https://id.sangamid.in`). **The RP ID cannot be
      changed later without every passkey stopping working.**
- [ ] A passkey added and used once on a phone and on a laptop, on the production host.
- [ ] SMS left **off** (`Sangam__Sms__Enabled=false`) until all of these exist (D-M): the **new company**
      registered as principal entity on a DLT platform; the header registered; the templates (`sign_in`,
      `mobile_verification`, `step_up`, `registration_notice`, `reset_notice`, `operator_alert`) registered
      with the exact texts in `SmsSettings` and their ids in `Sangam__Sms__Templates__<key>__Id`;
      Anjal's SMS endpoint live with its two aggregators (`Sangam__Sms__Provider=anjal`);
      `Sangam__Sms__HashKey` (32+ random characters) as a secret. Then switch it on for every host together.
- [ ] With SMS on: `Sangam__Sms__DailyAlertThreshold` set, and a test text received on a real phone.
- [ ] *(Superseded by D-J: the breached-password check is offline only — see below.)*
- [ ] Support procedure for a lost authenticator written into SGM-805 and SGM-813 (who may do it, the
      proofing methods, the reference format), and the operators told never to record ID numbers.

## R3: languages, branding and logout

- [ ] The Hindi and Malayalam screens and e-mails reviewed by native speakers (the catalogues are
      `src/Sangam.Web.Shared/Localization/*.json` and `src/Sangam.Identity.Infrastructure/Customisation/DefaultTemplates.json`;
      the open questions are in the R3 evidence, `i18n/REVIEW-NOTES.md`).
- [ ] With SMS on: each Hindi and Malayalam text message registered on the DLT platform and saved, with its
      template id, in the operator console → Defaults. Until then texts go in English.
- [ ] The root page `/` of the identity server replaced (it is still the PR-01 design-foundation check).
- [ ] `sangam_org` confirmed as the parameter name before partners build against it.
- [ ] Each partner application's back-channel (and, if wanted, front-channel) logout address registered in the
      operator console → Applications → Logout, and one sign-out seen to reach it.

## Founder decisions D-A to D-M (6–7 October 2026)

Staged go-live (D-I):

1. **Private pilot** (right after R3): Sangam on the production server with
   `Sangam__Registration__InvitationOnly=true` (testers by invitation link, or listed in
   `Sangam__Registration__AllowedEmails`); the founder and invited testers only. The first pilot application is the
   imagiQa sample at `demo.sangamid.in`, bannered "Demo, not for real patient data" (Compose, Caddy and its
   registration: SGM-108, R4); Anjal webmail is the second.
2. **Controlled launch** once: the legal texts are approved by counsel (D-C); Anjal's API is connected (D-B); a
   backup **and** restore drill has passed on the real server (D-E); monitoring is live (D-H).
3. **Public launch for hospitals** after: a penetration test; the second backup provider (D-E); ideally the company
   formation (D-C, D-M).

Checks:

- [ ] D-B: `Sangam__Anjal__BaseUrl` is Anjal's API (https, or `http://anjal:<port>` on the server's own network);
      `Sangam__Anjal__ApiKey` is a secret file given to **all four** hosts; `Sangam__Anjal__AllowedRecipients` is
      **empty** in production (set it on staging). Anjal's endpoints match `docs/anjal-messaging-contract.md`, or
      `EmailPath`/`SmsPath`/`ApiKeyHeader` are set to Anjal's. One real code received through Anjal.
- [ ] D-C: `Sangam__Operator` and `Sangam__Jurisdiction` unchanged (Dr. Arun Shiva Balasubramanian; Ahmedabad) until
      the new company takes Sangam over; then change them, the seeded applications' owner, and the legal texts.
- [ ] D-F: PostgreSQL 18 on the server (18.6 or later); local installs updated from 18.3.
- [ ] D-G: passkeys use ASP.NET Core Identity's own store (`user_passkeys`); add one passkey on a phone and one on a
      laptop on the production host after migration `IdentityPasskeys`.
- [ ] D-H: `Sangam__Monitoring__Evaluate=true` on the identity server only; `ExpectedHosts`, `TlsHosts`,
      `AnjalHealthUrl` set; `Sangam__Alerts__Emails` and `Sangam__Alerts__Mobiles` are the founder's (or left empty
      to use every Owner operator's address and verified mobile). `/srv/sangam/status` exists and the backup and
      restore-drill scripts write to it; the monitoring page shows both. The outside watchdog on Anjal's server calls
      `https://id.sangamid.in/health/ready` every minute (Anjal project). Thresholds reviewed
      (`Sangam__Monitoring__Alerts__*`).
- [ ] D-J: the Pwned Passwords list downloaded with the official downloader, imported on the server
      (`docker compose run --rm identity breach-list import --source … --output /var/lib/sangam/pwned/pwned-passwords.bin --date YYYY-MM-DD`,
      with `/srv/sangam/pwned` mounted writable for the import), the monitoring page shows its date; only then
      `Sangam__Passwords__BreachCheck__Enabled=true`. Refresh every few months.
- [ ] D-K: support staff know that a reset now waits 24 hours (72 for privileged accounts), that the verification
      method is required, and that the urgent override alerts the founder.
- [ ] D-L: `Sangam__Registration__ConcealExistingAccounts` is **true** (the default).
- [ ] D-M: SMS stays off until DLT is registered under the new company (above).
- [ ] D-A and D-E (R4): audit archive and anonymisation; encrypted off-region backups with object lock.

## Bootstrapping the first owner — a deliberate, one-time act

Nothing created on a developer's machine reaches production: migrations carry schema, never rows.
The first owner is made on the production database, once:

1. Register a **real** account at `id.sangamid.in` — the email must be deliverable, because it is
   where verification and password-reset codes go, and the last-resort recovery path.
2. Verify it.
3. On the server, once:
   `dotnet Sangam.Admin.Web.dll create-operator <that email>`
   It grants Owner, writes a `system` audit row, and refuses forever after.
4. Sign in to `account.sangamid.in` → Personal details → set up an authenticator app.
5. Save the ten recovery codes **offline**, somewhere that survives the phone being lost.
6. Open `admin.sangamid.in` and confirm the console opens.

## Two owners, on two phones

The last-owner guard stops anyone *revoking* the only owner. It cannot help if that one owner loses
their phone **and** their recovery codes — then nobody can open the console, and the only way back
is editing the production database by hand. So:

- [ ] Grant **Owner** to a second trusted person from the console, on the first day.
- [ ] Both enrol an authenticator on their **own** phone. Never share one device or one secret.
- [ ] Both store their recovery codes offline, separately.

## Owner mailbox

Decide whether the owner accounts use personal addresses or a role mailbox such as
`ops@sangamid.in`. A role mailbox survives staff changes; the authenticator must still be tied to a
named person, because the audit log records *who* acted.

## After go-live

- [ ] Restore test of the database backup — a backup that has never been restored is a hope, not
      a backup.
- [ ] Confirm the hourly maintenance sweep is running (`Sangam:Maintenance:Enabled` true on exactly
      one node).
