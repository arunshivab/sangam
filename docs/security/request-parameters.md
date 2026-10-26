# Request parameters review (rc.16, ASVS V8.1.3)

ASVS V8.1.3 asks that requests carry as few parameters as possible: hidden fields, cookies, custom headers. This is the
review done for rc.16 (26 October 2026), over the four hosts. It lists every cookie, every hidden form field and every
custom request header Sangam itself reads, and why each is there. Protocol parameters that OAuth 2.0, OpenID Connect
and SAML define are out of scope: their names and meaning are fixed by the standards, and V8.3.1 (rc.8) covered which
of them may travel in a URL.

**Method.** A search of `src/` for `type="hidden"`, `Cookies.Append`, `Cookie.Name` and `Request.Headers`, then a
reading of each page model or endpoint to see where the value is checked when it comes back. A value the server could
work out itself, or keep in a protected cookie, should not come from the browser.

**Result.** One change: the password-reset page no longer carries the e-mail address in a hidden field (below). Every
other parameter is needed, and each one that comes back is either protected by ASP.NET Data Protection (so it cannot be
altered) or checked again on the server. No hidden field carries a right, a role, an application id or a price.

## Cookies

All are `HttpOnly`, `SameSite=Lax`, host-only (no `Domain`) and `Secure` over HTTPS (always in production, where Caddy
forwards the scheme). The session, pending, sign-in, accounts, device and antiforgery cookies also carry the `__Host-`
prefix in production (`SecurityHeaders.CookieName`, rc.5); the merge cookie cannot, because it is limited to one path.

| Host | Cookie | What it holds | Protected |
|---|---|---|---|
| Identity | `sangam.session` | The sign-in session (ASP.NET Core authentication ticket) | Data Protection |
| Identity | `sangam.pending` | A sign-in step not yet finished (code sent, two-step pending), 15 minutes | Data Protection |
| Identity | `sangam.signin` | What was typed on the first sign-in screen, for the next screen | Data Protection, time-limited |
| Identity | `sangam.accounts` | The accounts this browser has signed in with (for "choose an account") | Data Protection |
| Identity | `sangam.device` | Whether this browser has signed in before (new-device notice) | Data Protection |
| Identity | `sangam.shared-device` | A shared ward device's key (rc.12), only on enrolled devices | Random key, checked in the database |
| Identity | `sangam.org.<app>` | The organisation a person chose for one application | Data Protection |
| Identity | `sangam.merge` | A merge of two accounts being confirmed, path `/account/merge` only | Data Protection |
| Identity, portal | `sangam.digilocker` | DigiLocker's state and PKCE verifier during verification | Data Protection |
| Portal, consoles | `sangam.portal`, `sangam.admin`, `sangam.partner` | The host's own sign-in session (OpenID Connect client) | Data Protection |
| All four | `sangam.antiforgery` | Antiforgery token | Data Protection |
| All four | `.AspNetCore.Culture` | The language picked with the language switch | Not secret; only a supported language is used |

## Hidden form fields

| Page | Field | Why it is there | Checked on the way back |
|---|---|---|---|
| Sign-in pages, registration, consent, verify, two-step, shared-device PIN, unlock, emergency, reset notice | `ReturnUrl` | Where to go after the step | Only a local path or a registered client address is followed |
| Sign-in, registration, reset request | `__form_ts` (antibot) | When the form was shown, to refuse inhumanly fast posts | Data Protection, time-limited |
| Sign-in, registration | `website` (antibot honeypot, hidden by CSS) | Left empty by people, filled by bots | Must be empty |
| Choose an account (`/login`, "Remove") | `key` | Which remembered account to take off this browser's list | Changes only this browser's own `sangam.accounts` cookie |
| Organisation choice | `returnUrl` | As above | As above |
| Emergency access | `Org` | The organisation the access is for | Must be one of the person's own organisations in that application |
| Identity required | `Join` | Whether the person came from a join code | A flag only; the join code itself is read again |
| New password (too short or breached) | `Reason` | Which notice to record | Only `breached` or `too_short` is recorded |
| Unlock (shared device) | `Tile` | Which person's tile was tapped | Must be a tile of this enrolled device |
| Shared device enrolment, staff invitation | `Token` | The single-use link token | Looked up by hash in the database |
| Staff invitation | `Ticket` | That the code step was passed | Data Protection, names the invitation |
| Passkeys | `id` | Which passkey to remove | Must be the signed-in person's own |
| Device sign-in (`/device`) | `user_code` | The code shown on the device (RFC 8628) | OpenIddict checks it |
| Sign out | the end-session parameters | OpenID Connect requires them to be carried to the confirmation | OpenIddict checks them |
| Profile (portal) | `agree` | The terms box, for the DigiLocker step | A flag only |
| Development callback (`/dev/callback`) | `Code`, `State` | Development only; not compiled into the image (rc.8, V14.2.2) | — |

**Changed in rc.16:** the password-reset page (`/reset`) used to carry the address the code was sent to in a hidden
`Email` field and use the posted value. The code is bound to that address, so changing the field gained nothing, but the
server already holds the address in the protected `sangam.pending` cookie. The page now reads it from there on both
the form and the post, and the hidden field is gone.

## Custom request headers

| Header | Who sends it | Why |
|---|---|---|
| `Sangam-Acting-Admin` | A partner's own server, on the management API (rc.13) | Names the organisation admin a membership change is made for; checked against that admin's rights |
| `X-Sangam-Sms-Token` | The SMS provider, on its delivery reports | Proves the report comes from the provider; compared in constant time |
| `X-Forwarded-For`, `X-Forwarded-Proto` | Caddy | The client address and scheme; trusted only from Caddy's network |

No host reads any other custom header to make a decision.

## Keeping it this way

The secure-coding checklist (SGM-913, C1) already asks for every input to be limited; a new hidden field, cookie or
header should be added to this page in the same change, with what checks it on the way back.
