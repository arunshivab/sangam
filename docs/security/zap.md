# OWASP ZAP scans (R7, SGM-503)

R7 scanned all four Sangam hosts with OWASP ZAP (the `ghcr.io/zaproxy/zaproxy:stable` image) twice: once before the
security fixes and once after. These scans are internal evidence. They do not replace the external penetration test
(see [pentest-scope.md](pentest-scope.md)).

| Host | Port (development) | Scans |
|---|---|---|
| Identity server (`id.`) | 5100 | baseline, authenticated full scan from `/account` |
| Account portal (`account.`) | 5200 | baseline, authenticated full scan |
| Operator console (`admin.`) | 5300 | baseline, authenticated full scan |
| Partner console (`partners.`) | 5400 | baseline, authenticated full scan |

- **Baseline** (`zap-baseline.py`, 2 minutes of spidering, passive rules only): what an anonymous visitor sees.
- **Authenticated full scan** (`zap-full-scan.py`, 3 minutes of spidering, 20 minutes of active scanning per host):
  - The scanner signs in as a dedicated account with an authenticator. That account is a platform owner and an
    application owner, so every console page is reachable.
  - ZAP sends the session cookies of all four hosts with every request.
  - A hook (`hook.py`) excludes only the sign-out and account-deletion paths, so the scan stays signed in. The test
    suite covers those paths.
- The scans run one at a time, with 1.5 GB of Java heap each. Four in parallel ran out of memory on the 8 GB build
  machine.
- The hosts ran in Development, so the policies below include `http://localhost:*`. Production policies do not.

## Results

The raw JSON and HTML reports are in the release evidence, under `security/zap/round1-before-fixes/`,
`security/zap/round2-after-fixes/` and `security/zap/round3-production-container/`.

| Scan | Round 1 (before) | Round 2 (after) |
|---|---|---|
| Baseline 5100 | 0 fail, 8 warn | 0 fail, 6 warn |
| Baseline 5200 / 5300 / 5400 | 0 fail, 4 warn each | 0 fail, 3 / 3 / 2 warn |
| Full 5100 | 0 fail, 1 warn | 0 fail, 1 warn |
| Full 5200 / 5300 / 5400 | 0 fail, 2 / 1 / 1 warn | 0 fail, 2 / 1 / 1 warn |

### Alerts in round 1

- **Medium: Content Security Policy header not set, on all four hosts.** Fixed. Every host now sends a full policy
  (`SecurityHeaders.Policy`).
- **Medium: Missing anti-clickjacking header (identity server).** Fixed with `frame-ancestors 'none'` and
  `X-Frame-Options: DENY` on every response.
- **Low: X-Content-Type-Options header missing, on all four hosts.** Fixed with `nosniff` on every response.
- **Medium: CSP script-src unsafe-inline (portal and consoles).** Fixed. On every rendered Blazor page, Blazor had
  replaced the policy with its own header, which only says `frame-ancestors 'self'`, so ZAP saw no script rule. `ContentSecurityFrameAncestorsPolicy = null` now
  keeps the full policy, and a signed-in page test on each host guards it.
- **Medium: CSP wildcard directive and "failure to define a directive with no fallback" (`form-action`).**
  - Narrowed in R7: the portal and consoles now submit forms only to themselves and the origins they name. That is
    the identity server, plus DigiLocker on the portal.
  - Kept on the identity server: `form-action https:` and `frame-src https:`. Its `form_post` answers, SAML POST
    binding and front-channel logout frames go to whichever application is registered. This is an accepted residual
    risk (SGM-908, R-14).
- **Medium: CSP style-src unsafe-inline.** Kept and accepted (SGM-908, R-14). Blazor and the password strength meter
  set `style` attributes. Script stays strictly `'self'` plus one hash.
- **Low: Cookie with SameSite=None (consoles).** Expected. These are the OpenID Connect correlation and nonce cookies,
  which must survive the cross-site `form_post` back from the identity server. They are short-lived, HttpOnly and
  Secure outside development. The session cookies are `SameSite=Lax`.
- **High (Low confidence): Path traversal, portal `/profile`, parameter `_handler`.** False positive, seen in both
  rounds:
  - ZAP replaced Blazor's form handler name with `profile` and got a different answer.
  - That answer is a 400 error in `text/plain` with `nosniff`. No file content is disclosed, and no path is built
    from the value.
  - It was checked by hand in round 1.
- **Informational:** suspicious comments (framework JavaScript), user-controllable attribute (the `returnUrl` on the
  sign-in form, which is validated as a local URL), user-agent fuzzer, modern web application. None needs action.

### Round 2

Round 2 ran after the header, cookie, session and authentication fixes. It shows no new alert. The remaining alerts
are the accepted CSP items above, the expected SameSite cookies and the path-traversal false positive. ZAP's active
scan found no injection, XSS, CSRF, open redirect or information disclosure on any host.

During the active scan, ZAP changed the scanner account's own password through the account forms. This shows that
the active scan does reach the signed-in forms.

### Round 3: the release image in Production

At the gate, ZAP's baseline scan ran once more against the identity server's release image, started in Production
(`sangam/identity`, built from the R7 code before it was labelled 1.0.0-rc.1; the label changes only the version
number and how the home page shows it). Requests carried `X-Forwarded-Proto: https`, as they do behind Caddy or
the ingress.

- The result was 0 failures and 5 warnings. Two are the accepted CSP items. The other three are informational:
  suspicious comments in framework JavaScript, cookie poisoning, and the authentication and session responses
  identified.
- A first attempt without `X-Forwarded-Proto` showed what happens when a host is reached over plain HTTP in
  Production. Pages with forms answer 500, because the antiforgery cookie must be Secure and the request is not
  HTTPS. This fails closed.
  - On the VM, only Caddy is exposed, and it always sends the header.
  - On Kubernetes, the ingress controller sends it.
  - Answering plain HTTP with a clear 400 instead is OI-057.

## Reproducing

```sh
# hosts in Development on 5100-5400; then sign the scanner account in and write the Cookie header
python3 zap_cookies.py
docker run --rm --network host -e JAVA_OPTS=-Xmx1536m -v "$PWD:/zap/wrk:rw" ghcr.io/zaproxy/zaproxy:stable \
  zap-baseline.py -t http://localhost:5100/ -m 2 -J baseline-5100.json -r baseline-5100.html -I
docker run --rm --network host -e JAVA_OPTS=-Xmx1536m -v "$PWD:/zap/wrk:rw" ghcr.io/zaproxy/zaproxy:stable \
  zap-full-scan.py -t http://localhost:5100/account -m 3 -T 20 -J full-5100.json -r full-5100.html -I \
  --hook=/zap/wrk/hook.py -z "-config replacer.full_list(0).matchtype=REQ_HEADER -config replacer.full_list(0).matchstr=Cookie ..."
```
