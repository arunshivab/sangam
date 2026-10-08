"""The shared audit event (SGM-208): build it, check it, buffer it until the audit service takes it."""
from __future__ import annotations

import json
import os
import re
import secrets
import threading
import time
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Callable, Iterable

from .memberships import SangamUser

SCHEMA_VERSION = "1.0"
MASKED = "[masked]"
_ENVIRONMENTS = {"production", "staging", "development"}
_ACTORS = {"user", "admin", "api", "system", "anonymous"}
_CATEGORIES = {"access", "create", "update", "delete", "approve", "sign", "export", "admin", "security"}
_OUTCOMES = {"success", "failure", "denied"}
_CLASSES = {"public", "internal", "personal", "sensitive-personal", "health"}
_UUID7 = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")
_UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
_RFC3339 = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$")
_ACTION = re.compile(r"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*){2,}$")
_VERSION = re.compile(r"^1\.[0-9]+$")


@dataclass(frozen=True)
class AuditConfig:
    app_id: str
    app_version: str
    environment: str = "production"


@dataclass
class AuditEntry:
    """What happened; the helper adds who, where and from which device."""

    action: str
    category: str
    target_type: str
    target_id: str
    outcome: str = "success"
    target_display: str | None = None
    org_id: str | None = None
    org_path: str | None = None
    data_classification: str = "internal"
    reason: str | None = None
    signature: tuple[str, str, str] | None = None
    """(token_id, meaning, record_hash)"""
    changes: list[dict[str, Any]] = field(default_factory=list)
    sensitive: Iterable[str] = ()
    actor_type: str | None = None
    client_id: str | None = None
    correlation_id: str | None = None


def uuid7(now_ms: int | None = None) -> str:
    """A time-ordered UUID (version 7, RFC 9562)."""
    ms = int(time.time() * 1000) if now_ms is None else now_ms
    b = bytearray(ms.to_bytes(6, "big") + secrets.token_bytes(10))
    b[6] = (b[6] & 0x0F) | 0x70
    b[8] = (b[8] & 0x3F) | 0x80
    h = b.hex()
    return f"{h[:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:]}"


def build_audit_event(config: AuditConfig, user: SangamUser | None, ip: str | None, user_agent: str | None, entry: AuditEntry, now: datetime | None = None) -> dict[str, Any]:
    """An event in schema 1.0: source from config, actor from the user, client from the request; sensitive fields masked."""
    at = now or datetime.now(timezone.utc)
    kind = entry.actor_type or ("user" if user and user.id else "anonymous")
    actor: dict[str, Any] = {"type": kind}
    if user and user.id and kind in ("user", "admin"):
        actor["sub"] = user.id
        if user.session_id:
            actor["session_id"] = user.session_id
        if user.acr:
            actor["acr"] = user.acr
        if user.amr:
            actor["amr"] = list(user.amr)
    if entry.client_id:
        actor["client_id"] = entry.client_id
    e: dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "event_id": uuid7(int(at.timestamp() * 1000)),
        "occurred_at": at.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.") + f"{at.microsecond // 1000:03d}Z",
        "source": {"app_id": config.app_id, "app_version": config.app_version, "environment": config.environment},
    }
    if entry.org_id:
        e["tenant"] = {"org_id": entry.org_id, **({"org_path": entry.org_path} if entry.org_path else {})}
    e["actor"] = actor
    e["action"] = entry.action
    e["category"] = entry.category
    e["target"] = {"type": entry.target_type, "id": entry.target_id, **({"display": entry.target_display} if entry.target_display else {})}
    e["outcome"] = entry.outcome
    if entry.reason:
        e["reason"] = entry.reason
    if entry.signature:
        e["signature"] = {"token_id": entry.signature[0], "meaning": entry.signature[1], "record_hash": entry.signature[2]}
    if entry.changes:
        sensitive = set(entry.sensitive)
        e["changes"] = [
            {"field": c["field"], "before": MASKED, "after": MASKED} if c["field"] in sensitive else {"field": c["field"], "before": c.get("before"), "after": c.get("after")}
            for c in entry.changes
        ]
    if ip:
        e["client"] = {"ip": ip, **({"user_agent": user_agent} if user_agent else {})}
    if entry.correlation_id:
        e["correlation_id"] = entry.correlation_id
    e["data_classification"] = entry.data_classification
    return e


def _filled(v: Any) -> bool:
    return isinstance(v, str) and len(v) > 0


