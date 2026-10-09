# Sangam SDKs (R6)

One SDK per platform, the same concepts and helper names in each (SGM-306), so every application signs people in,
checks permissions, steps up and audits the same way.

| Platform | Package | Where | Sample (port) |
|---|---|---|---|
| .NET | `Sangam.Client` | `src/Sangam.Client` | `samples/Sangam.Sample.AspNetCore` (5940); imagiQa (`samples/Imagiqa.Web`) |
| JavaScript / TypeScript | `@sangam/client`, `@sangam/node`, `@sangam/react` | `sdk/js` | `sdk/js/samples/react-node` (5910) |
| Python | `sangam-client` (FastAPI, Flask) | `sdk/python` | `sdk/python/samples/fastapi` (5920) |
| Java | `in.sangamid:sangam-client`, `in.sangamid:sangam-spring` | `sdk/java` | `sdk/java/samples/spring-boot` (5930) |

## The rules every SDK shares

- **Permissions**: a permission applies at an organisation when a membership grants it there, or at an ancestor
  whose membership inherits. Deny by default. Organisation paths end with `/` and compare case-insensitively; roles
  and permissions are case-sensitive. Malformed `sangam_orgs` entries are skipped, never fatal.
- **Step-up** (SGM-207): `urn:sangam:acr:1` < `:2` = `:sign` < `:3`; a level is met by itself or a stronger one,
  within `max_age` when given; the signature level always within five minutes. APIs answer with the RFC 9470
  challenge (`insufficient_user_authentication`, `acr_values`, `max_age`).
- **Tokens stay on the server**: authorization code with PKCE exchanged by your back end; the browser holds a
  session cookie only (React talks to its Node back end).
- **Webhooks**: Standard Webhooks signatures, five-minute tolerance, rotation (two signatures) accepted.
- **Audit** (SGM-208): events in schema 1.0 — `schema/audit-event-1.0.schema.json` — with source, tenant, actor and
  client filled in by the helper, sensitive changes masked, UUID v7 ids; checked before they are kept, buffered in a
  JSON Lines file until the audit service exists, then sent in batches of 500 with an `audit.write` token.

## Conformance

- `conformance/vectors.json`: permissions, step-up, the challenge header, webhook signatures, audit validation and
  the audit builder. Every SDK's test suite runs all of them (`dotnet test tests/Sangam.Client.Tests`,
  `npm test` in `sdk/js`, `pytest` in `sdk/python`, `mvn test` in `sdk/java`); the Python suite also checks the
  JSON Schema against the vectors.
- `conformance/live/check_samples.py`: drives each sample in a real browser against a development Sangam — sign in,
  see the organisation, a permission allowed where the role is and denied elsewhere, a signature that makes Sangam
  ask for a fresh two-factor sign-in, and the shared audit event checked against the JSON Schema.

## Running the samples against a development Sangam

Start the identity server (`dotnet run --project src/Sangam.Identity.Server`, http://localhost:5100). Its development
seed registers the redirect addresses `http://localhost:59x0/auth/callback` for the sample application
`sangam-dev-sample`, which every sample signs in as. Then:

```sh
cd sdk/js && npm ci && npm run build && npm start -w sangam-sample-react-node        # 5910
cd sdk/python && pip install -e ".[fastapi]" uvicorn && cd samples/fastapi && uvicorn main:app --port 5920
cd sdk/java && mvn -q package -DskipTests && java -jar samples/spring-boot/target/sangam-sample-spring-boot-1.0.0-rc.4.jar   # 5930
dotnet run --project samples/Sangam.Sample.AspNetCore --urls http://localhost:5940
python3 sdk/conformance/live/check_samples.py out http://localhost:5910 http://localhost:5920 http://localhost:5930 http://localhost:5940
```

The live check gives its test person a nurse role at "Sample Hospital (made up)" through the management API and an
authenticator through the development database; it is for development only.

## Publishing

Not yet published to npm, PyPI or Maven Central; the packages build from here (`npm pack`, `python -m build`,
`mvn package`). Publishing needs the `@sangam` npm scope, the `sangam-client` PyPI name and the `in.sangamid` Maven
namespace (OI-048).
