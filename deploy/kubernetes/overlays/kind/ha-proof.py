"""R7 (PR-33): proves Sangam's high-availability path on the local kind cluster (README, "Prove it locally").

Prints one line per check and exits non-zero if any fails. Needs kubectl and docker on the PATH, the cluster from
kind-cluster.yaml with this overlay applied, and Python with Playwright (pip install playwright; playwright install
chromium).
"""
import json
import os
import random
import re
import subprocess
import sys
import threading
import time
import uuid

from playwright.sync_api import sync_playwright

ID, PORTAL = 'https://id.sangam.test:8443', 'https://account.sangam.test:8443'
HOSTS = ['id', 'account', 'admin', 'partners']
DEPLOYMENTS = ['identity', 'portal', 'admin', 'partner']
PASSWORD = 'Kaveri-River-2026!'
results = []


def kubectl(*args, check=True):
    return subprocess.run(['kubectl', *args], capture_output=True, text=True, check=check).stdout


def report(ok, what):
    results.append(ok)
    print(('PASS ' if ok else 'FAIL ') + what, flush=True)


def pods(deployment=None):
    data = json.loads(kubectl('-n', 'sangam', 'get', 'pods', '-o', 'json'))['items']
    out = []
    for p in data:
        name = p['metadata'].get('labels', {}).get('app.kubernetes.io/name')
        if deployment and name != deployment:
            continue
        if name not in DEPLOYMENTS or p['metadata'].get('deletionTimestamp'):
            continue
        ready = any(c['type'] == 'Ready' and c['status'] == 'True' for c in p['status'].get('conditions', []))
        out.append({'name': p['metadata']['name'], 'app': name, 'node': p['spec'].get('nodeName'), 'ready': ready, 'ip': p['status'].get('podIP')})
    return out


def wait_ready(timeout=300):
    end = time.time() + timeout
    while time.time() < end:
        current = pods()
        if all(sum(1 for p in current if p['app'] == d and p['ready']) >= 2 for d in DEPLOYMENTS):
            return True
        time.sleep(3)
    return False


def curl(host, path='/health/ready', timeout=10):
    r = subprocess.run(['curl', '--noproxy', '*', '-sk', '-o', '/dev/null', '-w', '%{http_code}', '--max-time', str(timeout),
                        '--resolve', f'{host}.sangam.test:8443:127.0.0.1', f'https://{host}.sangam.test:8443{path}'],
                       capture_output=True, text=True)
    return r.stdout.strip()


def code_for(email):
    for _ in range(30):
        for line in reversed(kubectl('-n', 'sangam-data', 'logs', 'deploy/anjal').splitlines()):
            try:
                body = json.loads(line)['body']
            except (ValueError, KeyError):
                continue
            if body.get('to', {}).get('address') == email:
                m = re.search(r'\b\d{6}\b', body.get('subject', '') + ' ' + body.get('text', ''))
                if m:
                    return m.group(0)
        time.sleep(1)
    raise RuntimeError('no code for ' + email)


def submit(page, selector='form button[type=submit] >> nth=0'):
    with page.expect_navigation():
        page.click(selector)


def signed_in(page):
    page.goto(ID + '/account')
    page.wait_for_load_state()
    return '/account' in page.url and 'Namaste' in page.content()


