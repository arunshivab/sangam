import { test } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { mkdtemp, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { exportJWK, generateKeyPair, SignJWT } from "jose";
import { createSangam, validateAuditEvent } from "../src/index.js";

const ORG = "/0192a6b0-0000-7000-8000-000000000001/";
const orgs = [{ id: "0192a6b0-0000-7000-8000-000000000001", name: "Apulki Hospital", type: "hospital", path: ORG, role: "nurse", permissions: ["vitals:read"], inherits: true }];

async function listen(server: Server): Promise<string> {
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  return `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
}

/** A small OpenID provider: discovery, keys, and a token endpoint that checks PKCE as Sangam does. */
async function provider() {
  const { publicKey, privateKey } = await generateKeyPair("RS256");
  const jwk = { ...(await exportJWK(publicKey)), kid: "k1", alg: "RS256", use: "sig" };
  const codes = new Map<string, { challenge: string; nonce: string; acr: string; redirect: string }>();
  let base = "";
  const server = createServer(async (req, res) => {
    const url = new URL(req.url!, base);
    res.setHeader("content-type", "application/json");
    if (url.pathname === "/.well-known/openid-configuration") {
      res.end(JSON.stringify({ issuer: base + "/", authorization_endpoint: base + "/connect/authorize", token_endpoint: base + "/connect/token", jwks_uri: base + "/jwks", end_session_endpoint: base + "/connect/endsession", response_types_supported: ["code"], subject_types_supported: ["public"], id_token_signing_alg_values_supported: ["RS256"], code_challenge_methods_supported: ["S256"] }));
    } else if (url.pathname === "/jwks") {
      res.end(JSON.stringify({ keys: [jwk] }));
    } else if (url.pathname === "/connect/token") {
      let body = "";
      for await (const chunk of req) body += chunk;
      const form = new URLSearchParams(body);
      const grant = codes.get(form.get("code") ?? "");
      const challenge = createHash("sha256").update(form.get("code_verifier") ?? "").digest("base64url");
      if (!grant || grant.challenge !== challenge || form.get("client_secret") !== "secret" || form.get("redirect_uri") !== grant.redirect) {
        res.statusCode = 400;
        res.end(JSON.stringify({ error: "invalid_grant" }));
        return;
      }
      codes.delete(form.get("code")!);
      const idToken = await new SignJWT({ nonce: grant.nonce, name: "Meera Nair", sangam_orgs: orgs, acr: grant.acr, amr: ["pwd", "otp"], auth_time: Math.floor(Date.now() / 1000), sid: "s-1" })
        .setProtectedHeader({ alg: "RS256", kid: "k1" }).setIssuer(base + "/").setAudience("app").setSubject("0192a6b0-0000-7000-8000-000000000042").setIssuedAt().setExpirationTime("5m").sign(privateKey);
      res.end(JSON.stringify({ access_token: "at", token_type: "Bearer", expires_in: 300, id_token: idToken }));
    } else {
      res.statusCode = 404;
      res.end("{}");
    }
  });
  base = await listen(server);
  return {
    base,
    server,
    /** What Sangam does after the person signs in: a code for this authorisation request. */
    approve(authorizeUrl: string, acr = "urn:sangam:acr:1"): { code: string; state: string } {
      const q = new URL(authorizeUrl).searchParams;
      const code = "c" + codes.size + Math.random().toString(36).slice(2);
      codes.set(code, { challenge: q.get("code_challenge")!, nonce: q.get("nonce")!, acr, redirect: q.get("redirect_uri")! });
      return { code, state: q.get("state")! };
    },
  };
}

test("sign-in with PKCE, step-up, permission and audit, all server-side", async () => {
  const op = await provider();
  const dir = await mkdtemp(join(tmpdir(), "sangam-audit-"));
  let appBase = "";
  let sangam: ReturnType<typeof createSangam>;
  const app = createServer((req: IncomingMessage, res: ServerResponse) => {
    const fail = (e?: unknown) => {
      if (e) {
        res.statusCode = 500;
        res.end(String(e));
        return;
      }
      route();
    };
    const route = () => {
      if (req.url === "/secure") void sangam.requireSignIn()(req, res, () => res.end("secure"));
      else if (req.url === "/sign") void sangam.requireStepUp({ acr: "urn:sangam:acr:sign" })(req, res, async () => {
        const e = await sangam.audit.record(req, { action: "lims.result.sign", category: "sign", target: { type: "result", id: "R-1" }, signature: { tokenId: "sig-1", meaning: "Approved", recordHash: "sha256:" + "a".repeat(64) }, tenant: { orgId: orgs[0]!.id, orgPath: ORG } });
        res.end(JSON.stringify(e));
      });
      else if (req.url === "/vitals") void sangam.requirePermission(() => ORG + "ward/", "vitals:read")(req, res, () => res.end("vitals"));
      else if (req.url === "/orders") void sangam.requirePermission(() => ORG, "orders:write")(req, res, () => res.end("orders"));
      else res.end("home");
    };
    void sangam.router(req, res, fail);
  });
  appBase = await listen(app);
  sangam = createSangam({ authority: op.base, clientId: "app", clientSecret: "secret", baseUrl: appBase, cookieSecret: "a".repeat(32), allowInsecureRequests: true, audit: { appId: "lims", appVersion: "1.0.0", environment: "development", bufferPath: join(dir, "pending.jsonl") } });
  let cookie = "";
  const get = async (path: string, headers: Record<string, string> = {}) => {
    const r = await fetch(appBase + path, { redirect: "manual", headers: { cookie, ...headers } });
    const set = r.headers.getSetCookie().find((c) => c.startsWith("sangam.sid="));
    if (set) cookie = set.split(";")[0]!;
    return r;
  };
  try {
    // Not signed in: sent to sign in, or 401 to a fetch.
    assert.match((await get("/secure")).headers.get("location")!, /^\/auth\/login\?returnTo=%2Fsecure/);
    assert.equal((await get("/secure", { accept: "application/json" })).status, 401);
    assert.equal((await get("/auth/me")).status, 401);

    // Sign in: the browser is sent to Sangam with PKCE, comes back with a code, and lands where it started.
    const start = await get("/auth/login?returnTo=/secure");
    const authorize = start.headers.get("location")!;
    assert.ok(authorize.startsWith(op.base + "/connect/authorize?"));
    assert.equal(new URL(authorize).searchParams.get("code_challenge_method"), "S256");
    const before = cookie;
    const { code, state } = op.approve(authorize);
    const back = await get(`/auth/callback?code=${code}&state=${state}`);
    assert.equal(back.headers.get("location"), "/secure");
    assert.notEqual(cookie, before, "a new session id after sign-in");
    assert.equal(await (await get("/secure")).text(), "secure");
    const me = (await (await get("/auth/me")).json()) as { user: { id: string; memberships: unknown[] } };
    assert.equal(me.user.id, "0192a6b0-0000-7000-8000-000000000042");

    // Permission: inherited at a ward; refused for one the role lacks.
    assert.equal(await (await get("/vitals")).text(), "vitals");
    assert.equal((await get("/orders")).status, 403);

    // Step-up: a single-factor sign-in is sent back to Sangam for a signature.
    const needs = await get("/sign");
    const relogin = needs.headers.get("location")!;
    assert.match(relogin, /acr=urn%3Asangam%3Aacr%3Asign/);
    const again = await get(relogin);
    const signAuthorize = again.headers.get("location")!;
    assert.equal(new URL(signAuthorize).searchParams.get("acr_values"), "urn:sangam:acr:sign");
    const signed = op.approve(signAuthorize, "urn:sangam:acr:sign");
    assert.equal((await get(`/auth/callback?code=${signed.code}&state=${signed.state}`)).headers.get("location"), "/sign");

    // And now the signature goes through, with a shared audit event in the buffer.
    const event = (await (await get("/sign", { "user-agent": "flow-test" })).json()) as Record<string, unknown>;
    assert.deepEqual(validateAuditEvent(event), []);
    assert.equal((event.actor as Record<string, unknown>).acr, "urn:sangam:acr:sign");
    assert.equal((event.client as Record<string, unknown>).user_agent, "flow-test");
    const lines = (await readFile(join(dir, "pending.jsonl"), "utf8")).trim().split("\n");
    assert.equal(lines.length, 1);

    // A wrong state, a replayed code, and a returnTo that leaves the site are all refused.
    const third = op.approve((await get("/auth/login?returnTo=https://evil.example/")).headers.get("location")!);
    assert.equal((await get(`/auth/callback?code=${third.code}&state=${third.state}`)).headers.get("location"), "/");
    assert.equal((await get(`/auth/callback?code=${third.code}&state=${third.state}`)).headers.get("location"), "/", "no pending sign-in: nothing happens");
    const fourth = op.approve((await get("/auth/login?returnTo=/secure")).headers.get("location")!);
    assert.equal((await get(`/auth/callback?code=${fourth.code}&state=wrong`)).status, 500);

    // Sign out: the session is gone and the browser goes to Sangam's end-session endpoint.
    const out = await get("/auth/logout");
    assert.ok(out.headers.get("location")!.startsWith(op.base + "/connect/endsession?"));
    assert.equal((await get("/auth/me")).status, 401);
  } finally {
    app.close();
    op.server.close();
  }
});

test("a forged session cookie is ignored", async () => {
  const sangam = createSangam({ authority: "http://127.0.0.1:9", clientId: "app", clientSecret: "s", baseUrl: "http://localhost", cookieSecret: "b".repeat(32) });
  const req = { headers: { cookie: "sangam.sid=abc.def" } } as unknown as IncomingMessage;
  assert.equal(await sangam.user(req), undefined);
  assert.throws(() => createSangam({ authority: "x", clientId: "a", clientSecret: "s", baseUrl: "http://l", cookieSecret: "short" }));
});
