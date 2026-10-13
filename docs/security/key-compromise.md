# Key compromise: replacing keys and re-encrypting data

*rc.8, commands added in rc.9 (ASVS V1.6.3; SGM-911 compromise response; SGM-802 incident procedure). What to do, key by
key, when a key or secret may have been copied. SGM-911 sets the clock: respond within 24 hours, signing keys the same
day. Open an incident in the register first (SGM-802), and write every step taken into it.*

The commands below run through the identity server's own image on the server, with the running host's settings and
secrets, and exit without serving (section 4). Each needs the incident's reference, writes it to the audit log, and is
safe to run again.

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
| Token signing certificate | Mint ID and access tokens for anyone, to any application | New certificate as `Signing:0`. **Do not** keep the old one as `Signing:1` (that is for planned rotation only): it must leave the JWKS at once | Tokens signed with it stop verifying as soon as applications refresh the JWKS (they cache it; tell partners to refresh). Run `key-compromise revoke-tokens --incident <ref>`: every token and authorization is revoked, so every application signs its people in again |
| Token encryption certificate | Read encrypted tokens and authorization codes in transit or in logs | New certificate as `Encryption:0`; drop the old one | Outstanding codes and tokens become unreadable to Sangam and fail; people sign in again |
| Key-ring certificate | With a copy of the database: decrypt the data-protection keys, and so authentication cookies and authenticator secrets (`dp1:`) | New certificate as `DataProtection:Certificates:0`; keep the old one as `:1` only until the step on the right is done | Run `key-compromise revoke-key-ring --incident <ref>` (with the new certificate as `:0` and the old as `:1`): every data-protection key is revoked and a new one made, every authenticator secret is protected again under it (people keep their authenticators), and everyone is signed out (sessions ended with back-channel logout, security stamps changed). Restart every Sangam host, then remove the old certificate |
| Password pepper | With a copy of the database: attack password hashes offline; test recovery-code guesses | New pepper, `PepperVersion` + 1 | **Pepper only:** keep the old one under `PreviousPeppers` — hashes move to the new pepper as people sign in; recovery codes keep working. **Pepper and database:** do not keep the old pepper; every password stops working, people set a new one with an e-mailed code; recovery codes made under the old pepper stop working, so tell people to make new ones |
| DigiLocker subject key | With a copy of the database: check whether a known DigiLocker id has a Sangam account | New key as `SubjectKey`, the old one added to `PreviousSubjectKeys` | The ids are kept only as hashes, so they cannot be re-hashed. Run `key-compromise reverify --incident <ref>`: everyone verified is asked, on their profile page, to verify once more, and their hash moves to the new key when they do. Their verification stands meanwhile. Hashes under the previous key still count, so one identity still verifies one account. Remove the old key from `PreviousSubjectKeys` only when no verification uses it (or when the founder decides to remove the remaining verifications); record the decision in the incident |
| SAML signing key | Forge assertions to every service provider | New key, new metadata | Tell each service provider to load the new metadata; until they do, SAML sign-in to them fails |
| SAML pairwise key | Link a person's pairwise ids across service providers | Changing it makes everyone new to every service provider | Decide per incident with the service providers; usually leave it and accept the linkability risk, recorded in the incident |
| Client secrets (portal, consoles, demo, partners) | Act as that client | New secret file; restart the identity server and that host | For a partner: their owner makes a new secret and revokes the leaked one on the partner console (Credentials & redirects), no restart needed (rc.11); if the owner cannot be reached, disable the application in the operator console (Applications): from rc.11 a disabled application gets no tokens with any secret |
| Database role passwords | Read and write everything the role can | `ALTER ROLE … PASSWORD …`; new secret file; restart | — |
| Anjal API key | Send mail as Sangam | New key in Anjal; new secret file | — |
| Vault token (when `Sangam:Secrets:Source=vault`) | Read Sangam's settings from the vault | Revoke it in the vault; new token file | Rotate every secret that path held |
| Backup key (`age`, offline) | Read every backup made for it | New key pair; new public key on the server | Old backups under Object Lock cannot be deleted before they expire (12 months at most): download, decrypt, re-encrypt for the new key and upload (by hand, on the founder's own computer); the old copies stay readable to the holder of the old key until they expire — record this |
| Audit-archive key (offline) | Read the archive files | `audit-archive keygen`; new certificate as `audit_archive.crt` | `audit-archive reencrypt --dir <archive> --key <old key> --password <pass> --cert <new .crt> --out <new dir>`: each file read and checked with the old key and written again for the new one, into a new directory (the originals are left as they are). Check with `read` and the new key, upload the new files, then destroy the old key |
| TLS certificates (Caddy) | Impersonate the hosts | Revoke at the CA; Caddy obtains new ones | — |

## 3. Afterwards

- Write to the people affected when the law requires it (DPDP Act; CERT-In within 6 hours for reportable incidents).
- Review the [threat model](threat-model.md) and SGM-908.
- Run the restore drill if backups or their keys were involved.

## 4. The commands

Run on the server, in the `deploy/production` directory (with the same `-f` files the stack was started with), after the replacement keys are in place and the first start has accepted them
(section 1, step 4): the commands start the identity server's own checks first, and a changed key not yet accepted
stops them as it would stop the server.

```
docker compose run --rm identity key-compromise revoke-tokens --incident INC-2026-10-11-01
docker compose run --rm identity key-compromise revoke-key-ring --incident INC-2026-10-11-01
docker compose run --rm identity key-compromise reverify --incident INC-2026-10-11-01
docker compose run --rm identity audit-archive reencrypt --dir /archive --key /keys/old.key.pem --password '…' --cert /keys/new.crt --out /archive-new
```

| Command | Audit event | What it does |
|---|---|---|
| `revoke-tokens` | `security.tokens.revoke_all` | Every OpenIddict token (access, refresh, codes) and authorization set to revoked |
| `revoke-key-ring` | `security.keyring.revoke` | Data-protection keys revoked, a new key made; authenticator secrets read under the revoked key and protected under the new one; all sessions ended with back-channel logout; every security stamp changed |
| `reverify` | `security.identity.reverify` | `identity_reverify_requested_at` set for everyone verified; the portal asks them to verify again |
| `audit-archive reencrypt` | (offline tool) | Each archive file written again for the new archive certificate |

Each command can be run again: tokens already revoked stay revoked, a secret already under the new key is left alone, and
people already asked are not asked twice. The backup key's step (re-encrypting the backups for a new `age` key) stays
manual: it is done on the founder's own computer, never on the server.

With these, this procedure is **Met** (ASVS V1.6.3).
