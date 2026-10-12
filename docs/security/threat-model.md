# Threat model

*rc.8 (ASVS V1.1.2; founder decision 7, 11 October 2026). Kept in the repository and reviewed at every release and at
every design change that adds a component, a data flow or a trust boundary. The review is recorded in section 7. The
external penetration test before public launch (SGM-506) tests this model.*

Method: STRIDE at each trust boundary of [architecture.md](architecture.md) section 4. Each threat names the controls
that meet it in the code or the deployment, and what is left over. "Accepted" points at the founder's decision.

## 1. What is being protected

| Asset | Class (SGM-910) | Why it matters |
|---|---|---|
| Token signing and encryption keys | Restricted | Whoever holds them can mint tokens for any person to any application |
| Password pepper, password hashes | Restricted | With both, passwords can be attacked offline |
| Two-step secrets, recovery codes, one-time codes | Restricted | Bypass the second factor |
| DigiLocker subject key | Restricted | Re-identifies verified people; lets one identity verify two accounts |
| Personal data (profile, contact, sessions, consents, memberships, attributes) | Confidential | People's privacy; DPDP Act duties |
| Audit log and archive | Confidential | Evidence; must be complete and unaltered |
| Partner tenancy data (roles, organisations) | Confidential | One application must never see another's |
| Availability of sign-in | — | Hospitals and clinics cannot work without it |

## 2. Who might attack

| Actor | Can | Wants |
|---|---|---|
| Anyone on the internet | Reach the five public hosts | Accounts, data, a foothold |
| A signed-in person | Their own account | Someone else's account or data, more than their share of codes |
| A partner application or its administrator | Its own client credentials, its console | Another application's data, people who never linked it |
| A Sangam operator | The operator console at their rank | More than their rank allows, without a trace |
| Root on the VM, or the hosting provider | Files, memory, disk images | Keys and everything else |
| Someone with a backup or a disk copy | Data at rest | Personal data, credentials |

## 3. Threats and controls, by boundary

### TB1–TB2: internet, Caddy and the hosts

| STRIDE | Threat | Controls | Left over |
|---|---|---|---|
| S | Phishing site imitating sign-in | HSTS, the front page warns, passkeys bound to `id.sangamid.in` | People can still be fooled into typing a password elsewhere |
| T | Request smuggling, host-header tricks | Caddy in front; `AllowedHosts` per host; forwarded headers only from Caddy's network | — |
| I | Version or stack disclosure | `Server` stripped; no version headers; Development pages not compiled into the image (rc.8) | — |
| D | Flooding sign-in, token or API endpoints | Rate limits per endpoint, and per person on the portal and consoles (rc.9); CAPTCHA-free bot checks, request metrics with alerts | A large distributed attack would exhaust the single VM |
| E | Unexpected HTTP methods or parameter pollution reaching handlers | Only the methods Sangam uses are answered, every 405 logged; a repeated OAuth parameter is refused (rc.8) | — |

### TB3: a person and their account

| STRIDE | Threat | Controls | Left over |
|---|---|---|---|
| S | Credential stuffing, password guessing | Argon2id with pepper, breached-password check, lockout, notifications; authenticator or passkey optional for ordinary people | Single-factor sign-in for ordinary users — **accepted (decision 1)**; a sign-in from the last 15 minutes is required before sensitive changes (rc.9), and a sign-in from a new browser is e-mailed to the person (rc.9) |
| S | Account takeover by password reset | Reset by e-mailed code; an enrolled authenticator is still asked for after a reset | Whoever controls the mailbox can reset — **accepted (decision 3)** |
| S | Replay of an authenticator code | A code works once; a reuse is recorded as a replay and the person told (rc.8) | — |
| S | Guessing recovery codes | 120-bit codes, keyed hashes with the pepper (rc.8), one use each, lockout | — |
| T | Changing someone else's profile | Every action scoped to the signed-in account in the service | — |
| R | "I never did that" | Hash-chained audit log; every sensitive action and its notification e-mail | — |
| I | Account enumeration | Same answer whether or not an account exists (D-L) | Timing differences not measured under load |
| E | Merging into someone else's account | Signed in to both accounts within 15 minutes, a waiting period, e-mails to both with a one-click cancel (rc.7) | — |

### TB4: a partner application

| STRIDE | Threat | Controls | Left over |
|---|---|---|---|
| S | A stolen client secret | Secrets hashed in the database, rotatable, client authentication failures audited | A stolen secret works until rotated |
| T | Altering another application's roles or members | Application id taken from the token, never the URL; every query scoped | — |
| I | Reading people who never linked the application | Consent first; searches and lists only over the application's own grants; reads of people audited (rc.8) | — |
| I | Correlating one person across applications | Pairwise subject ids for SAML; OIDC pairwise ids planned (after rc.9) | OIDC subject ids are the same across applications today |
| D | Webhook or SCIM endpoint used to make Sangam attack a private network | `OutboundHttp`: https only, no redirects, private addresses refused after DNS | — |
| E | Using the management API beyond `sangam.manage` | Policy on the whole API group; refusals audited; bodies bound to strict contracts, unknown fields refused and audited (rc.9) | — |

