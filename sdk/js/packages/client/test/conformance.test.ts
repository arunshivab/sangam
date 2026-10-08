import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { buildAuditEvent, can, hasRole, parseMemberships, rolesIn, satisfies, stepUpChallenge, uuidv7, validateAuditEvent, verifyWebhook, type SangamUser } from "../src/index.js";

// The shared conformance vectors every Sangam SDK runs (sdk/conformance/vectors.json).
const V = JSON.parse(readFileSync(resolve(process.cwd(), "../../../conformance/vectors.json"), "utf8"));
const user: SangamUser = { id: "x", name: "Test", memberships: parseMemberships(V.permissions.sangam_orgs) };

test("permissions", () => {
  for (const c of V.permissions.has_permission) assert.equal(can(user, c.org_path, c.permission), c.expect, c.why);
  for (const c of V.permissions.has_role) assert.equal(hasRole(user, c.org_path, c.role), c.expect);
  for (const c of V.permissions.roles_in) assert.deepEqual(rolesIn(user, c.org_path), c.expect);
  for (const c of V.permissions.malformed_claims) assert.equal(parseMemberships(c.claim).length, c.memberships);
  assert.equal(can(null, "/x/", "a:b"), false);
});

test("step-up", () => {
  for (const c of V.step_up.cases) {
    const requirement = c.max_age === null ? { acr: c.level } : { acr: c.level, maxAge: c.max_age };
    assert.equal(satisfies({ acr: c.acr, authTime: c.auth_time }, requirement, V.step_up.now), c.expect, c.why);
  }
  for (const c of V.challenge) assert.equal(stepUpChallenge(c.level, c.max_age ?? undefined), c.header);
});

test("webhooks", async () => {
  for (const c of V.webhooks.cases) {
    assert.equal(await verifyWebhook({ id: c.id, timestamp: c.timestamp, signature: c.signature, body: c.body }, c.secret, c.now), c.expect, c.why);
  }
});

test("audit validation", () => {
  for (const c of V.audit.validation) {
    const problems = validateAuditEvent(c.event);
    assert.equal(problems.length === 0, c.valid, `${c.why}: ${problems.join("; ")}`);
  }
});

test("audit builder", () => {
  const b = V.audit.builder;
  const u: SangamUser = { id: b.user.sub, name: "", memberships: [], sessionId: b.user.sid, acr: b.user.acr, amr: b.user.amr };
  const i = b.input;
  const e = buildAuditEvent({ appId: b.config.app_id, appVersion: b.config.app_version, environment: b.config.environment }, u, { ip: b.request.ip, userAgent: b.request.user_agent }, {
    action: i.action, category: i.category, target: i.target, outcome: i.outcome, tenant: { orgId: i.tenant.org_id, orgPath: i.tenant.org_path },
    dataClassification: i.data_classification, changes: i.changes, sensitive: i.sensitive,
  });
  assert.deepEqual(validateAuditEvent(e), []);
  for (const [k, v] of Object.entries(b.expect)) assert.deepEqual(e[k], v, k);
  for (const k of b.absent) assert.equal(k in e, false);
});

test("uuid v7 is time-ordered", () => {
  const a = uuidv7(1_700_000_000_000);
  const b = uuidv7(1_700_000_000_001);
  assert.match(a, /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  assert.ok(a < b);
});
