# Log-content policy

*rc.8 (ASVS V7.1.1, V7.1.2, V7.1.3, V7.1.4, V7.3.1; SGM-801). What Sangam writes to its operational logs and its audit
log, and what it never writes.*

## 1. Two kinds of log

| | Operational log | Audit log |
|---|---|---|
| What | What the software did: start-up, errors, warnings, refused requests | What people and applications did: sign-ins, changes, reads of people's data, refusals |
| Where | Container console, `local` driver, rotated; one year in the encrypted backups | PostgreSQL `audit_events`, hash-chained; one year, then the encrypted archive for seven years; optionally the SIEM |
| Format | JSON, one object per line, UTC timestamps with milliseconds, scopes included (rc.8; Development keeps plain text) | SGM-208's schema |
| Levels | `Information` for Sangam; `Warning` for ASP.NET Core, Entity Framework's SQL commands and OpenIddict (OpenIddict from rc.8) | — |

## 2. Never written to either log

- Passwords, the pepper, any key or certificate password.
- One-time codes, authenticator codes, recovery codes, passkey material.
- Access, refresh, ID and logout tokens; authorization codes; client secrets; API keys; SCIM tokens; webhook secrets.
- Session or antiforgery cookies.
- Message bodies (e-mail or SMS).
- Identity-document numbers (Sangam never holds them), health data, card or bank numbers.
- Search words typed in the consoles (rc.8).
- Vault tokens and the values read from the vault (rc.8).

## 3. Written only masked or hashed

| Data | Form |
|---|---|
| E-mail address | `m***@example.in` |
| Mobile number | Last two digits only (`SmsNumbers.Mask`); a keyed hash in the SMS log |
| IP address | Full in the live audit log (security need); shortened in the archive |
| User agent | In the live audit log; dropped in the archive |
| Key fingerprints | SHA-256 of a certificate file; for a secret, a keyed hash cut to 128 bits (rc.8) |

## 4. Always written (security events)

| Event | Where |
|---|---|
| Sign-in success and failure, lockout, step-up, second-factor failure and replay (rc.8) | Audit |
| Every refused action in the consoles and the API, with the operation and the rank it needed (rc.8) | Audit (`access.denied`) |
| Refused clients and grants at the token endpoint | Audit (`token.refused`) |
| Reads of people's data by operators, partner administrators and applications (rc.8) | Audit |
| A changed or new key at start-up (rc.8) | Audit (`security.key.mismatch`, `security.key.recorded`) and an alert |
| Requests with a method Sangam does not use, or the wrong method for an address (rc.8) | Operational log, event 5100 |
| A protocol parameter sent twice (rc.8) | Operational log, event 5101 |
| A refused start, with its reason | Operational log, Critical |

## 5. Injection

The JSON formatter escapes control characters, so a value with a line break cannot forge a log line (V7.3.1). Values
from the caller that are logged (a request path, an HTTP method) are clipped. Audit metadata is serialised JSON.

## 6. Changing this policy

A new log line or audit event is classed in its pull request (SGM-913 D1, D2). A test that reads a log or audit row
checks that it does not contain the secret or personal value involved, as `ManagementApiTests`, `PartnerServiceTests`
and `SecretSourcesTests` do.
