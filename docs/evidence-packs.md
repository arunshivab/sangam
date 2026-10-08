# Evidence packs for connected applications (R7, PR-32, CAP-110)

An application connected to Sangam inherits Sangam's sign-in, two-step and audit controls. An auditor of that
application (ISO 27001, SOC 2, NABH, DPDP Act) asks for proof of those shared controls for a period. The evidence pack is
that proof: one zip, built on demand from the live records, for one application and one period of up to 366 days.

## Who can download one

An owner or administrator of the application, signed in to the partner console with an authenticator, from the
application's **Evidence** tab: choose the first and last day and press *Download evidence pack*. Each download is
audited (`evidence.export`, with the pack's SHA-256), and an administrator may build six packs in ten minutes. Sangam's
own platform applications have no pack.

## What is in it

| File | Contents |
|---|---|
| `README.md` | What the pack is, the application, the period, who made it and when, a summary (administrators, those without an authenticator, people with access, events), and a description of every file |
| `application.json` | The registration: client id and type, redirect and sign-out addresses, grant types, permissions, consent type |
| `sign-in-policy.json` | The sign-in rules in force when the pack was made: the application's and each organisation's |
| `administrators.csv` | Everyone who administers the application, with role, granted and revoked dates, and whether they have an authenticator |
| `roles.csv` | The application's roles and permissions |
| `organisations.csv` | The application's organisations and hierarchy |
| `access-list.csv` | Everyone with access at the end of the period: how they sign in, two-step status, roles per organisation |
| `access-changes.csv` | Access granted, changed and removed during the period |
| `audit-events.jsonl` | Every audit event about the application in the period, in the SIEM JSON envelope with the shared schema 1.0 event |
| `audit-summary.csv` | Counts by action and outcome |
| `audit-chain.txt` | The result of checking the audit log's hash chain when the pack was made |
| `integrations.json` | SCIM provisioning, webhooks, custom attributes and claims as configured — never a secret |
| `manifest.json` | Every file with its size and SHA-256 |

CSV cells that a spreadsheet would read as a formula are prefixed so they are shown as text. People are identified by
name, e-mail and Sangam id only where the application already holds them through sign-in.

## How an auditor checks it

1. Check `manifest.json` against the files (`sha256sum`).
2. Read `README.md`, then `audit-chain.txt` (the chain was intact when the pack was made).
3. Sample `access-list.csv` against the application's own user list, and `access-changes.csv` against its joiner and
   leaver records.
4. The event ids in `audit-events.jsonl` can be checked with the founder against the live log, and the SHA-256 of the
   whole zip against the `evidence.export` event.

The imagiQa demo's pack from R7 is in the release evidence as an example.
