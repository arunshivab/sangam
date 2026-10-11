# Input and output rules

*rc.8 (ASVS V1.5.1, with V5.1.x, V5.2.x, V5.3.x, V13.1.5, V13.2.5). How Sangam treats data coming in and going out, by
kind of data, content and law. Every new input or output follows these rules; SGM-913 checks C1–C4 hold a change to
them.*

## 1. General rules

1. **Validate on the server, in the service.** Screens may help, but the Infrastructure service refuses bad input
   itself, and a test shows it (V1.5.3).
2. **Allow-list, not deny-list.** A value with a fixed set of choices is picked from that set (an enum, a code list),
   never accepted as free text (SGM-913 C1).
3. **Every input has a limit.** Length for text, range for numbers, size for files, count for lists.
4. **Typed parsing only.** JSON through `System.Text.Json` into typed records; XML (SAML) through the hardened reader
   with DTDs off. No polymorphic deserialisers (V1.5.2).
5. **One value per parameter.** A protocol parameter sent twice to `/connect` is refused (rc.8, V5.1.1).
6. **The API speaks JSON only.** A body of another type gets 415; an `Accept` header that rules JSON out gets 406
   (rc.8, V13.1.5, V13.2.5).
7. **Only the methods in use.** Any other HTTP method gets 405, and every 405 is logged (rc.8, V14.5.1).
8. **Encode on output, where it is rendered.** Razor and Blazor encode; no markup is built from user data
   (SGM-913 C4). SQL is parameterised by EF Core (C2). An identifier placed in another query language is checked
   against a strict format first (C3).
9. **Fail closed.** Input that cannot be parsed is refused with a plain message; nothing partial is saved.

## 2. By kind of data

| Data | In: rule | Out: rule | Law or policy |
|---|---|---|---|
| Names | Required; at most 100 characters; trimmed | Encoded; shown to the person, linked applications in the `profile` scope, operators | DPDP Act: purpose limitation |
| E-mail address | RFC 5322 address of at most 254 characters; normalised for lookup; must be verified before it signs in | Masked in logs (`m***@example.in`); `email` scope only | DPDP Act |
| Mobile number | National number of at most 20 characters for the chosen country, stored as E.164; verified by code | Masked in logs; hashed in the SMS log; `phone` scope only | DPDP Act; TRAI DLT for texts |
| Date of birth | A real date; declared age 18 to 120 (`AgePolicy`) | `profile` scope | DPDP Act section 9 (children) |
| Gender | One of the fixed list | `profile` scope | — |
| Passwords | 12–128 characters (an application's policy may raise the minimum); breached-password check | Never output, never logged | SGM-910 Restricted |
| One-time and authenticator codes | Digits only after removing spaces and dashes; six digits | Never logged | Restricted |
| Recovery codes | Letters and digits; dashes, spaces and case ignored when a pepper is set | Shown once at issue; never logged | Restricted |
| Application attributes | Key: lower-case identifier; label: refused when it looks like health data; value: by the definition's type and length | Only to the application that defined it, and to the person | SGM-910 section 4: no health data |
| Organisation and role codes | Lower-case identifier, fixed length | Scoped to the calling application | — |
| Redirect and webhook addresses | Absolute https URL, no user info or fragment; exact match for redirects; private networks refused for outbound | — | OAuth 2.0 security BCP |
| Uploaded logos | PNG or SVG, checked by content, not name; an SVG must be only a picture (no script, event handlers, embedded HTML, external references or entities); size limit; virus scan | Served with a strict content policy | V12.1–V12.4 |
| SAML requests | Hardened XML reader; signature and issuer checked; identifiers checked before XPath | Signed assertions with a pairwise id | — |
| DigiLocker answers | Only name, date of birth, gender kept; the id hashed at once | Never output; the id never shown | SGM-910 section 4: no document numbers |
| Grievances | Text up to the length limit | To the grievance officer and the person | DPDP Act section 13 |
| Search terms (consoles) | Trimmed; length limit | Never written to the audit log (rc.8) | SGM-910 Confidential |

## 3. Output by channel

| Channel | Rule |
|---|---|
| HTML pages | Razor or Blazor encoding; CSP without inline script; `Cache-Control: no-store` |
| JSON (API, userinfo) | Only the fields the scope allows; `no-store` |
| Tokens | Only the claims ADR-0003 lists for the granted scopes |
| E-mail and SMS | Templates with fixed placeholders; values encoded for HTML e-mail; no personal data beyond the recipient's own name |
| Logs | [Log-content policy](log-content-policy.md) |
| Audit events | Metadata never holds secrets or search words; see SGM-208 |
| Exports and evidence packs | Spreadsheet formulas neutralised; every file hashed in the manifest |
