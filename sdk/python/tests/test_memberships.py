"""rc.12 (A6): the organisation chosen for this sign-in, from ``sangam_org``."""
import json

from sangam import user_from_claims
from sangam.web import user_from_dict, user_to_dict


def test_the_chosen_organisation_is_read_with_every_role_there():
    org = {"id": "0192a6b0-0000-7000-8000-000000000003", "name": "Apulki Pune", "type": "hospital", "path": "/g/p/", "roles": ["doctor", "administrator"], "permissions": ["staff:manage"]}
    user = user_from_claims({"sub": "x", "sangam_org": json.dumps(org)})
    assert user.current_organisation is not None
    assert user.current_organisation.organisation_name == "Apulki Pune"
    assert user.current_organisation.roles == ("doctor", "administrator")
    # It survives the session store.
    assert user_from_dict(user_to_dict(user)).current_organisation == user.current_organisation
    assert user_from_claims({"sub": "x", "sangam_org": "not json"}).current_organisation is None


def test_emergency_access_is_read_until_its_end_and_survives_the_session_store():
    """rc.12 (M12): ``sangam_emergency`` carries the use, your case reference, the organisation and the end."""
    claim = {"id": "0192a6b0-0000-7000-8000-0000000000e1", "case": "LIPI-48213", "org": "0192a6b0-0000-7000-8000-000000000003", "until": 1792000000}
    user = user_from_claims({"sub": "x", "sangam_emergency": json.dumps(claim)})
    assert user.emergency is not None
    assert user.emergency.case_reference == "LIPI-48213"
    assert user.emergency.is_active(1791999999) and not user.emergency.is_active(1792000000)
    assert user_from_dict(user_to_dict(user)).emergency == user.emergency
    assert user_from_claims({"sub": "x", "sangam_emergency": {"case": "x"}}).emergency is None
