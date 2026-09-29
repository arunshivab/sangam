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
