"""The framework-neutral part of signing in with Sangam: authorization code with PKCE, on the server."""
from __future__ import annotations

import secrets
import time
from dataclasses import asdict, dataclass, field
from typing import Any
from urllib.parse import urlencode

import httpx
from authlib.common.security import generate_token
from authlib.integrations.httpx_client import OAuth2Client
from joserfc import jwt
from joserfc.jwk import KeySet
from authlib.oauth2.rfc7636 import create_s256_code_challenge

from .audit import AuditBuffer, AuditConfig, AuditEntry, build_audit_event
from .management import ManagementClient
from .memberships import SangamMembership, SangamUser, user_from_claims
from .session import MemorySessionStore, SessionStore, cookie_name, new_id, sign_id, unsign_id
from .stepup import satisfies
from .tokens import ALGORITHMS, TokenVerifier


def safe_return(value: str | None) -> str:
    """Only local paths are followed after sign-in."""
    return value if value and value.startswith("/") and not value.startswith("//") and not value.startswith("/\\") else "/"


@dataclass
class SangamSettings:
    authority: str
    client_id: str
    client_secret: str
    base_url: str
    """Your application's address; the callback is {base_url}{base_path}/callback."""
    cookie_secret: str
    """32 characters or more."""
    base_path: str = "/auth"
    scopes: tuple[str, ...] = ("openid", "profile", "email", "orgs.read")
    session_ttl: int = 8 * 3600
    audit: AuditConfig | None = None
    audit_buffer: str = "sangam-audit/pending.jsonl"
    audit_endpoint: str | None = None
    trust_proxy: bool = False
    store: SessionStore = field(default_factory=MemorySessionStore)


def user_to_dict(user: SangamUser) -> dict[str, Any]:
    d = asdict(user)
    d.pop("claims", None)
    return d


def user_from_dict(d: dict[str, Any]) -> SangamUser:
    d = dict(d)
    d["memberships"] = tuple(SangamMembership(**{**m, "permissions": tuple(m["permissions"])}) for m in d.get("memberships", ()))
    d["amr"] = tuple(d.get("amr") or ())
    return SangamUser(**d)


