import express, { type NextFunction, type Request, type Response } from "express";
import { rateLimit } from "express-rate-limit";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { can, createSangam, SangamAcr } from "@sangam/node";

// Sample settings: the development Sangam on localhost:5100 and its sample application (see sdk/README.md).
const port = Number(process.env.PORT ?? 5910);
const baseUrl = process.env.BASE_URL ?? `http://localhost:${port}`;
const authority = process.env.SANGAM_AUTHORITY ?? "http://localhost:5100";

const sangam = createSangam({
  authority,
  clientId: process.env.SANGAM_CLIENT_ID ?? "sangam-dev-sample",
  clientSecret: process.env.SANGAM_CLIENT_SECRET ?? "sangam-dev-sample-secret-change-me",
  baseUrl,
  cookieSecret: process.env.COOKIE_SECRET ?? "sample-only-cookie-secret-change-me-0123456789",
  allowInsecureRequests: authority.startsWith("http://localhost") || authority.startsWith("http://127.0.0.1"),
  audit: { appId: "sangam-dev-sample", appVersion: "1.0.0-rc.4", environment: "development", bufferPath: process.env.AUDIT_BUFFER ?? "sangam-audit/pending.jsonl" },
});

const app = express();
// rc.3: a per-address limit on every route, sign-in included. Copy it: a real application needs one in front of its
// API too (tune the numbers; behind a proxy, set "trust proxy" so the limit sees the client's address).
app.use(rateLimit({ windowMs: 60_000, limit: 120, standardHeaders: "draft-8", legacyHeaders: false }));
app.use(express.json());
app.use((req, res, next) => void sangam.router(req, res, next));

// Who is signed in is on /auth/me (from @sangam/node). The API below needs a signed-in person.
app.get("/api/check", sangam.requireSignIn(), async (req, res) => {
  const user = await sangam.user(req);
  res.json({ allowed: can(user, String(req.query.org ?? ""), String(req.query.permission ?? "")) });
});

// Signing a record needs a fresh, stronger sign-in (urn:sangam:acr:sign: two factors within five minutes).
app.post(
  "/api/records/:id/sign",
  sangam.requireStepUp({ acr: SangamAcr.signature }, (req) => `/?resume=sign&record=${encodeURIComponent(String((req as Request).params?.id ?? ""))}`),
  async (req: Request, res: Response) => {
    const user = await sangam.user(req);
    const membership = user?.memberships[0];
    const id = String(req.params.id);
    const event = await sangam.audit.record(req, {
      action: "sample.record.sign",
      category: "sign",
      target: { type: "record", id, display: `Record ${id}` },
      signature: { tokenId: `sig-${Date.now()}`, meaning: "Approved", recordHash: "sha256:" + "0".repeat(64) },
      ...(membership ? { tenant: { orgId: membership.organisationId, orgPath: membership.path } } : {}),
    });
    res.json({ signed: id, event });
  },
);

app.get("/api/audit", sangam.requireSignIn(), async (_req, res) => {
  res.json({ bufferPath: sangam.audit.bufferPath, events: (await sangam.audit.pending()).slice(-5) });
});

const here = dirname(fileURLToPath(import.meta.url));
const publicDir = join(here, "..", "..", "public");
app.use(express.static(publicDir));
app.get("/", (_req, res) => res.sendFile(join(publicDir, "index.html")));

app.use((error: unknown, _req: Request, res: Response, _next: NextFunction) => {
  console.error(error);
  res.status(500).json({ error: "Something went wrong. If you were signing in, try again." });
});

app.listen(port, () => console.log(`Sangam React + Node sample on ${baseUrl}`));
