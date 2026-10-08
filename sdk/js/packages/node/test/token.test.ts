import { test } from "node:test";
import assert from "node:assert/strict";
import type { IncomingMessage, ServerResponse } from "node:http";
import { createSangam, type SangamUser } from "../src/index.js";

function call(handler: (req: IncomingMessage, res: ServerResponse, next: () => void) => unknown, authorization?: string) {
  return new Promise<{ status: number; headers: Record<string, string>; passed: boolean; req: IncomingMessage }>((resolve) => {
    const headers: Record<string, string> = {};
    const req = { headers: authorization ? { authorization } : {} } as IncomingMessage;
    const res = {
      statusCode: 200,
      setHeader: (k: string, v: string) => (headers[k.toLowerCase()] = v),
      end: () => resolve({ status: res.statusCode, headers, passed: false, req }),
    } as unknown as ServerResponse;
    void handler(req, res, () => resolve({ status: 200, headers, passed: true, req }));
  });
}

test("an API checks the access token, its scope and the step-up level", async () => {
  const sangam = createSangam({ authority: "http://127.0.0.1:9", clientId: "a", clientSecret: "s", baseUrl: "http://localhost", cookieSecret: "c".repeat(32) });
  const now = Math.floor(Date.now() / 1000);
  const person = (acr: string): SangamUser => ({ id: "u", name: "", memberships: [], acr, authTime: now - 30 });
  const verifier = async (token: string) => {
    if (token === "bad") throw new Error("bad");
    return { user: person(token === "strong" ? "urn:sangam:acr:2" : "urn:sangam:acr:1"), claims: {}, scopes: ["his.read"] };
  };
  assert.equal((await call(sangam.requireToken(undefined, verifier))).status, 401);
  assert.equal((await call(sangam.requireToken(undefined, verifier), "Bearer bad")).status, 401);
  assert.equal((await call(sangam.requireToken({ acr: "", scope: "his.write" }, verifier), "Bearer weak")).status, 403);
  const short = await call(sangam.requireToken({ acr: "urn:sangam:acr:2", maxAge: 300 }, verifier), "Bearer weak");
  assert.equal(short.status, 401);
  assert.match(short.headers["www-authenticate"]!, /insufficient_user_authentication.*acr_values="urn:sangam:acr:2", max_age=300/);
  const ok = await call(sangam.requireToken({ acr: "urn:sangam:acr:2", maxAge: 300, scope: "his.read" }, verifier), "Bearer strong");
  assert.equal(ok.passed, true);
  assert.equal(ok.req.sangamToken?.user.acr, "urn:sangam:acr:2");
});
