"""Server-side sessions: the browser gets a signed session id, never a token (SGM-306 §2)."""
from __future__ import annotations

import base64
import hashlib
import hmac
import secrets
import threading
import time
from typing import Any, Protocol

COOKIE = "sangam.sid"


def cookie_name(secure: bool) -> str:
    """The session cookie's name: over https it carries the __Host- prefix (rc.5), so only this exact host can set it."""
    return "__Host-" + COOKIE if secure else COOKIE


class SessionStore(Protocol):
    def get(self, sid: str) -> dict[str, Any] | None:
        """The session with this id, or None when there is none or it has expired."""

    def set(self, sid: str, value: dict[str, Any], ttl: int) -> None:
        """Stores the session for ttl seconds."""

    def delete(self, sid: str) -> None:
        """Forgets the session."""


class MemorySessionStore:
    """In memory, for one process and development. Give a shared store when you run more than one."""

    def __init__(self) -> None:
        self._items: dict[str, tuple[dict[str, Any], float]] = {}
        self._lock = threading.Lock()

    def get(self, sid: str) -> dict[str, Any] | None:
        with self._lock:
            item = self._items.get(sid)
            if not item:
                return None
            if item[1] < time.time():
                del self._items[sid]
                return None
            return dict(item[0])

    def set(self, sid: str, value: dict[str, Any], ttl: int) -> None:
        with self._lock:
            self._items[sid] = (dict(value), time.time() + ttl)

    def delete(self, sid: str) -> None:
        with self._lock:
            self._items.pop(sid, None)


def new_id() -> str:
    return secrets.token_urlsafe(24)


def sign_id(sid: str, secret: str) -> str:
    mac = base64.urlsafe_b64encode(hmac.new(secret.encode(), sid.encode(), hashlib.sha256).digest()).rstrip(b"=").decode()
    return f"{sid}.{mac}"


def unsign_id(value: str | None, secret: str) -> str | None:
    if not value or "." not in value:
        return None
    sid = value.rsplit(".", 1)[0]
    return sid if hmac.compare_digest(sign_id(sid, secret), value) else None
