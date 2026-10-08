"""The shared conformance vectors every Sangam SDK runs (sdk/conformance/vectors.json)."""
import json
import os

import pytest

from sangam import AuditConfig, AuditEntry, SangamUser, build_audit_event, has_permission, has_role, parse_memberships, roles_in, satisfies, step_up_challenge, uuid7, validate_audit_event, verify_webhook

V = json.load(open(os.path.join(os.path.dirname(__file__), "..", "..", "conformance", "vectors.json"), encoding="utf-8"))
USER = SangamUser(id="x", memberships=tuple(parse_memberships(V["permissions"]["sangam_orgs"])))


@pytest.mark.parametrize("c", V["permissions"]["has_permission"], ids=lambda c: c["why"])
def test_permissions(c):
    assert has_permission(USER, c["org_path"], c["permission"]) is c["expect"]


def test_roles_and_malformed_claims():
    for c in V["permissions"]["has_role"]:
        assert has_role(USER, c["org_path"], c["role"]) is c["expect"]
    for c in V["permissions"]["roles_in"]:
        assert roles_in(USER, c["org_path"]) == c["expect"]
    for c in V["permissions"]["malformed_claims"]:
        assert len(parse_memberships(c["claim"])) == c["memberships"]
    assert has_permission(None, "/x/", "a:b") is False


@pytest.mark.parametrize("c", V["step_up"]["cases"], ids=lambda c: c["why"])
def test_step_up(c):
    assert satisfies(c["acr"], c["auth_time"], c["level"], c["max_age"], V["step_up"]["now"]) is c["expect"]


def test_challenge():
    for c in V["challenge"]:
        assert step_up_challenge(c["level"], c["max_age"]) == c["header"]


@pytest.mark.parametrize("c", V["webhooks"]["cases"], ids=lambda c: c["why"])
def test_webhooks(c):
    assert verify_webhook(c["id"], c["timestamp"], c["signature"], c["body"], c["secret"], c["now"]) is c["expect"]


@pytest.mark.parametrize("c", V["audit"]["validation"], ids=lambda c: c["why"])
def test_audit_validation(c):
    assert (validate_audit_event(c["event"]) == []) is c["valid"]


def test_audit_builder():
    b = V["audit"]["builder"]
    i = b["input"]
    user = SangamUser(id=b["user"]["sub"], session_id=b["user"]["sid"], acr=b["user"]["acr"], amr=tuple(b["user"]["amr"]))
    entry = AuditEntry(action=i["action"], category=i["category"], target_type=i["target"]["type"], target_id=i["target"]["id"], target_display=i["target"]["display"],
                       outcome=i["outcome"], org_id=i["tenant"]["org_id"], org_path=i["tenant"]["org_path"], data_classification=i["data_classification"],
                       changes=i["changes"], sensitive=i["sensitive"])
    e = build_audit_event(AuditConfig(**b["config"]), user, b["request"]["ip"], b["request"]["user_agent"], entry)
    assert validate_audit_event(e) == []
    for k, v in b["expect"].items():
        assert e[k] == v, k
    for k in b["absent"]:
        assert k not in e


def test_the_schema_file_agrees_with_the_vectors():
    jsonschema = pytest.importorskip("jsonschema")
    schema = json.load(open(os.path.join(os.path.dirname(__file__), "..", "..", "schema", "audit-event-1.0.schema.json"), encoding="utf-8"))
    validator = jsonschema.Draft202012Validator(schema)
    for c in V["audit"]["validation"]:
        assert (not list(validator.iter_errors(c["event"]))) is c["valid"], c["why"]


def test_uuid7_is_time_ordered():
    a, b = uuid7(1_700_000_000_000), uuid7(1_700_000_000_001)
    assert a < b and a[14] == "7"
