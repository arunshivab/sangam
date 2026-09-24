# ADR-0003 — Authorization Code flow, consent, and the management API

Status: accepted (2026-09-24, PR-04). Builds on ADR-0001 and ADR-0002.

## The flow

Authorization Code with **PKCE required for every client**, confidential ones included.
Refresh tokens are issued only when the app asks for `offline_access`.

| Artefact | Lifetime |
|---|---|
| Authorization code | 5 minutes |
| Access token | 1 hour |
| ID token | 1 hour |
| Refresh token | 14 days, sliding |
| Authorization (the refresh chain) | 90 days absolute — after that the user signs in again |

The 90-day cap is enforced in the token endpoint against the OpenIddict authorization's
creation date, because refresh-token sliding renewal alone has no upper bound.

## Consent — every app is third-party

There is no first-party exemption. Any app, imagiQa's own included, shows the consent screen
on first use, when it asks for a scope the user has not granted, or when the app's
`consent_version` changes. The screen lists the **real values** that will be shared (the
actual name, email, mobile, organisations and roles) beside an explicit "will not be shared"
column, and records the decision as a `consents` row plus an `app_grants` row. Declining is
audited and answers the app with `access_denied`.

A user who is signed in but does not satisfy the app's `sign_in_policy` (ADR-0002) is sent
back through sign-in before consent is considered.

## Scopes and where claims travel

| Scope | Claims |
|---|---|
| `openid` | `sub` |
| `profile` | `name`, `given_name`, `family_name`, `birthdate`, `gender`, `locale`, `zoneinfo` |
| `email` | `email`, `email_verified` |
| `phone` | `phone_number`, `phone_number_verified` |
| `orgs.read` | `sangam_orgs` (id, name, type, path, role, permissions, inherits) |
| `offline_access` | refresh token |
| `sangam.manage` | the management API; client credentials only, never granted to a user token |

The **ID token stays small**: `sub`, `name`, `email`, `email_verified`, `sangam_orgs`.
Everything else is served by `/connect/userinfo`, which reads the user fresh, so a profile or
membership change reaches the app on its next call rather than at the next sign-in. Tokens
issued by refresh are also rebuilt from the database for the same reason.

## Sign-out

RP-initiated logout at `/connect/endsession` (`id_token_hint` + `post_logout_redirect_uri`,
validated by OpenIddict against the client's registration) renders screen 8, which offers
"Return to … without signing out". Signing out ends the Sangam session; partner sessions
expire on their own — there is no back-channel logout in v0, which is what the screen says.

## Management API

Partner apps administer their own tenancy with their client credentials
(`scope=sangam.manage`) at `/api/v1`:

```
GET    /roles                              PUT /roles/{code}          DELETE /roles/{code}
GET    /orgs/{orgId}                       PUT /orgs/{orgId}
GET    /orgs/{orgId}/members               PUT /orgs/{orgId}/members/{userId}
DELETE /orgs/{orgId}/members/{userId}
```

The app id comes **from the token, never from the URL**, so an app cannot address another
app's roles, organisations or memberships. Rules enforced: role codes are
`^[a-z][a-z0-9_]{1,49}$`; the system `org_admin` cannot be retired or re-scoped; an
organisation's type and parent are fixed at creation; `org_types` decides what may be a root
or have children; an org-scoped role wins over the app-wide role of the same code; granting a
membership creates the app grant if missing. Every write is audited with
`actor_type = 'api'` and the app id.

This is the surface `Sangam.Client` wraps in PR-07.

## Registration and password feedback (refinements accepted with PR-04)

- The mobile field is a **country dropdown plus a national number**. India is the default and
  is listed first (`Sangam.Shared.Constants.CountryCodes`); the dialling code is rendered as a
  static prefix, so a user can never type a malformed E.164 value. Where a country has a fixed
  national length, it is validated ("Enter your 10-digit India mobile number.").
- The password requirement rows are shown **while the user types**. The meter and the three
  rows are rendered by the server and are correct without JavaScript; `wwwroot/js/password-meter.js`
  (same origin, no network) updates that same markup live using the identical rules. The rule
  for the auth screens is therefore "works fully without JavaScript", not "no JavaScript".
- `/dev/callback` is a Development-only stand-in for a partner application's redirect URI: it
  shows the code and state, exchanges the code for tokens on a button press and prints the ID
  token claims and the userinfo response. The foundation page links a one-click flow to it.
  404 outside Development, like `/dev/outbox`.
