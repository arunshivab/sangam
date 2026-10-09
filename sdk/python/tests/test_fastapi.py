"""FastAPI: sign-in with PKCE on the server, step-up, permission, token guard and audit, against a real provider."""
import pytest
from fastapi import Depends, FastAPI, Request
from fastapi.testclient import TestClient

from fake_op import ORG, SUB, FakeOP
from sangam import AuditConfig, AuditEntry, SangamAcr, SangamSettings, validate_audit_event
from sangam.fastapi import SangamFastAPI


@pytest.fixture()
def op():
    o = FakeOP()
    yield o
    o.close()


def make(op, tmp_path):
    app = FastAPI()
    sangam = SangamFastAPI(SangamSettings(authority=op.base, client_id="app", client_secret="secret", base_url="http://testserver", cookie_secret="a" * 32,
                                          audit=AuditConfig("lims", "1.0.0", "development"), audit_buffer=str(tmp_path / "pending.jsonl")))
    sangam.install(app)

    @app.get("/secure")
    def secure(user=Depends(sangam.require_user())):
        return {"id": user.id}

    @app.get("/vitals")
    def vitals(user=Depends(sangam.require_permission(lambda r: ORG + "ward/", "vitals:read"))):
        return {"ok": True}

    @app.get("/orders")
    def orders(user=Depends(sangam.require_permission(lambda r: ORG, "orders:write"))):
        return {"ok": True}

    @app.post("/sign")
    def sign(request: Request, user=Depends(sangam.require_step_up(SangamAcr.SIGNATURE, return_to=lambda r: "/?resume=sign"))):
        return sangam.record(request, AuditEntry("lims.result.sign", "sign", "result", "R-1", signature=("sig-1", "Approved", "sha256:" + "a" * 64)), user)

    @app.get("/api/results")
    def results(token=Depends(sangam.require_token(scope="lims.read", acr=SangamAcr.TWO_FACTOR, max_age=300))):
        return {"sub": token.user.id}

    return app, sangam


def test_sign_in_step_up_permission_and_audit(op, tmp_path):
    app, _ = make(op, tmp_path)
    c = TestClient(app, follow_redirects=False)
    assert c.get("/secure").headers["location"].startswith("/auth/login?returnTo=%2Fsecure")
    assert c.get("/secure", headers={"accept": "application/json"}).status_code == 401
    assert c.get("/auth/me").status_code == 401

    authorize = c.get("/auth/login?returnTo=/secure").headers["location"]
    assert authorize.startswith(op.base + "/connect/authorize?") and "code_challenge_method=S256" in authorize
    before = c.cookies.get("sangam.sid")
    code, state = op.approve(authorize)
    back = c.get(f"/auth/callback?code={code}&state={state}")
    assert back.headers["location"] == "/secure"
    assert c.cookies.get("sangam.sid") != before
    assert c.get("/secure").json() == {"id": SUB}
    assert c.get("/auth/me").json()["user"]["memberships"][0]["role"] == "nurse"

    assert c.get("/vitals").json() == {"ok": True}
    assert c.get("/orders").status_code == 403

    needs = c.post("/sign", headers={"accept": "application/json"})
    assert needs.status_code == 401 and needs.json()["error"] == "step_up_required"
    authorize = c.get(needs.json()["login"]).headers["location"]
    assert "acr_values=urn%3Asangam%3Aacr%3Asign" in authorize
    code, state = op.approve(authorize, SangamAcr.SIGNATURE)
    assert c.get(f"/auth/callback?code={code}&state={state}").headers["location"] == "/?resume=sign"
    event = c.post("/sign", headers={"user-agent": "pytest"}).json()
    assert validate_audit_event(event) == [] and event["actor"]["acr"] == SangamAcr.SIGNATURE and event["client"]["user_agent"] == "pytest"
    assert len((tmp_path / "pending.jsonl").read_text().strip().splitlines()) == 1

    # A returnTo that leaves the site, a replayed code and a forged state.
    code, state = op.approve(c.get("/auth/login?returnTo=https://evil.example/").headers["location"])
    assert c.get(f"/auth/callback?code={code}&state={state}").headers["location"] == "/"
    assert c.get(f"/auth/callback?code={code}&state={state}").headers["location"] == "/"
    code, state = op.approve(c.get("/auth/login?returnTo=/secure").headers["location"])
    with pytest.raises(PermissionError):
        c.get(f"/auth/callback?code={code}&state=forged")

    out = c.get("/auth/logout")
    assert out.headers["location"].startswith(op.base + "/connect/endsession?")
    assert c.get("/auth/me").status_code == 401


def test_an_api_checks_token_scope_and_step_up(op, tmp_path):
    app, _ = make(op, tmp_path)
    c = TestClient(app)
    import time

    now = int(time.time())
    assert c.get("/api/results").status_code == 401
    assert c.get("/api/results", headers={"authorization": "Bearer nonsense"}).status_code == 401
    weak = op.access_token(scope="lims.read", acr=SangamAcr.SINGLE_FACTOR, auth_time=now)
    r = c.get("/api/results", headers={"authorization": f"Bearer {weak}"})
    assert r.status_code == 401 and 'acr_values="urn:sangam:acr:2", max_age=300' in r.headers["www-authenticate"]
    assert c.get("/api/results", headers={"authorization": f"Bearer {op.access_token(scope='other', acr=SangamAcr.TWO_FACTOR, auth_time=now)}"}).status_code == 403
    assert c.get("/api/results", headers={"authorization": f"Bearer {op.access_token(scope='lims.read', acr=SangamAcr.TWO_FACTOR, auth_time=now)}"}).json() == {"sub": SUB}
