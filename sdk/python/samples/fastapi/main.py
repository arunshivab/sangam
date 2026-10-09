"""Sample: FastAPI signing in with Sangam. Sign-in, a permission check, step-up for a signature, a shared audit event.

    pip install -e "sdk/python[fastapi]" uvicorn
    uvicorn main:app --port 5920        (from sdk/python/samples/fastapi)

Settings: SANGAM_AUTHORITY, SANGAM_CLIENT_ID, SANGAM_CLIENT_SECRET, BASE_URL, COOKIE_SECRET, AUDIT_BUFFER.
"""
import html
import os

from fastapi import Depends, FastAPI, Request
from fastapi.responses import HTMLResponse

from sangam import AuditConfig, AuditEntry, SangamAcr, SangamSettings, SangamUser, has_permission
from sangam.fastapi import SangamFastAPI

PORT = int(os.environ.get("PORT", "5920"))
sangam = SangamFastAPI(
    SangamSettings(
        authority=os.environ.get("SANGAM_AUTHORITY", "http://localhost:5100"),
        client_id=os.environ.get("SANGAM_CLIENT_ID", "sangam-dev-sample"),
        client_secret=os.environ.get("SANGAM_CLIENT_SECRET", "sangam-dev-sample-secret-change-me"),
        base_url=os.environ.get("BASE_URL", f"http://localhost:{PORT}"),
        cookie_secret=os.environ.get("COOKIE_SECRET", "sample-only-cookie-secret-change-me-0123456789"),
        audit=AuditConfig("sangam-dev-sample", "1.0.0rc3", "development"),
        audit_buffer=os.environ.get("AUDIT_BUFFER", "sangam-audit/pending.jsonl"),
    )
)
app = FastAPI(title="Sangam sample (Python)")
sangam.install(app)

with open(os.path.join(os.path.dirname(__file__), "page.html"), encoding="utf-8") as _page:
    PAGE = _page.read()


@app.get("/", response_class=HTMLResponse)
def home(user: SangamUser | None = Depends(sangam.current_user)) -> str:
    if user is None:
        body = '<section><h1>Sign in with Sangam</h1><p class="muted">This sample keeps every token on its FastAPI server; the browser only holds a session cookie.</p><a class="button" href="/auth/login?returnTo=/">Sign in with Sangam</a></section>'
        return PAGE.replace("{{user}}", "").replace("{{body}}", body)
    e = html.escape
    memberships = "".join(f"<li><b>{e(m.role)}</b> at {e(m.organisation_name)} <code>{e(m.path)}</code>{' (and below)' if m.applies_to_descendants else ''}: {e(', '.join(m.permissions))}</li>" for m in user.memberships)
    options = "".join(f'<option value="{e(m.path)}">{e(m.organisation_name)}</option>' for m in user.memberships)
    body = f"""
<section><h1>Signed in as {e(user.name)}</h1><p class="muted">Sangam id <code>{e(user.id)}</code> · {e(user.acr or '')}</p></section>
<section data-panel="organisations"><h2>Your organisations in this application</h2><ul>{memberships or '<li class="muted">No role yet.</li>'}</ul></section>
<section data-panel="permission"><h2>Check a permission</h2>
  <div class="row"><select id="org" aria-label="Organisation">{options}<option value="/0192a6b0-0000-7000-8000-00000000ffff/">An organisation you have no role in</option></select>
  <input id="permission" value="vitals:read" aria-label="Permission"><button class="ghost" id="ask">Ask the server</button></div>
  <p id="answer"></p></section>
<section data-panel="signature"><h2>Sign record SOP-114</h2>
  <p class="muted">Signing needs a two-factor sign-in from the last five minutes ({SangamAcr.SIGNATURE}). {'Yours qualifies.' if sangam.core.stepped_up(user, SangamAcr.SIGNATURE, None) else 'Sangam will ask you to sign in again first.'}</p>
  <button data-action="sign" id="sign">Sign as approved</button><div id="signed"></div></section>"""
    return PAGE.replace("{{user}}", f'<span class="row">{e(user.name)} <a class="button ghost" href="/auth/logout">Sign out</a></span>').replace("{{body}}", body)


@app.get("/api/check")
def check(org: str, permission: str, user: SangamUser = Depends(sangam.require_user())) -> dict:
    return {"allowed": has_permission(user, org, permission)}


@app.post("/api/records/{record_id}/sign")
def sign(record_id: str, request: Request, user: SangamUser = Depends(sangam.require_step_up(SangamAcr.SIGNATURE, return_to=lambda r: "/?resume=sign"))) -> dict:
    m = user.memberships[0] if user.memberships else None
    event = sangam.record(request, AuditEntry("sample.record.sign", "sign", "record", record_id, target_display=f"Record {record_id}",
                                              signature=(f"sig-{record_id}", "Approved", "sha256:" + "0" * 64),
                                              org_id=m.organisation_id if m else None, org_path=m.path if m else None), user)
    return {"signed": record_id, "event": event}
