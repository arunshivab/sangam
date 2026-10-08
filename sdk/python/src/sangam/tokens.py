"""Access-token checks for an API: signature against Sangam's published keys, issuer, expiry, audience."""
from __future__ import annotations

import time
from dataclasses import dataclass
from typing import Any

import httpx
from joserfc import jwt
from joserfc.errors import JoseError
from joserfc.jwk import KeySet

from .memberships import SangamUser, user_from_claims


ALGORITHMS = ["RS256", "PS256", "ES256"]


class InvalidToken(Exception):
    """The token is not Sangam's, has expired, or is not for this API."""


@dataclass(frozen=True)
class VerifiedToken:
    user: SangamUser
    claims: dict[str, Any]
    scopes: tuple[str, ...]


class TokenVerifier:
    """Checks Sangam access tokens (RS256 or PS256 JWTs); keys are fetched from discovery and refreshed when unknown."""

    def __init__(self, issuer: str, audience: str | None = None, jwks_uri: str | None = None, client: httpx.Client | None = None) -> None:
        self.issuer = issuer if issuer.endswith("/") else issuer + "/"
        self.audience = audience
        self._jwks_uri = jwks_uri
        self._http = client or httpx.Client(timeout=10)
        self._keys: Any = None
        self._fetched = 0.0

    def _load(self, force: bool = False) -> Any:
        if self._keys is not None and not force and time.time() - self._fetched < 3600:
            return self._keys
        if not self._jwks_uri:
            self._jwks_uri = self._http.get(self.issuer + ".well-known/openid-configuration").raise_for_status().json()["jwks_uri"]
        self._keys = KeySet.import_key_set(self._http.get(self._jwks_uri).raise_for_status().json())
        self._fetched = time.time()
        return self._keys

    def verify(self, token: str) -> VerifiedToken:
        options: dict[str, Any] = {"iss": {"essential": True, "value": self.issuer}, "exp": {"essential": True}}
        if self.audience:
            options["aud"] = {"essential": True, "value": self.audience}
        registry = jwt.JWTClaimsRegistry(leeway=30, **options)
        claims: dict[str, Any] | None = None
        for refresh in (False, True):
            # An unknown key may be a rotation: fetch the keys again once before refusing.
            try:
                decoded = jwt.decode(token, self._load(force=refresh), algorithms=ALGORITHMS)
                registry.validate(decoded.claims)
                claims = decoded.claims
                break
            except (JoseError, ValueError) as error:
                if refresh:
                    raise InvalidToken(str(error)) from error
        assert claims is not None
        data = dict(claims)
        scope = data.get("scope")
        scopes = tuple(scope.split()) if isinstance(scope, str) else tuple(data.get("scp") or ())
        return VerifiedToken(user_from_claims(data), data, scopes)
