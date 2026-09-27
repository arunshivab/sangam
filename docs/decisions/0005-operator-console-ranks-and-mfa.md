# ADR-0005 — The operator console: ranks, mandatory MFA, and what is never built

Status: accepted (2026-09-25, PR-06). Builds on ADR-0001 to ADR-0004.

## Four ordered ranks

Everyone in `platform_operators` is an operator. The rank is how much they may do, and the ranks
are cumulative, so every check in `IAdminService` is a single comparison — `role >= minimum`:

| Rank | Stored as | May |
|---|---|---|
| **Viewer** | `viewer` | Search users, open a record, list applications |
| **AppManager** | `app_manager` | + register and configure applications; enable or disable one |
| **Support** | `support` | + act on a user: suspend, reinstate, sign out everywhere, hold a deletion |
| **Owner** | `owner` | + grant and revoke console access; delete an account outright |

The names were chosen after the PR-02 names turned out to mislead: `Support` was the *read-only*
rank, which is the opposite of what a support desk does, and `Operator` was both one rank and the
word for everyone in the table. They were renamed while the table was still empty, so migration
`RenamePlatformRoles` is a no-op on every existing database. Its two statements must run in the
order written — `support → viewer` before `operator → support` — or the second collides with rows
the first has not moved.

`AppManager` exists so someone can run the application registry without being able to touch a
user's account. It sits *below* Support rather than beside it, because Support genuinely may do
everything an AppManager may; that keeps the ranks linear. A rank that had to be forbidden
something a lower rank may do would break the comparison model and need capability flags instead.
Don't add one without revisiting this.

Authorisation lives in the service, never in the Razor pages. The pages hide buttons a rank cannot
use, but that is courtesy: the tests call the service directly and prove every boundary.

## Two bars before the console opens

1. An unrevoked `platform_operators` row.
2. An enrolled authenticator app (TOTP).

Both are re-checked by `OperatorGate` on every visit rather than trusted from the sign-in, so
someone granted access mid-session must still enrol. An operator cannot remove their own
authenticator — `DisableAsync` refuses while the row exists — so the second bar cannot be lowered
from inside. A person without a rank is told plainly that the console is not for them and is
shown no data.

The authenticator uses ASP.NET Core Identity's own TOTP provider and token store: no new tables,
no hand-rolled cryptography. The QR code is rendered as inline SVG on this server; a hosted QR
service would mean sending the shared secret to a third party. Ten recovery codes are issued once
and shown once. Wrong codes count towards the same lockout as wrong passwords.

The TOTP option is open to every user, not only operators — it is stronger than an emailed code
because it does not depend on a mailbox.

## Looking is recorded

Opening a user's record writes `admin.user.read` **before** anything is returned, and the user
sees "A Sangam operator opened your account record" in their own audit log, flagged. Searching is
not audited — a list is not a dossier — but the list is kept thin on purpose.

Actions that change an account (suspend, hold, delete now) require a typed reason, stored in the
audit row's metadata.

## Guard rails

- The last owner cannot be revoked; the console would lock everyone out.
- An account holding console access cannot be deleted until its access is revoked.
- Delete-now is Owner-only, needs a reason, and pseudonymises exactly as the scheduled purge does:
  personal data destroyed, row and audit trail kept.
- Disabling an application stops every sign-in to it; it does not reach inside that application's
  tenancy. The platform governs whether an application may operate, not how it runs.

## Bootstrapping

`dotnet run --project src/Sangam.Admin.Web -- create-operator <email>` makes the first Owner,
writes a `system` audit row, and then **refuses forever** once any operator exists — so it can
never be used to quietly add a second one. After that, Owners grant access from the console.

## Never built

**Signing in as a user.** Not deferred — refused. In a clinical setting, impersonation would let
an operator act inside a hospital application *as a doctor*, which is a patient-record integrity
problem and not merely a security one. The interface's own documentation says so, so that anyone
proposing it later meets the argument where the code is.

**Reading a password** is impossible rather than disallowed: only Argon2id hashes exist.

**Editing a user's name, date of birth or email** is not an operator power at any rank. Identity
fields belong to the person, by the same reasoning that stops an application writing them back.

## What is not here yet

The application plane's human side — `app_admins`, which has existed as a table since PR-02 with
no surface at all. A partner's own administrator cannot yet sign in and manage their roles; every
application acts as a machine through `/api/v1`. That is PR-07.
