# Claims contract

What an application receives from Sangam when a person signs in: the tokens, every claim in them, and the samples
an application (or its mock issuer, for tests) can be built against. rc.17, founder's decision of 13 November 2026.

The samples below were **taken from a real sign-in** on Sangam rc.17 (15 November 2026, Sangam's in-process test
host with the development sample client `sangam-dev-sample`, which is why the issuer reads `http://localhost/`),
decoded and shown as they came out; only the e-mail address was replaced. A Sangam run locally with `dotnet run` has
the issuer `http://localhost:5100/`; in production it is `https://id.sangamid.in/`, and the `aud` is your own client id.

## Discovery and keys

- **Discovery:** `{issuer}.well-known/openid-configuration` (production `https://id.sangamid.in/.well-known/openid-configuration`;
  local `http://localhost:5100/.well-known/openid-configuration`).
- **Signing:** `RS256` (`id_token_signing_alg_values_supported: ["RS256"]`), RSA 3072 certificates in production.
  Read the keys from `jwks_uri` by `kid`; never pin one key. During a rotation the old and new keys are both published.
- **Flows:** authorization code with PKCE (`S256`; required for every client). Confidential clients authenticate at the
  token endpoint with their secret.
- **Logout:** `backchannel_logout_supported` and `frontchannel_logout_supported` are both `true`, with `sid`.
- **Other endpoints:** `userinfo`, `introspect`, `revocation`, `end_session`, all listed in discovery.

## Scopes

| Scope | Adds |
|---|---|
| `openid` | `sub`, `sid`, `acr`, `amr`, `auth_time` |
| `profile` | `name` (ID token); also `given_name`, `family_name`, `birthdate`, `gender`, `locale`, `zoneinfo`, `updated_at` (access token and userinfo) |
| `email` | `email`, `email_verified` |
| `phone` | `phone_number`, `phone_number_verified` |
| `org.current` | `sangam_org`: the one organisation the person chose for this sign-in |
| `orgs.read` | `sangam_orgs`: all their memberships in this application |
| `attributes` | the application's own custom claims (access token and userinfo only, never the ID token; `docs/attributes-and-claims.md`) |
| `offline_access` | a refresh token |
| `sangam.manage` | the management API (client credentials; only for applications registered with `ManagementApi: "true"`) |

A person sees what an application asks for on Sangam's consent page the first time ("Allow access").

## Token response

```text
access_token  = <JWT, typ at+jwt>
token_type    = Bearer
expires_in    = 900
scope         = openid profile email org.current offline_access
id_token      = <JWT>
refresh_token = <opaque to the application; never parse it>
```

## ID token (sample)

```json
{
  "alg": "RS256",
  "kid": "D637DDE4070E871DB190384E151AFA0064D11446",
  "x5t": "1jfd5AcOhx2xkDhOFRr6AGTRFEY",
  "typ": "JWT"
}
```

```json
{
  "iss": "http://localhost/",
  "exp": 1794702595,
  "iat": 1794698995,
  "aud": "sangam-dev-sample",
  "amr": ["pwd"],
  "sub": "28109190-aceb-47a6-8bde-a065f413c313",
  "name": "Lakshmi Pillai",
  "email": "lakshmi.pillai@example.in",
  "email_verified": true,
  "sangam_org": {
    "id": "b49316c9-d3ac-4071-b320-e67dba842bfc",
    "name": "Demo Hospital",
    "type": "hospital",
    "path": "/b49316c9-d3ac-4071-b320-e67dba842bfc/",
    "roles": ["radiologist"],
    "permissions": ["reports.sign", "studies.read"],
    "inherits": false,
    "role": "radiologist"
  },
  "sid": "20d51af9-594e-8c12-9348-119a70a81e45",
  "acr": "urn:sangam:acr:1",
  "auth_time": 1794698994,
  "oi_au_id": "f6eaafa2-67b8-4527-88cc-ff9caef6cba2",
  "azp": "sangam-dev-sample",
  "nonce": "n-0S6_WzA2Mj",
  "at_hash": "6End0YzD31SUp6EXs2Dybg",
  "oi_tkn_id": "50dcdd0c-bfd8-4004-b486-6fbeedd55a1d"
}
```

