import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { exportJWK, generateKeyPair, SignJWT } from "jose";
import { can, createTokenVerifier } from "../src/index.js";

test("access tokens are checked against Sangam's published keys, issuer and audience", async () => {
  const { publicKey, privateKey } = await generateKeyPair("RS256");
  const jwk = { ...(await exportJWK(publicKey)), kid: "k1", alg: "RS256", use: "sig" };
  const server = createServer((req, res) => {
    const base = `http://127.0.0.1:${(server.address() as AddressInfo).port}/`;
    res.setHeader("content-type", "application/json");
    res.end(req.url === "/.well-known/openid-configuration" ? JSON.stringify({ issuer: base, jwks_uri: base + ".well-known/jwks" }) : JSON.stringify({ keys: [jwk] }));
  });
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  try {
    const issuer = `http://127.0.0.1:${(server.address() as AddressInfo).port}/`;
    const orgs = [{ id: "0192a6b0-0000-7000-8000-000000000002", name: "Apulki Hospital", type: "hospital", path: "/h/", role: "nurse", permissions: ["vitals:read"], inherits: true }];
    const sign = (claims: Record<string, unknown>, iss = issuer, aud = "his-api") =>
      new SignJWT(claims).setProtectedHeader({ alg: "RS256", kid: "k1" }).setIssuer(iss).setAudience(aud).setSubject("0192a6b0-0000-7000-8000-000000000042").setIssuedAt().setExpirationTime("5m").sign(privateKey);
    const verify = createTokenVerifier({ issuer, audience: "his-api" });
    const { user, scopes } = await verify(await sign({ scope: "openid orgs.read", sangam_orgs: orgs, acr: "urn:sangam:acr:2" }));
    assert.equal(user.id, "0192a6b0-0000-7000-8000-000000000042");
    assert.deepEqual(scopes, ["openid", "orgs.read"]);
    assert.equal(can(user, "/h/ward/", "vitals:read"), true);
    await assert.rejects(verify(await sign({}, "https://evil.example/")));
    await assert.rejects(verify(await sign({}, issuer, "other-api")));
    const { privateKey: stranger } = await generateKeyPair("RS256");
    await assert.rejects(verify(await new SignJWT({}).setProtectedHeader({ alg: "RS256", kid: "k1" }).setIssuer(issuer).setAudience("his-api").setExpirationTime("5m").sign(stranger)));
  } finally {
    server.close();
  }
});
