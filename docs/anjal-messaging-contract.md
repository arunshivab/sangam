# Sangam → Anjal messaging contract

Founder's decisions **D-B** and **D-M** (7 October 2026): Anjal is Sangam's messaging gateway. It sends **all** of
Sangam's e-mail from day one, through **Anjal's API** (not SMTP).

**Confirmed by Anjal on 13 November 2026** (Anjal's write-up `claude/sangam-contract-answers-2026-11-13.md` in the
Anjal project): the API below is live on `https://mail.anjal.co.in` and accepts Sangam's requests as they are. The
differences it found (M1 to M10) were decided by the founder on 13 November 2026 and are built into rc.17; they are
noted where they apply. Every part that may still differ is a setting under `Sangam:Anjal`, so a change on Anjal's
side is a configuration change here.

**SMS (D-182, 13 November 2026): off for now, returning later.** Sangam keeps every person's mobile number, and the
SMS code stays in Sangam switched off (`Sangam__Sms__Enabled=false`). Whether Anjal sends SMS when it returns, or Sangam
talks to an SMS provider directly, is decided then. The SMS part of this page is kept as the design, not as an agreed
Anjal API.

## The two relationships

- **Sangam → Anjal is machine-to-machine.** Sangam presents an API key and, from rc.17, a request signature; there is
  no user and no sign-in.
- **Anjal → Sangam is an ordinary relying party.** Anjal webmail users could sign in with SangamID like any partner
  application's users. Not built on either side yet; whether an Anjal mailbox maps to a SangamID e-mail address
  (OI-029) is decided later.

## Settings (`Sangam:Anjal`)

| Setting | Default | Meaning |
|---|---|---|
| `BaseUrl` | — (required in production) | Anjal's API base address: `https://mail.anjal.co.in/` (M6; Anjal runs as system services, not on a Docker network). |
| `ApiKey` | — (secret file `Sangam__Anjal__ApiKey`) | Anjal's live key, `ak_live_` and 32 characters. Every host needs it: they all send e-mail. |
| `SigningKey` | — (secret file `Sangam__Anjal__SigningKey`, `docker-compose.anjal-signing.yml`) | rc.17 (M1 (b)): the P-256 private key in PEM that signs every request. Unset: the API key only (the pilot). Set before the first customer. |
| `ApiKeyHeader` / `ApiKeyScheme` | `Authorization` / `Bearer` | How the key is presented (Anjal also accepts `X-Api-Key` with an empty scheme). |
| `EmailPath` / `SmsPath` | `api/v1/messages/email` / `api/v1/messages/sms` | Endpoints, relative to `BaseUrl`. |
| `FromAddress` / `FromName` | `no-reply@sangamid.in` / `SangamID` | The sender, an allowed sender of the Sangam app in Anjal. SPF, DKIM (selector `anjal`) and DMARC must pass: `scripts/check-mail-dns.ps1`. |
| `AllowedRecipients` | empty | **Staging allowlist**: addresses, `@domains` and `+91…` numbers. Anything else is refused before it leaves. Empty in production; set on staging (M5 (a)). |
| `TimeoutSeconds` | 15 | One HTTP attempt. |
| `EmailAttempts` / `SmsAttempts` | 5 / 2 | Attempts per message (first plus retries). E-mail goes in the background; SMS while the person waits. |
| `RetryDelay` / `MaxRetryDelay` | 1 s / 30 s | First pause, doubling with jitter, capped; `Retry-After` is honoured up to the cap. |

`Sangam:Email:DeliverInBackground` (default `true`) hands e-mail to Anjal from a background queue, so a page never
waits for Anjal and a response takes the same time whether or not a message was sent (D-L).

## Requests

Both are `POST`, `Content-Type: application/json; charset=utf-8`, with:

- `Authorization: Bearer <api key>` (or the configured header);
- `Idempotency-Key: <32 hex>` — the **same** on every retry of one message, with the same body bytes. Anjal keeps a key
  for 24 hours and answers a repeat with its first answer, so a retry after a lost answer cannot send twice.
- From rc.17, with a signing key: `Anjal-Key-Id`, `Anjal-Timestamp` and `Anjal-Signature` (below).

### E-mail — `POST {BaseUrl}api/v1/messages/email`

```json
{
  "from": { "address": "no-reply@sangamid.in", "name": "SangamID" },
  "to":   { "address": "person@example.in", "name": "Kavya Iyer" },
  "subject": "123456 is your Sangam verification code",
  "text": "…plain text…",
  "html": "…HTML… (omitted when there is none)"
}
```

### SMS — `POST {BaseUrl}api/v1/messages/sms` (design only; off, D-182)

```json
{
  "to": "+919876543210",
  "header": "SANGAM",
  "dltTemplateId": "1107…",
  "text": "123456 is your SangamID sign-in code. It expires in 10 minutes. Do not share it. - SANGAM",
  "templateKey": "sign_in"
}
```