def validate_audit_event(e: Any) -> list[str]:
    """The problems with an event against schema 1.0 (the same rules as audit-event-1.0.schema.json); [] when valid."""
    if not isinstance(e, dict):
        return ["not an object"]
    p: list[str] = []
    s = lambda k: e.get(k) if isinstance(e.get(k), str) else ""  # noqa: E731
    if not _VERSION.match(s("schema_version")):
        p.append("schema_version must be 1.x")
    if not _UUID7.match(s("event_id")):
        p.append("event_id must be a UUID version 7")
    if not _RFC3339.match(s("occurred_at")):
        p.append("occurred_at must be RFC 3339")
    src = e.get("source")
    if not isinstance(src, dict) or not _filled(src.get("app_id")) or not _filled(src.get("app_version")) or src.get("environment") not in _ENVIRONMENTS:
        p.append("source needs app_id, app_version and an environment")
    if "tenant" in e:
        t = e["tenant"]
        path = t.get("org_path") if isinstance(t, dict) else None
        if not isinstance(t, dict) or not isinstance(t.get("org_id"), str) or not _UUID.match(t["org_id"]) or ("org_path" in t and not (isinstance(path, str) and path.startswith("/") and path.endswith("/"))):
            p.append("tenant needs a UUID org_id and an org_path that starts and ends with /")
    actor = e.get("actor")
    kind = actor.get("type") if isinstance(actor, dict) else None
    if not isinstance(actor, dict) or kind not in _ACTORS:
        p.append("actor.type must be user, admin, api, system or anonymous")
    else:
        sub = actor.get("sub")
        if kind in ("user", "admin") and not _filled(sub):
            p.append("a person's event needs actor.sub")
        if isinstance(sub, str) and "@" in sub:
            p.append("actor.sub is the Sangam subject id, never an e-mail address")
        if kind == "api" and not _filled(actor.get("client_id")):
            p.append("an api event needs actor.client_id")
    if not _ACTION.match(s("action")):
        p.append("action must be domain.object.verb in lowercase")
    category = s("category")
    if category not in _CATEGORIES:
        p.append("category is not one of the nine")
    target = e.get("target")
    if not isinstance(target, dict) or not _filled(target.get("type")) or not _filled(target.get("id")):
        p.append("target needs type and id")
    if s("outcome") not in _OUTCOMES:
        p.append("outcome must be success, failure or denied")
    if category == "sign":
        sig = e.get("signature")
        if not isinstance(sig, dict) or not all(_filled(sig.get(k)) for k in ("token_id", "meaning", "record_hash")):
            p.append("a sign event needs signature.token_id, meaning and record_hash")
    if "changes" in e and (not isinstance(e["changes"], list) or any(not isinstance(c, dict) or not _filled(c.get("field")) for c in e["changes"])):
        p.append("each change needs its field")
    client = e.get("client")
    if kind in ("user", "admin") and (not isinstance(client, dict) or not _filled(client.get("ip"))):
        p.append("a person's event needs client.ip")
    if s("data_classification") not in _CLASSES:
        p.append("data_classification is not one of the five")
    for k in ("recorded_at", "prev_hash", "hash"):
        if k in e:
            p.append(f"{k} is set by the audit service, never by the application")
    return p


class AuditBuffer:
    """A durable JSON Lines file where events wait for the audit service (SGM-208 §7)."""

    def __init__(self, path: str = "sangam-audit/pending.jsonl") -> None:
        self.path = path
        self._lock = threading.Lock()

    def append(self, event: dict[str, Any]) -> None:
        problems = validate_audit_event(event)
        if problems:
            raise ValueError("The audit event breaks the shared schema: " + "; ".join(problems))
        with self._lock:
            os.makedirs(os.path.dirname(os.path.abspath(self.path)), exist_ok=True)
            with open(self.path, "a", encoding="utf-8") as f:
                f.write(json.dumps(event, ensure_ascii=False) + "\n")

    def pending(self) -> list[dict[str, Any]]:
        try:
            with open(self.path, encoding="utf-8") as f:
                return [json.loads(line) for line in f if line.strip()]
        except FileNotFoundError:
            return []

    def flush(self, endpoint: str | None, token: Callable[[], str], post: Callable[..., Any] | None = None) -> int:
        """Sends up to 500 events to the audit service; keeps them when it refuses or is away."""
        if not endpoint:
            return 0
        events = self.pending()
        batch = events[:500]
        if not batch:
            return 0
        import httpx

        response = (post or httpx.post)(endpoint, json={"events": batch}, headers={"authorization": f"Bearer {token()}"})
        if response.status_code >= 300:
            return 0
        with self._lock:
            tmp = self.path + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                f.writelines(json.dumps(x, ensure_ascii=False) + "\n" for x in events[500:])
            os.replace(tmp, self.path)
        return len(batch)
