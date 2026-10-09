import { test } from "node:test";
import assert from "node:assert/strict";
import type { IncomingMessage } from "node:http";
import { cookies } from "../src/session.js";

// rc.3 (CodeQL js/remote-property-injection): cookie names come from the browser, so they must never reach a prototype.
test("cookies: a cookie named __proto__ or constructor is just a value, and a malformed one is skipped", () => {
  const req = { headers: { cookie: "__proto__=x; constructor=y; sangam.sid=abc%20def; broken=%E0%A4%A; empty" } } as IncomingMessage;
  const jar = cookies(req);

  assert.equal(jar["sangam.sid"], "abc def");
  assert.equal(Object.getPrototypeOf(jar), Object.prototype);
  assert.equal(({} as Record<string, unknown>)["x"], undefined);
  assert.equal(Object.hasOwn(jar, "__proto__"), true);
  assert.equal(jar["constructor"], "y");
  assert.equal(Object.hasOwn(jar, "broken"), false);
  assert.equal(Object.hasOwn(jar, "empty"), false);
});
