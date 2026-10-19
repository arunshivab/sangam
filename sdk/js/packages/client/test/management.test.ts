import { test } from "node:test";
import assert from "node:assert/strict";
import { ManagementClient, SangamApiError, lockSession, startIdleTimer } from "../src/index.js";

test("the management client caches its token and calls /api/v1", async () => {
  const calls: { url: string; method: string; auth?: string; body?: string }[] = [];
  const fake: typeof fetch = async (input, init) => {
    const url = String(input);
    calls.push({ url, method: init?.method ?? "GET", auth: (init?.headers as Record<string, string> | undefined)?.authorization, body: typeof init?.body === "string" ? init.body : init?.body?.toString() });
    if (url.endsWith("/connect/token")) return new Response(JSON.stringify({ access_token: "t1", expires_in: 3600 }));
    if (url.includes("/forbidden")) return new Response("{}", { status: 403 });
    return new Response(JSON.stringify({ ok: true }));
  };
  const client = new ManagementClient({ authority: "https://id.example.in/", clientId: "app", clientSecret: "s", fetch: fake });
  await client.upsertRole("nurse", "Nurse", ["vitals:read"]);
  await client.upsertMembership("o1", "u1", "admin-token", "nurse", true, "2026-12-31T23:59:59+05:30");
  assert.equal(calls.filter((c) => c.url.endsWith("/connect/token")).length, 1);
  assert.equal(calls[1]!.url, "https://id.example.in/api/v1/roles/nurse");
  assert.equal(calls[1]!.auth, "Bearer t1");
  assert.equal(JSON.parse(calls[2]!.body!).expiresAt, "2026-12-31T23:59:59+05:30");
  assert.match(calls[0]!.body!, /grant_type=client_credentials/);
  await assert.rejects(client.send("GET", "forbidden"), (e: unknown) => e instanceof SangamApiError && e.status === 403);
});

test("changing a place sends the admin's own token, as adding staff does (rc.13)", async () => {
  const seen: { method: string; url: string; admin?: string }[] = [];
  const fake: typeof fetch = async (input, init) => {
    const url = String(input);
    if (url.endsWith("/connect/token")) return new Response(JSON.stringify({ access_token: "t1", expires_in: 3600 }));
    seen.push({ method: init?.method ?? "GET", url, admin: (init?.headers as Record<string, string>)["sangam-acting-admin"] });
    return new Response(init?.method === "DELETE" ? "" : JSON.stringify({ ok: true }));
  };
  const client = new ManagementClient({ authority: "https://id.example.in", clientId: "app", clientSecret: "s", fetch: fake });
  await client.setRoles("o1", "u1", "admin-token", ["doctor", "administrator"]);
  await client.removeMember("o1", "u1", "admin-token");
  assert.deepEqual(seen, [
    { method: "PUT", url: "https://id.example.in/api/v1/orgs/o1/members/u1", admin: "admin-token" },
    { method: "DELETE", url: "https://id.example.in/api/v1/orgs/o1/members/u1", admin: "admin-token" },
  ]);
});

test("mergedInto gives the surviving id, and null for an id that was not merged (rc.7)", async () => {
  const fake: typeof fetch = async (input) => {
    const url = String(input);
    if (url.endsWith("/connect/token")) return new Response(JSON.stringify({ access_token: "t1", expires_in: 3600 }));
    if (url.endsWith("/users/u-old/merged")) return new Response(JSON.stringify({ merged_into: "u-new", merged_at: "2026-10-12T05:00:00Z" }));
    return new Response("{}", { status: 404 });
  };
  const client = new ManagementClient({ authority: "https://id.example.in/", clientId: "app", clientSecret: "s", fetch: fake });
  assert.equal(await client.mergedInto("u-old"), "u-new");
  assert.equal(await client.mergedInto("u-other"), null);
});

test("lockSession sends the person's own token, and says when the sign-in had already ended (rc.12)", async () => {
  const seen: string[] = [];
  const fake = (async (url: string | URL | Request, init?: RequestInit) => {
    seen.push(`${init?.method} ${String(url)} ${(init?.headers as Record<string, string>).authorization}`);
    return new Response(null, { status: seen.length === 1 ? 204 : 404 });
  }) as typeof fetch;
  assert.equal(await lockSession("https://id.example.in/", "person-token", fake), true);
  assert.equal(await lockSession("https://id.example.in", "person-token", fake), false);
  assert.deepEqual(seen[0], "POST https://id.example.in/api/v1/sessions/lock Bearer person-token");
});

test("startIdleTimer fires once after the quiet time, and activity restarts it (rc.12)", async () => {
  const target = new EventTarget();
  let fired = 0;
  const stop = startIdleTimer({ minutes: 0.001, onIdle: () => fired++, target });
  await new Promise((r) => setTimeout(r, 30));
  target.dispatchEvent(new Event("keydown"));
  await new Promise((r) => setTimeout(r, 120));
  assert.equal(fired, 1);
  stop();
  assert.throws(() => startIdleTimer({ minutes: 0, onIdle: () => undefined, target }));
});
