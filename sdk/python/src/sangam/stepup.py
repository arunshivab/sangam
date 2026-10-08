"""Step-up (SGM-207): is the person's sign-in strong and recent enough for this action?"""
from __future__ import annotations

import time


class SangamAcr:
    SINGLE_FACTOR = "urn:sangam:acr:1"
    TWO_FACTOR = "urn:sangam:acr:2"
    PHISHING_RESISTANT = "urn:sangam:acr:3"
    SIGNATURE = "urn:sangam:acr:sign"
    """For an electronic signature: two factors, at most five minutes ago."""


SIGNATURE_MAX_AGE_SECONDS = 300

_RANK = {SangamAcr.SINGLE_FACTOR: 1, SangamAcr.TWO_FACTOR: 2, SangamAcr.SIGNATURE: 2, SangamAcr.PHISHING_RESISTANT: 3}


def satisfies(acr: str | None, auth_time: int | None, level: str, max_age: int | None = None, now: int | None = None) -> bool:
    """The level or a stronger one, and recent enough; a signature level is capped at five minutes."""
    if level == SangamAcr.SIGNATURE:
        max_age = max_age if max_age is not None and max_age < SIGNATURE_MAX_AGE_SECONDS else SIGNATURE_MAX_AGE_SECONDS
    if _RANK.get(acr or "", 0) < _RANK.get(level, 0):
        return False
    if max_age is None:
        return True
    current = int(time.time()) if now is None else now
    return auth_time is not None and current - auth_time <= max_age


def step_up_challenge(level: str, max_age: int | None = None) -> str:
    """The RFC 9470 ``WWW-Authenticate`` value for an API whose caller must authenticate again."""
    header = f'Bearer error="insufficient_user_authentication", error_description="A stronger or more recent authentication is required", acr_values="{level}"'
    return header if max_age is None else f"{header}, max_age={int(max_age)}"
