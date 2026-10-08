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
    client.upsert_membership("o1", "u1", "nurse", True, "2026-12-31T23:59:59+05:30")
    assert sum(1 for c in calls if c.url.path == "/connect/token") == 1
    assert str(calls[1].url) == "https://id.example.in/api/v1/roles/nurse"
    assert calls[1].headers["authorization"] == "Bearer t1"
    assert b'"expiresAt": "2026-12-31T23:59:59+05:30"' in calls[2].content or b'"expiresAt":"2026-12-31T23:59:59+05:30"' in calls[2].content
    with pytest.raises(SangamApiError) as e:
        client.send("GET", "forbidden")
    assert e.value.status == 403
