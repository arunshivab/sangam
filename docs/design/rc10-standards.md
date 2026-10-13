# rc.10 design standards and decisions

Sangam 1.0.0-rc.10 rebuilds the operator console (`admin.`), the partner console (`partners.`) and the
self-service portal (`account.`) from the founder's design review of 12 October 2026. This file records the
standards the code now follows and the decisions taken in that review, including where the code
deliberately departs from the design handoff in this folder. Where this file and the handoff disagree,
this file wins.

## Standards

| What | Standard |
|---|---|
| Buttons and inputs | 44 px tall; input text 16 px; labels 14 px / 500; corners 2 px |
| Buttons inside a table row | 36 px, 14 px text |
| Toolbar filters | 40 px |
| Panels | 4 px corners; grids align to the top |
| Micro-labels (table headings, sidebar groups) | 11 px monospace capitals; nothing else under 13 px |
| Sidebar | 232 px; links 40 px with a 3 px rule on the current one; collapses above the work area under 1024 px |
| Top strip | 48 px: environment, how this session was signed in, when it ends, "Sign in again" |
| Colour | Every text colour clears WCAG AA on every surface it sits on (measured values are in each host's `site.css`) |
| Danger | Danger text stays light (`#F0A08E` operator); the ochre `#C2603F` is used for dots only, never text |
| Destructive actions | Last, on their own, set apart (`sg-actions-danger`) |
| Typeface | LiPi Sans for Latin and Devanagari; Malayalam falls back to the system font until LiPi Sans has Malayalam |
| Languages | Every new string in English, Hindi and Malayalam; role names are never translated |

The shared shell and controls are `src/Sangam.Web.Shared/wwwroot/css/sangam-console.css`; each console's
`site.css` sets only its palette (`--c-*`) and what is its own. The operator console stays deep green and the
partner console slate, so nobody mistakes one for the other.

## Decisions of the review

1. **No Turnstile.** The bot checks stay CAPTCHA-free.
2. **The strip's "Extend" is "Sign in again".** It starts a fresh sign-in (`/reauth`, `max_age=0`) and shows when
   the session ends. The 12-hour absolute limit and the 2-hour idle limit are unchanged; nothing extends a session.
3. **A console session must have been signed in with two-step sign-in** (`acr` level 2 or more). The strip shows
   the environment from configuration — `PRODUCTION · India` from `Sangam:Region` (default `India`) — and any
   other environment in a different colour.
4. **Two-step sign-in is two cards**: *Every time* (password and a code) and *Only when an app asks* (password).
   "Code by email" (`OtpOnly`) stays a separate toggle on the email-code row.
5. **Sign-in methods are listed strongest first**: passkey, authenticator app, email code, text code, password.
6. **Leave organisation is in rc.10**, with guards: a sign-in from the last 15 minutes; an inline confirmation that
   lists the roles and says "Only {organisation} can give these back"; it leaves at organisation level; the last
   organisation administrator of an application cannot leave that application's role; it uses the existing revoke
   path; it is audited as the person (`user.organisation.leave`); the person and every organisation administrator
   of each affected application are e-mailed. Roles held at a parent organisation are not touched.
7. **Organisations appear only on the app tiles** of the portal overview; there is no "Working as" or switcher.
8. **Roles are shown exactly as the application named them** ("as named by LiPi"), falling back to the code.
   Sangam does not define, interpret or translate roles: only the application knows what `doc` means.
9. **No review state for applications.** The registry shows the client secret's age and warns after 12 months.
   Applications registered before rc.10 show "date not recorded" until their secret is next changed.
10. **The work queue has no Merges tab**; merges stay with the person's own two accounts.
11. **No "Test sign-in"** on the partner console.
12. **Work queue order**: a recovery waiting for a decision comes first (someone is locked out until an operator
    decides), then everything with a clock, the soonest first. This departs from the handoff's single deadline order.

## Each console starts where its work is

| Rank | Starts at |
|---|---|
| Owner | Platform overview (`/`) |
| Support | Work queue (`/queue`) |
| AppManager | Applications (`/apps`) |
| Viewer | Monitoring (`/monitoring`) |

## Open items for SGM-701 (document set v25)

- **A6 — organisation context**: an `org_id` claim, or a chosen organisation, in tokens to applications.
- **T1 — ward tablet**: a shared device signed in by several people in turn.
- **M12 — break-glass**: emergency access when every owner is unavailable.
- **G1–G2 — an organisation console** for organisation administrators.
- ~~Zero-downtime secret rotation~~: done in rc.11 (two live secrets). `private_key_jwt` remains a later option.
- Decided and closed: Leave organisation (in rc.10).

## Before go-live

- A native Hindi and Malayalam review of the new strings (228 new; machine-assisted).
- Malayalam in LiPi Sans, then switch the `ml` font from the system fallback.

## rc.11 decisions (13 October 2026)

- **Sign in with an email, a mobile number or a login ID.** Login IDs are chosen by the person (the portal suggests
  `first.last`); a mobile number signs in once verified.
- **Identifier first, without telling anyone whether an account exists.** The "how do you want to sign in" screen depends
  only on the application's rule; the greeting is "Signing in as {what you typed}", never a name.
- **This device's list** for shared ward computers: opt-in, at most six people, 30 days, masked, removable. It departs from
  the handoff's single "remembered account" row, which showed the last person to everyone. A per-device or per-hospital
  switch to turn it off comes with the ward tablet (T1).
- **No "Resend verification" for operators (O3).** Sangam sends a fresh code each time an unverified person signs in, and
  a code sent separately could not be used without that sign-in.
- **The code screen stays a code-by-email screen for accounts that chose it.** A password account asking for a code gets a
  reminder by e-mail instead, as since rc.2: a code alone is not offered to accounts that use a password.
- **Two live client secrets, rotated by the owner.** Each owner-made secret lasts 12 months (warning at 30 days); at most two
  are live; the last cannot be revoked; only owners make or revoke. The secret Sangam issued from configuration counts as
  one of the two until the owner revokes it.
- **Sign-in rules keep the rc.10 policy model**, in the new layout (ladder, three groups, one save bar).
- **Access reviews**: Keep records the review only; Extend by 3, 6 or 12 months; End today with a reason shown in the
  person's Activity. "Unused" means the person has not signed in to that application for 90 days (`app_grants.last_used_at`).
- **The Users page's only bulk action is "Sign out everywhere"**, with a reason, audited per person.
- **The signing keys page is read-only**: current and previous token-signing keys from the published key set, with
  fingerprints, ages and when the key in use was issued. Rotation stays a server task (go-live checklist).