### TB5–TB6: partner administrators and operators

| STRIDE | Threat | Controls | Left over |
|---|---|---|---|
| S | Console access with a stolen password | Authenticator required for every console user; the operator console is not on the internet — only through an SSH tunnel with a key (OI-059) | Partner console stays public by design |
| E | Acting above one's rank | Rank or owner check inside each service method; every refusal audited as `access.denied` with the operation and the rank it needed (rc.8); many refusals, or one caller refused again and again, alert the founder (rc.9) | — |
| R | Operator browsing records unnoticed | Every record opened, export and recovery review audited; the person can see operator views in their activity | — |
| E | A single operator approving their own risky action | Cooling-off and review for privileged recoveries | A second operator's approval is not yet required (planned) |

### TB7: the database

| STRIDE | Threat | Controls | Left over |
|---|---|---|---|
| T | SQL injection | EF Core, parameterised raw SQL only | — |
| T | Rewriting the audit trail | Application role cannot alter `audit_events`; hash chain verified; archive files encrypted | The owner role can; it is used only for migrations |
| I | Data read in transit inside the VM | Private Docker network | Plain TCP inside the one VM — **accepted (decision 2)**; TLS when the VMs split |

### TB8: outside services

| STRIDE | Threat | Controls | Left over |
|---|---|---|---|
| S | A forged or revoked server certificate (DigiLocker, Anjal, SIEM, partner endpoints) | Certificates validated, revocation checked online (rc.8); a revoked certificate refused | When the revocation answer cannot be had, the connection goes ahead (soft fail); outbound port 80 must be open for OCSP |
| I | Anjal reading codes in transit | Same VM today; codes short-lived | Anjal is trusted with message content |
| T | Partner-registered destination turned hostile | Partner's own data only; signed payloads | **Accepted (decision 9)**: partners register their own destinations; the firewall allows only outbound 443 plus mail and DNS to Anjal |
| E | A vulnerable third-party library | Dependency, image and CodeQL scans on every pull request; Dependabot; lock files and digest-pinned images (rc.9); every container read-only, without capabilities (rc.9) | Libraries run in-process with full rights — **accepted (decision 5)** |

### TB9: root on the VM, the host and backups

| STRIDE | Threat | Controls | Left over |
|---|---|---|---|
| I | Keys copied from the secret files | Root-only files, never in images or backups, offline copies; fingerprints checked at each start, a changed key stops the start and alerts (rc.8); SSH key-only, no root login | Root can read any key — **accepted (decision A6)** until imagiQa's vault exists; after a copy, every key-compromise step has a command (rc.9) |
| I | Secrets left in process memory | — | .NET strings cannot be wiped — **accepted (decision 5)** |
| I | Stolen backup | `age`-encrypted before leaving the VM; the private key offline | — |
| D | Loss of the VM | Nightly off-site backups in another region, restore drills | Hours of downtime while a new VM is built |
| S | Unattested synced passkeys | Passkeys accepted without attestation | **Accepted (decision 5)**: synced passkeys cannot be attested |

## 4. Abuse cases tested

The tests named in `docs/security/asvs-l2.md` for V1.1.2-related rows, and in particular: cross-application access
(`PartnerServiceTests`, `ManagementApiTests`), operator rank refusals (`AdminServiceTests`), merge abuse
(`AccountMergeTests`), replay (`MfaAndBootstrapTests`), recovery-code guessing (`RecoveryCodeTests`), parameter
pollution and method refusal (`RequestHardeningTests`), outbound to private networks (`ScimProvisioningTests`), key swap
(`KeyFingerprintCheckTests`).

## 5. Decisions this model relies on

Decisions 1–9 of 11 October 2026 ([decision memo](decision-memo-rc8.md)) and A6 (SGM-908).

## 6. Open items

| Item | When |
|---|---|
| OIDC pairwise subject ids | After rc.9 |
| Second operator's approval for privileged actions | Planned |
| imagiQa vault (keys out of files) | Vault project |
| External penetration test | Before public launch |

## 7. Review record

| Release | Date | Reviewed by | Changes |
|---|---|---|---|
| 1.0.0-rc.8 | 11 Oct 2026 | Claude, under the direction of the founder | First version in the repository (SGM-203 summarised and brought up to date); rc.8 controls added |
| 1.0.0-rc.9 | 11 Oct 2026 | Claude, under the direction of the founder | Reviewed; rc.9 controls added (fresh sign-in, new-device e-mail, strict API contracts, host rate limits, hardened containers, key-compromise commands); no new threat found |
| After rc.9 | 12 Oct 2026 | Claude, under the direction of the founder | OI-058 (paths unchanged at the edge) and OI-059 (operator console off the internet, SSH tunnel) |
