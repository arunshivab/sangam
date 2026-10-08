# @sangam/node

Sign in with Sangam for Node.js back ends — Express, or plain `node:http` (R6, SGM-306). The authorization code with
PKCE is exchanged on the server and the browser only ever holds a signed, http-only session cookie (backend for
frontend); built on [openid-client](https://github.com/panva/openid-client).

```ts
import express from "express";
import { createSangam, SangamAcr } from "@sangam/node";

const sangam = createSangam({
  authority: "https://id.sangamid.in", clientId: "lims", clientSecret: process.env.SANGAM_SECRET!,
  baseUrl: "https://lims.example.in",              // callback: https://lims.example.in/auth/callback
  cookieSecret: process.env.COOKIE_SECRET!,        // 32+ characters
  audit: { appId: "lims", appVersion: "1.2.0", environment: "production", bufferPath: "/var/lib/lims/audit.jsonl" },
});

const app = express();
app.use((req, res, next) => void sangam.router(req, res, next));   // /auth/login, /callback, /logout, /me
app.get("/results", sangam.requireSignIn(), handler);
app.get("/wards/:id/vitals", sangam.requirePermission((req) => pathOf(req), "vitals:read"), handler);
app.post("/results/:id/sign", sangam.requireStepUp({ acr: SangamAcr.signature }), async (req, res) => {
  await sangam.audit.record(req, { action: "lims.result.sign", category: "sign", target: { type: "result", id: req.params.id }, signature });
});
app.get("/api/v1/results", sangam.requireToken({ scope: "lims.read" }), handler);    // APIs called with a token
```

- **Sessions**: in memory by default; pass `store` (get, set, delete) to share them between processes.
- **Step-up**: a page is sent back to Sangam with `acr_values` (and `max_age`); a `fetch` gets 401 with `login`,
  which `@sangam/react` follows. An API answers with the RFC 9470 challenge.
- **Audit**: events in the shared schema go to a JSON Lines buffer; `audit.flush()` sends them once `audit.endpoint`
  (the future audit service) is set.
- `sangam.management` is a `ManagementClient` for your back end.
