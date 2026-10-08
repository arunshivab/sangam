# Production secrets

Each file here becomes one setting inside the containers (file name = key, `__` for `:`). Real
secret files are never committed: `.gitignore` excludes everything here except this README.

| File | Contents |
|---|---|
| `postgres_password` | Password of the PostgreSQL superuser (database stack only) |
| `ConnectionStrings__Sangam` | Connection string of the **application** role — not the table owner |
| `Sangam__Portal__ClientSecret`, `Sangam__Admin__ClientSecret`, `Sangam__Partner__ClientSecret` | Fresh client secrets, matching the registrations |
| `Sangam__Email__Smtp__Username`, `Sangam__Email__Smtp__Password` | Anjal submission account (OI-027) |
| `signing_current.pfx`, `encryption_current.pfx` | Token certificates (SGM-803) |
| `Sangam__Certificates__Signing__0__Password`, `Sangam__Certificates__Encryption__0__Password` | Their passwords |
| `keyring_current.pfx`, `Sangam__DataProtection__Certificates__0__Password` | Certificate that encrypts the data-protection key ring, and its password (V-01). Every host needs it. |

Files ending in `.pfx`, and files named `ignore.*`, are not read as settings.
