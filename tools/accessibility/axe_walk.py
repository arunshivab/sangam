"""Nightly accessibility walk (rc.2; first run by hand in R7 PR-31, see docs/accessibility.md).

Walks every screen of the four hosts in Hindi, Malayalam and English and, on each one, runs axe-core (WCAG 2.0, 2.1
and 2.2 A and AA rules) at desktop width, checks the layout at desktop and phone widths, and records any
Content-Security-Policy violation and any console error the page reports. axe runs through the DevTools protocol,
which the page's policy does not govern, so the policy stays enforced while it runs.

The partner console walk also adds an organisation and selects it (the panel that ended the circuit before rc.1).

Fails (exit 1) on any axe violation, any policy violation, or any interactive page whose live connection breaks.
Layout findings and words still in English are reported in the JSON, not failed on.

    dotnet build Sangam.sln -c Release && bash tools/ci/start-hosts.sh
    npm install --prefix /tmp/axe axe-core@4.14.0
    pip install playwright && python -m playwright install --with-deps chromium
    AXE_JS=/tmp/axe/node_modules/axe-core/axe.min.js python tools/accessibility/axe_walk.py artifacts/a11y

Environment: AXE_JS (required), SANGAM_DB (psql connection URL to the development database), LANGS (comma list),
SHOTS=1 to keep a screenshot of every screen.
"""
import base64, hashlib, hmac, json, os, random, re, struct, subprocess, sys, time, urllib.parse, uuid
from playwright.sync_api import sync_playwright

B, PORTAL, ADMIN, PARTNER = 'http://localhost:5100', 'http://localhost:5200', 'http://localhost:5300', 'http://localhost:5400'
OUT = sys.argv[1] if len(sys.argv) > 1 else 'artifacts/a11y'
AXE = open(os.environ['AXE_JS'], encoding='utf-8').read()
DB = os.environ.get('SANGAM_DB', 'postgresql://sangam_identity:sangam_dev@localhost:5432/sangam_identity')
TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']
AXE_RUN = "async (tags) => { const r = await axe.run(document, { runOnly: { type: 'tag', values: tags }, resultTypes: ['violations', 'incomplete'] }); return { violations: r.violations.map(v => ({ id: v.id, impact: v.impact, help: v.help, tags: v.tags.filter(t => t.startsWith('wcag')), nodes: v.nodes.length, targets: v.nodes.slice(0, 6).map(n => n.target.join(' ')), summary: (v.nodes[0] || {}).failureSummary || '' })), incomplete: r.incomplete.map(v => ({ id: v.id, nodes: v.nodes.length })), passes: r.passes.length }; }"
LANGS = os.environ.get('LANGS', 'hi-IN,ml-IN,en-IN').split(',')
SHOTS = os.environ.get('SHOTS') == '1'
# A fixed authenticator key for the walk's own owner account (development database only).
KEY = 'JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP'
PW = 'Correct-Horse-2026!'
APP = "client_id='sangam-dev-sample'"
# Signs of a broken live connection in the browser console.
BROKEN = re.compile(r'circuit|unhandled exception|Connection disconnected', re.I)
os.makedirs(OUT, exist_ok=True)

