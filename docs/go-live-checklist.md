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
- [ ] `Sangam:Email:UseOutbox` is **false**, and the Anjal sender (PR-09) is configured with
      production credentials. `/dev/outbox` and `/dev/callback` return 404 outside Development —
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

- [ ] Anjal's SMTP submission settings in `Sangam__Email__Smtp__*` and its account in the secret files
      (OI-027). `Sangam:Email:Smtp:AllowedRecipients` is **empty** in production.
- [ ] `scripts/check-mail-dns.ps1 -Domain sangamid.in -DkimSelector <selector>` passes.
- [ ] Token certificates created, stored as secret files, and configured (`Sangam:Certificates`); the
      server starts.
- [ ] Two database roles: the owner runs the migrator; the hosts use the application role.
- [ ] Migrations applied with the `migrator` image before the hosts start.
- [ ] `scripts/backup-db.sh` in cron, with an off-site copy; `scripts/restore-drill.sh` run once and
      its time recorded in SGM-603.
- [ ] Telemetry collector chosen and `Sangam__Telemetry__OtlpEndpoint` set, or left unset knowingly.
- [ ] Terms and privacy notice reviewed by counsel (OI-002) and configured (`Sangam:Legal:TermsPath`,
      `PrivacyPath`); `/terms` and `/privacy` show them, not the pending notice.
- [ ] Grievance officer named and configured (`Sangam:Grievance:OfficerName`, `Email`, `Address`,
      `ResponseDays`); `/privacy/grievance` shows them.

## Added by R2

- [ ] A key-ring certificate created (separate from the token certificates), stored as
      `keyring_current.pfx` with its password secret; every host starts (they refuse without it).
      On rotation, the new certificate goes first and the old one stays listed until its keys expire.
- [ ] `Sangam__Passkeys__RpId` is the identity server's host (`id.sangamid.in`) and
      `Sangam__Passkeys__Origins` its exact origin (`https://id.sangamid.in`). **The RP ID cannot be
      changed later without every passkey stopping working.**
- [ ] A passkey added and used once on a phone and on a laptop, on the production host.
- [ ] SMS left **off** (`Sangam__Sms__Enabled=false`) until all of these exist: imagiQa registered as
      principal entity on a DLT platform; the header registered; the three templates (`sign_in`,
      `mobile_verification`, `step_up`) registered with the exact texts in SGM-206 §3 and their ids in
      `Sangam__Sms__Templates__<key>__Id`; the provider chosen and its adapter in the build;
      `Sangam__Sms__HashKey` (32+ random characters) as a secret. Then switch it on for every host together.
- [ ] With SMS on: `Sangam__Sms__DailyAlertThreshold` set, and a test text received on a real phone.
- [ ] Breached-password check decided: either left off knowingly, or `Sangam__Passwords__BreachCheck__Enabled`
      set and outbound HTTPS to the range endpoint allowed from the identity server.
- [ ] Support procedure for a lost authenticator written into SGM-805 and SGM-813 (who may do it, the
      proofing methods, the reference format), and the operators told never to record ID numbers.

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
`ops@imagiqa.in`. A role mailbox survives staff changes; the authenticator must still be tied to a
named person, because the audit log records *who* acted.

## After go-live

- [ ] Restore test of the database backup — a backup that has never been restored is a hope, not
      a backup.
- [ ] Confirm the hourly maintenance sweep is running (`Sangam:Maintenance:Enabled` true on exactly
      one node).
