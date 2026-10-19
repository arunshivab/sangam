# ADR-0017 — Pairwise subject ids: every application knows a person by its own id

Status: accepted as built (2026-10-20, 1.0.0-rc.14), the founder's decisions of 19 and 20 October 2026. Builds on
ADR-0003 (the OIDC flow and the management API) and replaces the open item "pairwise subject ids (A4)" of the go-live
checklist. ADR-0016 already gave SAML service providers a NameID of their own; this does the same for every OpenID
Connect application.

## Context

Until rc.13 every application saw the same `sub` for a person: their Sangam id. The same id appeared in the management
API (`/users/{id}`, `/orgs/{org}/members/{id}`), in webhooks (`subject.sub`, `merged_into`), in SCIM (`externalId`), in
logout and signature tokens and in evidence packs. Two applications, or a breach of one, could therefore join their
records about people by that id, and one application could take an id it learnt elsewhere and ask Sangam's API about
that person. Sangam's applications belong to different companies (LiPi, Medimaze and others), and the founder decided
that none of them, imagiQa's own included, shares an id with another.

## Decision

**Every application gets its own id for each person.** A random UUID, given out the first time the application meets
the person (their first sign-in, an admin adding them, an event or a delivery about them), kept in `app_subjects`
(application, person, id; unique per application) for as long as the account lives, so it never changes for that
application: not when the connection lapses for inactivity and is made again, not with a key rotation. Random, not
derived from a key (as SAML's NameID is), so there is no key whose loss or leak would expose or change every
application's ids, and the lookup from an application's id back to the person is an index, not a search. One
application's clients (web, mobile, a ward kiosk) share its id. Nothing was live, so there was no changeover: every
application has had its own ids from rc.14.

**Where the application's id replaces the Sangam id.** The `sub` of ID tokens, access tokens, refresh tokens and
codes, userinfo and introspection; an exchanged token (RFC 8693) carries the target application's id, not the caller's.
The sign-in session is per application too: each application's `sid` (in its tokens, logout tokens, front-channel
logout and webhooks) and SAML `SessionIndex` is a SHA-256 of the session and the application, so two applications used
in one session cannot match up through it; the session itself travels only inside codes and refresh tokens, which only
Sangam reads. A SAML service provider's NameID is its application's id as well (so SCIM, webhooks and SAML agree), which
retired the SAML pairwise key.
The management API in both directions: every id an application sends is looked up in its own ids, and one it does not
know (another application's, or a Sangam id) is answered *not found*, never *forbidden*, so the answer says nothing
about whether the person exists elsewhere; every id it is given back is its own. The acting admin's token names the
admin by the application's id too. Webhooks (`subject.sub`, and `merged_into` / `merged_from` in an account merge: the
surviving account's id for that same application, given out then if it had none), SCIM `externalId`, back-channel
logout tokens, electronic-signature tokens and a signature request's signer and decider, and evidence packs (every
person in every file).

**Where the Sangam id stays.** Inside Sangam, which must know that a person in one application is the same account as in
another (passwords and passkeys, DigiLocker, *Your organisations*, account merge, deletion, the audit log). Sangam's own
applications (the portal and the consoles, `IsPlatform`) use the Sangam id and keep no rows. The operator console shows
it; the hospital admin console and the partner console show names and e-mail addresses and never the Sangam id.
OpenIddict's authorizations stay under the Sangam id (so leaving an application, inactivity and merge revoke them); its
token entries are under the application's id.

**No translation between applications.** Sangam does not tell one application the id another uses. Two applications
that must agree on who a member of staff is (a HIS sending an order to a laboratory system) use an identifier the
hospital owns: its employee code, or the doctor's registration number. Patients are not in Sangam, so patient matching
is unaffected (the hospital's UHID).

## Consequences

- Applications cannot match people up by id, and an id leaked from one is useless with another or with Sangam's API.
- Accepted (founder, 20 Oct 2026): an application the person allows to see their e-mail address or mobile number can
  still match them by it; for staff it cannot be avoided, because the hospital admin enters the e-mail. Hiding them would
  need a relay address per application.
- Developers store people against the application's own `sub`, as before; nothing in the SDKs changes shape (still a
  UUID). Apps that compared ids with another application's must use the hospital's own identifiers instead.
- The demo's test data held rc.13 ids and is reset when rc.14 is deployed.
- **Open (decide before allowing an exchange):** token exchange is allowed only between pairs of applications an
  operator configures (`ExchangeAudiences`), for people who consented to both, and none is configured in production. The
  exchanged token is readable by the caller, so the caller learns the target's id for that person. Before the first pair
  is allowed, the founder decides between accepting that for such explicitly joined pairs, or tokens only the target can
  read (introspection).
- Residual, accepted: `auth_time` is the same in every application's tokens from one sign-in, a weak timing signal.
  Exchanged tokens (10 minutes, never refreshable) are not revoked early when the person leaves the calling application.
- Evidence packs name people by the application's ids, while the audit hashes in them were computed over Sangam's own
  records: the chain is checked by Sangam (the pack's `audit-chain.txt`), not recomputed by the auditor.
- Sangam gives out an id to an application for every person it tells that application about, including in evidence
  packs (administrators, operators in the audit trail); the ids are random and mean nothing elsewhere.
