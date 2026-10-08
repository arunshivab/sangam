# Private pilot guide

The first stage of go-live (D-I, `go-live-checklist.md`): Sangam on the production server, registration by
invitation only, the founder and invited testers only. The first pilot application is the imagiQa demo at
`https://demo.sangamid.in`; Anjal's webmail is the second.

## Before inviting anyone

- `Sangam__Registration__InvitationOnly: "true"` on the identity server (set in `docker-compose.yml` for the pilot;
  remove it at the controlled launch), so `/register` is closed without an invitation.
- The demo is registered from settings with `Sangam__Clients__demo__ManagementApi: "true"`, so it can place testers at
  its demo hospital (below). Nothing else is given the management API.
- `Imagiqa__Demo: "true"` on the demo: every page carries the banner "Demo, not for real patient data".

## Inviting a tester

Either:

- list their address in `Sangam__Registration__AllowedEmails` (comma-separated) and restart the identity server; they
  register at `https://id.sangamid.in/register`; or
- send an invitation from the partner console: imagiQa → *Demo Hospital (made up)* → **Invite someone by email**, with
  the role they should have. The link works once, for 7 days; accepting it registers them and gives them the role, so
  they skip the join step below. (The hospital exists once the first tester has joined it.)

Then they open `https://demo.sangamid.in`, sign in with Sangam and allow imagiQa on the consent screen.

## The demo hospital (V-15)

A tester who has no role at a hospital on imagiQa is shown *You have no role at a hospital on imagiQa yet* and, on the
demo only, two buttons: **Join as a doctor** and **Join as a nurse**. Either one places them at **Demo Hospital (made
up)** with that role and signs them in again so the role is in their session — no one has to grant roles by hand.

- imagiQa does it the way any application would: a client-credentials token with `sangam.manage`, then the
  management API (`PUT /api/v1/roles/{doctor|nurse}`, `PUT /api/v1/orgs/{id}`, `PUT /api/v1/orgs/{id}/members/{user}`).
  The hospital and the two roles are created the first time; every tester joins the same hospital
  (`d3770000-0000-4000-8000-000000000001`, or `Imagiqa__DemoHospitalId`).
- It is offered only when `Imagiqa:Demo` is true. Outside the demo the page says to ask the hospital's administrator.
- Each join is in Sangam's audit log (the membership grant, with imagiQa as the acting application).
- To take a tester out: partner console → imagiQa → Demo Hospital (made up) → remove their role. They can join again
  from the demo; to stop that, end the pilot or remove `ManagementApi` from the demo's registration.
- Patients, notes and vitals at the demo hospital are whatever testers type. Remind testers never to enter real
  patient data; the banner says so on every page.

## What to ask testers to try

- Registration from the invitation, e-mail verification, signing in with a password and with a code.
- Adding an authenticator app and a passkey in the account portal; signing in with each.
- The demo: join as a doctor, register a patient, write a note; sign out and join as a nurse with a second account,
  record vitals for the same patient.
- The account portal: connected applications (imagiQa should be listed), sessions, the activity log, withdrawing
  consent from imagiQa (the next visit asks again).
- The language picker: Hindi and Malayalam on every screen, including the demo's sign-in.

## Ending the pilot

Before the controlled launch, decide whether testers' accounts stay (they are real Sangam accounts) and remove the
demo hospital's memberships if the demo will be shown to hospitals.
