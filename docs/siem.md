# Streaming the audit log to a SIEM (R7, PR-32, CAP-084)

Sangam can send every audit event, as it is written, to a security information and event management system (SIEM)
such as Wazuh, Elastic, Splunk, QRadar or Microsoft Sentinel. Streaming is **off by default** and changes nothing until
an operator turns it on. The audit log in PostgreSQL stays the record; the stream is a copy.

## What is sent

One record per audit event, in the order the events were written, in one of two formats:

| `Format` | On the wire | Fields |
|---|---|---|
| `cef` (default) | RFC 5424 syslog with an ArcSight CEF:0 body, framed by octet counting (RFC 5425, syslog over TLS) | `rt`, `externalId` (event id), `act` (action), `cat`, `outcome`, `suid` (person), `duid` (target), `src` (IP), `requestClientApplication`, and custom strings for actor type, application, target type, the event's hash, the previous hash and the detail |
| `json` | One JSON object per line | `{ "schema": "sangam.siem.1", "id", "hash", "prev_hash", "detail", "event" }`, where `event` is the shared audit event schema 1.0 (SGM-208): `identity.<action>`, category, outcome, actor, tenant, client IP and user agent |

- Syslog facility is security/authorization (13), with severity *warning* for failures and *informational* otherwise.
  The CEF severity follows the same split.
- Each record carries the event's hash and the previous event's hash, so the SIEM can check the chain itself.
- Nothing secret is in the audit log, so nothing secret is streamed: no passwords, codes, tokens or client secrets. People are
  identified by id; e-mail addresses in event details are masked.

## Delivery

- One identity-server process streams at a time (a PostgreSQL advisory lock), so a second replica does not duplicate.
- Events are read from the audit table in batches (`BatchSize`, default 200) every `Interval` (default 10 seconds) and
  written to one TCP connection. The position reached is saved after each batch, so a restart resumes where it left off.
- Delivery is at least once: if the connection fails mid-batch, the batch is sent again. The SIEM can de-duplicate on
  the event id.
- If the SIEM is unreachable, events wait in the audit table; nothing is lost. The operator console's Monitoring page
  shows the stream's state (on or off, the last event sent, the last error) in the *SIEM streaming* panel.

## Settings (`Sangam:Siem`)

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Turn streaming on |
| `Host`, `Port` | —, `6514` | The SIEM's syslog or TCP input |
| `Format` | `cef` | `cef` or `json` |
| `Tls` | `true` | TLS 1.2 or 1.3. Plain TCP is refused at start-up outside Development and Testing |
| `ServerName` | `Host` | The name the SIEM's certificate must carry |
| `CaCertificatePath` | — | A PEM or DER CA certificate to trust instead of the system's (a private SIEM CA) |
| `ClientCertificatePath`, `ClientCertificatePassword` | — | A PFX for mutual TLS, if the SIEM asks for a client certificate |
| `BatchSize`, `Interval` | `200`, `00:00:10` | Batch size and polling interval |
| `StartFrom` | `now` | `now` sends only new events; `beginning` sends the whole retained log first |

The identity server refuses to start when streaming is on without a host, with an unknown format or start point, over plain
TCP outside development, or with a certificate file that does not exist.

Example (`deploy/production/.env` or the compose environment):

```
Sangam__Siem__Enabled=true
Sangam__Siem__Host=siem.example.in
Sangam__Siem__Port=6514
Sangam__Siem__Format=cef
Sangam__Siem__CaCertificatePath=/run/secrets/siem-ca.pem
```

## Tested

`tests/Sangam.Identity.Infrastructure.Tests/Siem/SiemTests.cs` runs a TLS receiver with its own CA and checks: CEF and
JSON framing (RFC 5425 octet counts), that every JSON record validates against the shared schema, resume after a restart,
nothing lost while the receiver is down, the connection refused for a certificate from the wrong CA, and the start-up
refusals.
