# ADR-0001 — Tenancy and identity model

Status: accepted (2026-09-24, PR-02). Supersedes the corresponding sections of
`docs/planning/sangam-multitenancy-data-model.md` where they differ.

## Users

- One `users` row per human, ever. Built on ASP.NET Core Identity's `IdentityUser<Guid>`
  (`SangamUser`) so `UserManager`, lockout, security stamps, confirmation flags and
  OpenIddict's Identity integration work unchanged. The password hash lives on the user row;
  the compensating rule is that **`SangamUser` never leaves the Infrastructure layer** — every
  read is projected into a DTO without credential columns.
- Passwords are hashed with **Argon2id** (PHC string, m=64 MiB, t=3, p=1; parameters travel
  with the hash and a weaker hash is rehashed on next successful sign-in).
- **Email** is the sign-in identifier, verified by OTP (sent through Anjal once hosted; a
  logging sender until then). **Mobile** (E.164) is stored, unique among non-deleted users,
  and stays unverified until mobile OTP ships (2027). A family/shared mobile is a later
  addition when patient-facing login is designed.
- Status: `active | suspended | deleted_soft | deleted_hard`. Hard-deleted users keep a
  pseudonymised row for audit integrity.

## Organisations

- A tree: corporate group → hospital / clinic / lab → department, via `parent_org_id` and a
  materialised `path` (`/{root}/{child}/…/{self}/`) so subtree queries are prefix matches.
  Depth is not hard-limited; `org_types` says which types may be roots or have children.
- Memberships are **explicit per node**. `applies_to_descendants` on a membership lets a role
  flow down a subtree (a corporate-level admin acting across daughter hospitals); the default
  is no inheritance.

## Roles — owned by the app, never by the organisation

- Each app registers its own role vocabulary (`roles(app_id, code)`), managed through the
  app-admin console or the management API using the app's client credentials. Organisations
  choose from the app's list; they map their own designations to it inside the app.
- A role may be **org-scoped** (`roles.org_id` set) when the app creates one for a single
  organisation; Sangam still only accepts it from the app. Permissions are opaque JSON that
  only the app interprets and that Sangam copies verbatim into tokens.
- Sangam's trust boundary is therefore the app, whether a human clicked "approve" in the
  app's own console or the app auto-approved.

## Admin levels

| Level | Table | Scope |
|---|---|---|
| Platform operator (Sangam admin) | `platform_operators` (`support`, `operator`, `owner`) | Whole platform; MFA mandatory |
| App admin (partner company staff) | `app_admins` | One app: its roles, URIs, users and orgs |
| Org admin | `org_memberships` with the app's `org_admin` role | One (org, app) pair |
| Member | `org_memberships` with any other role | One (org, app) pair |
| Individual | `app_grants` without any membership | Personal use of one app |

## Audit log

Append-only (`audit_events`; PostgreSQL rules discard UPDATE and DELETE). Actor types:
`user`, `admin`, `api`, `system`, `anonymous` — the last one covers events before the actor
is known (failed sign-in for an unknown email, rate-limit trips) and is an addition to the
planning document.

## Consequences

- Schema: 19 tables (Identity 4, Sangam 10, OpenIddict 4, migrations history 1), all
  snake_case, enums stored as lowercase snake text with CHECK constraints.
- Sessions are OpenIddict authorizations/tokens rather than a bespoke `sessions` table.
- Two-factor and external logins use Identity's own tables when they arrive (v1).
