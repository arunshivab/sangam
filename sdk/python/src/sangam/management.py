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

    def send(self, method: str, path: str, body: Any = None, admin_access_token: str | None = None) -> Any:
        """Any call; ``path`` is relative to /api/v1/.

        rc.12: ``admin_access_token`` is one of your admins' own access token, sent as Sangam-Acting-Admin on staff calls.
        """
        headers = {"authorization": f"Bearer {self.token()}"}
        if admin_access_token:
            headers["sangam-acting-admin"] = admin_access_token
        r = self._http.request(method, f"{self.authority}/api/v1/{path.lstrip('/')}", json=body, headers=headers)
        if r.status_code >= 300:
            raise SangamApiError(r.status_code, r.text, f"Sangam's management API answered {r.status_code} to {method} {path}")
        return r.json() if r.content else None

    def lock_session(self, person_access_token: str) -> bool:
        """rc.12 (T1): locks the person's Sangam session with their own access token (from their sign-in).

        Sangam tells the person's other applications in that session (``session.locked``); on an enrolled shared ward device
        they come back with their PIN, anywhere else with a full sign-in. False when that sign-in had already ended.
        """
        r = self._http.post(f"{self.authority}/api/v1/sessions/lock", headers={"authorization": f"Bearer {person_access_token}"})
        if r.status_code == 404:
            return False
        if r.status_code >= 300:
            raise SangamApiError(r.status_code, r.text, f"Sangam answered {r.status_code} to the lock")
        return True

    def end_emergency(self, person_access_token: str, reason: str) -> bool:
        """rc.12 (M12): ends the person's emergency access early, with a reason, with their own access token.

        False when none was in progress. Tokens refreshed afterwards no longer carry it.
        """
        if not reason.strip():
            raise ValueError("Say why emergency access ends.")
        r = self._http.post(f"{self.authority}/api/v1/sessions/emergency/end", json={"reason": reason}, headers={"authorization": f"Bearer {person_access_token}"})
        if r.status_code == 404:
            return False
        if r.status_code >= 300:
            raise SangamApiError(r.status_code, r.text, f"Sangam answered {r.status_code} to ending emergency access")
        return True

    def upsert_role(self, code: str, display_name: str, permissions: list[str]) -> Any:
        return self.send("PUT", f"roles/{quote(code)}", {"displayName": display_name, "description": None, "permissions": permissions, "orgId": None})

    def upsert_organisation(self, org_id: str, name: str, type: str, parent_id: str | None = None) -> Any:
        return self.send("PUT", f"orgs/{org_id}", {"name": name, "type": type, "parentId": parent_id, "metadata": None})

    def upsert_membership(self, org_id: str, user_id: str, admin_access_token: str, role: str, applies_to_descendants: bool = False, expires_at: str | None = None) -> Any:
        """Gives a person a role at an organisation. rc.13: for one of your admins (an admin of that organisation)."""
        body: dict[str, Any] = {"role": role, "appliesToDescendants": applies_to_descendants}
        if expires_at:
            body["expiresAt"] = expires_at
        return self.send("PUT", f"orgs/{org_id}/members/{user_id}", body, admin_access_token)

    def set_roles(self, org_id: str, user_id: str, admin_access_token: str, roles: list[str], expires_at: str | None = None) -> Any:
        """rc.12: every role a person holds at an organisation; an empty list keeps them there as staff.

        rc.13: for one of your admins: ``admin_access_token`` is the token Sangam gave your application when they signed in.
        """
        body: dict[str, Any] = {"roles": roles}
        if expires_at:
            body["expiresAt"] = expires_at
        return self.send("PUT", f"orgs/{org_id}/members/{user_id}", body, admin_access_token)

    def remove_member(self, org_id: str, user_id: str, admin_access_token: str) -> Any:
        """rc.13: ends a person's place at an organisation now, for one of your admins."""
        return self.send("DELETE", f"orgs/{org_id}/members/{user_id}", None, admin_access_token)

    def check_staff(self, org_id: str, admin_access_token: str, person: dict[str, Any]) -> Any:
        """rc.12: the check before adding a person (which details match, never what Sangam holds), for one of your admins.

        ``person`` has firstName, lastName, email, mobile (E.164 or None) and dateOfBirth (yyyy-mm-dd).
        """
        return self.send("POST", f"orgs/{org_id}/staff/check", person, admin_access_token)

    def add_staff(self, org_id: str, admin_access_token: str, addition: dict[str, Any]) -> Any:
        """rc.12: adds one person to one organisation for one of your admins (verified with DigiLocker).

        ``addition`` has exactly one of person, joinCode, staffRef or userId, and optionally roles, startsOn, expiresAt
        and confirmed. A new person's activationCode comes back once, for your admin to hand over in person.
        """
        return self.send("POST", f"orgs/{org_id}/staff", addition, admin_access_token)

    def merged_into(self, user_id: str) -> str | None:
        """Where a person's account went if they merged it into another of theirs (rc.7).

        Returns the surviving account's sub, or None when the id was not merged or your application never knew it.
        Use it on account.merged.
        """
        try:
            answer = self.send("GET", f"users/{user_id}/merged")
        except SangamApiError as error:
            if error.status == 404:
                return None
            raise
        return answer.get("merged_into") if isinstance(answer, dict) else None

    def get_attributes(self, user_id: str) -> dict[str, str | None]:
        return self.send("GET", f"users/{user_id}/attributes")

    def set_attributes(self, user_id: str, values: dict[str, str | None]) -> Any:
        return self.send("PUT", f"users/{user_id}/attributes", values)
