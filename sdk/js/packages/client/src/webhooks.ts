const SECRET_PREFIX = "whsec_";
/** How far a webhook's timestamp may be from the clock, in seconds. */
export const WEBHOOK_TOLERANCE_SECONDS = 300;

export interface WebhookMessage {
  /** The `webhook-id` header. */
  id: string | null | undefined;
  /** The `webhook-timestamp` header. */
  timestamp: string | null | undefined;
  /** The `webhook-signature` header: one or more space-separated `v1,` signatures. */
  signature: string | null | undefined;
  /** The body exactly as received, before any JSON parsing. */
  body: string;
}

function base64(bytes: ArrayBuffer): string {
  let s = "";
  for (const b of new Uint8Array(bytes)) s += String.fromCharCode(b);
  return btoa(s);
}

function equal(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

/**
 * Verifies a Sangam webhook (Standard Webhooks, SGM-217): HMAC-SHA256 of `id.timestamp.body` under the secret's bytes,
 * and a timestamp within five minutes. Works in browsers and Node (Web Crypto).
 */
export async function verifyWebhook(message: WebhookMessage, secret: string, now: number = Math.floor(Date.now() / 1000)): Promise<boolean> {
  const { id, timestamp, signature, body } = message;
  if (!id || !signature || !timestamp || !/^\d+$/.test(timestamp) || !secret.startsWith(SECRET_PREFIX)) return false;
  if (Math.abs(now - Number(timestamp)) > WEBHOOK_TOLERANCE_SECONDS) return false;
  let keyBytes: Uint8Array<ArrayBuffer>;
  try {
    keyBytes = Uint8Array.from(atob(secret.slice(SECRET_PREFIX.length)), (c) => c.charCodeAt(0));
  } catch {
    return false;
  }
  const key = await crypto.subtle.importKey("raw", keyBytes, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(`${id}.${timestamp}.${body}`));
  const expected = "v1," + base64(mac);
  return signature.split(" ").filter((s) => s.length > 0).some((s) => equal(s, expected));
}
