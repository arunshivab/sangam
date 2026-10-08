"""Sign in with Sangam for Flask: a blueprint, decorators and the audit helper."""
from __future__ import annotations

from functools import wraps
from typing import Any, Callable

from flask import Blueprint, Flask, Response, g, jsonify, redirect, request

from .audit import AuditEntry
from .memberships import SangamUser, has_permission
from .session import COOKIE
from .stepup import satisfies, step_up_challenge
from .tokens import InvalidToken
from .web import SangamCore, SangamSettings, user_to_dict


def _wants_json() -> bool:
    return "application/json" in request.headers.get("Accept", "") or request.headers.get("X-Requested-With") == "fetch"


class SangamFlask:
    """``sangam = SangamFlask(app, SangamSettings(...))``; then ``@sangam.login_required``, ``@sangam.require_step_up(...)``."""

    def __init__(self, app: Flask | None = None, settings: SangamSettings | None = None, core: SangamCore | None = None) -> None:
        if settings is None:
            raise ValueError("settings are required")
        self.settings = settings
        self.core = core or SangamCore(settings)
        bp = Blueprint("sangam", __name__, url_prefix=self.core.base_path)
        bp.add_url_rule("/login", "login", self._login)
        bp.add_url_rule("/callback", "callback", self._callback)
        bp.add_url_rule("/logout", "logout", self._logout)
        bp.add_url_rule("/me", "me", self._me)
        self.blueprint = bp
        if app is not None:
            app.register_blueprint(bp)

    def _cookie(self, response: Response, value: str, max_age: int | None = None) -> Response:
        response.set_cookie(COOKIE, value, max_age=self.settings.session_ttl if max_age is None else max_age, httponly=True, samesite="Lax", secure=self.core.secure, path="/")
        return response

    def _login(self) -> Response:
        url, cookie = self.core.begin(request.cookies.get(COOKIE), request.args.get("returnTo"), request.args.get("acr"), request.args.get("max_age"))
        return self._cookie(redirect(url, 302), cookie)

    def _callback(self) -> Response:
        target, cookie = self.core.finish(request.cookies.get(COOKIE), request.args.to_dict())
        return self._cookie(redirect(target, 302), cookie)

    def _logout(self) -> Response:
        return self._cookie(redirect(self.core.end(request.cookies.get(COOKIE)), 302), "", 0)

    def _me(self) -> Any:
        user = self.current_user()
        if user is None:
            return jsonify({"user": None, "loginUrl": self.core.login_url("/")}), 401
        return jsonify({"user": user_to_dict(user), "loginUrl": self.core.login_url("/"), "logoutUrl": f"{self.core.base_path}/logout"})

    def current_user(self) -> SangamUser | None:
        if "sangam_user" not in g:
            g.sangam_user = self.core.user(request.cookies.get(COOKIE))
        return g.sangam_user

    def _send_to_sangam(self, login: str, error: str, acr: str | None = None, max_age: int | None = None) -> Any:
        if _wants_json():
            return jsonify({"error": error, "acr": acr, "max_age": max_age, "login": login}), 401
        return redirect(login, 302)

    def login_required(self, view: Callable[..., Any]) -> Callable[..., Any]:
        @wraps(view)
        def wrapper(*args: Any, **kwargs: Any) -> Any:
            if self.current_user() is None:
                return self._send_to_sangam(self.core.login_url(request.full_path.rstrip("?")), "sign_in_required")
            return view(*args, **kwargs)

        return wrapper

    def require_step_up(self, acr: str, max_age: int | None = None, return_to: Callable[[], str] | None = None) -> Callable[[Callable[..., Any]], Callable[..., Any]]:
        def decorate(view: Callable[..., Any]) -> Callable[..., Any]:
            @wraps(view)
            def wrapper(*args: Any, **kwargs: Any) -> Any:
                if not self.core.stepped_up(self.current_user(), acr, max_age):
                    return self._send_to_sangam(self.core.login_url(return_to() if return_to else request.path, acr, max_age), "step_up_required", acr, max_age)
                return view(*args, **kwargs)

            return wrapper

        return decorate

    def require_permission(self, organisation_path: Callable[[], str | None], permission: str) -> Callable[[Callable[..., Any]], Callable[..., Any]]:
        def decorate(view: Callable[..., Any]) -> Callable[..., Any]:
            @wraps(view)
            def wrapper(*args: Any, **kwargs: Any) -> Any:
                user = self.current_user()
                if user is None:
                    return self._send_to_sangam(self.core.login_url(request.path), "sign_in_required")
                path = organisation_path()
                if not path or not has_permission(user, path, permission):
                    return jsonify({"error": "forbidden", "permission": permission}), 403
                return view(*args, **kwargs)

            return wrapper

        return decorate

    def require_token(self, scope: str | None = None, acr: str | None = None, max_age: int | None = None) -> Callable[[Callable[..., Any]], Callable[..., Any]]:
        def decorate(view: Callable[..., Any]) -> Callable[..., Any]:
            @wraps(view)
            def wrapper(*args: Any, **kwargs: Any) -> Any:
                header = request.headers.get("Authorization", "")
                if not header.startswith("Bearer "):
                    return jsonify({"error": "invalid_token"}), 401, {"WWW-Authenticate": 'Bearer realm="sangam"'}
                try:
                    verified = self.core.tokens.verify(header[7:])
                except InvalidToken:
                    return jsonify({"error": "invalid_token"}), 401, {"WWW-Authenticate": 'Bearer error="invalid_token"'}
                if scope and scope not in verified.scopes:
                    return jsonify({"error": "insufficient_scope"}), 403, {"WWW-Authenticate": f'Bearer error="insufficient_scope", scope="{scope}"'}
                if acr and not satisfies(verified.user.acr, verified.user.auth_time, acr, max_age):
                    return jsonify({"error": "insufficient_user_authentication"}), 401, {"WWW-Authenticate": step_up_challenge(acr, max_age)}
                g.sangam_token = verified
                return view(*args, **kwargs)

            return wrapper

        return decorate

    def record(self, entry: AuditEntry) -> dict[str, Any]:
        """Records a shared audit event (SGM-208) for the request's signed-in person."""
        forwarded = request.headers.get("X-Forwarded-For")
        ip = forwarded.split(",")[0].strip() if self.settings.trust_proxy and forwarded else request.remote_addr
        token = g.get("sangam_token")
        return self.core.record(token.user if token else self.current_user(), ip, request.headers.get("User-Agent"), entry)
