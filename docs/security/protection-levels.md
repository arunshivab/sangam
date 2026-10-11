# Protection levels

*rc.8 (ASVS V1.8.1, V1.8.2, V8.3.4). The classes are SGM-910's (approved by the founder, 10 October 2026); this table
says which protections each class gets in Sangam, and where each is built. Reviewed with SGM-910: every year and
whenever a new kind of data is added.*

## 1. The classes

| Class | Examples (SGM-910 section 3) |
|---|---|
| Restricted | Secrets held as files (token signing and encryption keys, key-ring certificate, SAML keys, pepper, DigiLocker subject key, backup and audit-archive private keys — the last two offline); credentials held in the database (password hashes, two-step secrets, recovery codes, one-time codes, reset and invitation tokens, client secrets, SCIM tokens, webhook signing secrets) |
| Confidential | Personal data (name, date of birth, gender, e-mail, mobile, the identity-verification record, roles, memberships, attributes, sessions, IP addresses, consents, invitations, grievances, the audit log); business documents under NDA |
| Internal | Monitoring data, non-secret production settings, CI logs, vulnerabilities before their fix |
| Public | The code, SDKs, published documents, the privacy notice, security.txt, the front page, partner logos |

## 2. Protections by class

| Protection | Restricted | Confidential | Internal | Public | Where it is built |
|---|---|---|---|---|---|
| Encrypted in transit outside the VM | Yes | Yes | Yes | Yes | Caddy (TLS 1.2+, HSTS); outbound TLS with revocation checks (`TlsRevocation`) |
| Encrypted in transit inside the VM | No (accepted, decision 2) | No (accepted, decision 2) | No | — | TLS between containers when the VMs split |
| Stored only hashed or encrypted | Yes: Argon2id + pepper for passwords; keyed hashes for recovery codes (rc.8); data protection for two-step secrets; OpenIddict hashes client secrets | Not field-encrypted; the DigiLocker id is held only as an HMAC | — | — | `Argon2idPasswordHasher`, `SangamUserStore`, `IdentityVerification` |
| Never in the repository, an image or a backup | Secret files: yes | — | — | — | `.gitignore`, Docker secrets, the backup job copies only the dump |
| Fingerprint checked at start | Secret files and secrets: yes (rc.8) | — | — | — | `KeyFingerprintCheck` |
| Never logged | Yes | E-mail and mobile masked; no message bodies, codes or tokens | May be logged | — | [Log-content policy](log-content-policy.md) |
| Never shown back or exported | Yes | Shown to the person, their applications within scope, and operators | Not published | — | Services and screens |
| Every read audited | Operator views of keys: none exist | Operator record views, exports, evidence packs; partner and API reads of people (rc.8) | — | — | `AuditActions.AdminUserRead`, `AppPeopleView`, `UserDataExport`, `EvidenceExport` |
| Access limited to | The process that needs it; root on the VM (accepted, A6) | The person; applications they linked, within scopes; operators by rank; partner administrators for their own application | People working on Sangam | Anyone | Rank and role checks inside each service |
| Backups | Database credentials only inside the encrypted dump; secret files never | Inside `age`-encrypted dumps under Object Lock, 12 months at most | Logs one year | — | `scripts/backup-db.sh` |
| Retention | Destroyed when replaced (SGM-803) | SGM-910 section 6, built (rc.6, rc.7, rc.8) | 90 days (metrics) | — | `AccountPurgeService`, `RetentionSweep`, `InactivityService`, `AuditArchiver` |
| Deletion | Shred on rotation | Pseudonymised at deletion; merge records pointing at a deleted account removed (rc.8) | — | — | `AccountPurgeService.Pseudonymise`, `ForgetMergesAsync` |
| Not cached by browsers or proxies | Yes | Yes (`Cache-Control: no-store` on every page and JSON answer) | — | Static assets may be cached | Security headers |
| Leaves Sangam only through | Never (offline copies with the founder) | The person's own export, an evidence pack, a lawful request, the application's own scopes | — | — | SGM-910 section 3 |

## 3. Never held

Identity-document numbers or images, health data, card or bank numbers, biometrics (SGM-910 section 4). The service
refuses attribute names that look like health data; the identity-verification record keeps only name, date of birth,
gender and a keyed hash of the DigiLocker id.

## 4. Choosing a class

A new table, field, log line or file is classed in the pull request that adds it (SGM-913 check D2). When in doubt, the
higher class.
