# Sangam production deployment (PR-10)

One E2E Networks VM in India (D-023), shared with Anjal. Three Docker Compose stacks, each separate
(OI-013): PostgreSQL (`postgres/`), Sangam (this folder), and Anjal (its own repository). Caddy here
terminates TLS for the four Sangam hosts. Decisions still open are marked **FOUNDER**.

## 1. Prepare

1. DNS: `id`, `account`, `admin`, `partners` and `demo.sangamid.in` point at the VM.
2. Mail: `scripts/check-mail-dns.ps1 -Domain sangamid.in -DkimSelector <Anjal's selector>` passes. **FOUNDER (OI-027)**
3. Secrets: create every file listed in `secrets/README.md`.

## 2. Database

    docker compose -f postgres/docker-compose.yml up -d

Create two roles: the **owner** (runs migrations, owns the tables) and the **application** role (used by
the hosts; can read and write, cannot alter `audit_events`). The application role's connection string
goes in `secrets/ConnectionStrings__Sangam` (go-live checklist, "Added by R0").

D-I, the demo at demo.sangamid.in: a database and role of its own, which never touch Sangam's:

    CREATE ROLE imagiqa_demo LOGIN PASSWORD '<random>';
    CREATE DATABASE imagiqa_demo OWNER imagiqa_demo;

Its connection string goes in `secrets/ConnectionStrings__Imagiqa`; the demo creates its tables when it starts.

## 3. Migrations — once per release, as the owner

    docker build --target migrator -t sangam/migrator ../..
    docker run --rm --network sangam-db -e CONNECTION="<owner connection string>" sangam/migrator

The bundle applies only migrations not yet applied; running it again does nothing.

## 4. Start

    docker compose up -d --build
    curl -fsS https://id.sangamid.in/health/ready

The identity server registers the portal, both consoles and the demo from its settings each time it starts
(`Sangam__Clients__…` in `docker-compose.yml`; R4): exactly the production redirect addresses, and the same secret files
the hosts use. A missing or short secret, or a non-https address, refuses the start. To rotate a client secret, replace
its file and restart the identity server and that host.

The demo (D-I) shows "Demo, not for real patient data" on every page. Sign in with a Sangam account; with registration
by invitation only (pilot), invite testers first. To give someone a role at a demo hospital, use the partner console.

## 5. Operate

- **Backups (D-E):** `scripts/backup-db.sh` nightly from cron; `scripts/restore-drill.sh` monthly (SGM-603). Both
  write their result to `/srv/sangam/status` (`backup.json`, `restore-drill.json`), which the hosts read for the
  monitoring page. Each night: a `pg_dump`, the day's container logs, and any new audit-archive file. Everything that
  leaves the server is first encrypted with `age` for the founder's public key — the private key stays offline with
  the founder — then uploaded to E2E Object Storage **in another region**, under S3 Object Lock (compliance mode):
  daily copies 14 days, Sunday copies 8 weeks, 1st-of-month copies 12 months, logs one year, audit-archive files
  until their events are seven years old. Set up once:
  1. On the founder's own computer: `age-keygen -o sangam-backup-key.txt` (keep offline, two copies apart); put the
     `age1…` public key line in `/etc/sangam/backup-recipients.txt` on the server.
  2. In the E2E console, in the other region: a bucket **created with Object Lock enabled**, and an access key limited
     to it. Apply the lifecycle rules: `aws s3api put-bucket-lifecycle-configuration --endpoint-url <EOS endpoint>
     --bucket <bucket> --lifecycle-configuration file://backup/lifecycle.json`. **FOUNDER: confirm E2E Object Storage
     supports Object Lock in that region before go-live; if it does not, the second provider must.**
  3. `/etc/sangam/backup-targets/e2e-region2.env` (mode 600): `ENDPOINT`, `BUCKET`, `REGION`, `AWS_ACCESS_KEY_ID`,
     `AWS_SECRET_ACCESS_KEY`. The second provider (before hospital go-live) is a second file there.
  4. Install `age`, `awscli` and `openssl` on the VM.
  The off-site restore drill brings the founder's key for its duration:
  `AGE_IDENTITY=/root/drill-key.txt scripts/restore-drill.sh --offsite e2e-region2`, then delete the key file. The
  monitoring page alerts when a backup did not go off-site, failed, or is overdue.
- **Monitoring (D-H):** no third party. Every host records its metrics into the database; the operator console's
  *Monitoring* page shows them; the identity server (`Sangam__Monitoring__Evaluate=true`) alerts the founder by
  e-mail and SMS through Anjal. Anjal's server runs the outside watchdog against `/health/ready`; Sangam watches
  Anjal through `Sangam__Monitoring__AnjalHealthUrl`. When Anjal itself is down, Sangam cannot send through it: the
  page shows it, and Anjal's own watchdog is the alert path.
