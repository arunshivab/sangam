"""rc.15: signing in to Sangam with a private key instead of a client secret (``private_key_jwt``, RFC 7523)."""
from __future__ import annotations

import json
import secrets
import time

from joserfc import jwt
from joserfc.jwk import ECKey, RSAKey, import_key

ASSERTION_TYPE = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
TOKEN_TYPE = "client-authentication+jwt"
_CURVES = {"P-256": "ES256", "P-384": "ES384", "P-521": "ES512"}


class ClientKey:
    """A private key your back end signs in to Sangam with. Add the matching public key on the partner console
    (Credentials, Keys); ``kid`` is its RFC 7638 thumbprint, the key ID the console shows.

    ``private_key`` is a PEM private key (PKCS#8, or PKCS#1 / SEC1) or a private JSON Web Key: RSA of 2048 bits or more,
    or elliptic curve P-256, P-384 or P-521.
    """

    def __init__(self, private_key: str) -> None:
        text = private_key.strip()
        try:
            if text.startswith("{"):
                data = json.loads(text)
                key = import_key(data, data.get("kty", ""))
            elif "PRIVATE KEY" in text:
                key = import_key(text, "EC" if "EC PRIVATE KEY" in text else "RSA")
            else:
                raise ValueError("Sangam: give the private key (PEM, -----BEGIN PRIVATE KEY-----, or a private JSON Web Key). The public key goes on the partner console.")
        except ValueError:
            raise
        except Exception:  # noqa: BLE001 - an RSA attempt on an EC PKCS#8 key fails: try EC before giving up
            try:
                key = import_key(text, "EC")
            except Exception as e:  # noqa: BLE001
                raise ValueError("Sangam: that private key cannot be read.") from e
        if not key.is_private:
            raise ValueError("Sangam: that is a public key. Your back end signs with the private key; the public key goes on the partner console.")
        if isinstance(key, RSAKey):
            if key.raw_value.key_size < 2048:
                raise ValueError("Sangam: the RSA key must have at least 2048 bits.")
            self.alg = "RS256"
        elif isinstance(key, ECKey) and key.curve_name in _CURVES:
            self.alg = _CURVES[key.curve_name]
        else:
            raise ValueError("Sangam: use an RSA key, or an elliptic-curve key on P-256, P-384 or P-521.")
        self._key = key
        self.kid: str = key.thumbprint()

    def assertion(self, client_id: str, authority: str) -> str:
        """A new assertion for one request to Sangam's token endpoint: ``iss`` and ``sub`` the client id, ``aud`` Sangam's
        issuer (``authority``), a fresh ``jti``, two minutes to live, typed ``client-authentication+jwt``."""
        now = int(time.time())
        claims = {"iss": client_id, "sub": client_id, "aud": authority, "jti": secrets.token_urlsafe(16), "iat": now, "nbf": now, "exp": now + 120}
        return jwt.encode({"alg": self.alg, "kid": self.kid, "typ": TOKEN_TYPE}, claims, self._key, algorithms=[self.alg])

    def form(self, client_id: str, authority: str) -> dict[str, str]:
        """The form fields that authenticate a token request."""
        return {"client_assertion_type": ASSERTION_TYPE, "client_assertion": self.assertion(client_id, authority)}