Lifetime one hour. Check `iss`, `aud`, `exp`, the signature and your `nonce`, as any OpenID Connect library does.

## Access token (sample)

```json
{
  "alg": "RS256",
  "kid": "D637DDE4070E871DB190384E151AFA0064D11446",
  "x5t": "1jfd5AcOhx2xkDhOFRr6AGTRFEY",
  "typ": "at+jwt"
}
```

```json
{
  "iss": "http://localhost/",
  "exp": 1794699895,
  "iat": 1794698995,
  "scope": "openid profile email org.current offline_access",
  "jti": "1a2ac9cd-703e-4bd7-b17a-c39ac4153507",
  "sub": "28109190-aceb-47a6-8bde-a065f413c313",
  "name": "Lakshmi Pillai",
  "given_name": "Lakshmi",
  "family_name": "Pillai",
  "birthdate": "1985-03-12",
  "gender": "female",
  "locale": "en-IN",
  "zoneinfo": "Asia/Kolkata",
  "updated_at": 1794698994,
  "email": "lakshmi.pillai@example.in",
  "email_verified": true,
  "sangam_org": {
    "id": "b49316c9-d3ac-4071-b320-e67dba842bfc",
    "name": "Demo Hospital",
    "type": "hospital",
    "path": "/b49316c9-d3ac-4071-b320-e67dba842bfc/",
    "roles": ["radiologist"],
    "permissions": ["reports.sign", "studies.read"],
    "inherits": false,
    "role": "radiologist"
  },
  "sid": "20d51af9-594e-8c12-9348-119a70a81e45",
  "acr": "urn:sangam:acr:1",
  "amr": "pwd",
  "auth_time": 1794698994,
  "oi_au_id": "f6eaafa2-67b8-4527-88cc-ff9caef6cba2",
  "oi_prst": "sangam-dev-sample",
  "client_id": "sangam-dev-sample",
  "oi_tkn_id": "c01ca19d-88e3-4632-8b71-3e010595df31"
}
```

Lifetime 15 minutes. **The access token carries no `aud`**: an application's own back end checks `iss`, the signature,
`exp` and that `client_id` is its own (or asks `introspect`).

## Logout token (sample, back-channel)

```json
{
  "alg": "RS256",
  "kid": "D637DDE4070E871DB190384E151AFA0064D11446",
  "x5t": "1jfd5AcOhx2xkDhOFRr6AGTRFEY",
  "typ": "logout+jwt"
}
```

```json
{
  "aud": "sangam-dev-sample",
  "iss": "http://localhost/",
  "exp": 1794699115,
  "iat": 1794698995,
  "nbf": 1794698995,
  "jti": "959873a9-9fa4-42d1-8199-386627a4b767",
  "sub": "28109190-aceb-47a6-8bde-a065f413c313",
  "sid": "20d51af9-594e-8c12-9348-119a70a81e45",
  "events": { "http://schemas.openid.net/event/backchannel-logout": {} }
}
```

POSTed as `logout_token=<JWT>` to the application's back-channel address, valid two minutes, never with a `nonce`.
Match `sid` (or `sub`) to end the application's own session. Sangam does not call private-network addresses, so an
application inside a hospital network uses front-channel logout instead (`?iss=…&sid=…` in a frame on Sangam's
signed-out page) together with short access tokens.

## Every claim

