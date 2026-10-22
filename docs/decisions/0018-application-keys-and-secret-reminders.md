# ADR-0018 — Application keys (private_key_jwt) and client-secret reminders

Status: accepted as built (2026-10-22, 1.0.0-rc.15), the founder's decision of 22 October 2026 ("full version now").
Builds on ADR-0006 (the partner console) and rc.11's two live client secrets. Answers ASVS V2.10.1 for partner
applications.

## Context

Until rc.14 an application's back end proved who it was to Sangam's token endpoint with a client secret: a shared value
sent with every request, valid until its owner revoked it (or, for one an owner made, for twelve months). A secret can
be copied from a configuration file, a log or a backup, and used from anywhere until someone notices. The secret Sangam
issued from its configuration had no end date at all, and nothing told the owner when it got old.

## Decision

**An application may sign in with a key instead of a secret** (`private_key_jwt`, RFC 7523, as OpenID Connect
defines it).

- The owner pastes a **public key** on the partner console (Credentials & redirects, Keys): a PEM public key or a JSON
  Web Key. RSA of 2048 bits or more, or elliptic curve P-256, P-384 or P-521. A private key pasted by mistake is refused
  with a warning and never kept. Sangam keeps the key as a JSON Web Key; its id (`kid`) is its RFC 7638 thumbprint, so
  the same key always gets the same id and the SDKs work it out themselves.
- Two keys can be live at once, so an owner moves to a new key with no break. Keys do not expire on their own; the owner
  removes one. Only an owner adds or removes keys; every administrator sees them, with when each was last used (to the
  hour). Each change is audited (`app.key.add`, `app.key.remove`).
- Once Sangam has seen the application sign in with a key, the owner may revoke **every** secret. What is left must
  always be a secret or a key that has been used, so an owner cannot lock their own application out by mistake (a wrong
  key pasted, servers not yet moved). Changes to one application's credentials are serialised (its row is locked), so
  two owners at once cannot both remove "one of two".
- A disabled application has no keys, as it has no secrets (the operator's emergency stop).

**What an assertion must be.** OpenIddict checks the signature against the application's live keys, the type
(`typ: client-authentication+jwt`, as draft-ietf-oauth-rfc7523bis asks), `iss` and `sub` (the client id), the audience
(Sangam's issuer, exactly: not the token endpoint's address, which the draft forbids) and the expiry. Sangam adds: it
says when it was made (`iat`) and lives five minutes at most from then, it has not expired (OpenIddict alone allows five
minutes for clock difference; Sangam allows one), it has a `jti`, and it is accepted **once** — the database keeps each
accepted `jti` until ten minutes after the assertion expires, longer than it could be accepted, so a replay is refused
on every host. A refusal is `invalid_client`, audited like any other.

**The SDKs do it.** .NET (`PrivateKeyPem` or `PrivateKeyPath` in `SangamOptions`), JavaScript (`privateKey`), Python
(`private_key`) and Java (`ClientKey`, and `sangam.private-key` for Spring Security, with the registration's
`client-authentication-method=private_key_jwt`) sign a fresh two-minute assertion for every code redemption and every
management-API token. The .NET SDK's assertions are tested against the real token endpoint.

**Secret reminders.** The hourly sweep e-mails an application's owners (template `client_secret_ageing`, in the owner's
language) when the secret Sangam issued is twelve months old — again each month until it is replaced or retired — and 30
days before a secret they made ends. Each e-mail says how to replace it and suggests a key instead. When no owner can be
e-mailed (the mail service is down), the reminder is tried again at the next sweep. Sangam's own
applications are left out: their secrets are the deployment's, rotated by the operator under SGM-911.

## Consequences

- A partner application's servers can prove who they are without anything shareable leaving them; a stolen assertion is
  worth nothing after one use or five minutes.
- Sangam's own hosts (portal and consoles) still sign in with secrets from secret files, and Anjal is still called with
  an API key: V2.10.1 stays Partly for those. Moving them to keys is for a later release.
- Mutual TLS client authentication (RFC 8705) is not offered; keys cover the same need without certificates at the edge.
- Access tokens now last 15 minutes (were an hour, ASVS V3.3.1), so an application's server keeps using Sangam's API
  for at most 15 minutes after the person signs out. Applications that kept a token longer refresh it.
