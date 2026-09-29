# ADR-0006 — The partner console: application administrators and their limits

Status: accepted (2026-09-28, PR-07). Builds on ADR-0001 to ADR-0005.

## Context

Until PR-07 a partner's application could manage its own tenancy only through its code — the
`/api/v1` management API, authenticated as the application. A partner's own staff (LiPi's Super
Admin, say) had no way to do the same things as themselves, and anything done on their behalf was
recorded as the application, not as a person.

## Decision

### A separate host

`partners.sangamid.in` (`Sangam.Partner.Web`) is its own Blazor Server host and its own OIDC
client, `sangam-partner`, alongside the operator console. It is deliberately not a mode of the
operator console: a partner's staff must never share a host, a cookie or a code path with the
power to act across every tenant. It is dark like the operator console but on a slate-blue ground
rather than green, so nobody mistakes which console — and which powers — they are in.

### Two ranks per application

| Rank | Stored as | May |
|---|---|---|
| **Admin** | `admin` | Roles, organisations, memberships, branding, sign-in policy |
| **Owner** | `owner` | + add, promote, demote and remove the application's administrators |

Ranks live in `app_admins.role` (migration `AppAdminRoles`, existing rows default to `admin`).
One person may administer several applications with a different rank in each.

### Who makes the first owner

A platform **AppManager** or above, from the operator console's Applications page. This is the
only way an administrator can be made without having linked the application first: it is imagiQa
onboarding a partner, not a partner reaching into Sangam. After that, owners manage their own.

There is always at least one owner. The last owner cannot be removed or demoted — by anyone on the
partner console, including themselves.

### Sangam's own applications are never a partner's

The portal, the operator console and the partner console are OIDC clients in the same `apps` table
as partners' applications, owned by imagiQa — as LiPi HIS will be, so ownership cannot tell them
apart. `apps.is_platform` does (migration `PlatformApps`, set by client id and by the seeder).

A platform application can never be given partner owners, by any platform rank, and the partner
service ignores it even if an `app_admins` row exists. Nor can it be disabled from the operator
console: disabling `sangam-admin` would stop every operator's sign-in, including the one needed to
turn it back on. Re-enabling stays allowed. The console shows such rows as *platform*, without
buttons.

### Two bars at the door

The gate admits someone only if they hold at least one live `app_admins` row **and** have an
authenticator app enrolled, re-read on every visit. This console can change who holds clinical
roles in a hospital; MFA is not optional here any more than on the operator console.

### The privacy boundary

A partner sees only people who have **linked** their application — a live `app_grants` row, which
exists only because the person signed in to it and allowed access. Searching for anyone else, even
by exact email, finds nothing. A role cannot be given to someone who has not linked the
application, and a refused grant does not create the link: a person joins an application by
consenting to it, never by being added by someone else.

The machine API keeps its PR-04 behaviour of creating the link when it grants a membership,
because there the application is acting for someone it already authenticated.

Adding an administrator follows the same rule, and gives one identical refusal for an unknown
address, an unverified account and an unlinked one, so the form cannot be used to test whether an
address is registered with Sangam.

### What partners may change about their application

| Setting | Partner | imagiQa only |
|---|:--:|:--:|
| Description, brand colour, tile letter | ✓ | |
| Sign-in policy: *each person's choice* or *always two-step* | ✓ | |
| Sign-in policy: *password only* or *email code only* | | ✓ |
| Display name, redirect URIs, client secrets, enable / disable | | ✓ |

Sangam's floor is `Default` — each person's own choice. A partner may require two-step
(`PasswordAndOtp`) or return to the floor, never go below it. `Password` would override people who
chose two-step; `OtpOnly` makes the mailbox the whole account. Both are imagiQa's call, and a
platform-set value of either can only be tightened to two-step from the partner console. The rule
is `EfPartnerService.MayMoveTo`, and every transition is a test case.

### Attribution

`IManagementService`'s mutating methods now take a required `ManagementActor`: `Api` for the
machine API, `AppAdmin(userId)` for a person. Audit rows record the person as the actor with the
application in `actor_app_id`; memberships record `granted_by_user_id` and `revoked_by_user_id`,
columns the API could never fill.

A person's own activity log keeps the three apart:

- partner staff — *"An administrator of LiPi HIS gave you a role…"*
- imagiQa — *"A Sangam operator made you an owner of LiPi HIS."* (no `actor_app_id`: platform
  capacity; the application is named in the metadata)
- the application's code — *"LiPi HIS gave you a role…"*

Being made or removed as an administrator is flagged security-sensitive in that log.

## Not in this PR

- **Invitations** by email for people who have not yet linked an application — with PR-09, when
  Anjal can send the mail.
- Editing or moving an organisation after creation (the management rules fix parent and type).
- A partner-visible audit trail of their own application's changes.

## Consequences

Every rule above lives in `IPartnerService` (or `IManagementService` beneath it) and is proved by
`PartnerServiceTests` against PostgreSQL; the pages only decide what to show. The console's gate,
its rank-dependent tabs, and the absence of any other application's data are proved by
`PartnerGateTests` on the real host, including the interactivity guard from PR-06.
