# ADR-0016 — Sangam as a SAML 2.0 identity provider

Status: accepted as built (2026-10-07, R4 PR-22), replacing the proposed ADR-0016 of design set v4 (SGM-302). Builds on ADR-0003. SGM-215 is the specification.

## Context

Some applications a hospital already runs (laboratory, PACS, HR and procurement systems bought in) speak only SAML
2.0. Without SAML, their staff keep a second password, and Sangam cannot be the one sign-in the hospitals were
promised. OpenID Connect stays the way new applications connect; SAML exists for the ones that cannot.

## Decision

**What is supported.** Web Browser SSO: an AuthnRequest by HTTP-Redirect or HTTP-POST, the Response by HTTP-POST.
Single logout started by the service provider (HTTP-Redirect, answered with a signed LogoutResponse). Sign-in
started at Sangam (IdP-initiated) only for a service provider registered to allow it — it is off by default because it
has no request to bind the answer to. Not supported: the Artifact binding, attribute queries, ECP, SLO started by
Sangam toward service providers, and service providers registering themselves.

**A SAML service provider is an application.** Registering one (operator console, *Applications → SAML service
providers*, AppManager or above; or by pasting its metadata) also creates an application, `saml-<hash>`. So the
sign-in rule, consent, disabling, the partner console and the audit log apply to it exactly as to an OpenID Connect
application, and the sign-in screens are the same (`/saml/continue` passes through the same gate as
`/connect/authorize`).

**What a service provider is told.** A NameID private to that service provider (HMAC of the person's id and its
entity id under `Sangam:Saml:PairwiseKey`), unless it is registered for the e-mail address. Attributes only from a
fixed list (name, given and family name, e-mail, roles, organisations), each chosen at registration and shown to the
person on the consent screen as the matching OpenID Connect scopes. The authentication context says what the sign-in
achieved: PasswordProtectedTransport, or REFEDS MFA for a two-step or passkey sign-in; a request for more than the
session has sends the person back to sign in at that level.

**Protection.** The assertion is signed; for a service provider with an encryption certificate it is then encrypted
(AES-256-GCM, key wrapped with RSA-OAEP), and the response is signed too. A request must come from a registered,
active service provider, to Sangam's own address, within five minutes of its clock, answered only once (replays are
refused from a table of request ids), and to an assertion consumer address registered for it. A service provider can
be required to sign its requests. SAML has its own signing key (`docker-compose.saml.yml`), not the OpenID Connect
token key, so the two rotate independently; previous keys stay in the metadata during a rotation.

**No SAML library.** The messages are built and checked with .NET's own `System.Security.Cryptography.Xml`
(`SignedXml`, `EncryptedXml`), which ships in the ASP.NET Core shared framework and is maintained with it. The SAML
libraries for .NET are either commercial, unmaintained, or bring a large dependency for a small, fixed profile. The
risk of writing it ourselves is signature wrapping and XML parsing, so: untrusted XML is loaded with DTDs prohibited,
no resolver and a 64 KB cap (inflated redirect messages too); a signature is accepted only when it is the single
signature directly under the root and its one reference is the root's own ID, which must be unique in the document;
and the tests include a wrapped response, a doubled signature, an altered NameID, an entity-expansion document and a
decompression bomb.

## Consequences

- SAML is off in production until the founder creates the SAML key and pairwise key and starts with
  `docker-compose.saml.yml` (production README).
- The pairwise key must never change: every service provider would see every person as someone new.
- Open: a partner editing its own service provider's attribute release on the partner console (deferred); SLO started
  by Sangam toward every service provider a person used (deferred until a service provider needs it).