CHECK = r"""
(names) => {
  const issues = [];
  const W = Math.min(window.innerWidth, screen.width);
  const de = document.documentElement;
  if (de.scrollWidth > W + 1) issues.push(`page scrolls sideways: ${de.scrollWidth}px wide in a ${W}px window`);
  const visible = el => { const cs = getComputedStyle(el); if (cs.display === 'none' || cs.visibility === 'hidden') return false; const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  const label = el => `${el.tagName.toLowerCase()}${el.className && typeof el.className === 'string' ? '.' + el.className.trim().split(/\s+/).join('.') : ''} "${(el.innerText || el.value || el.placeholder || '').trim().slice(0, 50)}"`;
  const sel = 'button, a.sg-btn, .sg-btn, .sg-tab, .sg-bar-link, label, legend, h1, h2, h3, th, td, dt, dd, .sg-chip, .sg-pill, .sg-rank, .sg-stat-label, .sg-stat-note, .sg-tile-title, .sg-lang-item, select, input, .sg-banner, .sg-partner-text, p, li';
  for (const el of document.querySelectorAll(sel)) {
    if (!visible(el) || el.closest('[hidden]') || el.closest('.sg-frontchannel') || el.closest('[aria-hidden=true]')) continue;
    // Inside a container that scrolls sideways by design (the portal's tab bar on a phone) is not breakage.
    let scroller = false; for (let a = el.parentElement; a; a = a.parentElement) { const ox = getComputedStyle(a).overflowX; if ((ox === 'auto' || ox === 'scroll') && a !== document.documentElement && a !== document.body) { scroller = true; break; } }
    if (scroller) continue;
    const cs = getComputedStyle(el);
    const r = el.getBoundingClientRect();
    if (['hidden', 'clip'].includes(cs.overflowX) && el.scrollWidth > el.clientWidth + 1 && !['INPUT', 'SELECT'].includes(el.tagName)) issues.push('text clipped: ' + label(el));
    if (r.right > W + 1 && !el.closest('.sg-table-wrap, .sg-scroll-x')) issues.push(`off-screen (right edge ${Math.round(r.right)}px): ` + label(el));
    if (['BUTTON', 'A'].includes(el.tagName) && el.matches('.sg-btn, button') && el.scrollWidth > el.clientWidth + 2) issues.push('button text overflows: ' + label(el));
    const box = el.parentElement && el.parentElement.closest('.sg-card, .sg-panel, .sg-confirm, .sg-tile-card, .sg-stat, .sg-consent-col');
    if (box) { const b = box.getBoundingClientRect(); if (r.right > b.right + 2 || r.left < b.left - 2) issues.push('outside its card: ' + label(el)); }
  }
  // Words still in English (names, addresses and codes are expected).
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  const english = new Set();
  const allow = new RegExp('^(' + names.join('|') + ')$', 'i');
  for (let n = walker.nextNode(); n; n = walker.nextNode()) {
    const p = n.parentElement;
    if (!p || !visible(p) || p.closest('script, style, code, .sg-mono, pre, .sg-chip, [aria-hidden=true], .sg-lang, .sg-frontchannel, .sg-auth-footer-host')) continue;
    for (const w of (n.textContent.match(/[A-Za-z][A-Za-z'’-]{2,}/g) || [])) if (!allow.test(w)) english.add(w);
  }
  return { issues: [...new Set(issues)], english: [...english] };
}
"""

ALLOW = ['Sangam', 'sangam', 'SangamID', 'imagiQa', 'LiPi', 'HIS', 'DigiLocker', 'Aadhaar', 'PAN', 'DPDP', 'PIN', 'OTP', 'SMS', 'JSON', 'UTC', 'DELETE',
         'sangamid', 'example', 'localhost', 'English', 'development', 'sample', 'Ravi', 'Asha', 'Meera', 'Menon', 'Nair', 'Lakshmi', 'Rao', 'Kumar',
         'Clinic', 'Hospital', 'Apulki', 'Baner', 'Pune', 'nurse', 'patient', 'vitals', 'read', 'write', 'owner', 'org', 'app', 'gmail', 'com',
         'Chrome', 'Chromium', 'Linux', 'HeadlessChrome', 'Mozilla', 'Windows', 'Safari', 'Firefox', 'Edge', 'iPhone', 'Android', 'Mac', 'Device',
         'Radiology', 'First', 'Floor', 'admin', 'portal', 'console', 'partners', 'Dev', 'Sample', 'Account', 'Portal', 'Console', 'Partner',
         'Foundation', 'v1', 'IN', 'GRV', 'SAML', 'Legacy', 'laboratory', 'system', 'lab', 'saml', 'acs', 'https', 'Medical', 'Center', 'IST', 'ABCD', 'EFGH', 'LiPicons', 'ltd', 'Pvt', 'Ltd', 'Healthcare', 'Services', 'help', 'support', 'grievance', 'privacy', 'id']


