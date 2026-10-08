"""Sign in with Sangam for FastAPI (and Starlette): routes, dependencies and the audit helper."""
from __future__ import annotations

from typing import Any, Callable

from fastapi import APIRouter, Depends, FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse, RedirectResponse, Response
from starlette.concurrency import run_in_threadpool

from .audit import AuditEntry
from .memberships import SangamUser, has_permission
from .session import COOKIE
from .stepup import satisfies, step_up_challenge
from .tokens import InvalidToken, VerifiedToken
from .web import SangamCore, SangamSettings


class SignInRequired(Exception):
    def __init__(self, login: str, error: str = "sign_in_required", acr: str | None = None, max_age: int | None = None) -> None:
        super().__init__(error)
        self.login, self.error, self.acr, self.max_age = login, error, acr, max_age


def _wants_json(request: Request) -> bool:
    return "application/json" in request.headers.get("accept", "") or request.headers.get("x-requested-with") == "fetch"


class SangamFastAPI:
    """
    ``sangam = SangamFastAPI(SangamSettings(...)); sangam.install(app)`` adds /auth/login, /auth/callback, /auth/logout and
    /auth/me, and answers a missing sign-in or step-up with a redirect to Sangam (or 401 with ``login`` to a fetch).
    """

    def __init__(self, settings: SangamSettings, core: SangamCore | None = None) -> None:
        self.core = core or SangamCore(settings)
        self.settings = settings
        self.router = APIRouter(prefix=self.core.base_path)
        self.router.add_api_route("/login", self._login, methods=["GET"], include_in_schema=False)
        self.router.add_api_route("/callback", self._callback, methods=["GET"], include_in_schema=False)
        self.router.add_api_route("/logout", self._logout, methods=["GET"], include_in_schema=False)
        self.router.add_api_route("/me", self._me, methods=["GET"], include_in_schema=False)

    def install(self, app: FastAPI) -> None:
        app.include_router(self.router)

        @app.exception_handler(SignInRequired)
        async def _handler(request: Request, exc: SignInRequired) -> Response:  # noqa: ANN202
            if _wants_json(request):
                return JSONResponse({"error": exc.error, "acr": exc.acr, "max_age": exc.max_age, "login": exc.login}, status_code=401)
            return RedirectResponse(exc.login, status_code=302)

    # ---------------------------------------------------------------- routes
    def _cookie(self, response: Response, value: str, max_age: int | None = None) -> None:
        response.set_cookie(COOKIE, value, max_age=self.settings.session_ttl if max_age is None else max_age, httponly=True, samesite="lax", secure=self.core.secure, path="/")

    async def _login(self, request: Request) -> Response:
        q = request.query_params
        url, cookie = await run_in_threadpool(self.core.begin, request.cookies.get(COOKIE), q.get("returnTo"), q.get("acr"), q.get("max_age"))
        response = RedirectResponse(url, status_code=302)
        self._cookie(response, cookie)
        return response

    async def _callback(self, request: Request) -> Response:
        target, cookie = await run_in_threadpool(self.core.finish, request.cookies.get(COOKIE), dict(request.query_params))
        response = RedirectResponse(target, status_code=302)
        self._cookie(response, cookie)
        return response

    async def _logout(self, request: Request) -> Response:
        url = await run_in_threadpool(self.core.end, request.cookies.get(COOKIE))
        response = RedirectResponse(url, status_code=302)
        self._cookie(response, "", max_age=0)
        return response

    async def _me(self, request: Request) -> Response:
        user = self.core.user(request.cookies.get(COOKIE))
        if user is None:
            return JSONResponse({"user": None, "loginUrl": self.core.login_url("/")}, status_code=401)
        from .web import user_to_dict

        return JSONResponse({"user": user_to_dict(user), "loginUrl": self.core.login_url("/"), "logoutUrl": f"{self.core.base_path}/logout"})

    # ---------------------------------------------------------------- dependencies
    def current_user(self, request: Request) -> SangamUser | None:
        """Dependency: the signed-in person, or None."""
        return self.core.user(request.cookies.get(COOKIE))

    def require_user(self) -> Callable[..., SangamUser]:
        """Dependency: the signed-in person; others are sent to sign in."""

        def dependency(request: Request) -> SangamUser:
            user = self.current_user(request)
            if user is None:
                raise SignInRequired(self.core.login_url(str(request.url.path) + (f"?{request.url.query}" if request.url.query else "")))
            return user

        return dependency

    def require_step_up(self, acr: str, max_age: int | None = None, return_to: Callable[[Request], str] | None = None) -> Callable[..., SangamUser]:
        """Dependency: a sign-in at the level, recent enough; others go back to Sangam to authenticate again."""

        def dependency(request: Request) -> SangamUser:
            user = self.current_user(request)
            if user is not None and self.core.stepped_up(user, acr, max_age):
                return user
            target = return_to(request) if return_to else str(request.url.path)
            raise SignInRequired(self.core.login_url(target, acr, max_age), "step_up_required", acr, max_age)

        return dependency

    def require_permission(self, organisation_path: Callable[[Request], str | None], permission: str) -> Callable[..., SangamUser]:
        """Dependency: the permission at the organisation the request names (granted there or inherited); 403 otherwise."""

        def dependency(request: Request, user: SangamUser = Depends(self.require_user())) -> SangamUser:
            path = organisation_path(request)
            if not path or not has_permission(user, path, permission):
                raise HTTPException(403, {"error": "forbidden", "permission": permission})
            return user

        return dependency

    def require_token(self, scope: str | None = None, acr: str | None = None, max_age: int | None = None) -> Callable[..., VerifiedToken]:
        """Dependency for an API called with a Sangam access token; answers short step-up with the RFC 9470 challenge."""

        async def dependency(request: Request) -> VerifiedToken:
            header = request.headers.get("authorization", "")
            if not header.startswith("Bearer "):
                raise HTTPException(401, {"error": "invalid_token"}, headers={"WWW-Authenticate": 'Bearer realm="sangam"'})
            try:
                verified = await run_in_threadpool(self.core.tokens.verify, header[7:])
            except InvalidToken:
                raise HTTPException(401, {"error": "invalid_token"}, headers={"WWW-Authenticate": 'Bearer error="invalid_token"'}) from None
            if scope and scope not in verified.scopes:
                raise HTTPException(403, {"error": "insufficient_scope"}, headers={"WWW-Authenticate": f'Bearer error="insufficient_scope", scope="{scope}"'})
            if acr and not satisfies(verified.user.acr, verified.user.auth_time, acr, max_age):
                raise HTTPException(401, {"error": "insufficient_user_authentication"}, headers={"WWW-Authenticate": step_up_challenge(acr, max_age)})
            return verified

        return dependency

    # ---------------------------------------------------------------- audit
    def record(self, request: Request, entry: AuditEntry, user: SangamUser | None = None) -> dict[str, Any]:
        """Records a shared audit event (SGM-208) for the request's signed-in person."""
        forwarded = request.headers.get("x-forwarded-for")
        ip = forwarded.split(",")[0].strip() if self.settings.trust_proxy and forwarded else (request.client.host if request.client else None)
        return self.core.record(user or self.current_user(request), ip, request.headers.get("user-agent"), entry)
