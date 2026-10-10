"""Sangam's management API for your back end, with client-credentials tokens cached (needs sangam.manage)."""
from __future__ import annotations

import threading
import time
from typing import Any
from urllib.parse import quote

import httpx


class SangamApiError(Exception):
    def __init__(self, status: int, body: str, message: str) -> None:
        super().__init__(message)
        self.status = status
        self.body = body


class ManagementClient:
    def __init__(self, authority: str, client_id: str, client_secret: str, client: httpx.Client | None = None) -> None:
        self.authority = authority.rstrip("/")
        self.client_id = client_id
        self._secret = client_secret
        self._http = client or httpx.Client(timeout=15)
        self._tokens: dict[str, tuple[str, float]] = {}
        self._lock = threading.Lock()

    def token(self, scope: str = "sangam.manage") -> str:
        """A client-credentials token, reused until a minute before it expires."""
        with self._lock:
            cached = self._tokens.get(scope)
            if cached and cached[1] > time.time() + 60:
                return cached[0]
            r = self._http.post(f"{self.authority}/connect/token", data={"grant_type": "client_credentials", "client_id": self.client_id, "client_secret": self._secret, "scope": scope})
            if r.status_code >= 300:
                raise SangamApiError(r.status_code, r.text, f"Sangam refused a token for {scope} ({r.status_code})")
            body = r.json()
            self._tokens[scope] = (body["access_token"], time.time() + int(body.get("expires_in", 300)))
            return body["access_token"]

    def send(self, method: str, path: str, body: Any = None) -> Any:
        """Any call; ``path`` is relative to /api/v1/."""
        r = self._http.request(method, f"{self.authority}/api/v1/{path.lstrip('/')}", json=body, headers={"authorization": f"Bearer {self.token()}"})
        if r.status_code >= 300:
            raise SangamApiError(r.status_code, r.text, f"Sangam's management API answered {r.status_code} to {method} {path}")
        return r.json() if r.content else None

    def upsert_role(self, code: str, display_name: str, permissions: list[str]) -> Any:
        return self.send("PUT", f"roles/{quote(code)}", {"displayName": display_name, "description": None, "permissions": permissions, "orgId": None})

    def upsert_organisation(self, org_id: str, name: str, type: str, parent_id: str | None = None) -> Any:
        return self.send("PUT", f"orgs/{org_id}", {"name": name, "type": type, "parentId": parent_id, "metadata": None})

    def upsert_membership(self, org_id: str, user_id: str, role: str, applies_to_descendants: bool = False, expires_at: str | None = None) -> Any:
        body: dict[str, Any] = {"role": role, "appliesToDescendants": applies_to_descendants}
        if expires_at:
            body["expiresAt"] = expires_at
        return self.send("PUT", f"orgs/{org_id}/members/{user_id}", body)

    def invite(self, org_id: str, email: str, role: str, applies_to_descendants: bool = False) -> Any:
        """Invites someone by e-mail to a role at an organisation (rc.5); they are added when they accept.

        upsert_membership adds only people who already use your application; invite anyone else. At most 200 a day.
        """
        return self.send("POST", f"orgs/{org_id}/invitations", {"email": email, "role": role, "appliesToDescendants": applies_to_descendants})

    def get_attributes(self, user_id: str) -> dict[str, str | None]:
        return self.send("GET", f"users/{user_id}/attributes")

    def set_attributes(self, user_id: str, values: dict[str, str | None]) -> Any:
        return self.send("PUT", f"users/{user_id}/attributes", values)
