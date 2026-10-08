"""Live conformance: drives each SDK sample against a development Sangam (R6, SGM-306 §2).

For each sample given (base URL), a new person registers at Sangam, gets an authenticator and a nurse role at a made-up
hospital through the management API, then in a real browser: signs in to the sample, sees their organisation, checks a
permission (allowed where the role is, denied elsewhere, in the browser and on the server), signs a record — which
makes Sangam ask for a fresh two-factor sign-in (step-up) — and the sample writes a shared audit event that must pass
the shared schema. Screenshots go to OUT.

Development only: it reads codes from Sangam's development outbox and sets the authenticator key in the development
database. Usage: python3 check_samples.py OUT http://localhost:5910 [http://localhost:5920 ...]
"""
import base64, hashlib, hmac, json, os, random, re, struct, subprocess, sys, time, urllib.parse, urllib.request, uuid
from playwright.sync_api import sync_playwright

B = os.environ.get('SANGAM_AUTHORITY', 'http://localhost:5100')
OUT = sys.argv[1]
SAMPLES = sys.argv[2:]
KEY = 'JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP'
PW = 'Correct-Horse-2026!'
CLIENT, SECRET = 'sangam-dev-sample', 'sangam-dev-sample-secret-change-me'
HOSPITAL = '0192a6b0-0000-7000-8000-00000000a001'
HERE = os.path.dirname(os.path.abspath(__file__))
os.makedirs(OUT, exist_ok=True)


def sql(q):
    r = subprocess.run(['psql', '-h', 'localhost', '-U', 'sangam_identity', '-d', os.environ.get('SANGAM_DB', 'sangam_identity'), '-tAc', q],
                       env={**os.environ, 'PGPASSWORD': os.environ.get('SANGAM_DB_PASSWORD', 'sangam_dev')}, check=True, capture_output=True, text=True)
    return r.stdout.strip()


_last_step = [-1]


def totp():
    if 30 - time.time() % 30 < 4:
        time.sleep(5)
    # Since R7 Sangam accepts each authenticator code once (ASVS V2.8.4): wait for a step not used yet.
    while int(time.time()) // 30 <= _last_step[0]:
        time.sleep(1)
    _last_step[0] = int(time.time()) // 30
    h = hmac.new(base64.b32decode(KEY), struct.pack('>Q', _last_step[0]), hashlib.sha1).digest()
    o = h[-1] & 15
    return '%06d' % ((struct.unpack('>I', h[o:o + 4])[0] & 0x7fffffff) % 1000000)


def api(method, path, body=None, token=None, form=None):
    data = urllib.parse.urlencode(form).encode() if form else (json.dumps(body).encode() if body is not None else None)
    req = urllib.request.Request(B + path, data=data, method=method)
    req.add_header('content-type', 'application/x-www-form-urlencoded' if form else 'application/json')
    if token:
        req.add_header('authorization', 'Bearer ' + token)
    with urllib.request.urlopen(req) as r:
        text = r.read().decode()
        return json.loads(text) if text else None


def submit(pg, selector='form button[type=submit] >> nth=0'):
    with pg.expect_navigation():
        pg.click(selector)


def person(pg):
    email = f'sdk-{uuid.uuid4().hex[:8]}@example.in'
    pg.goto(B + '/register')
    pg.fill('#FirstName', 'Meera'); pg.fill('#LastName', 'Nair'); pg.fill('#Email', email)
    pg.fill('#MobileNumber', str(random.randint(7000000000, 9999999999)))
    pg.fill('#BirthDay', '12'); pg.select_option('#BirthMonth', '6'); pg.fill('#BirthYear', '1986'); pg.select_option('#Gender', 'female')
    pg.fill('#Password', PW); pg.check('#AcceptTerms'); pg.wait_for_timeout(2200)
    submit(pg, 'button[type=submit]')
    pg.goto(B + '/dev/outbox')
    code = next(re.search(r'\b\d{6}\b', a.locator('pre').inner_text()).group(0) for a in pg.locator('article.sg-mail').all() if email in a.inner_text())
    pg.goto(B + '/verify'); pg.fill('#Code', code)
    submit(pg, 'form:not(.sg-resend) button[type=submit]')
    uid = sql(f"select id from users where email='{email}'")
    token = api('POST', '/connect/token', form={'grant_type': 'client_credentials', 'client_id': CLIENT, 'client_secret': SECRET, 'scope': 'sangam.manage'})['access_token']
    api('PUT', '/api/v1/roles/nurse', {'displayName': 'Nurse', 'description': 'SDK sample', 'permissions': ['vitals:read', 'vitals:write'], 'orgId': None}, token)
    api('PUT', f'/api/v1/orgs/{HOSPITAL}', {'name': 'Sample Hospital (made up)', 'type': 'hospital', 'parentId': None, 'metadata': None}, token)
    api('PUT', f'/api/v1/orgs/{HOSPITAL}/members/{uid}', {'role': 'nurse', 'appliesToDescendants': True}, token)
    return email, uid


def enrol_authenticator(uid):
    """What the person would do in the account portal: set up an authenticator app."""
    sql(f"update users set two_factor_enabled=true where id='{uid}'")
    sql(f"insert into user_tokens(user_id,login_provider,name,value) values ('{uid}','[AspNetUserStore]','AuthenticatorKey','{KEY}') on conflict do nothing")


