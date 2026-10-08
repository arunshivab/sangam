import type { SangamUser } from "./memberships.js";

/** The shared audit event schema version this SDK writes (SGM-208). */
export const AUDIT_SCHEMA_VERSION = "1.0";
/** What a sensitive value becomes in `changes`. */
export const MASKED = "[masked]";

export type AuditCategory = "access" | "create" | "update" | "delete" | "approve" | "sign" | "export" | "admin" | "security";
export type AuditOutcome = "success" | "failure" | "denied";
export type DataClassification = "public" | "internal" | "personal" | "sensitive-personal" | "health";
export type ActorType = "user" | "admin" | "api" | "system" | "anonymous";

export interface AuditConfig {
  /** Your application's Sangam client id. */
  appId: string;
  appVersion: string;
  environment: "production" | "staging" | "development";
}

export interface AuditRequest {
  ip?: string | undefined;
  userAgent?: string | undefined;
}

export interface AuditEntry {
  /** domain.object.verb, for example "qms.document.approve". */
  action: string;
  category: AuditCategory;
  target: { type: string; id: string; display?: string };
  outcome?: AuditOutcome;
  tenant?: { orgId: string; orgPath?: string };
  dataClassification?: DataClassification;
  reason?: string;
  signature?: { tokenId: string; meaning: string; recordHash: string };
  changes?: { field: string; before?: unknown; after?: unknown }[];
  /** Fields whose values are masked in `changes`. */
  sensitive?: string[];
  /** Who acted when it was not the signed-in person. */
  actorType?: ActorType;
  /** For an "api" actor: the calling application's client id. */
  clientId?: string;
  correlationId?: string;
}

/** The event as sent to the audit service. */
export type AuditEvent = Record<string, unknown>;

/** A time-ordered UUID (version 7, RFC 9562). */
export function uuidv7(now: number = Date.now()): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  const ms = BigInt(now);
  for (let i = 0; i < 6; i++) bytes[i] = Number((ms >> BigInt(8 * (5 - i))) & 0xffn);
  bytes[6] = (bytes[6]! & 0x0f) | 0x70;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;
  const hex = [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/**
 * Builds an event in schema 1.0: source from your configuration, actor from the signed-in user, client from the
 * request, sensitive fields masked. Never sets recorded_at, prev_hash or hash: the audit service does.
 */
export function buildAuditEvent(config: AuditConfig, user: SangamUser | null | undefined, request: AuditRequest, entry: AuditEntry, now: Date = new Date()): AuditEvent {
  const type: ActorType = entry.actorType ?? (user?.id ? "user" : "anonymous");
  const actor: Record<string, unknown> = { type };
  if (user?.id && (type === "user" || type === "admin")) {
    actor.sub = user.id;
    if (user.sessionId) actor.session_id = user.sessionId;
    if (user.acr) actor.acr = user.acr;
    if (user.amr && user.amr.length > 0) actor.amr = [...user.amr];
  }
  if (entry.clientId) actor.client_id = entry.clientId;
  const e: AuditEvent = {
    schema_version: AUDIT_SCHEMA_VERSION,
    event_id: uuidv7(now.getTime()),
    occurred_at: now.toISOString(),
    source: { app_id: config.appId, app_version: config.appVersion, environment: config.environment },
  };
  if (entry.tenant) e.tenant = entry.tenant.orgPath ? { org_id: entry.tenant.orgId, org_path: entry.tenant.orgPath } : { org_id: entry.tenant.orgId };
  e.actor = actor;
  e.action = entry.action;
  e.category = entry.category;
  e.target = entry.target.display ? { type: entry.target.type, id: entry.target.id, display: entry.target.display } : { type: entry.target.type, id: entry.target.id };
  e.outcome = entry.outcome ?? "success";
  if (entry.reason) e.reason = entry.reason;
  if (entry.signature) e.signature = { token_id: entry.signature.tokenId, meaning: entry.signature.meaning, record_hash: entry.signature.recordHash };
  if (entry.changes && entry.changes.length > 0) {
    const sensitive = new Set(entry.sensitive ?? []);
    e.changes = entry.changes.map((c) => (sensitive.has(c.field) ? { field: c.field, before: MASKED, after: MASKED } : { field: c.field, before: c.before ?? null, after: c.after ?? null }));
  }
  if (request.ip) e.client = request.userAgent ? { ip: request.ip, user_agent: request.userAgent } : { ip: request.ip };
  if (entry.correlationId) e.correlation_id = entry.correlationId;
  e.data_classification = entry.dataClassification ?? "internal";
  return e;
}

const ENVIRONMENTS = ["production", "staging", "development"];
const ACTORS = ["user", "admin", "api", "system", "anonymous"];
const CATEGORIES = ["access", "create", "update", "delete", "approve", "sign", "export", "admin", "security"];
const OUTCOMES = ["success", "failure", "denied"];
const CLASSES = ["public", "internal", "personal", "sensitive-personal", "health"];
const UUID7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const RFC3339 = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$/;
const ACTION = /^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*){2,}$/;