def sql(q):
    r = subprocess.run(['psql', DB, '-v', 'ON_ERROR_STOP=1', '-tAc', q], check=True, capture_output=True, text=True)
    return r.stdout.strip()


def totp():
    k = base64.b32decode(KEY)
    # Avoid the last seconds of a window so the code is still good when it arrives.
    if 30 - time.time() % 30 < 4:
        time.sleep(5)
    global _LAST_STEP
    while int(time.time()) // 30 <= globals().get('_LAST_STEP', -1):
        time.sleep(1)
    _LAST_STEP = int(time.time()) // 30
    c = struct.pack('>Q', _LAST_STEP)
    h = hmac.new(k, c, hashlib.sha1).digest()
    o = h[-1] & 15
    return '%06d' % ((struct.unpack('>I', h[o:o + 4])[0] & 0x7fffffff) % 1000000)


def authorize(scope='openid profile email'):
    v = base64.urlsafe_b64encode(os.urandom(48)).rstrip(b'=').decode()
    c = base64.urlsafe_b64encode(hashlib.sha256(v.encode()).digest()).rstrip(b'=').decode()
    return (B + '/connect/authorize?client_id=sangam-dev-sample&redirect_uri=' + urllib.parse.quote('http://localhost:5900/signin-sangam', safe='')
            + '&response_type=code&scope=' + urllib.parse.quote(scope) + '&state=xyz&code_challenge=' + c + '&code_challenge_method=S256')


def code_for(pg, needle):
    pg.goto(B + '/dev/outbox')
    for art in pg.locator('article.sg-mail').all():
        if needle in art.inner_text():
            return re.search(r'\b\d{6}\b', art.locator('pre').inner_text()).group(0)
    raise RuntimeError('no code for ' + needle)


def submit(pg, selector='form button[type=submit] >> nth=0'):
    with pg.expect_navigation():
        pg.click(selector)


def register(pg, first, last='Nair'):
    email = f'{first.lower()}-{uuid.uuid4().hex[:8]}@example.in'
    pg.goto(B + '/register')
    pg.fill('#FirstName', first); pg.fill('#LastName', last); pg.fill('#Email', email)
    pg.fill('#MobileNumber', str(random.randint(7000000000, 9999999999)))
    pg.fill('#BirthDay', '12'); pg.select_option('#BirthMonth', '6'); pg.fill('#BirthYear', '1986'); pg.select_option('#Gender', 'female')
    pg.fill('#Password', PW); pg.check('#AcceptTerms'); pg.wait_for_timeout(2200)
    submit(pg, 'button[type=submit]')
    return email


def verify(pg, email):
    c = code_for(pg, email)
    pg.goto(B + '/verify'); pg.fill('#Code', c)
    submit(pg, 'form:not(.sg-resend) button[type=submit]')


def login(pg, email, url=None, mfa=False):
    pg.goto(url or B + '/login'); pg.wait_for_load_state()
    if '/login' not in pg.url:
        return
    pg.fill('#Email', email); pg.fill('#Password', PW); pg.wait_for_timeout(2200)
    submit(pg)
    if mfa and '/login/authenticator' in pg.url:
        pg.fill('#Code', totp()); submit(pg)


