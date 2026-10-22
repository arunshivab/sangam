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
| T | Leaving an organisation to strip it of its only administrator, or someone leaving on another's behalf | "Leave organisation" (rc.10) needs a sign-in from the last 15 minutes; the last organisation administrator of an application cannot leave that role; it goes through the same revoke path as the management API, audited as the person; the person and every organisation administrator of each affected application are e-mailed | Roles held at a parent organisation are not touched |
| I | Learning who has an account, or how it is protected, from the sign-in screens | rc.11: the screen after "who" is the same for every email, mobile number or login ID (it depends only on the application's rule); an unknown name gets the wrong-password answer in the same time, with the same lockout; a mobile number signs in only when verified on one account | — |
| I | A shared ward computer showing who uses it | rc.11: the device's list is opt-in, at most six people, 30 days, first name and masked email only, removable by each person; it lives only in the browser | Colleagues on the same computer see each other's first names, by each person's choice |
| S | Taking over a login ID colleagues are used to typing | rc.11: a given-up login ID is held for its owner for 90 days; Sangam's own words are refused; a login ID only names an account, the password or passkey is still asked | — |
| S | Using an application's client secret that leaked or belongs to someone who left | rc.11: the owner makes a new secret and revokes the old one with no break; owner-made secrets end after 12 months on their own; at most two are live; making and revoking need an owner's two-step console session and are audited without the secret; secrets are stored as OpenIddict hashes and shown once | The secret issued from configuration has no end date until the owner revokes it |
| E | Roles outliving the job (a locum who left, a role nobody uses) | rc.11: access reviews list roles ending within 30 days and roles unused for 90 days; each is kept, extended or ended with a reason, recorded and audited | A review is only as good as the administrator doing it; nothing ends a role automatically |
| D | One operator signing many people out at once | rc.11: support and owners only, at most 100 people, never themselves, a reason required, each person audited on their own | A careless bulk sign-out is inconvenient (everyone signs in again), not destructive |
| S | Someone else unlocking a nurse's session on a shared ward tablet | rc.12 (T1): only on an enrolled device (admin-enrolled from the device; key hashed), only within 2 hours of the lock and 12 hours of a full sign-in (any organisation may shorten both for its own devices, rc.13; never longer than above), only on the device where the person last signed in fully; 5 wrong PINs cancel the PIN until the next full sign-in; PINs hashed like passwords, never all-same or a run; "Someone else" leaves the locked session locked; a removed device ends its sessions at once | A colleague who watches a PIN being typed has five tries within the window |
| I | The tablet showing who works on the ward | rc.12 (T1): only on an enrolled device, only the last six people who signed in fully there and still work at that hospital, name and role only | Colleagues see each other's names and roles on the ward tablet, as they would on a duty roster |
| E | Emergency access ("break-glass") used to browse records | rc.12 (M12): only the hospital's staff, only where its admin switched it on, a reason, a fresh passkey or authenticator check, 60 minutes at most, three a day; the reviewers are e-mailed at once and judge each use (green under 24 h, orange to 72 h, red after); Sangam keeps only the application's case number, never patient details | What is opened under it is the application's to record and limit |

### TB4: a partner application

| STRIDE | Threat | Controls | Left over |
|---|---|---|---|
| S | A stolen client secret | Secrets hashed in the database, rotatable, client authentication failures audited; rc.15: an application may sign in with a key instead (`private_key_jwt`), its private key never leaving its servers, each assertion for Sangam's issuer, at most five minutes long and accepted once; owners are e-mailed when a secret is a year old or ends within 30 days | A stolen secret works until rotated; a stolen private key until its owner removes the key |
| T | Altering another application's roles or members | Application id taken from the token, never the URL; every query scoped | — |
| I | Reading people who never linked the application | Consent first; searches and lists only over the application's own grants; reads of people audited (rc.8) | — |
| I | Correlating one person across applications | Pairwise subject ids for SAML (NameID) and, since rc.14, for every OIDC application (ADR-0017): tokens, userinfo, the management API, webhooks, SCIM, logout and signature tokens and evidence packs carry the application's own random id; an id from elsewhere is answered not found; no translation between applications | Applications given the e-mail or mobile can still match people by it (accepted, founder, 20 Oct 2026) |
| D | Webhook or SCIM endpoint used to make Sangam attack a private network | `OutboundHttp`: https only, no redirects, private addresses refused after DNS | — |
| E | Using the management API beyond `sangam.manage` | Policy on the whole API group; refusals audited; bodies bound to strict contracts, unknown fields refused and audited (rc.9) | — |

### TB5–TB6: partner administrators and operators

| STRIDE | Threat | Controls | Left over |
|---|---|---|---|
| S | Console access with a stolen password | Authenticator required for every console user, and the session itself must have been signed in with two-step (`acr` level 2 or more, rc.10); the operator console is not on the internet — only through an SSH tunnel with a key (OI-059) | Partner console stays public by design |
| E | Acting above one's rank | Rank or owner check inside each service method; every refusal audited as `access.denied` with the operation and the rank it needed (rc.8); many refusals, or one caller refused again and again, alert the founder (rc.9) | — |
| R | Operator browsing records unnoticed | Every record opened, export and recovery review audited; the person can see operator views in their activity | — |
| E | A single operator approving their own risky action | Cooling-off and review for privileged recoveries | A second operator's approval is not yet required (planned) |
| S | An admin adding the wrong person, or an impostor, as staff | rc.12: adding needs an admin verified with DigiLocker (or the application's dated exception, each addition marked); the check says only which fields match an existing account (5 a person per admin a day, logged); the person joins with an activation code handed over in person or a join code from their own portal; an existing account whose name or date of birth differs stops and tells the admin | An admin who hands the code to the wrong person in person |
| R | An application changing who works where with nobody accountable | rc.13: `PUT` and `DELETE /orgs/{id}/members/{user}` name the admin who asked (their own access token, issued to that application); Sangam checks their right on the organisation's path, records them as `granted_by` and in the audit entry, and a new place needs them verified with DigiLocker; rc.14: `PUT /users/{id}/attributes` names the admin too, who must administer the application or an organisation where the person works | An application that misuses a signed-in admin's token, as it could misuse any of its own sessions |
| T | A workflow with write access running code from a pull request | After rc.13 (OI-060): the Dependabot lock-file workflow runs only for pull requests Dependabot opened and pushed, from this repository's `dependabot/nuget/` and `dependabot/docker/` branches; it runs `dotnet restore` only (no build or tests), reads the SDK version from the image's configuration without running the image, moves `global.json` only forward within its feature band, refuses to push anything but lock files (and `global.json` on Docker branches), and its deploy key writes to this repository only | Restore evaluates the branch's project files; the branch is Dependabot's, and only people with write access could change it |
| E | Someone giving themselves a place at an organisation | rc.13: only where one of the application's administrators switched on "Testers may join themselves" for that one organisation (for a made-up demo hospital; audited); the person's own access token, issued to that application, names them; it never reaches the organisations under it, and a DigiLocker-only organisation still refuses the unverified | An administrator switching it on for a real hospital (go-live checklist checks it) |
| S | Someone unverified joining a hospital that asked for verified staff | rc.13: an organisation may take only people verified with DigiLocker (it holds for the organisations under it); an addition for anyone else waits and asks them to verify, joining is refused until they have, and every other way of giving a new place refuses too; rc.14: an account merge gives a place there only to a verified survivor (the DigiLocker identity moves first) | People already there keep their place when it is switched on |
| E | A customer or branch admin reaching beyond their hospital | rc.12: admin rights are per organisation and cover only what is beneath it; every console list, change and device action checks the right on the organisation's path; a right ends with the person's last place in its tree; application admins alone close a customer | — |

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
| Second operator's approval for privileged actions | Planned |
| imagiQa vault (keys out of files) | Vault project |
| External penetration test | Before public launch |

## 7. Review record

| Release | Date | Reviewed by | Changes |
|---|---|---|---|
| 1.0.0-rc.8 | 11 Oct 2026 | Claude, under the direction of the founder | First version in the repository (SGM-203 summarised and brought up to date); rc.8 controls added |
| 1.0.0-rc.9 | 11 Oct 2026 | Claude, under the direction of the founder | Reviewed; rc.9 controls added (fresh sign-in, new-device e-mail, strict API contracts, host rate limits, hardened containers, key-compromise commands); no new threat found |
| After rc.9 | 12 Oct 2026 | Claude, under the direction of the founder | OI-058 (paths unchanged at the edge) and OI-059 (operator console off the internet, SSH tunnel) |
| 1.0.0-rc.12 | 14 Oct 2026 | Claude, under the direction of the founder | Hospitals as customers with their own admins; adding staff safely (DigiLocker-verified admins, the check, activation and join codes); "Working at which branch?"; shared ward devices with PIN unlock; emergency access with review |
| 1.0.0-rc.13 | 14 Oct 2026 | Claude, under the direction of the founder | The management API names the admin for every change to a person's place; organisations that take only DigiLocker-verified people; device rules per organisation; the branch and emergency access in device-flow tokens and userinfo |
| 1.0.0-rc.15 | 22 Oct 2026 | Claude, under the direction of the founder | Applications sign in with a key instead of a secret (`private_key_jwt`, ADR-0018), with secret reminders; access tokens 15 minutes; SAML key transport RSA-OAEP-SHA256 per service provider and every inbound SAML message checked against the SAML schema; the founder accepted e-mailed-code sign-in for members and synced passkeys without attestation |
| 1.0.0-rc.14 | 20 Oct 2026 | Claude, under the direction of the founder | Pairwise subject ids for every application (ADR-0017), closing the correlation threat for OIDC; the acting admin named for setting a person's values; an account merge keeps a place at a DigiLocker-only organisation from an unverified survivor |
| 1.0.0-rc.10 | 12 Oct 2026 | Claude, under the direction of the founder | Consoles require a two-step session; "Leave organisation" reviewed (fresh sign-in, the last organisation administrator of an application cannot leave that role, every organisation administrator told, audited as the person); client-secret age shown; no new threat found |
