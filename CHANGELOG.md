# Changelog

All notable changes to Sangam are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/).

## [0.12.0] - R3 step-up and customisation, with founder decisions D-A to D-M

### Fixed — R2 verification findings (V-07 to V-09)
- **No HTTPS-port warning behind Caddy (V-07).** The hosts no longer run their own HTTP-to-HTTPS redirect
  where it cannot work: in Development and Testing (plain HTTP on localhost), and behind a trusted reverse
  proxy (`Sangam:ForwardedHeaders:KnownProxies` or `KnownNetworks` set, as Compose does), where Caddy
  terminates TLS, redirects HTTP itself and health-checks the app over plain HTTP on 8080. A host exposed
  directly still redirects. Setting port 443 instead would have redirected those health checks.
- **A refused start exits cleanly (V-08).** Until a host has started, an unhandled exception — a start-up
  rule refusing (no key-ring certificate, no mail server, unsafe SMS, missing token certificates) or anything
  else — is logged once at Critical as `Sangam refused to start: <reason>` and the process exits with code 1,
  instead of dying with exit code 139 and a stack dump. Inside a test runner the guard stands aside.
- **Whether registration reveals an existing account is a setting (V-09).** *(Since decided — D-L: on by default,
  and the owner of a taken mobile is told instead; see below.)* `Sangam:Registration:ConcealExistingAccounts`; off: the form says
  an account already exists. On: a registration with an address or mobile that already has an account goes
  on to the same "check your email" screen as a new one — identical, countdown included — no code ever
  matches, and the address's owner is e-mailed instead (`user.register.duplicate` is audited). Limit: with a
  new address and a taken mobile, the notice goes to that new address and says the number is taken, so
  whoever controls an address can still learn that a number is registered, one e-mail at a time.

### Added — R3 (PR-17 step-up and electronic signatures)
- **Assurance levels and step-up (PR-17, SGM-207 §3–4).** Every sign-in records its RFC 8176 methods
  (`pwd`, `otp`, `sms`, `pop` for a passkey, `mfa` for two distinct factors) and reaches a level:
  `urn:sangam:acr:1` single factor, `:2` two factors, `:3` a passkey; `:sign` is level 2 or 3 within the
  last five minutes. Every ID and access token now carries `acr`, `amr` and `auth_time` (kept across
  refreshes), and discovery lists `acr_values_supported`. An application asks for a level with
  `acr_values` and for freshness with `max_age`: a session that falls short is sent to sign in again — the
  sign-in page adds the code step for level 2 (unless an authenticator will follow) and accepts only a
  passkey for level 3 — and `prompt=none` gets `login_required`. Audited as `user.stepup.required` and
  `user.stepup.success`, narrated in the activity history.
- **Electronic-signature support (PR-17, SGM-207 §5; 21 CFR Part 11 style, to verify).** An application
  registers a request with `POST /api/v1/signatures` (record id, record hash, meaning, display text, return
  address — which must be one of its registered redirect URIs — and optionally the only person who may
  sign). The person signs at `/sign/{id}`: a fresh two-factor sign-in, the record, its fingerprint and the
  meaning shown, then Sign or Decline, once. The application reads the result with
  `GET /api/v1/signatures/{id}`: a JWS (`typ` `sangam-signature+jwt`) signed with Sangam's published
  token keys, binding signer, name, record id, record hash, meaning, time, `acr` and `amr`. Requests expire
  after fifteen minutes. Audited as `user.signature.sign` / `user.signature.decline`.
- **Client library.** `SangamStepUp`: `User.Satisfies(level, maxAge)`, `ChallengeAsync` (sends
  `acr_values` and `max_age`), and the RFC 9470 `WWW-Authenticate` value for APIs; `AddSangam` now keeps
  `acr`, `amr` and `auth_time` in the signed-in person's claims. `SangamAcr` holds the level names for both.
- Deviations from SGM-207, recorded here: the signature endpoints sit in the management API
  (`/api/v1/signatures`, `sangam.manage` scope) rather than a separate `/signatures/requests` route, and one
  table `signature_requests` keeps the request, the outcome and the token itself (as evidence the
  application can fetch again) instead of a separate `signature_events` table with a token hash.
- Founder decisions flagged (SGM-207 open questions): whether a signature needs DigiLocker-verified
  identity (R5) in every case; whether clinical orders use level 2 or signature grade. Both are the
  application's choice of `acr_values` today.
- Migration: `SignatureRequests`.

### Added — R3 (PR-18 languages: Hindi and Malayalam)

- **Every screen in Hindi and Malayalam** — sign-in and registration, the account portal, the operator console
  and the partner console — with English as the source and the fallback. A language picker (each language in its
  own script) sits in every footer.
- **One shared text catalogue** (`Sangam.Web.Shared/Localization/hi-IN.json`, `ml-IN.json`): a flat map from the
  English text to its translation, plain JSON so a translator can review it without tools. Pages and components
  inject `IStringLocalizer<SangamText>` as `L` and write the English as the key. Sentences that hold a link or a name
  are translated whole with `{0}` slots (`L.Html(...)` in Razor Pages, `L.Markup(...)` in Blazor), because Hindi
  and Malayalam put the verb last. A service's finished sentence ("LiPi HIS can sign users in again.") is translated
  by matching its template, so services did not change. The audit narrator now builds whole sentences instead of
  joining English fragments; its English output is unchanged.
- **How a language is chosen**, in order: `?culture=`, the picker (a year-long cookie), the person's profile
  language (the existing `locale`, now applied to Sangam's own screens after sign-in), an application's
  `ui_locales` on the authorization request, then the browser's Accept-Language. Malayalam (`ml-IN`) joins the
  supported cultures and the profile's language list. Dates and month names follow the language.
- **The i18n lint** (`Sangam.Web.Shared.Tests/Localization`): fails the build when visible text in a page,
  component or UI code bypasses the catalogue, when a key a screen needs is missing in either language, when a
  catalogue entry is no longer used, or when a translation drops a `{0}`. `SANGAM_I18N_DUMP=<path>` writes every key
  with its translations for translators.
- `sangam-i18n.css` (shared): no letter-spacing on Hindi and Malayalam (it breaks up letter clusters), and button
  rows, choice lists and the portal's row-tables wrap or stack on a phone instead of overflowing. This also fixes two
  phone layouts that overflowed in English (the Revoke button on Connected apps; the operator grant form).
- The password meter's verdicts and the passkey scripts' messages come from the page in the reader's language.

Notes for the review session (flagged, not decided):

- **The translations need a native-speaker review before go-live.** They were written for this release and checked
  mechanically (every key present, placeholders kept, brand names untouched), not by a fluent reviewer. The
  translators' open questions are listed in `_evidence/i18n/REVIEW-NOTES.md`.
- Tamil (`ta-IN`) stays a supported culture for dates and numbers and in the profile list, but has no catalogue
  yet, so its screens show English. The picker offers only English, Hindi and Malayalam.
- Not translated by design: data people or partners enter (application and role descriptions, organisation and
  person names, the record being signed), OAuth `error_description` texts (for developers), the management API,
  start-up refusals, the development-only pages, e-mails and SMS (PR-19), and the root page `/`, which is still the
  PR-01 design-foundation check — it should be replaced with a redirect or a landing page before go-live.
- ASP.NET Identity's own fallback messages (rare, behind Sangam's own checks) remain English.

