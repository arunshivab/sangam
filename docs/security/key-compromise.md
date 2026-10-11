# Key compromise: replacing keys and re-encrypting data

*rc.8 (ASVS V1.6.3; SGM-911 compromise response; SGM-802 incident procedure). What to do, key by key, when a key or
secret may have been copied. SGM-911 sets the clock: respond within 24 hours, signing keys the same day. Open an
incident in the register first (SGM-802), and write every step taken into it.*

Steps marked **(manual today)** have no command yet; they are listed in section 4 as work to build.

## 1. First, for any key

1. Open the incident (SGM-802) and note the time.
2. Find out what else the attacker could have had: a key file alone, or the database too, or the whole VM. The answer
   decides the steps below. With the whole VM, treat every key as compromised.
3. Make the replacement keys on the founder's own computer, never on the server.
4. Replace the secret files, then start once with `Sangam__Keys__AcceptChanged=true`: the key-fingerprint check
   records the new fingerprints, writes `security.key.mismatch` (accepted) to the audit log and alerts. Remove the
   setting at once. A start without it refuses the changed keys — that is the check doing its job.
5. Update the offline copies (SGM-911): the encrypted USB at home, the bank locker, the break-glass copy.
6. Destroy the old key files (`shred -u`), except where a step below says to keep the old key for a time.

## 2. Key by key

| Key | What an attacker can do with it | Replace | Re-encrypt or re-issue what it protected |
|---|---|---|---|
| Token signing certificate | Mint ID and access tokens for anyone, to any application | New certificate as `Signing:0`. **Do not** keep the old one as `Signing:1` (that is for planned rotation only): it must leave the JWKS at once | Tokens signed with it stop verifying as soon as applications refresh the JWKS (they cache it; tell partners to refresh). Revoke all refresh tokens and authorizations **(manual today)**, so every application signs its people in again |
| Token encryption certificate | Read encrypted tokens and authorization codes in transit or in logs | New certificate as `Encryption:0`; drop the old one | Outstanding codes and tokens become unreadable to Sangam and fail; people sign in again |
| Key-ring certificate | With a copy of the database: decrypt the data-protection keys, and so authentication cookies and authenticator secrets (`dp1:`) | New certificate as `DataProtection:Certificates:0`; keep the old one as `:1` only until the step on the right is done | Revoke the data-protection keys and let a new one be made **(manual today)**; sign everyone out; re-protect every authenticator secret under the new key **(not built: until it is, every person with an authenticator is asked to set it up again)** |
| Password pepper | With a copy of the database: attack password hashes offline; test recovery-code guesses | New pepper, `PepperVersion` + 1 | **Pepper only:** keep the old one under `PreviousPeppers` — hashes move to the new pepper as people sign in; recovery codes keep working. **Pepper and database:** do not keep the old pepper; every password stops working, people set a new one with an e-mailed code; recovery codes made under the old pepper stop working, so tell people to make new ones |
| DigiLocker subject key | With a copy of the database: check whether a known DigiLocker id has a Sangam account | New key | The ids are kept only as hashes, so they cannot be re-hashed. Mark every verification as needing to be redone and ask verified people to verify again **(not built)**. Until then, do **not** change the key: a new key lets one identity verify a second account. Record the decision in the incident |
| SAML signing key | Forge assertions to every service provider | New key, new metadata | Tell each service provider to load the new metadata; until they do, SAML sign-in to them fails |
| SAML pairwise key | Link a person's pairwise ids across service providers | Changing it makes everyone new to every service provider | Decide per incident with the service providers; usually leave it and accept the linkability risk, recorded in the incident |
| Client secrets (portal, consoles, demo, partners) | Act as that client | New secret file; restart the identity server and that host | For a partner: rotate in the operator console and tell the partner |
| Database role passwords | Read and write everything the role can | `ALTER ROLE … PASSWORD …`; new secret file; restart | — |
| Anjal API key | Send mail as Sangam | New key in Anjal; new secret file | — |
| Vault token (when `Sangam:Secrets:Source=vault`) | Read Sangam's settings from the vault | Revoke it in the vault; new token file | Rotate every secret that path held |
| Backup key (`age`, offline) | Read every backup made for it | New key pair; new public key on the server | Old backups under Object Lock cannot be deleted before they expire (12 months at most): download, decrypt, re-encrypt for the new key and upload **(manual today)**; the old copies stay readable to the holder of the old key until they expire — record this |
| Audit-archive key (offline) | Read the archive files | `audit-archive keygen`; new certificate as `audit_archive.crt` | Read each file with the old key and write it again for the new one **(not built: only `keygen` and `read` exist)** |
| TLS certificates (Caddy) | Impersonate the hosts | Revoke at the CA; Caddy obtains new ones | — |

## 3. Afterwards

- Write to the people affected when the law requires it (DPDP Act; CERT-In within 6 hours for reportable incidents).
- Review the [threat model](threat-model.md) and SGM-908.
- Run the restore drill if backups or their keys were involved.

## 4. Not built yet (planned for rc.9)

| Gap | Today's fallback |
|---|---|
| A command to revoke all refresh tokens and authorizations | Tokens run out on their own (access tokens in minutes, refresh tokens at their lifetime); an operator can sign a person out from the operator console |
| A command to revoke the data-protection keys and re-protect authenticator secrets | People set up their authenticator again |
| A command to mark identity verifications for re-verification | Keep the DigiLocker subject key (see above) |
| `audit-archive reencrypt` | Archive files stay readable with the old key until re-encrypted by hand |

These are tracked with the rc.9 items; until they exist, this procedure is **Partly** met (ASVS V1.6.3).
