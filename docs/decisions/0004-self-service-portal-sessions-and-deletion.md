# ADR-0004 — The self-service portal, sessions, and account deletion

Status: accepted (2026-09-25, PR-05). Builds on ADR-0001 to ADR-0003.

## The portal is an ordinary OIDC client

`account.sangamid.in` authenticates through `id.sangamid.in` with Authorization Code + PKCE,
its own `client_id` (`sangam-portal`) and its own cookie — exactly the flow a partner app uses.
A shared cookie across subdomains would have been less code, but it would bypass the flow the
rest of the platform depends on. Running the portal on its own rails means a break in the code
flow breaks our own product first, and it proves the shape `Sangam.Client` must have in PR-07.

Consequence: the portal appears on its own consent screen on first use, and in the user's own
list of connected apps. Every app is third-party (ADR-0003) and the portal is not exempt. It is
shown in the list without a Revoke button — revoking the page you are looking at is a footgun,
not a feature — labelled "Your account portal".

## Sessions are recorded rows, not inferred from tokens

`user_sessions` holds one row per browser sign-in. The session cookie carries the row id
(`sangam:sid`), and the five-minute security-stamp validation also confirms the row is live, so
revoking one row ends exactly one cookie — the point of the feature, for a clinic where several
people share a workstation. The alternative considered (listing OpenIddict authorizations only)
needed no new table and no write on the sign-in path, but could not end a single browser
session, which is the action that actually matters when a device is lost.

The row stores only what the request tells us: client IP, user agent, and the time. Sangam
does not guess a location: geo-IP gives a city at best and nothing at all on a hospital LAN,
and the screen says so in as many words. The user agent is summarised crudely
("Chrome on Windows") and reports "Unknown browser" rather than pretending.

A device name comes from the application, never from us: an app may send `sangam_device` on the
authorization request ("First Floor Radiology") and Sangam stores that string, shows it escaped,
and falls back to browser + IP when it is absent. Mapping IP ranges to floor names inside Sangam
was rejected: that is the clinic's network topology, it belongs to the app, and it is wrong the
moment DHCP changes.

The token's standard OIDC `sid` claim carries the session id to clients, so the portal can mark
"This device" in its own list.

Revoked rows are kept 90 days for the audit trail, then deleted by the maintenance sweep.

## The audit view shows everything about the user

Both what the user did and what was done to them (an app granting a role, an operator acting on
the account), with the actor named and the event phrased as a sentence
(`AuditNarrator`). An action with no phrasing falls back to its raw name, so a new action
appears in the portal rather than silently disappearing from it. The range selector offers
30 days / 90 days / 1 year / everything — the retention in ADR-0001 (five years for
healthcare-related events, one year otherwise) is what actually bounds it, not the view.

## Deletion: automatic after a grace period, with an operator hold

Requesting deletion sets `purge_after = now + 30 days`, suspends the account, ends every session
and revokes every application immediately. Signing in is blocked during the grace period; the
user can cancel and everything returns. `AccountPurgeService` sweeps hourly and, past the date,
**pseudonymises** the user row — name, email, mobile, password hash, date of birth and gender
destroyed, the email replaced with an unusable placeholder — while keeping the row and every
audit event, so foreign keys hold and the log stays coherent.

Operators get both directions: `hold_placed_at` blocks the sweep for a legal or investigative
hold (with the reason recorded), and an operator can delete sooner. The user-facing screen says
when a hold is in force.

## Export is a direct download

The JSON file is generated on request and handed to the browser as a blob: no link is minted, so
there is nothing to leak, log or forward. It contains the profile, applications, consents,
memberships, sessions and the full narrated activity log, and states plainly that the password
is absent because only a one-way hash exists.

## Which profile fields a user may change, and how a change reaches applications

Name, language, gender and mobile are self-service. A changed mobile is marked unverified
again, because nothing can verify it until mobile OTP ships.

Email is fixed here: it is the sign-in identifier and the address codes are sent to, so changing
it needs its own flow — a code to the new address and a notice to the old one. Date of birth is
fixed because applications rely on it to identify a person; it becomes changeable once DigiLocker
offline verification exists, after which a verified name, date of birth and gender are locked
until re-verification.

Sangam never pushes profile changes to applications and there is no standard back-channel for it.
What it does instead: `/connect/userinfo` always reads the user fresh; tokens minted by a refresh
are rebuilt from the database; and the standard OIDC `updated_at` claim moves on every profile
change, so an application can compare it with the copy it cached and re-read. The SDK (PR-07)
will make that the easy path. Webhooks are deferred until a partner needs push.

The reverse direction is refused by design: an application must not write a user's identity into
Sangam. A correction made at one clinic's front desk would otherwise rename that person at every
other clinic. Where an application's record and Sangam disagree, the application reports the
variance and the **user** decides in the portal (designed in ADR-0005, after PR-08).

## Minimum age: 18

Self-registration is closed below 18 (`AgePolicy`). The line is drawn from data-protection law,
not from employment law: India's DPDP Act treats anyone under 18 as a child, and processing a
child's data needs verifiable consent from a parent or lawful guardian, which Sangam has no
mechanism for. A threshold of 16 — the practical minimum for healthcare trainees, and the reason
16 was first considered — would leave 16- and 17-year-olds inside the Act's definition of a child
with no guardian consent recorded anywhere, which is the exposure the rule exists to avoid. (An
occupational argument would in fact give a lower number still: 14 for non-hazardous work.)

The age is declared, not proven. Until DigiLocker verification exists this is an honest barrier
and a compliance posture, not an assurance. The registration form's date picker stops at the
latest eligible date, and the server rejects the rest with a message pointing at support.

An account for a child is deliberately *not* this account with a lower limit. It would be a
separate type: created by a guardian, linked to them, with the guardian consenting and able to
revoke applications on the child's behalf, and handed over to the young person at 18. Building
that later is unaffected by this rule. Revisit if a partner actually asks.