class SangamCore:
    """Sign-in, sessions, guards and audit, without any web framework; the adapters call it."""

    def __init__(self, settings: SangamSettings, http: httpx.Client | None = None) -> None:
        if len(settings.cookie_secret) < 32:
            raise ValueError("cookie_secret must be at least 32 characters.")
        self.settings = settings
        self.authority = settings.authority.rstrip("/")
        self.base_path = settings.base_path.rstrip("/")
        self.base_url = settings.base_url.rstrip("/")
        self.redirect_uri = f"{self.base_url}{self.base_path}/callback"
        self.secure = self.base_url.startswith("https://")
        self.cookie = cookie_name(self.secure)
        self._http = http or httpx.Client(timeout=15)
        self._metadata: dict[str, Any] | None = None
        self._keys: Any = None
        self.tokens = TokenVerifier(self.authority + "/", client=self._http)
        self.management = ManagementClient(self.authority, settings.client_id, settings.client_secret, client=self._http)
        self.audit_buffer = AuditBuffer(settings.audit_buffer)

    # ---------------------------------------------------------------- discovery
    def metadata(self) -> dict[str, Any]:
        if self._metadata is None:
            self._metadata = self._http.get(self.authority + "/.well-known/openid-configuration").raise_for_status().json()
        return self._metadata

    def _jwks(self, force: bool = False) -> Any:
        if self._keys is None or force:
            self._keys = KeySet.import_key_set(self._http.get(self.metadata()["jwks_uri"]).raise_for_status().json())
        return self._keys

    def _oauth(self) -> OAuth2Client:
        return OAuth2Client(self.settings.client_id, self.settings.client_secret, scope=" ".join(self.settings.scopes), redirect_uri=self.redirect_uri,
                            code_challenge_method="S256", token_endpoint_auth_method="client_secret_post", timeout=15)

    # ---------------------------------------------------------------- sessions
    def load(self, cookie: str | None) -> tuple[str | None, dict[str, Any]]:
        sid = unsign_id(cookie, self.settings.cookie_secret)
        return sid, (self.settings.store.get(sid) if sid else None) or {}

    def save(self, sid: str | None, session: dict[str, Any]) -> str:
        """Stores the session; returns the cookie value to set."""
        sid = sid or new_id()
        self.settings.store.set(sid, session, self.settings.session_ttl)
        return sign_id(sid, self.settings.cookie_secret)

    def user(self, cookie: str | None) -> SangamUser | None:
        _, session = self.load(cookie)
        return user_from_dict(session["user"]) if session.get("user") else None

    def login_url(self, return_to: str = "/", acr: str | None = None, max_age: int | None = None) -> str:
        q: dict[str, str] = {"returnTo": return_to}
        if acr:
            q["acr"] = acr
            if max_age is not None:
                q["max_age"] = str(max_age)
        return f"{self.base_path}/login?{urlencode(q)}"

    # ---------------------------------------------------------------- the flow
    def begin(self, cookie: str | None, return_to: str | None, acr: str | None, max_age: str | None) -> tuple[str, str]:
        """Starts a sign-in; returns (Sangam address to send the browser to, cookie value)."""
        verifier = generate_token(64)
        state, nonce = secrets.token_urlsafe(24), secrets.token_urlsafe(24)
        extra: dict[str, str] = {"nonce": nonce, "code_challenge": create_s256_code_challenge(verifier), "code_challenge_method": "S256"}
        if acr:
            extra["acr_values"] = acr
        if max_age and max_age.isdigit():
            extra["max_age"] = max_age
        query = {"response_type": "code", "client_id": self.settings.client_id, "redirect_uri": self.redirect_uri, "scope": " ".join(self.settings.scopes), "state": state, **extra}
        url = self.metadata()["authorization_endpoint"] + "?" + urlencode(query)
        sid, session = self.load(cookie)
        session["pending"] = {"state": state, "nonce": nonce, "verifier": verifier, "return_to": safe_return(return_to)}
        return url, self.save(sid, session)

    def finish(self, cookie: str | None, params: dict[str, str]) -> tuple[str, str]:
        """Completes a sign-in from the callback; returns (where to go, new cookie value)."""
        sid, session = self.load(cookie)
        pending = session.pop("pending", None)
        if not pending:
            return "/", self.save(sid, session)
        if params.get("error"):
            return pending["return_to"], self.save(sid, session)
        if not secrets.compare_digest(params.get("state", ""), pending["state"]):
            raise PermissionError("The sign-in state does not match.")
        token = self._oauth().fetch_token(self.metadata()["token_endpoint"], grant_type="authorization_code", code=params.get("code", ""), code_verifier=pending["verifier"], redirect_uri=self.redirect_uri)
        id_token = token.get("id_token")
        if not id_token:
            raise PermissionError("Sangam returned no ID token.")
        claims = self._verify_id_token(id_token, pending["nonce"])
        user = user_from_claims(claims)
        if sid:
            self.settings.store.delete(sid)
        # A new session id after sign-in, so an id planted before it is worth nothing.
        return pending["return_to"], self.save(None, {"user": user_to_dict(user), "id_token": id_token})

    def _verify_id_token(self, id_token: str, nonce: str) -> dict[str, Any]:
        registry = jwt.JWTClaimsRegistry(leeway=30, iss={"essential": True, "value": self.metadata()["issuer"]}, aud={"essential": True, "value": self.settings.client_id},
                                         nonce={"essential": True, "value": nonce}, exp={"essential": True})
        try:
            decoded = jwt.decode(id_token, self._jwks(), algorithms=ALGORITHMS)
        except Exception:  # noqa: BLE001 - perhaps a rotated key: fetch the keys again once
            decoded = jwt.decode(id_token, self._jwks(force=True), algorithms=ALGORITHMS)
        registry.validate(decoded.claims)
        return dict(decoded.claims)

    def end(self, cookie: str | None) -> str:
        """Signs out here; returns Sangam's end-session address."""
        sid, session = self.load(cookie)
        if sid:
            self.settings.store.delete(sid)
        q = {"post_logout_redirect_uri": self.base_url + "/"}
        if session.get("id_token"):
            q["id_token_hint"] = session["id_token"]
        return self.metadata().get("end_session_endpoint", self.authority + "/connect/endsession") + "?" + urlencode(q)

    # ---------------------------------------------------------------- guards and audit
    def stepped_up(self, user: SangamUser | None, acr: str, max_age: int | None) -> bool:
        return user is not None and satisfies(user.acr, user.auth_time, acr, max_age, int(time.time()))

    def record(self, user: SangamUser | None, ip: str | None, user_agent: str | None, entry: AuditEntry) -> dict[str, Any]:
        if self.settings.audit is None:
            raise RuntimeError("Pass audit=AuditConfig(...) in the settings to record audit events.")
        event = build_audit_event(self.settings.audit, user, ip, user_agent, entry)
        self.audit_buffer.append(event)
        return event

    def flush_audit(self) -> int:
        return self.audit_buffer.flush(self.settings.audit_endpoint, lambda: self.management.token("audit.write"))
