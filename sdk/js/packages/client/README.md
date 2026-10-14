# @sangam/client

Sangam for JavaScript and TypeScript, in browsers and Node 20+ (R6, SGM-306). No sign-in flow here — that is
`@sangam/node` (server) and `@sangam/react` (browser) — but everything both need, with the same rules as every other
Sangam SDK, proved by the shared conformance vectors (`sdk/conformance/vectors.json`).

```ts
import { userFromClaims, can, hasRole, satisfies, SangamAcr, stepUpChallenge, verifyWebhook,
         buildAuditEvent, validateAuditEvent, createTokenVerifier, ManagementClient } from "@sangam/client";

const user = userFromClaims(idTokenClaims);                 // memberships from sangam_orgs, acr, amr, auth_time, sid
can(user, "/group/hospital/ward/", "vitals:read");          // granted there, or at an ancestor that inherits
satisfies({ acr: user.acr, authTime: user.authTime }, { acr: SangamAcr.signature });   // two factors, ≤ 5 minutes
res.setHeader("WWW-Authenticate", stepUpChallenge(SangamAcr.twoFactor, 300));          // RFC 9470, for an API

await verifyWebhook({ id, timestamp, signature, body }, "whsec_…");   // Standard Webhooks, ±5 minutes

const verify = createTokenVerifier({ issuer: "https://id.sangamid.in/", audience: "his-api" });
const { user: caller, scopes } = await verify(bearerToken);           // signature, issuer, expiry, audience

const sangam = new ManagementClient({ authority, clientId, clientSecret });   // back end only
await sangam.upsertMembership(orgId, sub, "nurse", true, "2026-12-31T23:59:59+05:30");

const event = buildAuditEvent({ appId: "lims", appVersion: "1.2.0", environment: "production" }, user,
  { ip, userAgent }, { action: "lims.result.sign", category: "sign", target: { type: "result", id: "R-1" },
  signature: { tokenId, meaning: "Approved", recordHash } });
validateAuditEvent(event);   // [] — the shared schema 1.0 (SGM-208)
```

rc.12:

```ts
user.currentOrganisation;                 // sangam_org: where they are working for this sign-in
await sangam.checkStaff(orgId, adminAccessToken, person);           // which fields match an existing account
await sangam.addStaff(orgId, adminAccessToken, { person, roles: ["nurse"] });
await sangam.setRoles(orgId, sub, ["nurse", "charge-nurse"]);       // or [] for staff with no role
await lockSession(authority, personAccessToken);                    // shared ward devices
const stop = startIdleTimer({ minutes: 10, onIdle: () => location.assign("/auth/lock") });   // in the browser
if (emergencyActive(user.emergency)) showBanner(user.emergency!.caseReference);
await endEmergency(authority, personAccessToken, "Care handed over");
```

`npm test` runs the shared vectors and unit tests.
