# ADR-0007 — The Sangam.Client SDK and the imagiQa sample

Status: accepted (2026-09-30, PR-08). Builds on ADR-0003 and ADR-0006.

## Context

An application signing people in with Sangam needed about forty lines of OpenID Connect set-up,
and then had to parse the `sangam_orgs` claim and work out, by hand, whether a role held in a given
organisation. Every partner would have written that differently. And no application had yet used
Sangam end to end the way a partner would.

## Decision

### The SDK

`Sangam.Client` is a thin, packable layer over `Microsoft.AspNetCore.Authentication.OpenIdConnect`.
It references nothing server-side (`LayeringTests`).

- `services.AddSangam(o => …)` — authorization code with PKCE, a cookie for the application,
  Sangam's claim names kept as they are on the wire (`MapInboundClaims = false`), and the default
  scopes `openid profile email orgs.read`. Options are validated at start-up: a missing client id
  or an `http` authority outside development fails loudly rather than at the first sign-in.
- `SaveTokens` is **on** by default. Signing out sends the ID token back as `id_token_hint`, which
  is how Sangam knows which application is asking and returns the person to it. Found in the
  browser run: with it off, sign-out ended on Sangam's sign-in page instead of coming back.
- `app.MapSangamSignOut()` — ends the session in the application and at Sangam.
- `principal.GetSangamUser()` → `SangamUser`: id, name, email, and every membership.

### The organisation rule, in one place

A role given at organisation A holds at organisation B when B is A, or when the role applies to
descendants and B's path starts with A's. Paths end in `/`, so `/a/` can never match a sibling
`/ab/`. `SangamUser.HasRole` and `HasPermission` apply this; applications never re-implement it.

The claim parser accepts both shapes a token handler may produce — one claim per organisation, or
one claim holding the whole array — and skips malformed entries rather than failing the sign-in.

Roles are as of sign-in. A role granted on the partner console arrives the next time the person
signs in. That is stated in the package README rather than hidden.

### imagiQa, the sample

`samples/Imagiqa.Web` is a deliberately small hospital information system, built exactly as a
partner would build it: it references only `Sangam.Client`, keeps its own PostgreSQL database
(`imagiqa_sample`), and is registered on Sangam as an ordinary partner application — not a
platform one — so its hospital roles are managed on the partner console.

| | Doctor | Nurse | Anyone else signed in |
|---|:--:|:--:|:--:|
| Register and find patients | ✓ | ✓ | — |
| Record vital signs | — | ✓ | — |
| Write consultation notes | ✓ | — | — |
| Read vitals and notes | ✓ | ✓ | — |

Patients belong to the hospital (Sangam organisation) that registered them and are seen only
there. Every rule is in `PatientRecords` and decided from the `SangamUser` alone; the pages only
choose what to show.

It is built and tested by CI and never shipped. It is also the reference for integrating a real
application — Anjal's webmail first, as an additional sign-in option that decides nothing yet
about how Anjal and Sangam relate.

## Consequences

The two packages (`Sangam.Client`, `Sangam.Shared`) are produced with `dotnet pack` and consumed
from a local feed, like the Chuvadi packages. Publishing to nuget.org is a later decision.

Not in this PR: a typed client for the management API (`sangam.manage`), and refreshing roles
without signing in again.
