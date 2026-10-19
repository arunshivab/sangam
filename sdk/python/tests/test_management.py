import httpx
import pytest

from sangam import ManagementClient, SangamApiError


def test_the_management_client_caches_its_token_and_calls_api_v1():
    calls = []

    def handler(request: httpx.Request) -> httpx.Response:
        calls.append(request)
        if request.url.path == "/connect/token":
            return httpx.Response(200, json={"access_token": "t1", "expires_in": 3600})
        if request.url.path.endswith("/forbidden"):
            return httpx.Response(403, json={})
        return httpx.Response(200, json={"ok": True})

    client = ManagementClient("https://id.example.in/", "app", "s", client=httpx.Client(transport=httpx.MockTransport(handler)))
    client.upsert_role("nurse", "Nurse", ["vitals:read"])
    client.upsert_membership("o1", "u1", "admin-token", "nurse", True, "2026-12-31T23:59:59+05:30")
    assert sum(1 for c in calls if c.url.path == "/connect/token") == 1
    assert str(calls[1].url) == "https://id.example.in/api/v1/roles/nurse"
    assert calls[1].headers["authorization"] == "Bearer t1"
    assert b'"expiresAt": "2026-12-31T23:59:59+05:30"' in calls[2].content or b'"expiresAt":"2026-12-31T23:59:59+05:30"' in calls[2].content
    with pytest.raises(SangamApiError) as e:
        client.send("GET", "forbidden")
    assert e.value.status == 403


def test_changing_a_place_sends_the_admins_own_token():
    # rc.13: as adding staff does; rc.14: setting a person's values too.
    seen = []

    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/connect/token":
            return httpx.Response(200, json={"access_token": "t1", "expires_in": 3600})
        seen.append((request.method, request.url.path, request.headers.get("sangam-acting-admin")))
        return httpx.Response(200, json={"ok": True})

    client = ManagementClient("https://id.example.in", "app", "s", client=httpx.Client(transport=httpx.MockTransport(handler)))
    client.set_roles("o1", "u1", "admin-token", ["doctor"])
    client.remove_member("o1", "u1", "admin-token")
    client.set_attributes("u1", "admin-token", {"badge": "S-77"})
    assert seen == [
        ("PUT", "/api/v1/orgs/o1/members/u1", "admin-token"),
        ("DELETE", "/api/v1/orgs/o1/members/u1", "admin-token"),
        ("PUT", "/api/v1/users/u1/attributes", "admin-token"),
    ]


def test_merged_into_gives_the_surviving_id_and_none_for_an_id_that_was_not_merged():
    # rc.7: a 404 is an answer ("not merged, or not yours"), not an error.
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/connect/token":
            return httpx.Response(200, json={"access_token": "t1", "expires_in": 3600})
        if request.url.path == "/api/v1/users/u-old/merged":
            return httpx.Response(200, json={"merged_into": "u-new", "merged_at": "2026-10-12T05:00:00Z"})
        return httpx.Response(404, json={})

    client = ManagementClient("https://id.example.in/", "app", "s", client=httpx.Client(transport=httpx.MockTransport(handler)))
    assert client.merged_into("u-old") == "u-new"
    assert client.merged_into("u-other") is None


def test_lock_session_sends_the_persons_own_token():
    seen = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append((request.method, str(request.url), request.headers["authorization"]))
        return httpx.Response(204 if len(seen) == 1 else 404)

    client = ManagementClient("https://id.example.in/", "app", "s", client=httpx.Client(transport=httpx.MockTransport(handler)))
    assert client.lock_session("person-token") is True
    assert client.lock_session("person-token") is False
    assert seen[0] == ("POST", "https://id.example.in/api/v1/sessions/lock", "Bearer person-token")


def test_end_emergency_sends_the_persons_own_token_and_the_reason():
    seen = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append((str(request.url), request.headers["authorization"], request.content))
        return httpx.Response(204)

    client = ManagementClient("https://id.example.in/", "app", "s", client=httpx.Client(transport=httpx.MockTransport(handler)))
    assert client.end_emergency("person-token", "Care handed over") is True
    assert seen[0][0] == "https://id.example.in/api/v1/sessions/emergency/end"
    assert seen[0][1] == "Bearer person-token"
    assert b"Care handed over" in seen[0][2]
    with pytest.raises(ValueError):
        client.end_emergency("t", " ")
