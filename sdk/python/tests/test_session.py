"""rc.5 (ASVS V3.4.4): the session cookie's name."""
from sangam.session import cookie_name


def test_cookie_name_is_host_prefixed_over_https():
    assert cookie_name(True) == "__Host-sangam.sid"
    assert cookie_name(False) == "sangam.sid"
