"""Sangam's signed webhooks (Standard Webhooks, SGM-217)."""
from __future__ import annotations

import base64
import binascii
import hashlib
import hmac
import time

SECRET_PREFIX = "whsec_"
TOLERANCE_SECONDS = 300


def verify_webhook(id: str | None, timestamp: str | None, signature: str | None, body: str | bytes, secret: str, now: int | None = None) -> bool:
    """HMAC-SHA256 of ``id.timestamp.body`` under the secret's bytes, and a timestamp within five minutes."""
    if not id or not signature or not timestamp or not timestamp.isdigit() or not secret.startswith(SECRET_PREFIX):
        return False
    current = int(time.time()) if now is None else now
    if abs(current - int(timestamp)) > TOLERANCE_SECONDS:
        return False
    try:
        key = base64.b64decode(secret[len(SECRET_PREFIX):], validate=True)
    except (binascii.Error, ValueError):
        return False
    payload = body if isinstance(body, bytes) else body.encode("utf-8")
    expected = "v1," + base64.b64encode(hmac.new(key, f"{id}.{timestamp}.".encode() + payload, hashlib.sha256).digest()).decode()
    return any(hmac.compare_digest(s, expected) for s in signature.split(" ") if s)