def main():
    # 1. Replicas ready, on different nodes.
    report(wait_ready(), 'every Sangam host has two ready replicas')
    spread = all(len({p['node'] for p in pods(d)}) >= 2 for d in DEPLOYMENTS)
    report(spread, 'each host\'s replicas run on different nodes: ' + ', '.join(f"{d}={sorted({p['node'] for p in pods(d)})}" for d in DEPLOYMENTS))

    with sync_playwright() as p:
        # The browser goes straight to the cluster: no proxy from the environment.
        direct = {k: v for k, v in os.environ.items() if 'proxy' not in k.lower()}
        browser = p.chromium.launch(env=direct, args=['--host-resolver-rules=MAP *.sangam.test 127.0.0.1'])
        ctx = browser.new_context(ignore_https_errors=True)
        page = ctx.new_page()

        # 2. Register, verify, sign in to the portal.
        email = f'ha-{uuid.uuid4().hex[:8]}@sangam.test'
        page.goto(ID + '/register')
        page.fill('#FirstName', 'Lakshmi'); page.fill('#LastName', 'Rao'); page.fill('#Email', email)
        page.fill('#MobileNumber', str(random.randint(7000000000, 9999999999)))
        page.fill('#BirthDay', '12'); page.select_option('#BirthMonth', '6'); page.fill('#BirthYear', '1986')
        page.select_option('#Gender', 'female'); page.fill('#Password', PASSWORD); page.check('#AcceptTerms')
        page.wait_for_timeout(2500)
        submit(page, 'button[type=submit]')
        page.fill('#Code', code_for(email)); submit(page)
        report(signed_in(page), f'a person registers through the replicas and is signed in ({email})')
        page.goto(PORTAL + '/'); page.wait_for_load_state(); page.wait_for_timeout(2500)
        report(PORTAL in page.url and 'Lakshmi' in page.content(), 'the portal signs them in through the identity server')

        # 3. Each identity pod in turn goes; the person stays signed in.
        for victim in [x['name'] for x in pods('identity')]:
            kubectl('-n', 'sangam', 'delete', 'pod', victim, '--wait=false')
            time.sleep(2)
            ok = signed_in(page)
            report(ok, f'identity pod {victim} deleted: still signed in')
            wait_ready()
        page.goto(ID + '/logout'); page.wait_for_load_state()
        with page.expect_navigation():
            page.click('form button[type=submit] >> nth=0')
        page.goto(ID + '/login'); page.fill('#Email', email); page.fill('#Password', PASSWORD); page.wait_for_timeout(2500); submit(page)
        report(signed_in(page), 'after both identity pods were replaced, the person signs in again')

        # 4. The portal pods go one at a time; the page comes back, still signed in.
        page.goto(PORTAL + '/'); page.wait_for_load_state(); page.wait_for_timeout(2000)
        for victim in [x['name'] for x in pods('portal')]:
            kubectl('-n', 'sangam', 'delete', 'pod', victim, '--wait=false')
            time.sleep(3)
            page.goto(PORTAL + '/profile'); page.wait_for_load_state(); page.wait_for_timeout(3000)
            report(PORTAL in page.url and 'Lakshmi' in page.content(), f'portal pod {victim} deleted: the profile opens, still signed in')
            wait_ready()

        # 5. A rolling restart of every host while requests run.
        failures, total, stop = [], [0], threading.Event()

        def hammer():
            while not stop.is_set():
                for h in HOSTS:
                    code = curl(h)
                    total[0] += 1
                    if code != '200':
                        failures.append((h, code))
                time.sleep(0.1)
        t = threading.Thread(target=hammer); t.start()
        # One host after another, as a release would roll out (and as a two-CPU test machine can take).
        for d in DEPLOYMENTS:
            kubectl('-n', 'sangam', 'rollout', 'restart', f'deploy/{d}')
            kubectl('-n', 'sangam', 'rollout', 'status', f'deploy/{d}', '--timeout=600s')
        time.sleep(5); stop.set(); t.join()
        report(not failures, f'rolling restart of all four hosts: {total[0]} requests, {len(failures)} failed {failures[:5]}')
        report(signed_in(page), 'after the rolling restart, the person is still signed in')

        # 6. A worker node goes away.
        nodes = sorted({x['node'] for x in pods() if 'worker' in (x['node'] or '')})
        victim = nodes[0]
        subprocess.run(['docker', 'stop', victim], capture_output=True, check=True)
        started = time.time()
        recovered = {}
        while time.time() - started < 240 and len(recovered) < len(HOSTS):
            for h in HOSTS:
                if h not in recovered and all(curl(h, timeout=2) == '200' for _ in range(5)):
                    recovered[h] = round(time.time() - started)
            time.sleep(1)
        report(len(recovered) == len(HOSTS), f'worker node {victim} stopped: every host answering again from the other node after {recovered} seconds')
        report(signed_in(page), 'with one worker node down, the person is still signed in')
        # Bring the node back, and let the ingress controller's pods start afresh on it.
        subprocess.run(['docker', 'start', victim], capture_output=True, check=True)
        kubectl('wait', '--for=condition=Ready', f'node/{victim}', '--timeout=300s')
        kubectl('-n', 'traefik', 'rollout', 'restart', 'deploy/traefik')
        kubectl('-n', 'traefik', 'rollout', 'status', 'deploy/traefik', '--timeout=300s')
        browser.close()

    # 7. One background round at a time: sample PostgreSQL's advisory locks 20 times a second for a minute, in the
    # database itself, and record the most sessions ever holding one lock and which pods took the rounds.
    sql = """
CREATE TEMP TABLE seen (k bigint, holders int, addrs text[]);
DO $$
BEGIN
  FOR i IN 1..1200 LOOP
    INSERT INTO seen
      SELECT l.classid::bigint * 4294967296 + l.objid::bigint, count(*), array_agg(host(a.client_addr))
      FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
      WHERE l.locktype = 'advisory' AND l.granted GROUP BY 1;
    PERFORM pg_sleep(0.05);
  END LOOP;
END $$;
SELECT coalesce(max(holders), 0), count(*), coalesce(string_agg(DISTINCT addr, ','), '') FROM seen, unnest(addrs) addr;
"""
    out = kubectl('-n', 'sangam-data', 'exec', 'statefulset/postgres', '--', 'psql', '-U', 'sangam', '-tA', '-F', '|', '-c', sql, check=False)
    line = [x for x in out.splitlines() if '|' in x][-1]
    worst, samples, addrs = line.split('|')
    ips = {x['ip']: x['name'] for x in pods()}
    who = sorted({ips.get(a, a) for a in addrs.split(',') if a})
    report(int(worst) <= 1 and int(samples) > 0,
           f'advisory locks sampled 20 times a second for 60 s: {samples} sightings, at most {worst} session per lock; rounds taken by {who}')

    print(f'\n{sum(results)}/{len(results)} checks passed')
    return 0 if all(results) else 1


if __name__ == '__main__':
    sys.exit(main())
