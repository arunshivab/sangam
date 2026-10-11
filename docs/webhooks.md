# Webhooks (PR-24, SGM-217)

Sangam tells an application when something changes by posting a signed event to an https address the application
registered on the partner console (*your application → Webhooks*). An event says *what* changed and carries ids, never
personal data; the application then reads what it is entitled to through tokens or the management API.

## Events

| Type | When |
|---|---|
| `user.created` | A person linked the application (consented to it, or was given a role in it) |
| `user.updated` | A person's name, e-mail or mobile changed |
| `user.deactivated` | Suspended, deletion requested or completed, or their last role in the application revoked |
| `user.reactivated` | A suspended account was reinstated |
| `membership.granted` | A role given at an organisation (or its descendants) |
| `membership.revoked` | A role taken away, or a time-limited one expired (PR-25) |
| `session.revoked` | A session in which the person used the application was ended: end your own session |
| `consent.revoked` | The person withdrew their consent to the application |
| `role.changed` | A role created, changed or retired |
| `account.merged` | rc.7: the person merged this account into another of theirs; `data.merged_into` is the surviving `sub` |
| `ping` | *Send a test* on the partner console |

An endpoint receives only the types it chose (`ping` goes only to the endpoint it was sent for). An endpoint added
before rc.7 does not have `account.merged` yet: tick it on the partner console (new endpoints have it ticked).

### `account.merged` (rc.7)

A person who held two Sangam accounts merged them (SGM-914 section 9). The event is sent once to every application the
closed account was connected to, with `subject.sub` the closed account and `data.merged_into` the account that stays:

```json
{"id":"evt_…","type":"account.merged","created_at":"2026-10-14T05:00:00Z","app_id":"lipi-his",
 "subject":{"sub":"019…closed"},"data":{"merged_into":"019…survivor"}}
```

Move what you keep under the closed id to the surviving one, keeping the surviving id's own record where you have both.
The closed id never signs in again. A `user.updated` for the surviving id follows (with `data.merged_from`), and the
surviving id may hold roles the closed one had. An application that missed the event can ask
`GET /api/v1/users/{closed id}/merged` (every SDK has a `mergedInto` call); it answers only for ids the application knew.

## The message

```http
POST /sangam/events HTTP/1.1
Content-Type: application/json; charset=utf-8
User-Agent: Sangam-Webhooks/1
webhook-id: evt_01929f3c8a3b7c1d9e2f3a4b5c6d7e8f
webhook-timestamp: 1791359472
webhook-signature: v1,<base64 HMAC-SHA256, see below>

{"id":"evt_01929f3c8a3b7c1d9e2f3a4b5c6d7e8f","type":"membership.granted","created_at":"2026-10-06T09:41:12Z",
 "app_id":"lipi-his","tenant":{"org_id":"…","org_path":"/…/"},"subject":{"sub":"019…"},
 "data":{"org_id":"…","role":"nurse","applies_to_descendants":true}}
```

`subject.sub` is the person's Sangam id (the `sub` of their tokens). `tenant` is present when the event is about an
organisation.

## Checking the signature (do it on every message)

Sangam follows [Standard Webhooks](https://www.standardwebhooks.com/): the signature is `v1,` followed by the base64
HMAC-SHA256 of `webhook-id + "." + webhook-timestamp + "." + body`, keyed with the bytes of your secret (the part after
`whsec_`, base64-decoded). After a secret rotation, for 24 hours the header carries two signatures separated by a space;
accept the message if either matches. Then:

- reject a `webhook-timestamp` more than five minutes from your clock;
- remember `webhook-id`s for at least a day and ignore one you have seen (retries and *Send again* reuse the id);
- answer `2xx` quickly, and do the work afterwards.

Any Standard Webhooks library verifies Sangam's messages. The published test vector, which Sangam's own tests check:

```
secret     whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw
webhook-id msg_p5jXN8AQM9LWM0D4loKWxJek
timestamp  1614265330
body       {"test": 2432232314}
signature  v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=
```

C#:

```csharp
static bool Verify(string id, string timestamp, string signatures, string body, string secret)
{
    if (!long.TryParse(timestamp, out long t) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t) > 300) return false;
    byte[] key = Convert.FromBase64String(secret["whsec_".Length..]);
    string expected = "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));
    return signatures.Split(' ').Any(s => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s), Encoding.ASCII.GetBytes(expected)));
}
```

Node.js:

```js
const crypto = require("crypto");
function verify(id, timestamp, signatures, body, secret) {
  if (Math.abs(Date.now() / 1000 - Number(timestamp)) > 300) return false;
  const key = Buffer.from(secret.slice("whsec_".length), "base64");
  const expected = "v1," + crypto.createHmac("sha256", key).update(`${id}.${timestamp}.${body}`).digest("base64");
  return signatures.split(" ").some(s => s.length === expected.length && crypto.timingSafeEqual(Buffer.from(s), Buffer.from(expected)));
}
```

Python:

```python
import base64, hashlib, hmac, time
def verify(id, timestamp, signatures, body, secret):
    if abs(time.time() - int(timestamp)) > 300:
        return False
    key = base64.b64decode(secret[len("whsec_"):])
    expected = "v1," + base64.b64encode(hmac.new(key, f"{id}.{timestamp}.{body}".encode(), hashlib.sha256).digest()).decode()
    return any(hmac.compare_digest(s, expected) for s in signatures.split(" "))
```

Use the body exactly as received, before any JSON parsing.

## Retries and the log

A delivery that does not get a `2xx` within 15 seconds is tried again after 1 min, 5 min, 30 min, 2 h, 6 h and 12 h —
about a day. Then it gives up, the endpoint is marked *failing* and the application's owners are e-mailed. Every attempt
is in the delivery log on the partner console with its status code, time taken, the start of your answer and the body
sent; *Send again* repeats any finished delivery with the same `webhook-id`. Delivery is in order of the events, but a
retry can arrive after a later event: make handling idempotent and read current state rather than trusting the order.

## Secrets

An endpoint's secret is shown once, when it is added or when *New secret* is pressed, and is stored encrypted. After
*New secret* both secrets sign every message for 24 hours, so the receiver can be switched over without losing
messages. Addresses must be https and may not be on a private network (see `provisioning.md`).
