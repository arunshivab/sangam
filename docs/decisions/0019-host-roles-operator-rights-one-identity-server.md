# ADR-0019 — One database role per host, named operator rights, one identity server

Status: accepted as built (2026-10-26, 1.0.0-rc.16), the founder's decisions of 26 October 2026 on ASVS batches 2 and 3.
Builds on ADR-0005 (operator ranks) and R7's cluster locks. Answers ASVS V1.2.2, V1.4.4, V1.4.5, V1.11.2 and V1.12.2.

## Context

The ASVS self-assessment left these rows Partly:

- **V1.2.2.** All four hosts signed in to PostgreSQL with one application role and the same rights; any host could do
  what any other could.
- **V1.4.4, V1.4.5.** Operator rights were a ladder of ranks (Viewer < AppManager < Support < Owner), and each service
  method compared the operator's rank with the one it needed. Nothing made a new method check at all.
- **V1.11.2.** The sign-in rate limits are counted in each process's memory. With two identity servers each would allow
  the full limit; nothing stopped a second one being started.
- **V1.12.2.** Partners' logos could be SVG, which can carry script, served from Sangam's own origin (made safe only by
  a sandboxing Content-Security-Policy and an allow-list check).

## Decision

**Each host has its own database role.** `sangam_identity_app`, `sangam_portal_app`, `sangam_admin_app` and
`sangam_partner_app` belong to the group `sangam_app`, which reads and writes the application's tables, only adds to the
audit log, and never writes the migration history or makes tables. Only the identity server's role may delete archived
audit events (the hourly sweep and the archive now run only there) and use the tables only its own endpoints touch
(`client_assertion_uses`, `saml_requests`). `sangam_owner` owns the schema and runs the migrations; no host has its
password. The roles are made once by the database administrator (`deploy/production/postgres/roles.sql`); the rights
come with a migration (`Rc16HostRoles`) and are granted to every later table automatically (the owner's default
privileges). Each role has its own connection limit and `application_name`.

The consoles and the portal share Sangam's service layer, so most tables are used by most hosts; denying more per host
would need that layer split by host. That is not done: the gain now is a separate credential per host (one can be
changed or cut off alone, and the database log names the host), the audit log protected from every host but the one
that archives it, and the identity server's own tables closed to the rest.

Caddy reaches the hosts over plain HTTP inside the VM's private Docker network: accepted, as for the other hops inside
the VM (founder decision 2 of 11 October 2026, V9.2.2). The database administrator is `sangam_dba`, not `postgres`.

**Operators' rights are named.** Each console action asks for one right (`OperatorRight`: `SuspendAccounts`,
`ManageApplications`, `ReviewRecoveries` and so on) through one check, `OperatorAccess`, which audits every refusal
with the operation, the right and the rank it needs. Ranks remain how rights are given, and cumulative, so who may do
what is unchanged (docs/authority-model.md lists every right). A test reads the service layer and fails the build when a
public method that acts for an operator does not go through `OperatorAccess`.

**Exactly one identity server per database.** It holds a PostgreSQL advisory lock for as long as it runs, on a
connection of its own outside the pool; a second one refuses to start. If the lock's connection is lost, it takes the
lock again, or stops itself if another server has it. The Kubernetes layout, which runs two identity replicas, turns the
guard off and accepts limits doubled by the number of replicas, as it already did (deploy/kubernetes/README.md).
Moving the limits into the database was weighed and not chosen: it adds writes to every sign-in attempt for a single
VM that does not need it.

**Logos are PNG only.** An SVG is refused at upload, however plain. An SVG kept from before is not served. The logo is
still served from Sangam's origin, with `nosniff` and the sandboxing policy; with nothing scriptable allowed, a separate
domain was judged not worth a new host name and DNS record.

**Smaller decisions in the same release.** Finished sign-in tokens and authorizations are deleted 30 days after they
were made (refresh tokens last 14). Every host shows the same 404 and error pages, in the visitor's language, the error
page with a reference to quote. Every pull request names at least one reason for the change (an issue, OI, DEF, SGM or
ASVS id); "none" is refused.

## Consequences

- Production needs the roles made before the first migration, five passwords, and four connection-string files instead
  of one (`deploy/production/README.md`, the go-live checklist). `scripts/deploy.sh` checks the secret files, runs the
  migrator as the owner and starts the stack.
- An operator check can no longer be forgotten without the build failing. Changing who may do what is a change to
  `OperatorRights` and the authority model, reviewed like code.
- Sangam cannot be scaled to a second identity server on the VM without first sharing the rate limits.
- A partner with only an SVG logo must export a PNG.
