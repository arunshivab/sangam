# Sangam → Anjal messaging contract (proposed)

Founder's decisions **D-B** and **D-M** (7 October 2026): Anjal is Sangam's single messaging gateway. It sends
**all** of Sangam's e-mail from day one, through **Anjal's API** (not SMTP), and — once DLT is registered under the
new company — its SMS, which Anjal hands to Indian aggregators with failover between two of them.

This page is the contract Sangam's sender is built to. Anjal's API details follow Anjal's own testing; every part
that may differ is a setting under `Sangam:Anjal`, so a change on Anjal's side is a configuration change here.

## The two relationships

- **Sangam → Anjal is machine-to-machine.** Sangam presents an API key; there is no user and no sign-in.
- **Anjal → Sangam is an ordinary relying party.** Anjal webmail users sign in with SangamID like any partner
  application's users. Whether an Anjal mailbox maps to a SangamID e-mail address (OI-029) is decided later.

## Settings (`Sangam:Anjal`)

| Setting | Default | Meaning |
|---|---|---|
| `BaseUrl` | — (required in production) | Anjal's API base address. `https://…`, or `http://anjal:8080/` on the server's own Docker network. |
| `ApiKey` | — (secret file `Sangam__Anjal__ApiKey`) | At least 20 characters. Every host needs it: they all send e-mail. |
| `ApiKeyHeader` / `ApiKeyScheme` | `Authorization` / `Bearer` | How the key is presented. For a bare key header: `X-Api-Key` and an empty scheme. |
| `EmailPath` / `SmsPath` | `api/v1/messages/email` / `api/v1/messages/sms` | Endpoints, relative to `BaseUrl`. |
| `FromAddress` / `FromName` | `no-reply@sangamid.in` / `SangamID` | The sender. SPF, DKIM (Anjal's selector) and DMARC must pass: `scripts/check-mail-dns.ps1`. |
| `AllowedRecipients` | empty | **Staging allowlist**: addresses, `@domains` and `+91…` numbers. Anything else is refused before it leaves. Empty in production. |
| `TimeoutSeconds` | 15 | One HTTP attempt. |
| `EmailAttempts` / `SmsAttempts` | 5 / 2 | Attempts per message (first plus retries). E-mail goes in the background; SMS while the person waits. |
| `RetryDelay` / `MaxRetryDelay` | 1 s / 30 s | First pause, doubling with jitter, capped; `Retry-After` is honoured up to the cap. |

`Sangam:Email:DeliverInBackground` (default `true`) hands e-mail to Anjal from a background queue, so a page never
waits for Anjal and a response takes the same time whether or not a message was sent (D-L).

## Requests

Both are `POST`, `Content-Type: application/json`, with:

- `Authorization: Bearer <api key>` (or the configured header);
- `Idempotency-Key: <32 hex>` — the **same** on every retry of one message. Anjal should send a message once per key.

### E-mail — `POST {BaseUrl}/api/v1/messages/email`

```json
{
  "from": { "address": "no-reply@sangamid.in", "name": "SangamID" },
  "to":   { "address": "person@example.in", "name": "Kavya Iyer" },
  "subject": "123456 is your Sangam verification code",
  "text": "…plain text…",
  "html": "…HTML… (omitted when there is none)"
}
```

### SMS — `POST {BaseUrl}/api/v1/messages/sms`

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
own per-number and per-IP hourly limits; Anjal chooses the aggregator and fails over.

## Answers

| Anjal answers | Sangam does |
|---|---|
| `2xx` with `{"id": "…"}` | Accepted. The id is kept (`sms_messages.provider_message_id` for SMS). |
| `408`, `429`, `5xx`, timeout, connection error | Retries with the same idempotency key, up to the attempt limit. |
| any other `4xx` | Final: not retried. Counted as a failure. |

Failures are counted (`Sangam.Messaging` meter: `sangam.email.sent/failed`, `sangam.sms.sent/failed`) for the
monitoring page and its alerts (D-H). Logs carry a masked recipient and the outcome only — never a subject, body,
text or the key — because messages carry one-time codes (OI-038).

## Delivery reports (SMS)

Unchanged from PR-15: Anjal may post delivery reports to Sangam's webhook with the shared
`Sangam:Sms:DeliveryReportToken`, using provider name `anjal` and its message id.

## Go-live dependency (D-M)

SMS stays **off** (`Sangam__Sms__Enabled=false`) until DLT registration (entity, header `SANGAM`, every template in
every language) is done under the **new company**, which is not yet formed. E-mail codes, passkeys and
authenticator apps work without it.
