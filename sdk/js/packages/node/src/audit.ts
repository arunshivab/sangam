import { appendFile, mkdir, readFile, rename, writeFile } from "node:fs/promises";
import { dirname } from "node:path";
import type { IncomingMessage } from "node:http";
import { buildAuditEvent, validateAuditEvent, type AuditConfig, type AuditEntry, type AuditEvent, type SangamUser } from "@sangam/client";

export interface AuditOptions extends AuditConfig {
  /** A durable JSON Lines file where events wait for the audit service (SGM-208 §7). */
  bufferPath?: string;
  /** The audit service's POST /v1/events address; leave empty until it exists. */
  endpoint?: string;
}

/** The client address: the socket's, or the first X-Forwarded-For hop when you trust a proxy. */
export function clientIp(req: IncomingMessage, trustProxy: boolean): string | undefined {
  const forwarded = req.headers["x-forwarded-for"];
  if (trustProxy && typeof forwarded === "string" && forwarded.length > 0) return forwarded.split(",")[0]!.trim();
  return req.socket.remoteAddress ?? undefined;
}

/**
 * The shared audit helper for Node: builds the event from the signed-in person and the request, refuses one that breaks
 * the schema, and appends it to the buffer. `flush` sends buffered events to the audit service in batches of 500.
 */
export class AuditRecorder {
  readonly #options: AuditOptions;
  readonly #path: string;
  #queue: Promise<unknown> = Promise.resolve();

  constructor(options: AuditOptions, readonly trustProxy = false) {
    this.#options = options;
    this.#path = options.bufferPath ?? "sangam-audit/pending.jsonl";
  }

  get bufferPath(): string {
    return this.#path;
  }

  async record(req: IncomingMessage | undefined, user: SangamUser | undefined, entry: AuditEntry): Promise<AuditEvent> {
    const e = buildAuditEvent(this.#options, user, req ? { ip: clientIp(req, this.trustProxy), userAgent: req.headers["user-agent"] } : {}, entry);
    const problems = validateAuditEvent(e);
    if (problems.length > 0) throw new Error(`The audit event breaks the shared schema: ${problems.join("; ")}`);
    await this.#serial(async () => {
      await mkdir(dirname(this.#path), { recursive: true });
      await appendFile(this.#path, JSON.stringify(e) + "\n", "utf8");
    });
    return e;
  }

  /** Buffered events, oldest first. */
  async pending(): Promise<AuditEvent[]> {
    try {
      return (await readFile(this.#path, "utf8")).split("\n").filter((l) => l.length > 0).map((l) => JSON.parse(l) as AuditEvent);
    } catch {
      return [];
    }
  }

  /** Sends up to 500 buffered events with a token for audit.write; keeps them when the service refuses or is away. */
  async flush(token: () => Promise<string>): Promise<number> {
    if (!this.#options.endpoint) return 0;
    const events = await this.pending();
    const batch = events.slice(0, 500);
    if (batch.length === 0) return 0;
    const response = await fetch(this.#options.endpoint, { method: "POST", headers: { authorization: `Bearer ${await token()}`, "content-type": "application/json" }, body: JSON.stringify({ events: batch }) });
    if (!response.ok) return 0;
    await this.#serial(async () => {
      const temporary = this.#path + ".tmp";
      await writeFile(temporary, events.slice(500).map((e) => JSON.stringify(e) + "\n").join(""), "utf8");
      await rename(temporary, this.#path);
    });
    return batch.length;
  }

  #serial<T>(work: () => Promise<T>): Promise<T> {
    const next = this.#queue.then(work, work);
    this.#queue = next.catch(() => undefined);
    return next;
  }
}
