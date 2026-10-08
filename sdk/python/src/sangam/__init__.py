"""Sangam for Python (R6, SGM-306): the same concepts and rules as every Sangam SDK.

The FastAPI and Flask adapters are in ``sangam.fastapi`` and ``sangam.flask`` (install the extra).
"""
from .audit import AuditBuffer, AuditConfig, AuditEntry, MASKED, SCHEMA_VERSION, build_audit_event, uuid7, validate_audit_event
from .management import ManagementClient, SangamApiError
from .memberships import SangamMembership, SangamUser, has_permission, has_role, parse_memberships, roles_in, user_from_claims
from .stepup import SIGNATURE_MAX_AGE_SECONDS, SangamAcr, satisfies, step_up_challenge
from .tokens import InvalidToken, TokenVerifier, VerifiedToken
from .web import SangamCore, SangamSettings
from .webhooks import verify_webhook

__version__ = "1.0.0rc2"
__all__ = [
    "AuditBuffer", "AuditConfig", "AuditEntry", "MASKED", "SCHEMA_VERSION", "build_audit_event", "uuid7", "validate_audit_event",
    "ManagementClient", "SangamApiError",
    "SangamMembership", "SangamUser", "has_permission", "has_role", "parse_memberships", "roles_in", "user_from_claims",
    "SIGNATURE_MAX_AGE_SECONDS", "SangamAcr", "satisfies", "step_up_challenge",
    "InvalidToken", "TokenVerifier", "VerifiedToken",
    "SangamCore", "SangamSettings",
    "verify_webhook",
]
