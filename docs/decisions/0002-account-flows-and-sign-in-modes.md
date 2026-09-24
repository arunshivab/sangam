# ADR-0002 — Account flows and sign-in modes

Status: accepted (2026-09-24, PR-03). Refines ADR-0001 and deviates from the design handoff
where noted.

## Verification and recovery use emailed codes, not links

Six-digit codes, valid 10 minutes, five wrong guesses void the code, resend after 60 seconds,
at most five codes per hour per user and purpose. Only a SHA-256 of (user, purpose, code) is
stored; issuing a new code voids the previous one. Reasons: hospital mail gateways pre-fetch
links and burn one-time tokens; a code survives copy-typing from a phone; the same primitive
serves email verification, password reset and the sign-in second factor.

The handoff's `/verify?token=…` and `/reset?token=…` therefore become code-entry screens.
The "Resend in 0:47" countdown is server-rendered with `<meta http-equiv="refresh">`; the
server decides the button's state.

## No CAPTCHA in v0

Turnstile loads a script from Cloudflare, which breaks both "no external loads" and "works
without JavaScript". v0 relies on per-IP rate limiting on every auth POST (default 20 per
minute, in-memory, per node), per-user code limits, Identity lockout (5 failures, 15 minutes)
and the fact that registration cannot complete without receiving mail. The handoff's Turnstile
strip is therefore not rendered. Revisit if abuse appears (self-hosted proof-of-work is the
likely next step).

## Sign-in modes

Three modes: `password`, `password_and_otp` (second factor by email), `otp_only`
(passwordless by email). Two settings:

- `users.sign_in_preference` — the user's own choice (default `password`).
- `apps.sign_in_policy` — `default` (user's preference applies) or one of the three modes,
  which the user cannot override.

Effective mode = app policy unless `default`, else user preference; a portal sign-in (no app)
uses the preference. `SignInModes.Resolve` is the single implementation. The app policy starts
applying when apps initiate sign-in (authorization code flow, PR-04). Registration always sets
a password, so every mode is reachable for every user.

## Profile at registration

First name, last name, email, mobile (E.164; an Indian ten-digit number is normalised to
`+91…`; the form takes the country code and the national number as two fields), date of birth,
gender, password — all required; terms must be accepted explicitly. Mobile is stored unverified
until mobile OTP ships. Password policy is the same as Anjal's: at least 8 characters, an
uppercase letter, a lowercase letter, a digit and a symbol, and not on a local blocklist;
14+ characters is reported as "strong". ASP.NET Core Identity is configured with the identical
rules, and the strength meter is computed server-side so it renders after a failed post
without JavaScript.

## Sessions

The session cookie carries the user's security stamp and is re-validated against the database
every five minutes; a password reset rotates the stamp, so other devices are signed out within
that window and the current device immediately. Half-finished flows (registered but unverified,
password accepted but code outstanding, reset requested) travel in a separate 15-minute
`sangam.pending` cookie, never in the URL.

## Enumeration

Sign-in, code sign-in, forgot and reset all respond identically for unknown and known
addresses; registration reports "an account already exists with this email or mobile" without
saying which. Failed sign-ins for unknown addresses are audited as `anonymous`.

## Development mail

`Sangam:Email:UseOutbox` (Development and Testing) captures messages in memory; `/dev/outbox`
shows the last fifty in Development only and is a 404 elsewhere. The Anjal-backed sender
replaces the logging sender once Anjal is hosted.
