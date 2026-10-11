# Cached data and request parameters: review

*rc.8 (ASVS V8.1.2, V8.3.1, V8.1.1). A review of where Sangam keeps copies of personal data outside the database, and of
personal or secret data sent in URLs. Repeat at each release when a cache, a temporary file or a new URL parameter is
added.*

## 1. Copies of personal data outside the database

| Where | What | How long | Protection | Verdict |
|---|---|---|---|---|
| Browser and proxy caches | Pages and JSON | Not kept | `Cache-Control: no-store` on every page and JSON answer unless the endpoint sets its own (static assets, logos) | Acceptable |
| Blazor Server circuits (portal, consoles) | The screen's data, including an export being prepared | While the browser tab is open; the circuit ends when it closes or after the disconnect timeout | Memory of the host process only; never written to disk | Acceptable |
| ASP.NET Core in-memory caches | Settings, templates, branding, OpenIddict's application and scope lookups | Minutes | Process memory; no personal data except application names | Acceptable |
| Data-protection key ring | Keys, encrypted with the key-ring certificate | Until rotated | PostgreSQL; certificate as a secret file | Acceptable |
| Container console logs | Operational log lines: masked e-mail and mobile, request paths, IP addresses in refusals | One year (`local` driver, then backups) | Root-only on the VM; `age`-encrypted off-site | Acceptable; paths must not carry personal data (section 2) |
| `/srv/sangam/status` | Backup and drill results | Overwritten each run | No personal data | Acceptable |
| `/srv/sangam/audit-archive` | Audit events older than a year, IPs shortened | 7 years | Encrypted for the founder's offline key | Acceptable |
| Temporary files | None written by Sangam (evidence packs and exports are built in memory) | — | — | Acceptable |
| In-memory e-mail and SMS outboxes | Captured messages | Development and Testing only; refused in Production | — | Acceptable |
| Anjal's queue | Messages waiting to be sent | Anjal's retention | Anjal project | Anjal to confirm its own queue retention |

## 2. Personal or secret data in URLs

URLs end up in browser history, Caddy's and the hosts' logs, and `Referer` headers. Sangam sets
`Referrer-Policy: strict-origin-when-cross-origin`, so a full URL is never sent to another site.

| Parameter | Where | Sensitivity | Why in the URL | Mitigation | Verdict |
|---|---|---|---|---|---|
| `code` (authorization code) | Redirect to the application | Secret, short-lived | OAuth 2.0 requires it | Single use, minutes long, PKCE-bound | Accepted (protocol) |
| `state`, `nonce`, `code_challenge` | `/connect/authorize` | Not secret | Protocol | — | Fine |
| `login_hint` (an e-mail) | `/connect/authorize` from an application | Personal | Protocol (OIDC) | Optional; sent only by an application that chooses to | Accepted |
| `id_token_hint` | `/connect/endsession` | Contains personal claims | Protocol (OIDC) | The ID token is signed, short-lived; Sangam's own sign-out confirmation posts it | Accepted |
| Invitation token | Link in an e-mail | Secret, single use | The link is the invitation | Hashed in the database; expires; single use | Accepted |
| Merge-cancel token | Link in an e-mail (rc.7) | Secret, single use | The link cancels | Hashed; opening the link changes nothing until the button is pressed | Accepted |
| E-mail verification | Code typed on the page, not in the link | — | — | — | Fine |
| Password reset | Code typed on the page | — | — | — | Fine |
| Passwords, one-time codes, recovery codes, profile fields | POST bodies only | — | — | — | Fine |
| Search words in the consoles | Blazor messages over the circuit, not the URL | Personal | — | Not logged or audited (rc.8) | Fine |
| Management API ids (`/users/{id}`) | URL path | Pseudonymous id | REST | The id is not personal data by itself; scoped to the calling application | Fine |

## 3. Outcome

No change in code was needed beyond rc.8's: search words are kept out of the audit log. ASVS V8.1.2 is Met with this
review; V8.3.1 stays Partly only for the protocol parameters OAuth and OpenID Connect require in URLs.
