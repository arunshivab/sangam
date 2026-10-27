# Production secrets

Each file here becomes one setting inside the containers (file name = key, `__` for `:`). Real
secret files are never committed: `.gitignore` excludes everything here except this README.

| File | Contents |
|---|---|
| `postgres_password` | Password of the PostgreSQL administrator, `sangam_dba` (rc.16; not the well-known `postgres`). Database stack only |
| `ConnectionStrings__Sangam__identity` | rc.16 (ASVS V1.2.2): the identity server's connection string, as its own role `sangam_identity_app` (`postgres/roles.sql`): `Host=postgres;Port=5432;Database=sangam_identity;Username=sangam_identity_app;Password=<its password>;Maximum Pool Size=50` |
| `ConnectionStrings__Sangam__portal` | The same for the account portal, as `sangam_portal_app`, with `Maximum Pool Size=25` |
| `ConnectionStrings__Sangam__admin` | The same for the operator console, as `sangam_admin_app`, with `Maximum Pool Size=10` |
| `ConnectionStrings__Sangam__partner` | The same for the partner console, as `sangam_partner_app`, with `Maximum Pool Size=25` |
| `Sangam__Portal__ClientSecret`, `Sangam__Admin__ClientSecret`, `Sangam__Partner__ClientSecret` | Fresh client secrets, 32+ characters. The identity server registers the three clients with these same files (R4), so they always match. |
| `Sangam__Anjal__ApiKey` | Sangam's API key on Anjal, for sending e-mail and SMS (D-B, D-M). Every host needs it. |
| `Sangam__PasswordHashing__Pepper` | rc.5 (ASVS V2.4.5): the password pepper, `openssl rand -base64 32`. Every host needs it and refuses to start without it. One offline copy with the founder; never in a backup. |
| `signing_current.pfx`, `encryption_current.pfx` | Token certificates (SGM-803) |
| `Sangam__Certificates__Signing__0__Password`, `Sangam__Certificates__Encryption__0__Password` | Their passwords |
| `keyring_current.pfx`, `Sangam__DataProtection__Certificates__0__Password` | Certificate that encrypts the data-protection key ring, and its password (V-01). Every host needs it. |
| `Imagiqa__ClientSecret` | D-I: the demo's client secret (32+ random characters). The identity server registers the demo with it; the demo signs in with it. |
| `ConnectionStrings__Imagiqa` | D-I: the demo's own database, `imagiqa_demo`, as its own role (`imagiqa_demo`) — never Sangam's. |
| `audit_archive.crt` | D-A: the **public** certificate the audit archive is encrypted for. Made once on the founder's own computer with `audit-archive keygen`; the private key (`audit-archive.key.pem`) never comes to the server. Identity server only. |
| `saml_signing_current.pfx`, `Sangam__Saml__Certificates__0__Password` | PR-22, only when SAML is switched on (`docker-compose.saml.yml`): a SAML-only signing certificate and its password. Identity server only. (rc.14: the pairwise key is gone; NameIDs are the applications' own ids, ADR-0017.) |
| `Sangam__DigiLocker__ClientId`, `Sangam__DigiLocker__ClientSecret`, `Sangam__DigiLocker__SubjectKey` | PR-26, required from rc.6 (`docker-compose.digilocker.yml`): the client id and secret DigiLocker issues at onboarding, and the subject key (32+ random characters, `openssl rand -base64 48`, never changed) DigiLocker ids are hashed with. The portal and the identity server share the same three files. One offline copy of the subject key with the founder. |
| `log-receiver-ca.pem` | rc.16 (ASVS V1.7.2), only with `docker-compose.logs.yml`: the CA certificate of the log receiver. Read by Docker on the host for the syslog-over-TLS logging driver and mounted into no container; not a secret, but kept here with the rest of the deployment's files. |

Each pool stays below its role's connection limit in `roles.sql` (60, 30, 15 and 30), leaving room for the connections a
host opens outside its pool (the single-instance and cluster locks).

Files ending in `.pfx` or `.crt`, and files named `ignore.*`, are not read as settings.

rc.16: the single `ConnectionStrings__Sangam` file is gone; each host has its own (above), mounted inside its container
under the old name. The migrator runs with the owner's connection string (`sangam_owner`), which `scripts/deploy.sh`
reads from `/etc/sangam/owner-connection` (root, mode 600), outside this folder; no host has it.

rc.16 (ASVS V2.10.3): this folder is the administrator's only. `scripts/deploy.sh` refuses to run unless it is owned by
root with mode `700`, which keeps every other user on the server out, and every file in it (this README aside) is owned
by root with mode `644` or `444`. The files must be readable, because Docker mounts them into the containers, whose
programs run as a non-root user; they are never writable by anyone but root:

    sudo chown -R root:root secrets && sudo chmod 700 secrets && sudo find secrets -type f ! -name README.md -exec chmod 644 {} +