The text is exactly the DLT-registered text with its variables filled in. Sangam keeps the DLT template ids and its
own per-number and per-IP hourly limits.

## Signing (rc.17, M1 (b))

Exactly as Anjal verifies it (Anjal's spec of 13 November 2026):

- **Signed text:** four lines joined by `\n`, with no trailing newline — the method in upper case (`POST`); the raw
  target, path and query exactly as sent (`/api/v1/messages/email`); the timestamp exactly as sent; the lowercase hex
  SHA-256 of the exact body bytes (the empty-body hash when there is no body).
- **Algorithm:** ECDSA P-256 with SHA-256; the signature in its 64-byte r‖s form (IEEE P1363), base64url without padding.
- **Headers:** `Anjal-Key-Id` (`sk_` and the first 12 hex characters of the SHA-256 of the key's SubjectPublicKeyInfo,
  which Sangam works out from the key, so it cannot drift); `Anjal-Timestamp` (Unix seconds; Anjal allows ±300 s);
  `Anjal-Signature`.
- **The API key is still sent;** the signature adds to it.
- **Every attempt is signed afresh** over the same body bytes, because Anjal remembers each signature for 10 minutes and
  refuses a repeat (`401 signature_replayed`).
- **Refusals are final:** `401 timestamp_out_of_window`, `signature_required`, `signature_invalid` and
  `signature_replayed`, and `403 signing_key_retired`. On a 401 Sangam logs how far its clock is from Anjal's (from
  Anjal's `Date` header), so a drifting clock shows up.
- **Key:** made on the server (`docker-compose.anjal-signing.yml` says how); its public half is pasted into Anjal under
  Organisation settings → Sending systems → Sangam, where Anjal shows the key id. Each host logs the id it signs with
  at start-up; they must match. A host refuses to start when the key is not a P-256 private key.
- **Rotation:** Anjal keeps at most two live keys. Add the new key in Anjal, move Sangam to it (replace the secret file,
  restart the four hosts), check one code arrives, then retire the old key.

## Answers

| Anjal answers | Sangam does |
|---|---|
| `2xx` (Anjal: `202 {"id","status","messageId","createdAt"}`) | Accepted. Only `id` is read (M3); it is kept (`sms_messages.provider_message_id` for SMS). |
| `409 idempotency_in_progress` with `Retry-After: 2` | rc.17 (M2): the first try with this key is still sending; Sangam waits and asks again with the same key. |
| `408`, `429`, `5xx`, timeout, connection error | Retries with the same idempotency key, up to the attempt limit. `429` and `503` carry `Retry-After`. |
| any other `4xx` | Final: not retried. Counted as a failure. |

Failures are counted (`Sangam.Messaging` meter: `sangam.email.sent/failed`, `sangam.sms.sent/failed`) for the
monitoring page and its alerts (D-H). Logs carry a masked recipient and the outcome only — never a subject, body,
text or the key — because messages carry one-time codes (OI-038).

## Limits (M4)

Anjal limits each app (60 a minute, fixed; per hour and per day, raisable to 2,000 and 20,000) and each organisation
(per day, counting every route: webmail, port 587, SMTP keys and the API). Every limit answers `429` with
`Retry-After`. Sangam waits at most 30 seconds between attempts, so an hour or day window would lose messages. Decided:
the Sangam app at 2,000 an hour and 20,000 a day, and the `sangamid` organisation's daily limit at 20,000 — set by the
server operator on Anjal's Ops page. (Open with Anjal: whether the organisation's own admin may raise the app's limits.)

## Health (rc.17, M7 (a))

`Sangam:Monitoring:AnjalHealthUrl` = `https://mail.anjal.co.in/api/v1/health`. Anjal's health endpoint needs Sangam's
key, so the identity server's monitor asks it through the Anjal client, with the key and (when set) a signature over an
empty body; it answers `200` or `503`, which also shows whether Sangam can send. The key is never sent to any other host.

## "Allowed from"

Anjal can pin the Sangam app's key to source addresses (up to 20 lines, CIDR allowed); it checks the TCP source address.
Which address it sees depends on how Sangam's traffic leaves the VM, so it is found, not guessed: leave the field empty,
send one real code, read `app.api.new-address` in the organisation's Activity log, and enter that address (its /16 only
for a Docker bridge address with nothing untrusted on it). Then send one more code.

## Delivery reports (SMS)

Unchanged from PR-15, and off with SMS: Anjal may post delivery reports to Sangam's webhook with the shared
`Sangam:Sms:DeliveryReportToken`, using provider name `anjal` and its message id.

## Bounces

Anjal blocks hard-bouncing addresses itself. Sangam does not read bounces today; Anjal's webhooks or
`GET /api/v1/events` could tell it later (a later decision).