| Claim | In | Meaning |
|---|---|---|
| `sub` | all | The person, **as this application knows them** (pairwise: every application, and every client registered as its own application, gets a different `sub` for the same person). A GUID. Never changes for this application. |
| `sid` | ID, access, logout | This application's own id for the Sangam session; matches the logout token. |
| `acr` | ID, access | How strongly the person signed in: `urn:sangam:acr:1` (password only, or an e-mailed code), `urn:sangam:acr:2` (two steps: password and authenticator app), `urn:sangam:acr:3` (a passkey). `urn:sangam:acr:sign` is only for signature tokens. |
| `amr` | ID, access | How: `pwd` (password), `otp` (an authenticator app or e-mailed code), `pop` (a passkey: only a passkey gives `pop`), `mfa` (more than one factor). A passkey sign-in gives `["pop","mfa"]`; password and authenticator app give `["pwd","otp","mfa"]`. **In the ID token it is always an array; in the access token a single value is a plain string** (as in the sample) — read both forms. |
| `auth_time` | ID, access | When the person last actually signed in (Unix seconds). |
| `name`, `given_name`, `family_name` | ID (`name`), access, userinfo | The name the person gave. |
| `birthdate`, `gender`, `locale`, `zoneinfo`, `updated_at` | access, userinfo | With `profile`. |
| `email`, `email_verified` | ID, access | With `email`. Any address (Gmail and so on); `email_verified` is `true` once the person proved it. |
| `phone_number`, `phone_number_verified` | access, userinfo | With `phone`. SMS is off for now, so the number is collected but not yet verified (D-182). |
| `sangam_org` | ID, access | With `org.current`: the organisation chosen for this sign-in — `id`, `name`, `type`, `path` (from the top organisation down), `roles` and `permissions` (in this application), `inherits` (whether the role applies to organisations below). `role` is the first role, kept for older applications; read `roles`. |
| `sangam_orgs` | ID, access | With `orgs.read`: an array of the same shape, one per membership in this application. |
| `sangam_identity_verified` | access, userinfo | With `profile`: `true` when the person verified with DigiLocker (only the status; Sangam never holds an Aadhaar number). |
| `sangam_emergency` | ID, access | During emergency access ("break glass"): `id` (the use), `case` (the application's case reference), `org` and `until` (Unix seconds). |
| `nonce`, `at_hash`, `azp` | ID | Standard OpenID Connect. |
| `jti`, `scope`, `client_id` | access | Standard. |
| `oi_*` | ID, access | OpenIddict's own bookkeeping. **Ignore them**; they may change. |

## Roles

Roles belong to the application, in its own words ("radiologist", "doctor"). The application defines them once — on
the partner console (Roles) or with the management API (`PUT /api/v1/roles/{code}` with
`{"displayName": "Radiologist", "description": null, "permissions": ["studies.read", "reports.sign"], "orgId": null}`).
The organisation's admin gives them to people, per organisation; the application reads them from `sangam_org.roles`
(and `permissions`, which Sangam passes through without reading). Adding people needs a DigiLocker-verified admin
(or the temporary operator exception until DigiLocker is live). Custom claims are not the place for roles: they are for
extra details and never appear in the ID token.

## Asking for more (step-up)

Send `acr_values=urn:sangam:acr:2` or `urn:sangam:acr:3` (and `max_age=<seconds>` for a recent sign-in) on the
authorize request: if the session is weaker or older, Sangam asks the person to sign in again at that level. Then check
`acr` (or that `amr` contains `pop`) in the new ID token. The .NET client's `SangamStepUp` does this.

## Lifetimes and sessions

| What | Lifetime |
|---|---|
| Authorization code | 5 minutes |
| Access token | 15 minutes |
| ID token | 1 hour |
| Refresh token | 14 days, rotating (each use gives a new one) |
| A chain of refresh tokens | 90 days from the sign-in, then sign in again |
| Sangam's own sign-in cookie (the browser session at Sangam) | 12 hours |

The 12 hours are the person's browser session at Sangam, not the application's tokens: a refresh token keeps working
after that cookie expires, up to the 90-day chain, unless the sign-in it came from is ended. It is ended — and the
refresh token refused at once — when the person signs out (here or everywhere), an operator ends the session or
suspends the account, the password is reset, the e-mail changes, two-step sign-in changes, or a shared ward device
asks for a full sign-in again. A person who is only locked out for a while after wrong passwords keeps their existing
sessions. An access token already issued lives until it expires (at most 15 minutes); an application that must know
sooner asks `introspect`.

## For .NET applications

Keep `acr`, `amr` and `auth_time` (ASP.NET Core's OpenID Connect handler drops them by default), and keep claim names
as they are:

```csharp
options.MapInboundClaims = false;
options.ClaimActions.Remove("acr");
options.ClaimActions.Remove("amr");
options.ClaimActions.Remove("auth_time");
```

Sangam's callbacks are fixed: `<BaseUrl>signin-sangam` and `<BaseUrl>signout-sangam`.
