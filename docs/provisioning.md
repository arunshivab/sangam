# SCIM provisioning (PR-23, SGM-216)

Sangam keeps an application's own list of people in step through SCIM 2.0 (RFC 7643, RFC 7644). Sangam is the SCIM
**client**; the application runs a SCIM **server**. Set it up on the partner console: *your application →
Provisioning*.

## Who is provisioned

A person is provisioned in an application while all of these hold:

- they have at least one role in it (a membership that is not revoked and has not expired, PR-25);
- their consent to the application stands (they have not withdrawn it in the account portal);
- their Sangam account is active (not suspended, not being deleted).

When any of them stops holding, Sangam deactivates them (`active: false`), or deletes them if the application chose
*Delete people who lose access*. They are removed from every group Sangam put them in.

## What is sent

```json
{
  "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User", "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"],
  "externalId": "<the person's Sangam id — the sub claim>",
  "userName": "asha@example.in",
  "name": { "givenName": "Asha", "familyName": "Nair", "formatted": "Asha Nair" },
  "displayName": "Asha Nair",
  "emails": [{ "value": "asha@example.in", "type": "work", "primary": true }],
  "phoneNumbers": [{ "value": "+919876500000", "type": "mobile" }],
  "active": true,
  "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User": { "organization": "Apulki Hospital" }
}
```

`phoneNumbers` only when the person consented to the `phone` scope for the application. `externalId` is the stable key:
match on it, never on the e-mail address, which can change.

**Groups:** one per role (`Nurse`), or one per role at each organisation (`Nurse — Apulki Hospital`), as chosen. Their
`externalId` is the role code, or `role@organisation-id`. A renamed role renames its group.

## Calls Sangam makes

| Call | When |
|---|---|
| `GET /Users?filter=externalId eq "<sub>"` | Before creating someone, to adopt a person the server already has |
| `POST /Users` | A person who should be provisioned and is not |
| `PATCH /Users/{id}` (`replace`) | Their details, or `active` |
| `DELETE /Users/{id}` | Only with *Delete people who lose access* |
| `POST /Groups`, `PATCH /Groups/{id}` (`add`/`remove` `members`, `replace` `displayName`) | Group changes |
| `GET /Users?filter=active eq true&count=0` | Nightly reconciliation (compares counts) |
| `GET /ServiceProviderConfig` | *Test the connection* |

Every request is `application/scim+json` with `Authorization: Bearer …`. A `404` on a `PATCH` means the person was
removed on your side: Sangam creates them again. Redirects are never followed.

## Order, retries and the log

Each delivery brings one person to the state Sangam holds *at that moment*, so deliveries are idempotent, can be merged
and cannot apply out of order. A failed delivery is tried again after 1 min, 5 min, 30 min, 2 h, 6 h and 12 h; then it is
marked *gave up*, the application is marked *failing*, and an administrator can *Try again* from the log. Every attempt
is in the delivery log on the partner console: the calls made, status codes, latency, and the start of any error. Each
night (and on *Reconcile now*) everyone who should be provisioned, and everyone ever provisioned, is queued again.

## Authenticating Sangam

- **Bearer token:** a token your SCIM server issued. Sangam stores it encrypted and never shows it again.
- **Sangam-signed token:** no shared secret. Each call carries a JWT signed with Sangam's published token keys:
  `iss` = `https://id.sangamid.in/`, `aud` = your SCIM base address exactly as entered, `typ` = `sangam-service+jwt`,
  `sub` = `sangam`, `scope` = `scim`, lifetime five minutes. Validate it against `https://id.sangamid.in/.well-known/jwks`
  as you validate Sangam access tokens.

## Addresses

The SCIM base address must be `https`. Sangam refuses addresses on private networks (loopback, RFC 1918, link-local,
carrier-grade NAT), checked again when each connection is made. A deployment that must reach an on-premises server sets
`Sangam__Outbound__AllowPrivateNetworks=true`.
