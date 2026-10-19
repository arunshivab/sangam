# sangam-client (Python)

Sangam for Python 3.10+ (R6, SGM-306): the same concepts and rules as every Sangam SDK, proved by the shared
conformance vectors (`sdk/conformance/vectors.json`). Built on [Authlib](https://authlib.org) and joserfc.

```sh
pip install "sangam-client[fastapi]"      # or [flask]
```

```python
from sangam import SangamSettings, SangamAcr, AuditConfig, AuditEntry, has_permission
from sangam.fastapi import SangamFastAPI

sangam = SangamFastAPI(SangamSettings(
    authority="https://id.sangamid.in", client_id="lims", client_secret=os.environ["SANGAM_SECRET"],
    base_url="https://lims.example.in",            # callback: https://lims.example.in/auth/callback
    cookie_secret=os.environ["COOKIE_SECRET"],     # 32+ characters
    audit=AuditConfig("lims", "1.2.0", "production"), audit_buffer="/var/lib/lims/audit.jsonl"))
sangam.install(app)                                # /auth/login, /callback, /logout, /me

@app.get("/results")
def results(user=Depends(sangam.require_user())): ...

@app.get("/wards/{ward}/vitals")
def vitals(user=Depends(sangam.require_permission(lambda r: path_of(r), "vitals:read"))): ...

@app.post("/results/{id}/sign")
def sign(id: str, request: Request, user=Depends(sangam.require_step_up(SangamAcr.SIGNATURE))):
    sangam.record(request, AuditEntry("lims.result.sign", "sign", "result", id, signature=(token_id, "Approved", record_hash)), user)

@app.get("/api/v1/results")                        # an API called with a Sangam access token
def api(token=Depends(sangam.require_token(scope="lims.read", acr=SangamAcr.TWO_FACTOR, max_age=300))): ...
```

Flask: `SangamFlask(app, settings)` with `@sangam.login_required`, `@sangam.require_step_up(acr, max_age)`,
`@sangam.require_permission(org_fn, permission)`, `@sangam.require_token(...)` and `sangam.record(entry)`.

rc.12: `user.current_organisation` (from `sangam_org`); `core.switch_organisation_url(...)`; `/auth/lock` locks the
person's Sangam session (`management.lock_session(token)` from your own code); `core.emergency_url(case, return_to)` asks
for emergency access to one of your cases, then `user.emergency` (`case_reference`, `until`, `is_active()`) is set; end it
with `management.end_emergency(token, reason)`. Staff: `check_staff`, `add_staff`, `set_roles` (the invitation call is
gone).

rc.13: `upsert_membership`, `set_roles` and the new `remove_member` take the acting admin's own access token third
(`set_roles(org_id, user_id, admin_access_token, roles)`), as `add_staff` does; Sangam checks they are an
admin of that organisation and records who asked.

rc.14: `set_attributes(user_id, admin_access_token, values)` takes the acting admin too, who must administer your
application or an organisation where the person works. Every application knows a person by its own id (the `sub` of its tokens, and the id the management API, webhooks and
SCIM use); another application's id for the same person is unrelated, and the API answers *not found* to an id that is
not yours (ADR-0017).

- **Tokens stay on the server**: the code is exchanged with PKCE on the server; the browser holds only a signed session
  id. Sessions are in memory by default; give `store=` (get, set, delete) to share them between processes.
- **Without a framework**: `has_permission`, `satisfies`, `step_up_challenge`, `verify_webhook`, `TokenVerifier`,
  `ManagementClient`, `build_audit_event`, `validate_audit_event`, `AuditBuffer`.
- **Audit**: events in the shared schema 1.0 (SGM-208) go to a JSON Lines buffer; `sangam.core.flush_audit()` sends them
  once `audit_endpoint` (the future audit service) is set.
- Django: not yet; the core (`sangam.web.SangamCore`) is framework-neutral for an adapter.

`pip install -e ".[test]" && pytest` runs the shared vectors and the flow tests against a local OpenID provider.
Sample: `samples/fastapi` (port 5920).
