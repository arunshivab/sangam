"""Who signed in, and what they may do in which organisation (SGM-306): the same rule as every Sangam SDK."""
from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from typing import Any, Iterable, Mapping

_UUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", re.I)


@dataclass(frozen=True)
class SangamMembership:
    """One role a person holds at one organisation, from the ``sangam_orgs`` claim."""

    organisation_id: str
    organisation_name: str
    organisation_type: str
    path: str
    """Materialised path, ending with "/"."""
    role: str
    permissions: tuple[str, ...]
    applies_to_descendants: bool

    def covers(self, organisation_path: str) -> bool:
        """The organisation itself, or a descendant when the role is inherited. Paths compare case-insensitively."""
        target, own = organisation_path.lower(), self.path.lower()
        if target == own:
            return True
        # Paths end with "/", so "/a/" never matches a sibling "/ab/".
        return self.applies_to_descendants and own.endswith("/") and target.startswith(own)


@dataclass(frozen=True)
class SangamUser:
    """The signed-in person: identify them by ``id`` (the Sangam subject id) only."""

    id: str
    name: str = ""
    email: str | None = None
    memberships: tuple[SangamMembership, ...] = ()
    acr: str | None = None
    amr: tuple[str, ...] = ()
    auth_time: int | None = None
    session_id: str | None = None
    identity_verified: bool = False
    claims: Mapping[str, Any] = field(default_factory=dict, compare=False, repr=False)

    def has_permission(self, organisation_path: str, permission: str) -> bool:
        return has_permission(self, organisation_path, permission)

    def has_role(self, organisation_path: str, role: str) -> bool:
        return has_role(self, organisation_path, role)

    def roles_in(self, organisation_path: str) -> list[str]:
        return roles_in(self, organisation_path)


def parse_memberships(claim: Any) -> list[SangamMembership]:
    """Reads ``sangam_orgs``: a JSON string, a list, or one object. Anything malformed is skipped, never fatal."""
    value = claim
    if isinstance(claim, str):
        try:
            value = json.loads(claim)
        except ValueError:
            return []
    items: Iterable[Any] = value if isinstance(value, list) else ([] if value is None else [value])
    result: list[SangamMembership] = []
    for item in items:
        if not isinstance(item, dict):
            continue
        org_id, path, role = item.get("id"), item.get("path"), item.get("role")
        if not (isinstance(org_id, str) and _UUID.match(org_id) and isinstance(path, str) and path and isinstance(role, str) and role):
            continue
        permissions = item.get("permissions")
        result.append(
            SangamMembership(
                organisation_id=org_id,
                organisation_name=item.get("name") if isinstance(item.get("name"), str) else "",
                organisation_type=item.get("type") if isinstance(item.get("type"), str) else "",
                path=path,
                role=role,
                permissions=tuple(p for p in permissions if isinstance(p, str)) if isinstance(permissions, list) else (),
                applies_to_descendants=item.get("inherits") is True,
            )
        )
    return result


def user_from_claims(claims: Mapping[str, Any]) -> SangamUser:
    """The person from token claims: an ID token, an access token or userinfo."""
    email = claims.get("email") if isinstance(claims.get("email"), str) else None
    amr = claims.get("amr")
    return SangamUser(
        id=str(claims.get("sub") or ""),
        name=claims.get("name") if isinstance(claims.get("name"), str) else (email or ""),
        email=email,
        memberships=tuple(parse_memberships(claims.get("sangam_orgs"))),
        acr=claims.get("acr") if isinstance(claims.get("acr"), str) else None,
        amr=tuple(a for a in amr if isinstance(a, str)) if isinstance(amr, list) else (),
        auth_time=claims.get("auth_time") if isinstance(claims.get("auth_time"), int) else None,
        session_id=claims.get("sid") if isinstance(claims.get("sid"), str) else None,
        identity_verified=claims.get("sangam_identity_verified") is True,
        claims=dict(claims),
    )


def has_permission(user: SangamUser | None, organisation_path: str, permission: str) -> bool:
    """Granted at the organisation, or at an ancestor whose membership inherits. Deny by default."""
    return user is not None and any(m.covers(organisation_path) and permission in m.permissions for m in user.memberships)


def has_role(user: SangamUser | None, organisation_path: str, role: str) -> bool:
    return user is not None and any(m.role == role and m.covers(organisation_path) for m in user.memberships)


def roles_in(user: SangamUser | None, organisation_path: str) -> list[str]:
    if user is None:
        return []
    return sorted({m.role for m in user.memberships if m.covers(organisation_path)})