def through_sangam(pg, email, sample):
    """Answers whatever Sangam asks — password, authenticator code, consent — until the browser is back at the sample."""
    for _ in range(16):
        pg.wait_for_load_state()
        if pg.url.startswith(sample):
            return
        if 'Too many attempts' in pg.inner_text('body'):
            # Sangam's sign-in rate limit, met when several samples run back to back from one address: wait it out.
            pg.wait_for_timeout(61000); pg.go_back(); continue
        if pg.locator('#Email').count() and pg.locator('#Password').count():
            if not pg.input_value('#Email'):
                pg.fill('#Email', email)
            pg.fill('#Password', PW); pg.wait_for_timeout(2200); submit(pg)
        elif pg.locator('#Password').count():
            pg.fill('#Password', PW); pg.wait_for_timeout(1200); submit(pg)
        elif pg.locator('#Code').count():
            pg.fill('#Code', totp()); submit(pg)
        elif pg.locator('button[value=Allow], .sg-btn--consent-allow').count():
            submit(pg, 'button[value=Allow], .sg-btn--consent-allow')
        else:
            pg.screenshot(path=f'{OUT}/stuck.png', full_page=True)
            raise RuntimeError('stuck at ' + pg.url)
    raise RuntimeError('too many steps at Sangam')


def validate(event):
    """The shared schema, through the JSON Schema itself."""
    import jsonschema
    schema = json.load(open(os.path.join(HERE, '..', '..', 'schema', 'audit-event-1.0.schema.json')))
    return [e.message for e in jsonschema.Draft202012Validator(schema).iter_errors(event)]


report = {}
with sync_playwright() as p:
    browser = p.chromium.launch()
    for sample in SAMPLES:
        name = urllib.parse.urlparse(sample).port
        ctx = browser.new_context(viewport={'width': 1200, 'height': 900})
        pg = ctx.new_page()
        email, uid = person(pg)
        ctx.clear_cookies()
        r = {'sample': sample, 'person': email}
        pg.goto(sample + '/'); pg.wait_for_load_state('networkidle')
        pg.screenshot(path=f'{OUT}/{name}-1-signed-out.png', full_page=True)
        pg.click('button:has-text("Sign in with Sangam"), a:has-text("Sign in with Sangam")')
        pg.wait_for_url(lambda u: not u.startswith(sample), timeout=10000)
        through_sangam(pg, email, sample)
        pg.wait_for_selector('[data-panel=organisations]'); pg.wait_for_timeout(500)
        r['signed_in'] = 'Sample Hospital' in pg.inner_text('[data-panel=organisations]')
        # A React sample also answers in the browser; every sample answers on the server.
        r['browser_allowed'] = pg.inner_text('[data-result=browser]').strip() if pg.locator('[data-result=browser]').count() else 'allowed'
        pg.click('[data-panel=permission] button'); pg.wait_for_selector('[data-result=server]')
        r['server_allowed'] = pg.inner_text('[data-result=server]').strip()
        pg.screenshot(path=f'{OUT}/{name}-2-signed-in.png', full_page=True)
        pg.select_option('[data-panel=permission] select', index=1); pg.wait_for_timeout(300)
        pg.click('[data-panel=permission] button'); pg.wait_for_function("document.querySelector('[data-result=server]') && document.querySelector('[data-result=server]').textContent.trim() === 'denied'", timeout=5000)
        r['elsewhere'] = (pg.inner_text('[data-result=browser]').strip() if pg.locator('[data-result=browser]').count() else 'denied', pg.inner_text('[data-result=server]').strip())
        # The first sign-in was with a password only; the person now sets up an authenticator, and signing asks for it.
        r['first_acr'] = pg.inner_text('h1 + p').split('·')[-1].strip()
        enrol_authenticator(uid)
        pg.click('[data-action=sign]')
        pg.wait_for_timeout(1500)
        r['stepped_up_at'] = pg.url.split('?')[0]
        if not pg.url.startswith(sample):
            pg.screenshot(path=f'{OUT}/{name}-3-step-up-at-sangam.png', full_page=True)
            through_sangam(pg, email, sample)
        pg.wait_for_selector('[data-result=signed]', timeout=20000); pg.wait_for_timeout(500)
        pg.screenshot(path=f'{OUT}/{name}-4-signed-and-audited.png', full_page=True)
        event = json.loads(pg.inner_text('[data-panel=signature] pre'))
        r['event'] = event
        r['schema_problems'] = validate(event)
        r['passed'] = (r['signed_in'] and r['browser_allowed'] == 'allowed' and r['server_allowed'] == 'allowed' and r['elsewhere'] == ('denied', 'denied')
                       and r['first_acr'] == 'urn:sangam:acr:1' and r['stepped_up_at'].startswith(B) and not r['schema_problems'] and event['actor']['acr'] in ('urn:sangam:acr:sign', 'urn:sangam:acr:2')
                       and event['category'] == 'sign')
        print(f"{sample}: {'PASS' if r['passed'] else 'FAIL'}  signed in {r['signed_in']}, permission {r['browser_allowed']}/{r['server_allowed']}, "
              f"elsewhere {r['elsewhere']}, first sign-in {r['first_acr']}, step-up via {r['stepped_up_at']}, audit event {event['action']} acr {event['actor'].get('acr')}, schema problems {r['schema_problems']}", flush=True)
        report[sample] = r
        ctx.close()
    browser.close()
json.dump(report, open(f'{OUT}/live-conformance.json', 'w'), indent=1)
sys.exit(0 if all(r['passed'] for r in report.values()) else 1)