- **Logs:** the `local` logging driver, rotated (50 MB × 20 per container). They stay on the server and go into the
  encrypted backups, kept one year (D-H; backup job in R4; one year from rc.6, SGM-910: CERT-In asks 180 days, the DPDP Rules' safeguards a year).
- **Breached-password check (rc.5, D-J revised):** nothing to install. Every password set, and every password
  sign-in, is checked against the built-in list of the 10,000 most common passwords and then against Pwned Passwords
  by k-anonymity (only five characters of the password's SHA-1 leave the server; the full list, over 50 GB, is not
  kept). The server needs outbound HTTPS to `api.pwnedpasswords.com`. When it cannot reach it, the built-in list is
  the check and the monitoring page says since when; after an hour the founder is alerted.
- **Password pepper (rc.5, ASVS V2.4.5):** once, before the first start, make the pepper and keep one copy offline
  with the founder (never on the server's backup path):

      openssl rand -base64 32 > secrets/Sangam__PasswordHashing__Pepper

  Every host refuses to start without it. Losing it does not lose any account — people sign in with an e-mailed
  code or a passkey and set a new password — but every password stops working, so keep the offline copy safe. To
  replace it: give the old value as `Sangam__PasswordHashing__PreviousPeppers__1`, the new one as the pepper and
  `Sangam__PasswordHashing__PepperVersion: "2"`; hashes move to the new pepper as people sign in.
- **Virus scanner (rc.5, ASVS V12.4.2):** uploaded logos are scanned by Anjal's ClamAV. Once:
  `docker network create anjal-clamav`, attach Anjal's ClamAV container to it (Anjal project), and set
  `Sangam__Antivirus__Host` to its name on that network. The partner and operator consoles refuse to start without a
  scanner; an upload is refused while the scanner does not answer, and the monitoring page shows it.
- **Audit archive (D-A):** once, on the founder's own computer (never on the server):

      docker run --rm -v "$PWD/keys:/keys" sangam/identity audit-archive keygen --out /keys --password '<long passphrase>'

  Copy `keys/audit-archive.crt` to `secrets/audit_archive.crt`; keep `keys/audit-archive.key.pem` and its passphrase
  offline (two copies, apart). Create `/srv/sangam/audit-archive` (owned by the container user). Every hour the identity
  server moves audit events older than a year into encrypted files there — IP addresses shortened, browser details
  dropped — and deletes files whose newest event is over seven years old; each step is itself an audit event, and the
  monitoring page shows the archive. The archive directory is inside the encrypted backups (D-E). To read a file:
  `audit-archive read --file <file> --key audit-archive.key.pem --password '<passphrase>' --out events.jsonl`.
- **PostgreSQL:** 18 everywhere (D-F).
- **SAML (PR-22):** off until it is needed. To switch it on, create a SAML-only signing key (RSA 3072, two years)
  and a pairwise key, then start with the SAML file as well:

      openssl req -x509 -newkey rsa:3072 -sha256 -days 730 -nodes -subj "/CN=Sangam SAML signing" \
        -keyout saml.key -out saml.crt
      openssl pkcs12 -export -inkey saml.key -in saml.crt -out secrets/saml_signing_current.pfx \
        -passout file:secrets/Sangam__Saml__Certificates__0__Password && shred -u saml.key
      openssl rand -base64 48 | tr -d '\n' > secrets/Sangam__Saml__PairwiseKey
      docker compose -f docker-compose.yml -f docker-compose.saml.yml up -d

  (Write a long random password into `secrets/Sangam__Saml__Certificates__0__Password` first.) Sangam's metadata is
  then at `https://id.sangamid.in/saml/metadata`; register each service provider on the operator console's
  *Applications → SAML service providers* page. Keep the pairwise key in the founder's offline copies: if it changes,
  every service provider sees every person as someone new. A missing or short pairwise key refuses the start.

- **DigiLocker (PR-26; required from rc.6, SGM-914):** since rc.6 a person who loses their authenticator and recovery
  codes recovers their account only with DigiLocker — there is no support-assisted recovery — and an application may
  require its people to be verified. So production starts with the DigiLocker file too. Once DigiLocker's onboarding
  (API Setu, MeitY) is done, register **both** return addresses with it:
  `https://account.sangamid.in/verify/digilocker/callback` and `https://id.sangamid.in/identity/digilocker/callback`.
  Then write the client id and secret it issued, and the subject key, and start with the file:

      openssl rand -base64 48 | tr -d '\n' > secrets/Sangam__DigiLocker__SubjectKey
      docker compose -f docker-compose.yml -f docker-compose.digilocker.yml up -d

  The portal and the identity server share the one subject key and refuse to start without the three secrets. Keep the
  subject key in the founder's offline copies and never change it: DigiLocker ids are hashed with it, so a new key lets
  a verified person's DigiLocker identity verify a second account and stops recovery recognising verified people.
  Recoveries whose DigiLocker record does not match the account wait on the operator console's *Recoveries* page.
- **Retention and inactivity (rc.6, SGM-910):** nothing to install. Once a day the identity server deletes expired
  codes and passkey challenges (after a day), finished invitations, e-mail changes and recoveries (after 30 days), the
  SMS log (after 180 days) and closed grievances (after 3 years). It ends a person's connection to an application that
  set an inactivity limit, 30 days after telling them, and deletes accounts that use no partner application and have
  not signed in for 3 years, 30 days after telling them (operators and application administrators excepted).

Not verified in Claude's environment: the Caddyfile under Caddy, Anjal's real API, and DigiLocker's real service. The
images build and the identity server starts in Production in Docker (release evidence); certificates, secret files,
health checks and the migration bundle against an empty database are covered by tests.