### Added — R3 (PR-19 branding, sign-in pages, e-mail and SMS templates)

- **Customisation by level** (SGM-209): every setting resolves from the most specific level that sets it — the
  organisation (and the organisations above it), then the application, then Sangam's platform defaults, then the
  built-in default. New tables `customisations`, `message_templates` and `branding_assets` (migration
  `Customisation`).
- **Sign-in page branding** per application and per organisation: a logo, an accent colour (checked: white text on it
  must reach 4.5:1), a welcome line in each language, and help, terms and privacy links. The accent recolours only
  the primary button and links; structure never changes. An application names the organisation a sign-in is for
  with the new authorization parameter `sangam_org` (its Sangam id; an organisation of another application is
  ignored).
- **Logos** are PNG, or SVG that is only a picture (no scripts, event handlers, embedded HTML, outside references or
  DTDs), at most 200 KB, served at `/branding/logo/{id}` with `nosniff` and a sandboxing Content-Security-Policy so
  even an SVG opened on its own cannot run anything in Sangam's origin.
- **E-mail templates** for every message Sangam sends, per language, with `{{variables}}` checked on save (unknown
  ones refused, required ones — the code, the invitation link — enforced). Built-in texts in English (word for word
  the e-mails sent before), Hindi and Malayalam. Every e-mail now also has an HTML part, built from the text inside
  Sangam's fixed layout (header in the application's accent, the code in a box, the link as a button); a template
  changes words, never structure. Applications and organisations may set the verification, sign-in, password-reset
  and invitation e-mails; security notices stay Sangam's own. The person's language comes first: a Hindi reader
  gets Sangam's Hindi text rather than an application's English-only wording.
- **Language of e-mails:** the account's profile language, which is now set at registration from the language the
  person registered in. Invitations go in the inviter's language (the invitee may have no account yet).
- **SMS templates** per language at platform level only, each with its DLT template id and exactly as many
  `{#var#}` as the registered English text; a code is texted in the person's language when that version is
  registered, otherwise in English.
- **Screens:** partner console → Settings → *Sign-in page* and a new *Messages* tab; partner console → an
  organisation → its own sign-in page and messages (tenant overrides); operator console → *Defaults* (AppManager
  and above) for the platform's sign-in page and every message, including SMS. Each message editor previews the
  e-mail with sample values; the sign-in page editor links to a live preview.
- Audit actions `customisation.branding.update` and `customisation.template.update`.

Fixed while building PR-19:

