## What this PR delivers

<!-- One paragraph: what changes and why. Name the release (e.g. rc.7) and any defect (DEF-nnn) or policy (SGM-nnn) it answers. -->

## Checked by the build

These are enforced by CI and cannot be merged while one fails, so they are not ticked here (SGM-913 section 3):
compiler and analysers with warnings as errors; tests on PostgreSQL 16, an empty PostgreSQL 18 and without a database;
`dotnet format --verify-no-changes`; migrations match the model; CodeQL; dependency, secret, image and manifest scans;
every screen text in Hindi and Malayalam with no stale keys; the SDK builds.

## Secure-coding checklist (SGM-913)

Tick each item, or write `n/a` and the reason after it. The source is the ASVS 4.0.3 requirement, defect or decision behind the check.

### Access
- [ ] **A1** Every query and command is scoped to the calling application (and organisation); a test shows another application gets nothing. *(V4.1.1, V4.2.1)*
- [ ] **A2** Records Sangam owns itself (`is_platform`) refuse dangerous actions in the service, not only in the screen. *(V4.1.1; DEF-020)*
- [ ] **A3** A refused action is audited (`access.denied`) without the data it was refused. *(V7.2.2)*

### Accounts and tokens
- [ ] **B1** The same answer whether or not an account, address or id exists: registration, sign-in, reset, invitation, management API. *(D-L; rc.5 consent first)*
- [ ] **B2** A new token claim follows ADR-0003 and is tested in the token and in userinfo. *(DEF-015)*
- [ ] **B3** A secret a person must type is shown so it cannot be misread (lower case, grouped), with instructions. *(DEF-018)*

### Input and output
- [ ] **C1** Every input has a length or range limit; a constrained value is chosen from a fixed list, never free text. *(V5.1.3, V5.1.4; DEF-008)*
- [ ] **C2** The database is reached through EF Core; any raw SQL is parameterised. *(V5.3.4)*
- [ ] **C3** An identifier placed in a query language (XPath, SQL, LDAP, a regular expression) is first checked against a strict format. *(V5.3.10; rc.4 SAML fix)*
- [ ] **C4** Output is encoded by Razor or Blazor; no raw markup built from user data. *(V5.3.3)*

### Data and logs
- [ ] **D1** Nothing Restricted is ever logged; e-mail and mobile are masked. *(V7.1.1, V7.1.2)*
- [ ] **D2** A new table, field, log line or file has its class under SGM-910 and, if it holds personal data, a retention row. *(V8.3.4; SGM-910)*
- [ ] **D3** Nothing on SGM-910's never-held list can be stored (document numbers, health data, card or bank numbers, biometrics). *(SGM-910)*

### Secrets and cryptography
- [ ] **E1** A new key or secret is in SGM-911's inventory, refused at start-up outside Development and Testing when missing or a development value, and in the go-live checklist. *(V6.4.1; DEF-022)*
- [ ] **E2** No home-made cryptography; a library's defaults are checked in the library itself. *(V6.2.2; DEF-004)*

### Outbound calls and files
- [ ] **F1** Calls to partner-supplied addresses go through `OutboundHttp`: https only, no redirects, private addresses refused. *(V12.6.1)*
- [ ] **F2** An upload has a size limit, a type check and the virus scan. *(V12.1.1, V12.4.2)*

### Tests
- [ ] **G1** Failure paths are tested (wrong id, another application, expired, too long, missing permission), not only success.
- [ ] **G2** Tests pass on an empty database, as CI runs them. *(rc.5 demo test)*
- [ ] **G3** A guard test is shown to fail with the fault present. *(DEF-017, DEF-020)*
- [ ] **G4** Every interactive control changed is clicked in a real browser. *(DEF-017)*

### Delivery and dependencies
- [ ] **H1** Files the change removes are deleted in the install instructions. *(DEF-012)*
- [ ] **H2** No reliance on a suppression made for another purpose; a new suppression carries its reason. *(DEF-013)*
- [ ] **H3** A new dependency is supported, current, and its licence recorded in SGM-304. *(V14.2.1; DEF-001)*
- [ ] **H4** The go-live checklist and the documents the change affects are updated in the same change. *(DEF-019, DEF-022)*

### Design
- [ ] Visual changes verified against `docs/design/README.md` (the design handoff wins).
- [ ] Public members carry XML docs; no `Class1.cs` / `UnitTest1.cs` scaffold left behind.
