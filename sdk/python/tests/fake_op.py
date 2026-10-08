"""A small OpenID provider on a real port: discovery, keys, and a token endpoint that checks PKCE as Sangam does."""
import base64
import hashlib
import json
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

from joserfc import jwt
from joserfc.jwk import RSAKey

ORG = "/0192a6b0-0000-7000-8000-000000000001/"
ORGS = [{"id": "0192a6b0-0000-7000-8000-000000000001", "name": "Apulki Hospital", "type": "hospital", "path": ORG, "role": "nurse", "permissions": ["vitals:read"], "inherits": True}]
SUB = "0192a6b0-0000-7000-8000-000000000042"


class FakeOP:
    def __init__(self, client_id="app", client_secret="secret"):
        self.key = RSAKey.generate_key(2048, parameters={"kid": "k1", "use": "sig", "alg": "RS256"})
        self.codes = {}
        self.client_id, self.client_secret = client_id, client_secret
        op = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def _json(self, body, status=200):
                data = json.dumps(body).encode()
                self.send_response(status)
                self.send_header("content-type", "application/json")
                self.send_header("content-length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def do_GET(self):
                path = urlparse(self.path).path
                if path == "/.well-known/openid-configuration":
                    self._json({"issuer": op.base + "/", "authorization_endpoint": op.base + "/connect/authorize", "token_endpoint": op.base + "/connect/token",
                                "jwks_uri": op.base + "/jwks", "end_session_endpoint": op.base + "/connect/endsession"})
                elif path == "/jwks":
                    self._json({"keys": [op.key.as_dict(private=False)]})
                else:
                    self._json({}, 404)

            def do_POST(self):
                form = {k: v[0] for k, v in parse_qs(self.rfile.read(int(self.headers["content-length"])).decode()).items()}
                grant = op.codes.pop(form.get("code"), None)
                challenge = base64.urlsafe_b64encode(hashlib.sha256(form.get("code_verifier", "").encode()).digest()).rstrip(b"=").decode()
                if form.get("grant_type") == "client_credentials" and form.get("client_secret") == op.client_secret:
                    self._json({"access_token": "cc-token", "expires_in": 3600, "token_type": "Bearer"})
                    return
                if not grant or grant["challenge"] != challenge or form.get("client_secret") != op.client_secret or form.get("redirect_uri") != grant["redirect"]:
                    self._json({"error": "invalid_grant"}, 400)
                    return
                self._json({"access_token": "at", "token_type": "Bearer", "expires_in": 300, "id_token": op.id_token(grant["nonce"], grant["acr"])})

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.base = f"http://127.0.0.1:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def id_token(self, nonce, acr, aud=None, iss=None):
        now = int(time.time())
        claims = {"iss": iss or self.base + "/", "aud": aud or self.client_id, "sub": SUB, "nonce": nonce, "iat": now, "exp": now + 300, "name": "Meera Nair",
                  "sangam_orgs": ORGS, "acr": acr, "amr": ["pwd", "otp"], "auth_time": now, "sid": "s-1"}
        return jwt.encode({"alg": "RS256", "kid": "k1"}, claims, self.key)

    def access_token(self, **extra):
        now = int(time.time())
        claims = {"iss": self.base + "/", "sub": SUB, "iat": now, "exp": now + 300, "sangam_orgs": ORGS, **extra}
        return jwt.encode({"alg": "RS256", "kid": "k1"}, claims, self.key)

    def approve(self, authorize_url, acr="urn:sangam:acr:1"):
        q = {k: v[0] for k, v in parse_qs(urlparse(authorize_url).query).items()}
        code = uuid.uuid4().hex
        self.codes[code] = {"challenge": q["code_challenge"], "nonce": q["nonce"], "acr": acr, "redirect": q["redirect_uri"]}
        return code, q["state"]

    def close(self):
        self.server.shutdown()
