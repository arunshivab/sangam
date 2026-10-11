# Decision memo: ASVS Level 2 decisions 1–9 and risk acceptance

*Prepared for signature by the founder. Decided in conversation on 11 October 2026; recorded in 1.0.0-rc.8. When
signed, the signed copy is kept offline with the founder, and this file records the date in section 4. The decisions
feed SGM-908 (risk treatment), the [threat model](threat-model.md) and `docs/security/asvs-l2.md`.*

| Field | Detail |
|---|---|
| Owner | Dr. Arun Shiva Balasubramanian, founder and owner of Sangam |
| Prepared by | Claude (Anthropic), AI engineer, under the direction of the founder |
| Date decided | 11 October 2026 |
| Review | At the review before public launch, and whenever a decision's condition changes |

## 1. Decisions

| # | ASVS | Decision | Condition or follow-up |
|---|---|---|---|
| 1 | Level 2 multi-factor intent (V2 introduction), V3.7.1 | Ordinary users may sign in with one factor (password, e-mailed code, or passkey). Operators and application administrators must use an authenticator. Before a sensitive change (password, e-mail, mobile, authenticator, deletion, merge) a fresh sign-in is required | Fresh sign-in before sensitive changes is built in rc.9 (V3.7.1) |
| 2 | V9.2.2, V9.2.5 | Traffic between the containers inside the single VM (hosts ↔ PostgreSQL, hosts ↔ Anjal) is not encrypted | TLS is switched on when Sangam moves to its own VM or the database moves off the VM |
| 3 | V2.5.1–V2.5.6 | Password reset by a code e-mailed to the verified address is accepted | An enrolled authenticator is still asked for after a reset |
| 4 | V9.2.1, V9.2.3 | Anjal, Sangam's mail server, always sends outbound mail with STARTTLS and never in plain text; it insists on a valid certificate only for domains that publish MTA-STS or require it. Sangam calls Anjal's API over https, or plain http only to a private host | Confirm in the Anjal project that Anjal calls its SMS aggregator over HTTPS |
| 5 | Authenticator attestation (no exact ASVS 4.0.3 item), V8.3.6, V14.2.6 | Risk accepted, with the reasons recorded: (a) synced passkeys are accepted without attestation, because synced passkeys cannot be attested; (b) secrets held in .NET strings cannot be wiped from memory; (c) third-party libraries run in the same process with the same rights | (a) review if a regulator requires attested authenticators; (b) and (c) reduced by dependency scanning, CodeQL and the vault (keys out of the process) |
| 6 | V1.1.1, V1.1.7 | Every pull request names its release and the defects (DEF-nnn), policies (SGM-nnn) or ASVS requirements it answers, on a mandatory "Release:" line, checked by CI. GitHub access (collaborators, deploy keys, tokens, apps) is reviewed at each release | The `pr-description` check is added to the branch ruleset by the founder |
| 7 | V1.1.2 | The threat model is kept in the repository and updated at each release. An external penetration test is done before public launch | SGM-506 scope; tester chosen by the founder |
| 8 | V8.3.2, V8.3.3 | Claude drafts the privacy notice in English, Hindi and Malayalam; counsel approves it before go-live | `docs/legal/privacy-notice/` (draft); go-live checklist |
| 9 | V12.6.1, V14.1.x | Partners register their own outbound destinations (SCIM, webhooks, back-channel logout); this is accepted. The server firewall allows outbound only 443, plus mail and DNS to Anjal | `OutboundHttp` still refuses private networks and redirects. Note: online certificate-revocation checks (rc.8) need outbound 80 for OCSP and CRLs; without it they soft-fail (see [architecture](architecture.md) and the deployment guide) |

Related, decided the same day (vault brief, `sangam/vault-requirements-brief.md` in the project; A6 in SGM-908):

| # | Decision |
|---|---|
| A6 (continued) | Keys stay in files, with compensating controls, until imagiQa's own vault exists: the secret source layer (rc.8), the key-fingerprint check at start-up with an alert (rc.8), SSH hardening, and the offline copies. The vault is a separate imagiQa application, hosted for now on the E2E VM shared with Anjal, serving every imagiQa application, using only approved .NET cryptography, with offline backup keys kept |
| Hosting | E2E Networks is final. Sangam shares the VM with Anjal for now and moves to its own E2E VM when load requires |

## 2. Risk acceptance (decision 5, expanded)

| Risk | Likelihood | Impact | Why accepted | Compensating controls | Review |
|---|---|---|---|---|---|
| Synced passkeys are not attested: a passkey could live on a device or in a manager Sangam does not know | Low | Medium | Synced passkeys (iCloud, Google, Windows) are what people have; attestation would exclude them | Passkeys are one sign-in method among several; operators and administrators still need an authenticator; new passkeys are notified by e-mail | Public launch |
| Secrets in managed memory cannot be wiped | Low | High (needs root or a memory dump) | A platform limit of .NET | Root-only VM access, SSH hardening; the vault will keep signing keys out of the process | Vault go-live |
| Third-party libraries run in-process | Low | High | Every .NET web application works this way | Few dependencies, all scanned on every pull request; CodeQL; Dependabot; SGM-304 licence and support record | Each release |

## 3. Consequences for ASVS

The rows of `docs/security/asvs-l2.md` that depend on these decisions say "accepted (decision n, 11 Oct 2026)".

## 4. Signature

| Role | Name | Signature | Date |
|---|---|---|---|
| Decided and approved by | Dr. Arun Shiva Balasubramanian, founder | | |