report = {}
with sync_playwright() as p:
    browser = p.chromium.launch()

    # People and an application to show, set up once in English.
    ctx = browser.new_context(viewport={'width': 1280, 'height': 900}); pg = ctx.new_page()
    owner = register(pg, 'Ravi', 'Menon'); verify(pg, owner)
    owner_id = sql(f"select id from users where email='{owner}'")
    sql(f"update users set two_factor_enabled=true where id='{owner_id}'")
    sql(f"insert into user_tokens(user_id,login_provider,name,value) values ('{owner_id}','[AspNetUserStore]','AuthenticatorKey','{KEY}') on conflict do nothing")
    app_id = sql(f"select id from apps where {APP}")
    sql(f"insert into app_admins(id,app_id,user_id,role,granted_at) values ('{uuid.uuid4()}','{app_id}','{owner_id}','owner',now())")
    sql(f"insert into platform_operators(id,user_id,role,granted_at) values ('{uuid.uuid4()}','{owner_id}','owner',now())")
    # PR-19: the sample application's own sign-in page branding, so the sign-in screens are checked themed too.
    sql("delete from customisations where key='branding'")
    sql("insert into customisations(id,scope,scope_id,key,value,version,updated_at) values ('" + str(uuid.uuid4()) + "','app','" + app_id + "','branding',"
        + "'{\"accent\":\"#1D4E89\",\"help\":\"https://help.example.in\",\"welcome\":{\"en-IN\":\"Welcome to the LiPi sample hospital system\",\"hi-IN\":\"LiPi नमूना अस्पताल प्रणाली में आपका स्वागत है\",\"ml-IN\":\"LiPi സാമ്പിൾ ആശുപത്രി സംവിധാനത്തിലേക്ക് സ്വാഗതം\"}}',1,now())")
    gid = str(uuid.uuid4())
    sql(f"insert into grievances(id,reference,received_at,channel,category,complainant_name,complainant_contact,summary,status,acknowledge_by,resolve_by,acknowledged_at,created_by_user_id) values ('{gid}','GRV-2026-{random.randint(1000,4999)}',now()-interval '3 days','email','access','Lakshmi Rao','lakshmi.rao@example.in','Asks for a copy of every record Sangam holds about her, and which applications she has used.','acknowledged',now()-interval '1 day',now()+interval '27 days',now()-interval '2 days','{owner_id}')")
    sql(f"insert into grievance_entries(grievance_id,at,operator_user_id,kind,text) values ('{gid}',now()-interval '3 days','{owner_id}','logged','Asks for a copy of every record Sangam holds about her, and which applications she has used.'),('{gid}',now()-interval '2 days','{owner_id}','acknowledged','Acknowledgement e-mailed.'),('{gid}',now()-interval '1 day','{owner_id}','note','Export prepared from the portal; checking the audit log for applications used.')")
    sql(f"insert into grievances(id,reference,received_at,channel,category,complainant_name,complainant_contact,summary,status,acknowledge_by,resolve_by,created_by_user_id) values ('{uuid.uuid4()}','GRV-2026-{random.randint(5000,9999)}',now()-interval '5 days','letter','erasure','Asha Nair','+91 98450 00000','Wants her account deleted and asks what is kept afterwards.','received',now()-interval '2 days',now()+interval '25 days','{owner_id}')")
    ctx = browser.new_context(viewport={'width': 1280, 'height': 900}); pg = ctx.new_page()
    pg.goto(ADMIN + '/apps/saml')
    if '/login' in pg.url:
        login(pg, owner, pg.url, mfa=True); pg.goto(ADMIN + '/apps/saml')
    pg.wait_for_load_state('networkidle'); pg.wait_for_timeout(1500)
    print('setup at', pg.url, flush=True)
    pg.fill('#s-name', 'Legacy laboratory system'); pg.fill('#s-owner', 'Apulki Medical Center')
    pg.fill('#s-entity', 'https://lab.example.in/saml'); pg.fill('#s-acs', 'https://lab.example.in/saml/acs')
    pg.wait_for_timeout(500); pg.click('[data-panel=saml-sp] .sg-btn--primary'); pg.wait_for_timeout(2000)
    print('saml registered:', sql("select count(*) from saml_service_providers"), flush=True)
    ctx.close()
    names = ALLOW + [re.escape(owner.split('@')[0])]

    def run(lang):
        shots = SHOTS
        desk = browser.new_context(viewport={'width': 1280, 'height': 900}, locale=lang)
        pg = desk.new_page()
        results = {}

        def set_lang():
            pg.goto(B + f'/culture?c={lang}&returnUrl=%2Fhelp')

        csp = []
        errors = []
        pg.on('console', lambda m: csp.append(m.text[:300]) if 'Content Security Policy' in m.text
              else (errors.append(m.text[:300]) if m.type == 'error' else None))
        pg.on('pageerror', lambda e: errors.append(str(e)[:300]))

        def shot(name, wait=0, blazor=False):
            if blazor:
                try:
                    pg.wait_for_load_state('networkidle', timeout=6000)
                except Exception:
                    pass
                pg.wait_for_timeout(1200)
            elif wait:
                pg.wait_for_timeout(wait)
            entry = {'url': pg.url}
            entry['desktop'] = pg.evaluate(CHECK, names)
            pg.evaluate(AXE)
            entry['axe'] = pg.evaluate(AXE_RUN, TAGS)
            entry['csp'] = list(csp); csp.clear()
            entry['console'] = list(errors); errors.clear()
            entry['broken'] = [e for e in entry['console'] if BROKEN.search(e)]
            if shots:
                pg.screenshot(path=f'{OUT}/{lang}/{name}-desktop.png', full_page=True)
            phone = browser.new_context(viewport={'width': 390, 'height': 844}, is_mobile=True, has_touch=True, locale=lang, storage_state=desk.storage_state())
            ph = phone.new_page()
            try:
                ph.goto(pg.url); ph.wait_for_load_state()
                if blazor:
                    try:
                        ph.wait_for_load_state('networkidle', timeout=6000)
                    except Exception:
                        pass
                    ph.wait_for_timeout(1200)
                entry['phone'] = ph.evaluate(CHECK, names)
            finally:
                phone.close()
            results[name] = entry
            v = entry['axe']['violations']
            layout = entry['desktop']['issues'] + entry.get('phone', {}).get('issues', [])
            print(f"  {lang} {name}: axe {len(v)} rules / {sum(x['nodes'] for x in v)} nodes {[x['id'] for x in v]}; layout {len(layout)}; csp {len(entry['csp'])}; broken {len(entry['broken'])}", flush=True)

        os.makedirs(f'{OUT}/{lang}', exist_ok=True)
        set_lang()
        # Public pages.
        for path, name in [('/login', 'login'), ('/register', 'register'), ('/forgot', 'forgot'), ('/login/code', 'login-code'), ('/terms', 'terms'),
                           ('/privacy', 'privacy'), ('/privacy/grievance', 'grievance'), ('/help', 'help'), ('/invite/not-a-real-token', 'invite-not-found'),
                           ('/Error', 'error')]:
            pg.goto(B + path); shot(name)
        pg.goto(B + '/login'); pg.fill('#Email', 'nobody@example.in'); pg.fill('#Password', 'Wrong-Password-1!'); pg.wait_for_timeout(2200); submit(pg)
        shot('login-wrong-password')
        pg.goto(B + '/register'); pg.fill('#Password', 'abc'); pg.wait_for_timeout(300); shot('register-password-meter')

        # A new person: register, verify, their account, mobile, passkeys, sign out.
        person = register(pg, 'Meera')
        shot('verify-email')
        verify(pg, person)
        shot('verified')
        person_id = sql(f"select id from users where email='{person}'")
        for path, name in [('/account', 'account'), ('/account/mobile', 'account-mobile'), ('/account/passkeys', 'account-passkeys'), ('/account/password', 'account-password'), ('/device', 'device-code-entry'), ('/device?user_code=ABCD-EFGH', 'device-code-unknown'), ('/logout', 'logout-confirm')]:
            pg.goto(B + path); shot(name)
        # Consent for the sample application, then allow it (the redirect target is not running).
        pg.goto(B + '/login?returnUrl=' + urllib.parse.quote(authorize(), safe='').replace('http%3A%2F%2Flocalhost%3A5100', '')); shot('login-branded-application')
        pg.goto(authorize()); pg.wait_for_load_state(); shot('consent')
        try:
            with pg.expect_request(lambda r: r.url.startswith('http://localhost:5900'), timeout=8000):
                pg.click('button[value], .sg-btn--consent-allow')
        except Exception:
            pass
        # Front-channel logout: the frame never loads, so the page stays up for its three seconds.
        sql(f"update apps set front_channel_logout_uri='http://localhost:5100/never-loads' where {APP}")
        pg.route('**/never-loads**', lambda route: route.abort())
        pg.goto(B + '/logout'); pg.wait_for_timeout(300)
        with pg.expect_navigation():
            pg.click('form button[type=submit]')
        shot('signed-out-frontchannel')
        sql(f"update apps set front_channel_logout_uri=null where {APP}")
        pg.unroute('**/never-loads**')
        pg.wait_for_timeout(3500)
        pg.goto(B + '/login?signedout=true'); shot('login-signed-out')

        # Forgot password → reset.
        pg.goto(B + '/forgot'); pg.fill('#Email', person); submit(pg); shot('reset')

        # Sign-in with an e-mailed code.
        sql(f"update users set sign_in_preference='password_and_otp' where id='{person_id}'")
        pg.goto(B + '/login'); pg.fill('#Email', person); pg.fill('#Password', PW); pg.wait_for_timeout(2200); submit(pg)
        shot('login-code-step')
        c = code_for(pg, person); pg.goto(B + '/login/verify'); pg.fill('#Code', c); submit(pg)
        sql(f"update users set sign_in_preference='password' where id='{person_id}'")
        desk.clear_cookies(); set_lang()

        # An application that requires a second factor, then one that needs longer passwords.
        sql(f"update apps set mfa_requirement='required' where {APP}")
        login(pg, person, authorize()); pg.wait_for_load_state(); shot('two-step-required')
        sql(f"update apps set mfa_requirement='optional' where {APP}")
        desk.clear_cookies(); set_lang()
        sql(f"update apps set min_password_length=24 where {APP}")
        pg.goto(authorize()); pg.fill('#Email', person); pg.fill('#Password', PW); pg.wait_for_timeout(2200); submit(pg)
        shot('login-new-password')
        sql(f"update apps set min_password_length=null where {APP}")
        sql(f"update apps set sign_in_policy='passkey_only' where {APP}")
        desk.clear_cookies(); set_lang(); pg.goto(authorize()); pg.wait_for_load_state(); shot('login-passkey-only-application')
        sql(f"update apps set sign_in_policy='default' where {APP}")

        # The owner: authenticator step, then a signature ceremony.
        desk.clear_cookies(); set_lang()
        pg.goto(B + '/login'); pg.fill('#Email', owner); pg.fill('#Password', PW); pg.wait_for_timeout(2200); submit(pg)
        shot('login-authenticator')
        pg.fill('#Code', totp()); submit(pg)
        token = json.loads(subprocess.run(['curl', '-s', '-X', 'POST', B + '/connect/token', '-d', 'grant_type=client_credentials', '-d', 'client_id=sangam-dev-sample',
                                           '-d', 'client_secret=sangam-dev-sample-secret-change-me', '-d', 'scope=sangam.manage'], capture_output=True, text=True).stdout)['access_token']
        req = json.loads(subprocess.run(['curl', '-s', '-X', 'POST', B + '/api/v1/signatures', '-H', 'Authorization: Bearer ' + token, '-H', 'Content-Type: application/json', '-d',
                                         json.dumps({'recordId': 'SOP-QA-014/3', 'recordHash': 'sha256:' + 'a' * 64, 'meaning': 'Approved',
                                                     'displayText': 'SOP-QA-014 Handling of radioactive waste, revision 3', 'returnUrl': 'http://localhost:5900/signin-sangam'})],
                                        capture_output=True, text=True).stdout)
        pg.goto(B + req['ceremonyPath']); shot('sign-record')

        # Account portal.
        for path, name in [('/', 'portal-overview'), ('/apps', 'portal-connected-apps'), ('/devices', 'portal-devices'), ('/audit', 'portal-audit-log'), ('/profile', 'portal-personal-details')]:
            pg.goto(PORTAL + path); login(pg, owner, None, mfa=True) if '/login' in pg.url else None
            if not pg.url.startswith(PORTAL):
                pg.goto(PORTAL + path)
            shot(name, blazor=True)
        # Operator console.
        for path, name in [('/', 'admin-users'), (f'/users/{person_id}', 'admin-user-detail'), ('/apps', 'admin-applications'), ('/operators', 'admin-operators'), ('/customisation', 'admin-defaults'), ('/monitoring', 'admin-monitoring'), ('/grievances', 'admin-grievances'), (f'/grievances/{gid}', 'admin-grievance-record'), ('/apps/saml', 'admin-saml-providers')]:
            pg.goto(ADMIN + path)
            if '/login' in pg.url:
                login(pg, owner, pg.url, mfa=True); pg.goto(ADMIN + path)
            shot(name, blazor=True)
        # Partner console.
        for path, name in [('/', 'partner-home'), (f'/apps/{app_id}', 'partner-organisations'), (f'/apps/{app_id}/roles', 'partner-roles'),
                           (f'/apps/{app_id}/admins', 'partner-administrators'), (f'/apps/{app_id}/settings', 'partner-settings'), (f'/apps/{app_id}/messages', 'partner-messages')]:
            pg.goto(PARTNER + path)
            if '/login' in pg.url:
                login(pg, owner, pg.url, mfa=True); pg.goto(PARTNER + path)
            shot(name, blazor=True)
        # rc.2: add an organisation and select it, so the people panel and its editors render (before rc.1 this ended the
        # circuit), then open the invitation form.
        pg.goto(PARTNER + f'/apps/{app_id}')
        pg.wait_for_load_state('networkidle'); pg.wait_for_timeout(1200)
        org_name = f'Walk Hospital {uuid.uuid4().hex[:6]}'
        pg.fill('#org-name', org_name); pg.select_option('#org-type', index=1); pg.wait_for_timeout(300)
        pg.locator('section.sg-panel', has=pg.locator('#org-name')).locator('.sg-btn--primary').click()
        pg.wait_for_timeout(1500)
        pg.click(f'.sg-tree-item:has-text("{org_name}")')
        shot('partner-organisation-selected', blazor=True)
        if not pg.locator('.sg-tree-item--on[aria-pressed=true]').count():
            results['partner-organisation-selected']['broken'].append('the chosen organisation is not marked as chosen')

        # Screens added since R4: provisioning, webhooks, attributes and claims, evidence (R5-R7).
        for path, name in [(f'/apps/{app_id}/provisioning', 'partner-provisioning'), (f'/apps/{app_id}/webhooks', 'partner-webhooks'),
                           (f'/apps/{app_id}/attributes', 'partner-attributes'), (f'/apps/{app_id}/evidence', 'partner-evidence')]:
            pg.goto(PARTNER + path)
            if '/login' in pg.url:
                login(pg, owner, pg.url, mfa=True); pg.goto(PARTNER + path)
            shot(name, blazor=True)
        desk.close()
        return results

    try:
        for lang in LANGS:
            print('==', lang, flush=True)
            report[lang] = run(lang)
    finally:
        sql(f"update apps set sign_in_policy='default', min_password_length=null, mfa_requirement='optional', front_channel_logout_uri=null where {APP}")
        sql("delete from customisations where key='branding'")
        json.dump(report, open(f'{OUT}/a11y-report.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
        browser.close()

failures = []
for lang, screens in report.items():
    for name, entry in screens.items():
        for v in entry['axe']['violations']:
            failures.append(f"{lang} {name}: axe {v['id']} ({v['impact']}) on {v['nodes']} element(s): {v['help']}")
        failures += [f'{lang} {name}: policy violation: {c}' for c in entry['csp']]
        failures += [f'{lang} {name}: live connection broken: {e}' for e in entry['broken']]
screens = sum(len(s) for s in report.values())
print(f'\n{screens} screens walked in {len(report)} languages; {len(failures)} failure(s).')
for f in failures:
    print('  FAIL ' + f)
sys.exit(1 if failures or screens == 0 else 0)
