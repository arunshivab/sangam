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
- [ ] *(Superseded by D-A, built in R4: `Sangam:Audit:RetentionDays` is refused; see "Added by R4" below.)*
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
- [ ] D-A and D-E (R4): audit archive and anonymisation; encrypted off-region backups with object lock (see "Added by R4").

## Added by R4

- [ ] D-A: the audit-archive key pair made on the founder's own computer (`audit-archive keygen`); only
      `audit_archive.crt` on the server; the private key and passphrase offline in two copies kept apart;
      `/srv/sangam/audit-archive` exists and is writable by the container user; the monitoring page shows the archive.
- [ ] D-E: the founder's `age` key made offline, its public line in `/etc/sangam/backup-recipients.txt`; the off-site
      bucket created **with object lock**, in another region, with `backup/lifecycle.json` applied; **E2E Object
      Storage's object-lock support confirmed** (OI-040); one nightly run seen to upload, and one off-site restore
      drill passed (`restore-drill.sh --offsite`). The second provider before the public launch.
- [ ] D-I: `demo.sangamid.in` in DNS; the demo's database and role (`imagiqa_demo`) created; `Imagiqa__ClientSecret`
      and `ConnectionStrings__Imagiqa` secret files present; the banner seen.
- [ ] Clients: every `Sangam__Clients__*__Secret` file is 32+ random characters and the same file the host reads; a
      change of a client's base address is made in `docker-compose.yml`, never in the database.
- [ ] D-D: the `grievance@sangamid.in` mailbox exists; the grievance-officer details in the privacy policy (counsel);
      `Sangam__Grievance__Holidays` set for the year; support staff know to log a grievance the day it arrives and
      never to record identity-document numbers.
- [ ] PR-21: each native or device application registered with its kind and exact redirect addresses; token exchange
      audiences (`ExchangeAudiences`) granted only where one application must call another for the same person.
- [ ] PR-22 (only if a SAML application is connected): SAML signing key and pairwise key made and kept offline as well
      (`docker-compose.saml.yml`, production README); each service provider registered on the operator console and one
      sign-in and one logout seen to work; SAML included in the penetration test (OI-045).

### Added by R5

- [ ] PR-23 (only for an application with SCIM): its SCIM base address and token set on the partner console, *Test
      connection* green, one person seen created and deactivated in the application.
- [ ] PR-24 (only for an application with webhooks): each endpoint https and public, its receiver checking the
      signature (the published test vector passes), *Send a test* seen to arrive.
- [ ] PR-25: no application attribute holds health data (read the attribute list of each application at go-live and
      after each pilot); time-limited roles seen to end on their own (one contractor role given until tomorrow).
- [ ] PR-26 (only if DigiLocker is switched on): DigiLocker partner onboarding complete (OI-046); client id, secret and
      a 32+ character subject key in `secrets/`, kept offline as well; the callback address registered; one real
      verification and one removal seen to work; `docker-compose.digilocker.yml` in the start command.
- [ ] R5: the delivery worker (SCIM, webhooks, expiry) runs in the one identity container; do not scale the identity
      service to two replicas until it is made to share the work (OI-047).

### Added by R6

- [ ] V-16: every application's back-channel logout address is public https (the console now refuses private ones);
      one sign-out seen to reach an application.
- [ ] SDKs: before a partner uses an SDK from npm, PyPI or Maven Central, the `@sangam` scope, the `sangam-client`
      name and the `in.sangamid` namespace are registered to the founder and the packages published (OI-048); until
      then partners build them from the repository.
- [x] Add the `sdks` CI job to the branch ruleset once it has passed on main. (Done at the R7 merge.)

## Added by R7 (v1.0.0-rc.1, certification readiness)

Security (docs/security/, SGM-503):

- [x] Add the `security` CI job to the branch ruleset, beside build, format and sdks. It already fails on any high or
      critical finding. (Done at the R7 merge.)
- [ ] Commission the external penetration test from `docs/security/pentest-scope.md`, on a staging copy built from
      this release. Record its findings in SGM-503 and SGM-701.
- [ ] Decide the ASVS gaps that are yours to decide (SGM-908 lists the options):
  - [ ] Password policy: keep 8 characters with classes, or move to 12 without classes (V2.1.1, V2.1.9).
  - [ ] A pepper for password hashes (V2.4.5).
  - [ ] Keys: files on the VM for the pilot, or a key vault or HSM (V6.4.2).
  - [ ] Antivirus on logo uploads (V12.4.2).
  - [ ] `__Host-` cookie names at the cut-over (V3.4.4).
  - [ ] Linking people through the management API: consent first, or pairwise ids (V4.2.1, OI-050).
- [ ] Confirm volume encryption on the E2E VM, and NTP on it (V6.1.1, V7.3.4).
- [ ] Confirm the reverse proxy sends `X-Forwarded-Proto: https` (Caddy does, unchanged). Since R7, cookies are always
      Secure, so a page with a form reached over plain HTTP answers 500 instead of setting a cookie (OI-057).
- [ ] Publish a security contact (`/.well-known/security.txt` through Caddy) and register the CERT-In point of
      contact.

Audit and evidence (PR-32):

- [ ] Turn on SIEM streaming to a store outside the VM (`Sangam:Siem:*`, docs/siem.md). Until then, the audit log's
      only copy lives with the database and its backups.
- [ ] Tell each partner about evidence packs (partner console → application → Evidence, docs/evidence-packs.md).

Accessibility (PR-31):

- [ ] Keep the three-language accessibility check in each release's gate (WCAG 2.2 AA with axe; results in SGM-507).

ISMS (SGM-907 to SGM-909, drafts):

- [ ] Adopt or amend the core policies (SGM-909) and sign the information security policy.
- [ ] Review the statement of applicability (SGM-907) and the risk treatment plan (SGM-908). Accept the residual
      risks you accept, in writing.
- [ ] Certification needs an internal audit and a management review before a certification body's stage 1 audit.
      Neither has happened.

Kubernetes (PR-33, optional):

- [ ] Only when one VM is no longer enough: `deploy/kubernetes/README.md`. It needs a replicated PostgreSQL and a
      ReadWriteMany volume for the audit archive. Since R7, every host may run the background work: one replica at a
      time takes each round.

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
- [ ] Confirm the hourly maintenance sweep is running (`Sangam:Maintenance:Enabled`). Since R7 every host may run
      it: a database lock lets one host at a time sweep.

## Added by rc.2 (v1.0.0-rc.2)

Checks (docs/security/README.md, "Automated checks"):

- [ ] Add the `migrations` CI job to the branch ruleset, beside the other five.
- [ ] In the same ruleset, turn on **Require code scanning results** for CodeQL: security alerts "High or higher",
      alerts "Errors". After the first CodeQL run on main, look through Security -> Code scanning and decide each
      alert it found in the existing code.
- [ ] Settings -> Code security: turn on Dependabot alerts and Dependabot security updates, so `dependabot.yml`'s
      weekly pull requests are joined by immediate ones for advisories.
- [ ] Watch the first nights of the `Nightly` workflow (accessibility and zap-baseline). GitHub e-mails you when a
      scheduled run fails.

Text:

- [ ] Have a native speaker read the Hindi and Malayalam texts added in rc.2: the "your sign-in uses a password"
      e-mail (`password_account_code_notice`), the two lines on the code screens, and the connection messages.
