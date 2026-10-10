import { createHmac, randomBytes, timingSafeEqual } from "node:crypto";
import type { IncomingMessage, ServerResponse } from "node:http";

/** Where sessions live. The default keeps them in memory; give a shared store when you run more than one process. */
export interface SessionStore<T> {
  get(id: string): Promise<T | undefined> | T | undefined;
  set(id: string, value: T, ttlSeconds: number): Promise<void> | void;
  delete(id: string): Promise<void> | void;
}

/** An in-memory store with expiry: for one process and for development. */
export class MemorySessionStore<T> implements SessionStore<T> {
  readonly #items = new Map<string, { value: T; expires: number }>();
  get(id: string): T | undefined {
    const item = this.#items.get(id);
    if (!item) return undefined;
    if (item.expires < Date.now()) {
      this.#items.delete(id);
      return undefined;
    }
    return item.value;
  }
  set(id: string, value: T, ttlSeconds: number): void {
    this.#items.set(id, { value, expires: Date.now() + ttlSeconds * 1000 });
  }
  delete(id: string): void {
    this.#items.delete(id);
  }
}

export function cookies(req: IncomingMessage): Record<string, string> {
  // A Map, not a plain object, while parsing: a cookie named "__proto__" or "constructor" cannot reach a prototype.
  const out = new Map<string, string>();
  for (const part of (req.headers.cookie ?? "").split(";")) {
    const i = part.indexOf("=");
    if (i <= 0) continue;
    try {
      out.set(part.slice(0, i).trim(), decodeURIComponent(part.slice(i + 1).trim()));
    } catch {
      // A malformed %-escape: skip that cookie rather than fail the request.
    }
  }
  return Object.fromEntries(out);
}

/** A session id, signed so a forged cookie is ignored. */
export function signId(id: string, secret: string): string {
  return `${id}.${createHmac("sha256", secret).update(id).digest("base64url")}`;
}

export function unsignId(value: string | undefined, secret: string): string | undefined {
  if (!value) return undefined;
  const i = value.lastIndexOf(".");
  if (i <= 0) return undefined;
  const id = value.slice(0, i);
  const expected = Buffer.from(signId(id, secret));
  const given = Buffer.from(value);
  return expected.length === given.length && timingSafeEqual(expected, given) ? id : undefined;
}

export function newId(): string {
  return randomBytes(24).toString("base64url");
}

/** The session cookie's name: over https it carries the `__Host-` prefix (rc.5, ASVS V3.4.4), so only this exact host can set it. */
export function sessionCookieName(secure: boolean): string {
  return secure ? "__Host-sangam.sid" : "sangam.sid";
}

export function setCookie(res: ServerResponse, name: string, value: string, options: { maxAge?: number; secure: boolean; path?: string }): void {
  const parts = [`${name}=${encodeURIComponent(value)}`, `Path=${options.path ?? "/"}`, "HttpOnly", "SameSite=Lax"];
  if (options.secure) parts.push("Secure");
  if (options.maxAge !== undefined) parts.push(`Max-Age=${options.maxAge}`);
  const existing = res.getHeader("set-cookie");
  const list = Array.isArray(existing) ? existing.map(String) : existing ? [String(existing)] : [];
  res.setHeader("set-cookie", [...list, parts.join("; ")]);
}