- Two console components loading at once on one Blazor circuit shared its `DbContext` ("A second operation was
  started on this context instance"). The customisation service now uses its own context per call.
- A platform-rank check written as a SQL comparison compared the stored text ("viewer" > "app_manager"); it is now
  compared in memory. (The new code only; the existing admin service already compared in memory.)

Notes for the review session (flagged, not decided):

- `sangam_org` is a new authorization-request parameter name; confirm it before partners build against it.
- Templates are per language first, level second (above). The alternative — an application's English wording for
  everyone — is a one-line change if preferred.
- A custom domain for the sign-in page stays rejected (D-017, SGM-209 §3).
- The SMS language variants still have to be registered on the DLT platform by imagiQa before use; until one is
  saved with its id, texts go in English as today.

### Added — R3 (PR-20 logout channels, introspection and revocation)

- **Back-channel logout** (OpenID Connect Back-Channel Logout 1.0). Sangam now records which applications take part
  in each browser session (`session_apps`). When a session ends — the person signs out, ends it from the portal, or
  ends all sessions — a notification is queued (`logout_notifications`) for each of those applications that has a
  back-channel address. The identity server posts a signed `logout_token` (`typ` `logout+jwt`; `iss`, `aud`, `iat`,
  `exp`, `jti`, `sub`, `sid`, and the logout `events` claim; no `nonce`) every few seconds, retrying a failure with
  growing pauses, six attempts in all. A slow or broken application never holds up a sign-out.
- **Front-channel logout** (Front-Channel Logout 1.0). When an application used in the session has a front-channel
  page, signing out shows "You're signed out of Sangam" with that page in a hidden frame (with `iss` and `sid`), then
  carries on after the frames load or three seconds. For an application-initiated sign-out (`/connect/endsession`) the
  page re-posts the request with `frames_done`, so the person still lands back at the application's
  `post_logout_redirect_uri`. Without JavaScript, a Continue button does the same.
- **Introspection** (`/connect/introspect`, RFC 7662) and **revocation** (`/connect/revoke`, RFC 7009), for
  applications' own back ends. Both endpoints are in discovery; the seeded clients are permitted to use them.
- Discovery advertises `frontchannel_logout_supported`, `frontchannel_logout_session_supported`,
  `backchannel_logout_supported` and `backchannel_logout_session_supported`.
- **Admin console:** Applications → *Logout* sets each application's back-channel address and front-channel page
  (AppManager and above; https only, http accepted for localhost; no fragment). Changes are audited.
- Migration `LogoutChannels`. New setting `Sangam:Logout:DeliverInBackground` (default `true`; the test host turns it
  off and delivers explicitly).

Notes for the review session:

- Applications must allow Sangam to frame their front-channel page (their own `X-Frame-Options` / CSP
  `frame-ancestors`). Browsers that block third-party cookies may not send the application's cookie inside the
  frame; back-channel logout is the dependable channel and the one to recommend.
- Sangam's own consoles (portal, admin, partner) do not yet register logout addresses; they keep their existing
  short-lived sessions. Wiring them, and a logout-token validator in `Sangam.Client`, is a candidate for R4.
- A notification that gives up after six attempts is kept (with its last error) for inspection; there is no
  console view of failed deliveries yet.

### Changed — founder decisions D-A to D-M (6–7 October 2026), folded into R3
- **Existing accounts are concealed at registration (D-L, on by default).** A taken address or mobile gets exactly
  the page a new registration gets; nothing is created. The real owner is told — by e-mail for an address; for a
  mobile, by SMS when SMS is on, otherwise by e-mail to that account — at most three times a day
  (`Sangam:Registration:AttemptNoticesPerDay`); every attempt is audited against the account. The person who typed
  a new address with a taken mobile is no longer told anything (closing the leak V-09 noted). The password is
  hashed on that path too, so the response takes as long as a real registration. Checked alongside: forgot-password
  and code sign-in look identical for unknown addresses (now tested), and an address with no account now "locks
  out" after the same five wrong passwords as a real one, with the same password-hashing time, so neither the
  lockout message nor timing reveals an account.
- **Anjal is Sangam's single messaging gateway, by its API (D-B, D-M).** The MailKit/SMTP sender and the
  direct-aggregator failover are removed. `AnjalClient` posts JSON with an API key (header and scheme configurable),
  an idempotency key that is the same on every retry, and retries with growing, jittered pauses on timeouts, 408,
  429 and 5xx (honouring `Retry-After`); other 4xx are final. `AnjalEmailSender` (five attempts, in the background:
  a page never waits for Anjal) and `AnjalSmsSender` (two attempts; provider name `anjal`) sit behind the existing
  `IEmailSender` and `ISmsSender`. Staging allowlist `Sangam:Anjal:AllowedRecipients` (addresses, `@domains`,
  numbers). Logs carry a masked recipient and the outcome only. Every host now checks at start that Anjal is
  configured (https, or a private host name; a key of 20+ characters) — the portal, admin and partner consoles send
  e-mail too, and previously had no sender configured in Compose. The proposed API contract is
  `docs/anjal-messaging-contract.md`; `scripts/check-mail-dns.ps1` takes `-AnjalSpf`. SMS stays off until DLT is
  registered under the new company (D-M); Sangam keeps the DLT ids and its per-number and per-IP limits.
- **Ownership (D-C).** Sangam is owned personally by Dr. Arun Shiva Balasubramanian; jurisdiction Ahmedabad
  (`PlatformOwner`, `Sangam:Operator`, `Sangam:Jurisdiction`). The terms and privacy placeholders name the owner (and
  the data fiduciary), the seeded applications, README, SECURITY.md, package metadata and the docs follow; screens
  that said "set by imagiQa" now say "set by Sangam" (or "the Sangam team").
- **Grievances (D-D).** The page names Arun Shiva Balasubramanian and grievance@sangamid.in, and says every
  grievance is acknowledged within 2 working days, resolved within 30 days, and logged.
- **PostgreSQL 18 everywhere (D-F).** CI and the development Compose file run `postgres:18`.
- **Invitation-only registration (D-I).** `Sangam:Registration:InvitationOnly` (off by default) closes the
  register page except through an open invitation link (with the invited address only) or for addresses on
  `Sangam:Registration:AllowedEmails` (full addresses or `@domains`), for the private pilot.
- **Passkeys on ASP.NET Core Identity (D-G).** Fido2NetLib is removed. Identity's `PasskeyHandler` makes the options
  and verifies every answer (origin, relying party, user verification, signature, counter); Identity's store keeps
  the keys (`user_passkeys`, Identity schema version 3). Sangam keeps the ceremony state (a challenge works once),
  its record of each passkey (name, last use, removal kept) and the audit events. Same screens, same refusals
  (another site's answer, a counter going back, no user verification, a suspended account, a removed passkey), and
  an unreadable answer is still a plain 400. Passkeys made before this release are not carried over (no production
  data). Migration `IdentityPasskeys` (it also aligns two Identity key columns to 128 characters, which the
  design-time model had missed).
- **Self-hosted monitoring (D-H).** OpenTelemetry is removed. Every host records its own metrics with .NET's
  `MeterListener` into `metric_points` (a minute per row and host): requests by status class and their times
  (`UseSangamRequestMetrics`), sign-ins, refusals and lockouts, e-mail and SMS accepted or failed by Anjal, host
  heartbeat, free disk space and certificate days left. The operator console's new *Monitoring* page shows hosts
  and database, the last hour, the last 24 hours by hour, Anjal (its health check and delivery counts), the
  breached-password list, disk, certificates (token, key ring, and the public sites' TLS), and the last backup and
  restore drill (written by the scripts to `/srv/sangam/status`). The identity server evaluates configurable
  thresholds every minute (`Sangam:Monitoring:Alerts:*`) and alerts the founder by e-mail and SMS through Anjal
  (`Sangam:Alerts:Emails`/`Mobiles`, else every Owner), once per condition and again every six hours while it
  stays open; conditions are kept in `monitoring_alerts`. Sangam watches Anjal (`AnjalHealthUrl`); the watchdog of
  Sangam runs on Anjal's server (Anjal project). Logs use Docker's `local` driver with rotation. Migration
  `Monitoring`.
- **Breached-password check offline only (D-J).** The online range checker is removed. `PwnedPasswordList` stores
  the downloadable Pwned Passwords SHA-1 list as a compact sorted file (first 8 bytes of each hash, a 65,536-bucket
  index, binary search: a handful of reads per check) and `breach-list import|status` builds it from the official
  downloader's single file or directory, running from the identity server's image. The check runs only when
  switched on **and** the list is loaded; a refreshed list is picked up within a minute; the monitoring page shows
  its state and date.
- **Support recovery cooling-off (D-K).** A support reset of two-step sign-in is now a request: the verification
  method is required and audited; the owner is alerted by e-mail with a one-click "this wasn't me, cancel" link, by
  SMS when it is on, and by a notice at their next sign-in (cancel or continue); the reset is applied after 24 hours
  (72 for operators, application administrators and organisation administrators) only if nobody cancelled, by a
  background step that claims each request once. An urgent override (Support or Owner, a written reason) applies it
  at once, is audited separately and alerts the platform owner immediately. Operators can withdraw a pending
  request. Per-organisation periods are designed (a 24-hour minimum for privileged accounts) but deferred.
  Migration `MfaResetRequests`.
- Scheduled in SGM-108 rather than built here: D-A (audit archive and anonymisation), D-E (encrypted off-region
  backups, the second provider), the demo application at `demo.sangamid.in` and the grievance log (D-I, D-D).

### Founder decisions still open for R3 (built configurable or defaulted; nothing here blocks the release)
- *(V-09 decided: D-L, conceal by default.)*
- **PR-17:** whether a signature needs DigiLocker-verified identity (R5) in every case, and whether clinical
  orders need level 2 or signature grade (each application asks with `acr_values` today).
- **PR-18:** native-speaker review of the Hindi and Malayalam texts before go-live; when Tamil (and SGM-209's
  other R3 languages — Marathi, Gujarati) get catalogues (SGM-108's exit criterion names Hindi and Malayalam,
  SGM-209 §5 lists more); what replaces the root page `/`.
- **PR-19:** the parameter name `sangam_org`; templates choose the reader's language before an application's
  own wording; the Hindi and Malayalam SMS registrations on DLT.
- **PR-20:** whether Sangam's own consoles register logout addresses (they keep short sessions today).

## [0.11.0] - R2 strong authentication

### Fixed — R1 verification findings (V-01 to V-06)
- **The data-protection key ring is encrypted at rest (V-01, security).** The keys that protect cookies,
  antiforgery tokens and pending sign-ins were stored in PostgreSQL in clear. They are now encrypted with
  a certificate (`Sangam:DataProtection:Certificates:n`, current first; older ones stay listed so keys
  written under them can still be read after rotation). Outside Development and Testing every host
  refuses to start if keys are not persisted or no certificate is configured. Compose passes
  `keyring_current.pfx` and its password as secrets.
- **No SQL in production logs (V-02).** `Microsoft.EntityFrameworkCore.Database.Command` logs at Warning
  in every host's base settings; a test keeps it so.
- **The container image is complete and has one port setting (V-03, V-04).** The runtime stages install
  `libgssapi-krb5-2`, which Npgsql loads; the app stage sets only `ASPNETCORE_HTTP_PORTS=8080`.
- **Dates read day-first (V-05).** Pages carry the request's culture (`lang="en-IN"` by default; hi-IN and
  ta-IN supported; no fallback to en-US). The cause of the mm/dd/yyyy date of birth was the browser's
  native date picker, which follows the browser's own locale whatever the page says, so registration
  now asks for **Day, Month (by name) and Year** as separate fields — the same in every browser — and
  refuses dates that do not exist ("31 February"). A single ISO value is still accepted from scripts.
- **Sangam's own applications do not ask for consent (V-06).** The portal and the consoles are marked
  first-party (`IsPlatform`); the authorisation step records an implicit consent instead of showing the
  consent screen, audited with `basis: first_party_implicit` and narrated as "part of Sangam itself".

### Added — R2 (PR-14 passkeys, PR-15 SMS codes, PR-16 policies and recovery)
- **Passkeys (PR-14, SGM-205).** Add a passkey on the identity server at `/account/passkeys` and sign in
  with it from the sign-in page ("Sign in with a passkey", shown only where the browser supports it).
  WebAuthn through Fido2NetLib 4.2.0 (MIT): user verification required, no attestation collected,
  discoverable credentials preferred, single-use challenges that expire after 5 minutes, and a signature
  counter that may never go backwards — a cloned authenticator is refused and audited
  (`user.passkey.fail`, reason `counter_regression`). A passkey satisfies an application's
  password-and-code or code-only rule. Settings: `Sangam:Passkeys:Enabled`, `RpId` (defaults to the
  issuer's host), `Origins`, `RpName`. Audit: `user.passkey.add`, `user.passkey.remove`,
  `user.passkey.fail`; sign-ins record mode `passkey`.
- **Deviation from SGM-205:** passkeys are managed on the identity server, not inside the portal, because
  a passkey is bound to one site and the identity server is the only site that uses it. The portal's
  profile shows the passkeys set up and links to that page.
- Migration: `Passkeys` (`passkey_credentials`, `passkey_challenges`; `passkey` added to the sign-in
  preference check).
- Founder decision flagged: Fido2NetLib as a dependency (stable 4.2.0; 5.0 is in preview).
- **SMS codes and mobile verification (PR-15, SGM-206, D-118).** A person verifies their mobile at
  `/account/mobile` with a texted code (linked from the account page and the portal's profile); until
  then it stays "not yet verified" (D-048). On the code step of a sign-in — after the password, or for
  code-only accounts — "Text the code to my mobile instead" sends it to the verified mobile, and "Email me
  the code instead" switches back. Before the password is proven, the texted-code screen says the same
  thing for every address, so it reveals neither whether an account exists nor its number; after the
  password it shows the number's last two digits and says plainly when no text can be sent. Sign-ins
  record `channel: sms`; the activity history says "with a texted code".
- **The rules for SMS codes.** The e-mail rules per account (6 digits, 10 minutes, 5 tries, 60-second
  resend, 5 an hour; SMS and e-mail sign-in codes have separate allowances), plus at most 5 texts an hour
  to one number and 10 from one IP address, and a country allowlist (`+91` only by default) against SMS
  pumping. A day's volume reaching `Sangam:Sms:DailyAlertThreshold` logs a warning and audits
  `sms.volume.alert`. A verification code proves only the number it was sent to.
- **What is stored.** `sms_messages` keeps the template, provider, provider message id, status and times;
  the number and the requesting IP only as HMAC-SHA256 hashes under `Sangam:Sms:HashKey` (a plain hash of
  a ten-digit number can be reversed by trying them all), and never the code or the text. Audit:
  `user.sms.send` (template and outcome only), `user.mobile.verify`.
- **Provider abstraction.** `ISmsSender` takes the number, the DLT template id, the header and the text
  rendered exactly as registered (`{#var#}` placeholders filled in order); `FailoverSmsSender` tries an
  optional second provider. The development outbox keeps texts in memory for `/dev/outbox` and logs only
  the template and a masked number. Delivery reports arrive at `POST /sms/delivery-report` in Sangam's
  provider-neutral form, with a shared token (`Sangam:Sms:DeliveryReportToken`; unset, the webhook is off).
- **SMS stays off in production until the founder decides.** No provider adapter is in this build: the
  provider, the failover provider, the six-letter header and the DLT template ids are SGM-206's open
  questions 1–3. The identity server refuses to start outside Development and Testing if SMS is on with
  the development outbox, a provider without an adapter, a hash key shorter than 32 characters, a header
  that is not six capital letters, or a template without its DLT id. Compose sets `Sangam__Sms__Enabled`
  to false for every host. Offering SMS outside India is likewise the founder's (`AllowedCountryCodes`).
- Migration: `SmsMessages`.
- **Security policies per application and per organisation (PR-16, SGM-209 §7, REQ-052).** Each level can set
  the sign-in rule, the shortest password, a second factor (each person decides / required for the
  application's administrators / required for everyone) and the breached-password check. They combine
  platform → application → the person's organisations in that application, each with all its ancestors, and
  every level can only be stricter than what it inherits (the application's sign-in rule stays imagiQa's to
  weaken, D-092). Partners edit them in the partner console: the application's under Settings, an
  organisation's on its panel under Organisations & people. Changes are audited (`app.policy.update`,
  `org.policy.update`, before and after).
- **Passkey only.** A new sign-in rule for an application or an organisation. The sign-in page of a
  passkey-only application offers only the passkey; a password that is right is still refused, and a
  password session is sent back to sign in.
- **How the policies take effect.** At sign-in: an organisation that requires two-step adds the code step;
  a password shorter than the policy, or found in a breach, must be replaced at `/login/new-password` before
  the sign-in goes on (other devices are signed out). At every authorization request: the person's policy is
  enforced again, and where a second factor is required a session without one — no authenticator step and no
  passkey — goes to `/login/two-step-required`, which links to setting up an authenticator or a passkey;
  `prompt=none` gets `login_required`. Whenever a password is set (registration, reset, replacement), it
  must meet the longest minimum over every application and organisation the person belongs to.
- **Breached-password check (CAP-024, REQ-058).** A k-anonymity range lookup: only the first five hex
  characters of the password's SHA-1 leave Sangam, with padding requested; the match is made here. An outage
  never blocks anyone — the password is then simply not checked, and a warning is logged. **Off until the
  founder decides** (`Sangam:Passwords:BreachCheck:Enabled`; the endpoint defaults to Pwned Passwords and is
  configurable; `RequiredForEveryone` makes it a platform-wide sign-in rule). While it is off, the partner
  console shows the option as not yet available and refuses to require it.
- **Recovery without an authenticator (CAP-019, REQ-055).** When someone has lost both their authenticator
  and their recovery codes, support (Support rank or above) resets two-step sign-in from the operator
  console after proving who they are — video call with photo identification, in person, or a call back to
  the verified mobile — and records a ticket or note. The authenticator is removed, the security stamp
  rotates, every session ends, the person is told by e-mail, and `admin.user.mfa.reset` records the method
  and reference. Never your own; an operator's only by an Owner; a reference with eight or more digits in a
  row is refused, so identity-document numbers are never recorded. The authenticator step tells people
  how to ask for this.
- Platform settings: `Sangam:Policy:MinPasswordLength` (not below 8) and `Sangam:Policy:Mfa`.
- Migration: `SecurityPolicies` (policy columns on `apps` and `organisations`, `passkey_only` added to the
  sign-in rule check).

### Fixed — found while building R2
- The activity history never said what a code was for ("to verify your email", "to reset your
  password"): the audit wrote the purpose as `emailverification` while the history read
  `email_verification`. Purposes are now written in snake_case.
- A forged or unreadable passkey answer produced a server error page; it is now refused with a 400 and a
  plain message.
- The identity server's account page still said the portal "arrives in PR-05"; it now points to the portal,
  to mobile verification and to passkeys.

### Founder decisions still open for R2 (built configurable, off or defaulted)
- The SMS provider, a failover provider, the header and the DLT template ids; whether to offer SMS outside
  India (SGM-206 questions 1–3). SMS is off in production until then.
- The breached-password service, given that a five-character hash prefix leaves Sangam (off until decided).
- Fido2NetLib as a dependency.
- Whether a support reset of two-step sign-in should wait a cooling-off period, with the e-mail notice
  giving the person time to object, rather than take effect at once.

## [0.10.0] - R1 go-live

### Added — R1 go-live (PR-09 e-mail through Anjal, PR-10 production, PR-13 account and tenancy)
- **E-mail through Anjal (PR-09).** `SmtpEmailSender` submits mail over SMTP (MailKit) with settings under
  `Sangam:Email:Smtp` — host, port, TLS mode, account, sender and an optional recipient allowlist so a
  staging system can never reach real people. Logs show a masked recipient and the message id only.
  Anjal's values are the founder's to supply (OI-027). `scripts/check-mail-dns.ps1` checks SPF, DKIM
  and DMARC before go-live.
- **Production can start (PR-10).** The identity server loads its signing and encryption certificates
  from PFX files (`Sangam:Certificates:Signing:n`, `:Encryption:n`, current first, previous ones kept
  published for rotation) and refuses to start with a clear message if one is missing or keyless.
  Every host reads Docker secrets from `/run/secrets` (file name = key; `.pfx` files are skipped).
  OpenTelemetry traces and metrics are exported over OTLP when `Sangam:Telemetry:OtlpEndpoint` is set.
- **Deployment set (PR-10).** `Dockerfile` (one recipe for every host, plus a `migrator` target that runs
  the EF migrations bundle once per release as the schema owner, using the repository's pinned
  `dotnet-ef`), `deploy/production/` (Compose stacks for Sangam and PostgreSQL, the production Caddyfile
  with TLS, security headers and health-checked upstreams, the procedure and the list of secrets),
  `scripts/backup-db.sh` and `scripts/restore-drill.sh`.
- **Change of e-mail (PR-13, OI-022).** In the portal: the current password (wrong ones count towards
  lockout), a code sent only to the new address under its own purpose, and on confirmation a notice to
  the old address so a hijack cannot pass unnoticed. `updated_at` moves; the security stamp rotates.
- **Invitations by e-mail (PR-13).** From the partner console's People tab, an administrator invites an
  address to an organisation and role. The link works once, for 7 days; only a hash of its token is
  stored. Accepting requires signing in with the invited, verified address — a forwarded link does not
  work for anyone else — and only then is the application linked and the role given (D-093 holds).
- **Public documents and grievances (PR-13, REQ-089, DEF-025).** `/terms`, `/privacy` and `/help` existed
  only as links — the footer since PR-01 and the registration form's "I agree" since PR-03 pointed at
  pages that returned 404. They now exist: terms and privacy notice from files the founder supplies
  after counsel's review (`Sangam:Legal:TermsPath`, `PrivacyPath`; until then the page says the document
  is pending), and `/privacy/grievance` with the grievance officer and response time from
  `Sangam:Grievance:*` — showing only what has been decided — and the route to the Data Protection Board.
- Migrations: `EmailChangeRequests`, `Invitations`. Tests: 289 (20 new).
- Founder decisions still open, built configurable: Anjal's SMTP settings and DKIM selector, the legal
  texts, the grievance officer and response time, the PostgreSQL version (18 pinned), the telemetry
  collector, the off-site backup target, MailKit and OpenTelemetry as dependencies.

## [0.9.0] - R0 stabilisation

### Fixed — R0 stabilisation (PR-11 logs and audit, PR-12 abuse protection and host hardening)
- **One-time codes never reach a log (OI-038).** The logging e-mail sender, which wrote whole messages —
  codes included — to the log outside Development, is removed. Without a real sender, sends are refused
  and name no recipient or content; the identity server refuses to start outside Development and Testing
  if no sender is configured (`Sangam:Email:Smtp:Host`) or if the development outbox is on. The outbox's
  own log line now shows only a masked recipient: subjects carry the code.
- **The audit trail refuses tampering and shows it (OI-039).** Triggers replace the silent append-only
  rules: UPDATE, DELETE and TRUNCATE on `audit_events` now fail with an error (TRUNCATE was not covered
  before). Every event carries a SHA-256 hash chained to the previous one; `AuditChain.VerifyAsync` finds
  the first altered event. Metadata is hashed in a canonical form so jsonb normalisation cannot break the
  chain. Every audit entry now records the client's IP and browser — the consoles and the portal had
  passed none — through a per-request and per-circuit client context.
- **Audit retention is configurable and off by default.** `Sangam:Audit:RetentionDays` unset keeps every
  event; when set, the maintenance sweep removes older events and records the cut as
  `audit.retention.purge`. The periods are the founder's decision.
- **Bot friction without a CAPTCHA (OI-034, D-111).** Sign-in, code sign-in, registration and forgotten
  password carry a hidden honeypot field and a signed form timestamp; posts that fill the honeypot, lack
  the timestamp or come faster than `Sangam:Antibot:MinimumSeconds` (default 2) are refused with one
  neutral message. No JavaScript needed.
- **Rate limits on every endpoint (OI-035).** `/connect/token` (60 a minute per IP), `/connect/userinfo`
  (120) and `/api/v1` (300), configurable under `Sangam:RateLimit`.
- **Ready for running behind Caddy (OI-037).** The data-protection key ring is kept in the database
  (`data_protection_keys`) so cookies and form tokens survive a restart; forwarded headers are trusted only
  from `Sangam:ForwardedHeaders:KnownProxies` or `KnownNetworks`; every host serves `/health/live` and
  `/health/ready`.
- PostgreSQL is described as 16 or later (OI-014); the expected HTTPS-port warning is silenced in tests
  (OI-015); a test covers individuals without an organisation (REQ-021).
- Migrations: `AuditProtection`, `DataProtectionKeys`. Tests: 269 (29 new).

### Added — PR-08 Sangam.Client SDK and the imagiQa sample
- `Sangam.Client` is now a real, packable SDK (with `Sangam.Shared`): `AddSangam` adds sign-in with
  authorization code + PKCE in one call, validating its options at start-up; `MapSangamSignOut`
  signs out of the application and Sangam; `GetSangamUser` gives the person's id, name, email and
  memberships, with `HasRole` and `HasPermission` applying the organisation rule (a role holds where
  given, and below only if it inherits). The claim parser accepts both token shapes and skips
  malformed entries. Package README included. ADR-0007.
- `samples/Imagiqa.Web` — imagiQa, a small Blazor hospital application (port 5500, own database
  `imagiqa_sample`): doctors and nurses register and find patients, nurses record vital signs,
  doctors write consultation notes; patients are seen only at the hospital that registered them.
  Every rule in `PatientRecords`, decided from Sangam's roles alone.
- The development seeder registers imagiQa as a partner application with `doctor`, `nurse` and
  `org_admin` roles, created once and never overwritten.
- Tests: 9 SDK tests; 11 imagiQa tests (rules against PostgreSQL, pages on the real host, the
  interactivity guard). Test database `sangam_identity_test_imagiqa` and dev database
  `imagiqa_sample` in CI and `deploy/postgres/init.sql`.

### Changed — PR-08
- `SaveTokens` defaults to on in the SDK, so sign-out sends `id_token_hint` and Sangam returns the
  person to the application; found in the browser run.
- The operator console shows sign-in rules in words ("Always two-step") instead of raw values.
- `Microsoft.EntityFrameworkCore.Relational` is pinned centrally, removing a version-conflict
  warning; generated migrations under `samples/` are exempt from analysers like those under `src/`.

### Added — PR-07 Partner console and application administrators
- `partners.sangamid.in` (`Sangam.Partner.Web`, Blazor Server, ports 5400/5401): where an
  application's own staff manage it. An ordinary OIDC client, `sangam-partner`, with a 2-hour
  cookie; seeded in Development alongside the operator console's client.
- Two ranks per application — **Admin** and **Owner** — in `app_admins.role` (migration
  `AppAdminRoles`; existing rows become `admin`). Admins manage roles, organisations, memberships,
  branding and sign-in policy; owners also manage administrators. The last owner cannot be removed
  or demoted.
- `PartnerGate`: at least one live `app_admins` row **and** an enrolled authenticator, re-checked
  on every visit.
- `IPartnerService` / `EfPartnerService`: every rule enforced per application and per rank. People
  search sees only users who have linked the application; roles can be given only to them, and a
  refused grant never creates the link. Adding an administrator gives one identical refusal for an
  unknown, unverified or unlinked address.
- Sign-in policy "stricter only": partners may choose each person's own choice or always two-step;
  password-only and email-code-only stay imagiQa's, and a platform-set value of either can only be
  tightened (`EfPartnerService.MayMoveTo`).
- Operator console: **Assign owner** on Applications (AppManager and above) and a *Partner owners*
  column that shows *none* for an application no partner can manage yet.
- `ManagementActor` on the five mutating `IManagementService` methods: audit rows name the person
  on the partner console and the application on the API; memberships now record
  `granted_by_user_id` and `revoked_by_user_id`.
- A person's own log tells partner staff ("An administrator of LiPi HIS…"), imagiQa ("A Sangam
  operator…") and the application's code ("LiPi HIS…") apart. Being made or removed as an
  administrator is flagged security-sensitive. New audit actions `app.admin.grant`,
  `app.admin.revoke`, `app.settings.update`.
- ADR-0006; `docs/authority-model.md` application plane filled in; `docs/go-live-checklist.md` and
  the PR-07 → PR-10 roadmap in `docs/README.md` (both intended for v0.6.0 and missing from it).
- Tests: 24 partner service tests against PostgreSQL; 9 partner console tests on the real host,
  including the interactivity guard; an operator console test that Sangam's own applications offer
  no buttons. Test database `sangam_identity_test_partner` in CI and
  `deploy/postgres/init.sql`.
- `apps.is_platform` (migration `PlatformApps`): Sangam's own portal, operator console and
  partner console are marked, by client id and by the seeder. They can never be given partner
  owners or be disabled from the console, and the partner console ignores them even if an
  administrator row exists. The operator console shows them as *platform*, without buttons.

### Fixed — PR-07 (after review)
- Disabling `sangam-admin` from the operator console was possible since PR-06 and would have
  locked every operator out; now refused for all of Sangam's own applications.
- A heading straight after a table sat flush against it on both consoles.

### Changed — PR-07
- Roadmap renumbered again: the SDK is PR-08, Anjal email delivery PR-09, production PR-10.
- Redirect URIs, secrets and an application's name are platform-only; the authority model no
  longer lists them as something application administrators edit.

### Added — PR-06 Operator console
- `admin.sangamid.in` (Blazor Server, dark scope): Users, user detail with actions, Applications,
  Operators. An ordinary OIDC client with a 2-hour cookie.
- Four ordered platform ranks — **Viewer**, **AppManager**, **Support**, **Owner** — enforced in
  `IAdminService`, never in the pages. Migration `RenamePlatformRoles` renames the PR-02 values
  (`support` → `viewer`, `operator` → `support`); a no-op on every existing database.
- `OperatorGate`: an unrevoked operator row **and** an enrolled authenticator, re-checked on every
  visit. People without a rank are told plainly and shown no data.
- Authenticator-app (TOTP) second factor for every user, on ASP.NET Core Identity's own provider:
  QR code rendered on this server as inline SVG, ten recovery codes, lockout shared with passwords.
  Operators cannot remove theirs. New sign-in step at `/login/authenticator`.
- Opening a user's record writes `admin.user.read` before returning anything; the user sees it in
  their own audit log, flagged.
- Suspend, reinstate, sign out everywhere, place and clear a hold, delete now (Owner, with reason),
  enable and disable an application, grant and revoke operators. The last owner cannot be revoked;
  an account holding console access cannot be deleted.
- `create-operator` bootstrap: makes the first Owner, then refuses forever.
- `docs/authority-model.md`, ADR-0005, and `docs/go-live-checklist.md` — bootstrapping the first
  owner on production and the two-owner rule, written now so they are not forgotten by PR-09.
- Roadmap renumbered: application administrators are PR-07, the SDK PR-08, production PR-09.
- Tests: every rank boundary in the authority chart against PostgreSQL; TOTP enrolment,
  verification, recovery codes and lockout; the operator lock; the bootstrap; the console's gate
  and what each rank is shown, rendered by the real host; the password-then-authenticator sign-in.

### Fixed — PR-06
- **No button in the portal or the console did anything.** Neither Blazor app ever set an
  interactive render mode, so every page was static HTML and no click handler ran. The portal
  shipped this way in PR-05 (v0.5.0): revoking an app, ending a session, signing out everywhere,
  the audit range and paging, saving the sign-in preference, downloading data and deleting the
  account were all inert. Only "Save changes" worked, being a real form post. Tests had asserted
  on rendered HTML, which proves a page renders, not that a button works. Both apps now render
  interactively; a guard test in each fails if a page is ever static again, and has been shown to
  fail with the bug reinstated. Each app was also exercised by clicking in a real browser.
- With interactivity on, a page renders twice. The console's user-detail page carries the loaded
  record from the first render to the second, so opening a record writes exactly one
  `admin.user.read` row rather than two.
- The authenticator key shown for typing by hand was in capitals, where a monospace letter O reads
  as a zero; base32 has no zero, so the authenticator app rejected it. It is now shown in lowercase
  groups of four, with a note that it contains only letters and the digits 2 to 7, and that the
  app must be set to Time based.
- Recovery codes could never be redeemed: code normalisation stripped the dash that Identity
  stores in them. A lost phone would have locked the person out for good.

### Added — PR-05 Self-service portal
- `account.sangamid.in` (Blazor Server) as a real OpenID Connect client of the identity server:
  Overview, Connected apps, Devices, Audit log and Personal details, behind its own cookie.
- `user_sessions`: one row per browser sign-in carrying the client IP, user agent, sign-in mode
  and an optional app-supplied `sangam_device` label. The session cookie carries the row id and
  the five-minute validation tick confirms the row is live, so a single device can be signed out.
- Standard OIDC `sid` claim on tokens, so a client can tell which session is its own.
- Revoking an app ends its grant, its consent and its OpenIddict authorizations and tokens —
  that app's only.
- `AuditNarrator`: audit rows phrased as sentences, with a 30 days / 90 days / 1 year /
  everything range selector.
- DPDPA: a direct-download JSON export of everything Sangam holds, and account deletion with a
  30-day grace period, immediate suspension and app revocation, and cancellation.
- `AccountPurgeService`: hourly sweep that pseudonymises accounts past their grace period
  (keeping the audit trail), skips accounts under an operator hold, and deletes session rows
  revoked more than 90 days ago.
- Migration `SessionsAndAccountDeletion`: the `user_sessions` table, and `purge_after`,
  `hold_placed_at`, `hold_reason` on users.
- Tests: portal reads and writes against PostgreSQL (apps, sessions, audit, export, deletion,
  purge, holds), the user-agent summariser, and the five screens rendered by the real host
  including cross-user isolation.
- ADR-0004.

### Added — PR-05 (after review)
- `AgePolicy`: self-registration requires an age of at least 18, matching the DPDP Act's
  definition of a child rather than any employment threshold. The date picker stops at the latest
  eligible date and the server rejects the rest. Previously the date of birth was only
  sanity-checked, so a child could register.

### Changed — PR-05 (after review)
- Mobile entry is one row: the dialling-code select **is** the prefix, so the code shown can
  never disagree with the country chosen — the previous two-control layout displayed a stale
  prefix until the next post. Codes are right-aligned in the list so the ISO codes form a column.
- The password requirement box is gone; the five tokens beside the verdict (`8+`, `upper`,
  `lower`, `number`, `symbol`) turn green with a tick as each is met.
- The portal shows a loopback address as "· this computer" rather than a bare `::1`.
- Personal details now accepts gender and mobile as well as name and language; a changed mobile
  is marked unverified. Email and date of birth stay fixed (email needs its own verified change
  flow; date of birth waits for DigiLocker identity verification).
- `updated_at` is emitted in tokens and userinfo, as ADR-0003 always said it should be: it is
  how a partner application notices that the profile copy it cached has moved.

### Changed — PR-05
- `UserSummary` gained `CreatedAt`.
- `init.sql` and CI create `sangam_identity_test_portal` for the portal's in-process host.

### Added — PR-04 Consent, sign-out and the full OIDC flow
- `/connect/authorize` (Authorization Code + PKCE, `prompt=login|none`), `/connect/token`
  (authorization code, refresh token, client credentials), `/connect/userinfo`,
  `/connect/endsession`; refresh chains capped at 90 days.
- Screen 7 (consent) showing the real values that will be shared and an explicit
  "will not be shared" column; screen 8 (sign-out) serving both the user's own `/logout` and
  app-initiated end-session with "Return to … without signing out".
- Partner chip on sign-in, registration, verification and the code screens; "Continue to …"
  on screen 4; the app's `sign_in_policy` now applies to app-initiated sign-ins.
- `sangam_orgs` claim built from live memberships, and the `phone`, `offline_access` and
  `sangam.manage` scopes.
- Management API at `/api/v1` (roles, organisations, memberships), scoped to the calling app
  by its client-credentials token; every write audited as `api`.
- `IAppDirectory`, `IConsentService`, `ITenancyQuery`, `IManagementService` and their EF
  implementations; app branding columns (`brand_colour`, `glyph`, `consent_version`) with
  migration `AppBranding`.
- Tests: management rules and consent versioning against PostgreSQL; the whole browser
  journey in-process (authorize → sign-in → consent → code → token → userinfo → refresh →
  sign-out), consent denial, unknown client, `prompt=none`, and the management API's
  authorisation.
- ADR-0003.

### Added — PR-04 (refinements)
- Registration takes a country from a dropdown (India first and default, 45 countries) with the
  dialling code shown as a prefix, and validates the national number length per country.
- Password requirements are shown live while typing (`wwwroot/js/password-meter.js`, progressive
  enhancement over the server-rendered meter) on both registration and password reset.
- Development-only `/dev/callback`: a stand-in partner redirect URI that shows the code, exchanges
  it for tokens and prints the ID token claims and userinfo, plus a one-click "start a sign-in"
  link on the foundation page.

### Fixed — PR-04
- Org-scoped roles were losing to app-wide roles of the same code (PostgreSQL sorts
  `NULLS FIRST` on `ORDER BY … DESC`).
- The development seeder now refreshes the sample app's branding on existing databases
  instead of only at creation.

### Added — PR-03 Auth screens 1–6
- Screens on `id.sangamid.in`, all working with JavaScript disabled: `/login`, `/login/code`
  (passwordless), `/login/verify` (code step), `/register`, `/verify` (code + server-rendered
  resend countdown), `/verified`, `/forgot`, `/reset`, a minimal signed-in `/account` with the
  sign-in preference, `/logout`, `/Error`, and Development-only `/dev/outbox`.
- `IAccountService` (register, find, issue/verify codes, password check with lockout and
  audit, passwordless start, reset with security-stamp rotation, sign-in preference) on
  ASP.NET Core Identity; `OneTimeCodeService`; `PasswordStrength` (Anjal's policy: 8+,
  all four classes, blocklist); `InMemoryEmailOutbox`.
- Session cookie (`sangam.session`, 12 h sliding, security stamp re-validated every 5 min)
  and pending-flow cookie (`sangam.pending`, 15 min); per-IP rate limiting on auth POSTs.
- Migration `ProfileFieldsSignInModesAndOneTimeCodes`: `first_name`, `last_name`,
  `date_of_birth`, `gender`, `sign_in_preference` on users (replacing `name`),
  `sign_in_policy` on apps, and the `one_time_codes` table.
- Tests: sign-in mode resolution, password strength, one-time codes (cooldown, attempts,
  expiry, rate window), account flows through the real DI graph, and HTTP journeys with a
  cookie-keeping no-JS browser session (register → verify → account → sign out → forgot →
  reset → sign in; two-step and passwordless; lockout; rate limiter; enumeration safety).
- ADR-0002.

### Changed — PR-03
- `.editorconfig`: CA2007 off for the three web hosts (no SynchronizationContext).
- Development logging: EF command and migration noise reduced to Warning.

### Added — PR-02 Domain + Infrastructure + first OIDC endpoint
- Domain entities for the tenancy model (`SangamUser`, `OrgType`, `Organisation` tree with
  materialised path, `App`, `Role` with optional org scope, `OrgMembership` with
  `applies_to_descendants`, `AppGrant`, `Consent`, `PlatformOperator`, `AppAdmin`,
  append-only `AuditEvent`) and the audit action taxonomy — see ADR-0001.
- `SangamDbContext` (ASP.NET Core Identity user tables, Sangam tables, OpenIddict tables; all
  snake_case) and the `InitialSchema` migration, with PostgreSQL rules that make
  `audit_events` append-only and partial unique indexes that allow re-grants after revocation.
- Argon2id password hashing (`Argon2idPasswordHasher`, PHC format, rehash-on-upgrade).
- `IClock`, `IEmailSender` (logging implementation until Anjal), `ISmsSender` (reserved),
  `IAuditWriter` (dedicated-context EF implementation).
- OpenIddict server in `Sangam.Identity.Server`: discovery document, JWKS, and
  `POST /connect/token` for the client-credentials grant; development certificates in
  Development/Testing, refuses to start elsewhere until PR-08 configures real ones.
- Development seeding of scopes and the `sangam-dev-sample` partner app (client credentials).
- PostgreSQL-backed tests (`[PostgresFact]`) driven by `SANGAM_TEST_CONNECTION`: run against
  a native PostgreSQL locally and a service container on the Ubuntu CI job; skipped elsewhere.
- `dotnet-ef` tool manifest; `sangam_identity_test` and `sangam_identity_test_server`
  databases in `init.sql` (the server tests use their own so parallel test projects never
  truncate under the in-process host); bootstrap script enables the PostgreSQL-backed tests
  when a server is reachable.

### Changed — PR-02
- `.editorconfig`: interface members need no accessibility modifier; EF migrations are exempt
  from style analysis.
- CI: explicit Ubuntu (with PostgreSQL service) and Windows jobs instead of a matrix; check
  names unchanged.

### Added — PR-01 Foundation
- Solution with nine `src/` projects (Identity Domain / Application / Infrastructure / Server,
  SelfService.Web, Admin.Web, Shared, Web.Shared, Client) and a mirrored test project for each.
- Clean-architecture layering tests that fail the build if dependencies point outward.
- Central package management, `global.json` pinned to the .NET 10 SDK, `.editorconfig` with
  style enforced in the build, warnings as errors.
- GitHub Actions CI: build + test on Ubuntu and Windows, plus a `dotnet format` check.
- Design system: `sangam-tokens.css`, `sangam-brand.css`, `SangamMark` and `SangamLockup`
  components, favicon set (16 / 32 / 180 / 512, `.ico`, `.svg`).
- LiPi Sans self-hosted (`Sangam.Web.Shared/wwwroot/fonts/lipi/`, SIL OFL) and LiPicons
  (`LiPicons.Blazor` 1.1.0 via `localpackages/`); no external font, icon or script loads,
  enforced by a test in every host.
- Foundation pages in all three hosts rendering the tokens, lockups and palette.
- `scripts/bootstrap-dev.ps1` (native PostgreSQL by default, `-InitDatabase`, `-WithDocker`),
  idempotent `deploy/postgres/init.sql`, and the server Compose stack
  (`deploy/docker-compose.dev.yml`: PostgreSQL 16 + Caddy).
- Apache 2.0 licence, README, CONTRIBUTING, CODE_OF_CONDUCT, SECURITY, issue and PR templates.
- Planning documents filed under `docs/planning/`; design handoff under `docs/design/`.
