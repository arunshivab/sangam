"""Flask: the same flow through the blueprint and decorators."""
import pytest
from flask import Flask, jsonify

from fake_op import ORG, SUB, FakeOP
from sangam import AuditConfig, AuditEntry, SangamAcr, SangamSettings, validate_audit_event
from sangam.flask import SangamFlask


@pytest.fixture()
def op():
    o = FakeOP()
    yield o
    o.close()


def test_flask_sign_in_step_up_permission_and_audit(op, tmp_path):
    app = Flask(__name__)
    sangam = SangamFlask(app, SangamSettings(authority=op.base, client_id="app", client_secret="secret", base_url="http://localhost", cookie_secret="b" * 32,
                                             audit=AuditConfig("lims", "1.0.0", "development"), audit_buffer=str(tmp_path / "a.jsonl")))

    @app.get("/secure")
    @sangam.login_required
    def secure():
        return jsonify(id=sangam.current_user().id)

    @app.get("/orders")
    @sangam.require_permission(lambda: ORG, "orders:write")
    def orders():
        return "orders"

    @app.get("/vitals")
    @sangam.require_permission(lambda: ORG + "ward/", "vitals:read")
    def vitals():
        return "vitals"

    @app.post("/sign")
    @sangam.require_step_up(SangamAcr.SIGNATURE)
    def sign():
        return jsonify(sangam.record(AuditEntry("lims.result.sign", "sign", "result", "R-1", signature=("sig-1", "Approved", "sha256:x"))))

    c = app.test_client()
    assert c.get("/secure").headers["Location"].startswith("/auth/login?returnTo=%2Fsecure")
    authorize = c.get("/auth/login?returnTo=/secure").headers["Location"]
    code, state = op.approve(authorize)
    assert c.get(f"/auth/callback?code={code}&state={state}").headers["Location"] == "/secure"
    assert c.get("/secure").json == {"id": SUB}
    assert c.get("/vitals").data == b"vitals"
    assert c.get("/orders").status_code == 403
    needs = c.post("/sign", headers={"Accept": "application/json"})
    assert needs.status_code == 401
    code, state = op.approve(c.get(needs.json["login"]).headers["Location"], SangamAcr.SIGNATURE)
    c.get(f"/auth/callback?code={code}&state={state}")
    event = c.post("/sign").json
    assert validate_audit_event(event) == [] and event["actor"]["sub"] == SUB


def test_a_back_end_with_a_key_signs_in_with_an_assertion_not_a_secret():
    # rc.15 (private_key_jwt): the code and the management token are both got with a typed assertion for the issuer.
    from joserfc.jwk import ECKey

    from sangam import ClientKey, ManagementClient

    private = ECKey.generate_key("P-256", private=True)
    o = FakeOP(client_key=ECKey.import_key(private.as_dict(private=False)))
    try:
        app = Flask(__name__)
        pem = private.as_pem(private=True).decode()
        sangam = SangamFlask(app, SangamSettings(authority=o.base, client_id="app", client_secret=None, private_key=pem, base_url="http://localhost", cookie_secret="b" * 32))

        @app.get("/secure")
        @sangam.login_required
        def secure():
            return jsonify(id=sangam.current_user().id)

        c = app.test_client()
        code, state = o.approve(c.get("/auth/login?returnTo=/secure").headers["Location"])
        assert c.get(f"/auth/callback?code={code}&state={state}").headers["Location"] == "/secure"
        assert c.get("/secure").json == {"id": SUB}
        assert ManagementClient(o.base, "app", private_key=pem).token() == "cc-token"
        assert ClientKey(pem).kid == private.thumbprint()
    finally:
        o.close()

    with pytest.raises(ValueError, match="public key"):
        ClientKey(private.as_pem(private=False).decode())
    with pytest.raises(ValueError):
        ManagementClient("https://id.example.in", "app")
