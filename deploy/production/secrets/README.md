# Production secrets

Each file here becomes one setting inside the containers (file name = key, `__` for `:`). Real
secret files are never committed: `.gitignore` excludes everything here except this README.

| File | Contents |
|---|---|
| `postgres_password` | Password of the PostgreSQL superuser (database stack only) |
| `ConnectionStrings__Sangam` | Connection string of the **application** role — not the table owner |
| `Sangam__Portal__ClientSecret`, `Sangam__Admin__ClientSecret`, `Sangam__Partner__ClientSecret` | Fresh client secrets, 32+ characters. The identity server registers the three clients with these same files (R4), so they always match. |
| `Sangam__Anjal__ApiKey` | Sangam's API key on Anjal, for sending e-mail and SMS (D-B, D-M). Every host needs it. |
| `Sangam__PasswordHashing__Pepper` | rc.5 (ASVS V2.4.5): the password pepper, `openssl rand -base64 32`. Every host needs it and refuses to start without it. One offline copy with the founder; never in a backup. |
| `signing_current.pfx`, `encryption_current.pfx` | Token certificates (SGM-803) |
| `Sangam__Certificates__Signing__0__Password`, `Sangam__Certificates__Encryption__0__Password` | Their passwords |
| `keyring_current.pfx`, `Sangam__DataProtection__Certificates__0__Password` | Certificate that encrypts the data-protection key ring, and its password (V-01). Every host needs it. |
| `Imagiqa__ClientSecret` | D-I: the demo's client secret (32+ random characters). The identity server registers the demo with it; the demo signs in with it. |
| `ConnectionStrings__Imagiqa` | D-I: the demo's own database, `imagiqa_demo`, as its own role (`imagiqa_demo`) — never Sangam's. |
| `audit_archive.crt` | D-A: the **public** certificate the audit archive is encrypted for. Made once on the founder's own computer with `audit-archive keygen`; the private key (`audit-archive.key.pem`) never comes to the server. Identity server only. |
| `saml_signing_current.pfx`, `Sangam__Saml__Certificates__0__Password`, `Sangam__Saml__PairwiseKey` | PR-22, only when SAML is switched on (`docker-compose.saml.yml`): a SAML-only signing certificate and its password, and the pairwise key (48+ random characters, never changed) behind each service provider's private NameID. Identity server only. |

Files ending in `.pfx` or `.crt`, and files named `ignore.*`, are not read as settings.
