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
class SangamCurrentOrganisation:
    """rc.12 (A6): the organisation the person chose to work at for this sign-in, from ``sangam_org``."""

    organisation_id: str
    organisation_name: str
    organisation_type: str
    path: str
    roles: tuple[str, ...]
    """Every role held there; empty when your application keeps roles itself."""
    permissions: tuple[str, ...]


@dataclass(frozen=True)
class SangamEmergency:
    """rc.12 (M12): emergency access in progress, from ``sangam_emergency``.

    Show a banner, apply your own limits, and stop at ``until`` (Unix seconds) even if the token lives longer.
    """

    id: str
    case_reference: str
    """Your own case reference, as you sent it."""
    organisation_id: str
    until: int

    def is_active(self, now: int | None = None) -> bool:
        import time

        return (int(time.time()) if now is None else now) < self.until


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
    current_organisation: SangamCurrentOrganisation | None = None
    """rc.12: where they are working for this sign-in (ask again with ``prompt=select_org``)."""
    emergency: SangamEmergency | None = None
    """rc.12 (M12): emergency access in progress for this sign-in."""

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
        org_id, path = item.get("id"), item.get("path")
        if not (isinstance(org_id, str) and _UUID.match(org_id) and isinstance(path, str) and path):
            continue
        # rc.12: one entry carries every role held there ("roles"; older tokens carry only "role"). Each role is one
        # membership; a place with no role in Sangam (the application keeps roles itself) is one with an empty role.
        listed = item.get("roles")
        roles = [r for r in listed if isinstance(r, str) and r] if isinstance(listed, list) else []
        single = item.get("role")
        if not roles and isinstance(single, str) and single:
            roles = [single]
        if not roles:
            roles = [""]
        permissions = item.get("permissions")
        perms = tuple(p for p in permissions if isinstance(p, str) and p) if isinstance(permissions, list) else ()
        for role in dict.fromkeys(roles):
            result.append(
                SangamMembership(
                    organisation_id=org_id,
                    organisation_name=item.get("name") if isinstance(item.get("name"), str) else "",
                    organisation_type=item.get("type") if isinstance(item.get("type"), str) else "",
                    path=path,
                    role=role,
                    permissions=perms,
                    applies_to_descendants=item.get("inherits") is True,
                )
            )
    return result


def parse_current_organisation(claim: Any) -> SangamCurrentOrganisation | None:
    """Reads ``sangam_org``: a JSON string or one object. Anything malformed is no organisation."""
    value = claim
    if isinstance(claim, str):
        try:
            value = json.loads(claim)
        except ValueError:
            return None
    if not isinstance(value, dict):
        return None
    org_id, path = value.get("id"), value.get("path")
    if not (isinstance(org_id, str) and _UUID.match(org_id) and isinstance(path, str) and path):
        return None
    listed = value.get("roles")
    roles = [r for r in listed if isinstance(r, str) and r] if isinstance(listed, list) else []
    if not roles and isinstance(value.get("role"), str) and value.get("role"):
        roles = [value["role"]]
    permissions = value.get("permissions")
    return SangamCurrentOrganisation(
        organisation_id=org_id,
        organisation_name=value.get("name") if isinstance(value.get("name"), str) else "",
        organisation_type=value.get("type") if isinstance(value.get("type"), str) else "",
        path=path,
        roles=tuple(roles),
        permissions=tuple(p for p in permissions if isinstance(p, str) and p) if isinstance(permissions, list) else (),
    )


def parse_emergency(claim: Any) -> SangamEmergency | None:
    """Reads ``sangam_emergency``: a JSON string or one object. Anything malformed is no emergency access."""
    value = claim
    if isinstance(claim, str):
        try:
            value = json.loads(claim)
        except ValueError:
            return None
    if not isinstance(value, dict):
        return None
    use, org, case, until = value.get("id"), value.get("org"), value.get("case"), value.get("until")
    if not (isinstance(use, str) and _UUID.match(use) and isinstance(org, str) and _UUID.match(org) and isinstance(case, str) and case):
        return None
    if not isinstance(until, int) or isinstance(until, bool):
        return None
    return SangamEmergency(id=use, case_reference=case, organisation_id=org, until=until)


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
        current_organisation=parse_current_organisation(claims.get("sangam_org")),
        emergency=parse_emergency(claims.get("sangam_emergency")),
    )


def has_permission(user: SangamUser | None, organisation_path: str, permission: str) -> bool:
    """Granted at the organisation, or at an ancestor whose membership inherits. Deny by default."""
    return user is not None and any(m.covers(organisation_path) and permission in m.permissions for m in user.memberships)


def has_role(user: SangamUser | None, organisation_path: str, role: str) -> bool:
    return user is not None and bool(role) and any(m.role == role and m.covers(organisation_path) for m in user.memberships)


def roles_in(user: SangamUser | None, organisation_path: str) -> list[str]:
    if user is None:
        return []
    return sorted({m.role for m in user.memberships if m.role and m.covers(organisation_path)})