const obj = (v: unknown): Record<string, unknown> | undefined => (typeof v === "object" && v !== null && !Array.isArray(v) ? (v as Record<string, unknown>) : undefined);
const str = (v: unknown): string | undefined => (typeof v === "string" ? v : undefined);
const filled = (v: unknown): boolean => typeof v === "string" && v.length > 0;

/** Checks an event against schema 1.0 (the same rules as audit-event-1.0.schema.json); returns the problems. */
export function validateAuditEvent(e: unknown): string[] {
  const o = obj(e);
  if (!o) return ["not an object"];
  const problems: string[] = [];
  if (!/^1\.[0-9]+$/.test(str(o.schema_version) ?? "")) problems.push("schema_version must be 1.x");
  if (!UUID7.test(str(o.event_id) ?? "")) problems.push("event_id must be a UUID version 7");
  if (!RFC3339.test(str(o.occurred_at) ?? "")) problems.push("occurred_at must be RFC 3339");
  const source = obj(o.source);
  if (!source || !filled(source.app_id) || !filled(source.app_version) || !ENVIRONMENTS.includes(str(source.environment) ?? "")) problems.push("source needs app_id, app_version and an environment");
  if (o.tenant !== undefined) {
    const t = obj(o.tenant);
    const path = t?.org_path;
    if (!t || !UUID.test(str(t.org_id) ?? "") || (path !== undefined && !(typeof path === "string" && path.startsWith("/") && path.endsWith("/")))) problems.push("tenant needs a UUID org_id and an org_path that starts and ends with /");
  }
  const actor = obj(o.actor);
  const type = str(actor?.type);
  if (!actor || !ACTORS.includes(type ?? "")) {
    problems.push("actor.type must be user, admin, api, system or anonymous");
  } else {
    const sub = actor.sub;
    if ((type === "user" || type === "admin") && !filled(sub)) problems.push("a person's event needs actor.sub");
    if (typeof sub === "string" && sub.includes("@")) problems.push("actor.sub is the Sangam subject id, never an e-mail address");
    if (type === "api" && !filled(actor.client_id)) problems.push("an api event needs actor.client_id");
  }
  if (!ACTION.test(str(o.action) ?? "")) problems.push("action must be domain.object.verb in lowercase");
  const category = str(o.category) ?? "";
  if (!CATEGORIES.includes(category)) problems.push("category is not one of the nine");
  const target = obj(o.target);
  if (!target || !filled(target.type) || !filled(target.id)) problems.push("target needs type and id");
  if (!OUTCOMES.includes(str(o.outcome) ?? "")) problems.push("outcome must be success, failure or denied");
  if (category === "sign") {
    const s = obj(o.signature);
    if (!s || !filled(s.token_id) || !filled(s.meaning) || !filled(s.record_hash)) problems.push("a sign event needs signature.token_id, meaning and record_hash");
  }
  if (o.changes !== undefined && (!Array.isArray(o.changes) || o.changes.some((c) => !filled(obj(c)?.field)))) problems.push("each change needs its field");
  if ((type === "user" || type === "admin") && !filled(obj(o.client)?.ip)) problems.push("a person's event needs client.ip");
  if (!CLASSES.includes(str(o.data_classification) ?? "")) problems.push("data_classification is not one of the five");
  for (const f of ["recorded_at", "prev_hash", "hash"]) if (f in o) problems.push(`${f} is set by the audit service, never by the application`);
  return problems;
}
