import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { SangamAcr, SangamProvider, SignedIn, SignedOut, SignInButton, SignOutButton, useCan, useSangam, useStepUp } from "@sangam/react";

function Organisations() {
  const { user } = useSangam();
  if (!user) return null;
  return (
    <section data-panel="organisations">
      <h2>Your organisations in this application</h2>
      {user.memberships.length === 0 ? (
        <p className="muted">No role yet. An administrator gives you one on Sangam's partner console or through the management API.</p>
      ) : (
        <ul>{user.memberships.map((m) => <li key={m.organisationId + m.role}><b>{m.role}</b> at {m.organisationName} <code>{m.path}</code>{m.appliesToDescendants ? " (and below)" : ""}: {m.permissions.join(", ") || "no permissions"}</li>)}</ul>
      )}
    </section>
  );
}

function PermissionCheck() {
  const { user, client } = useSangam();
  const [org, setOrg] = useState(user?.memberships[0]?.path ?? "/");
  const [permission, setPermission] = useState("vitals:read");
  const [server, setServer] = useState<boolean | null>(null);
  const here = useCan(org, permission);
  useEffect(() => setServer(null), [org, permission]);
  return (
    <section data-panel="permission">
      <h2>Check a permission</h2>
      <div className="row">
        <select value={org} onChange={(e) => setOrg(e.target.value)} aria-label="Organisation">
          {(user?.memberships ?? []).map((m) => <option key={m.path} value={m.path}>{m.organisationName}</option>)}
          <option value="/0192a6b0-0000-7000-8000-00000000ffff/">An organisation you have no role in</option>
        </select>
        <input value={permission} onChange={(e) => setPermission(e.target.value)} aria-label="Permission" />
        <button className="ghost" onClick={async () => setServer((await client.fetchJson<{ allowed: boolean }>(`/api/check?org=${encodeURIComponent(org)}&permission=${encodeURIComponent(permission)}`))?.allowed ?? null)}>Ask the server</button>
      </div>
      <p>In the browser (@sangam/react): <span className={here ? "ok" : "no"} data-result="browser">{here ? "allowed" : "denied"}</span>
        {server !== null && <> · on the server (@sangam/node): <span className={server ? "ok" : "no"} data-result="server">{server ? "allowed" : "denied"}</span></>}</p>
    </section>
  );
}

function Signature() {
  const { client } = useSangam();
  const { satisfied } = useStepUp({ acr: SangamAcr.signature });
  const [result, setResult] = useState<unknown>(null);
  async function sign() {
    setResult(await client.fetchJson("/api/records/SOP-114/sign", { method: "POST" }));
  }
  useEffect(() => {
    const q = new URLSearchParams(window.location.search);
    if (q.get("resume") === "sign") {
      window.history.replaceState(null, "", "/");
      void sign();
    }
  }, []);
  return (
    <section data-panel="signature">
      <h2>Sign record SOP-114</h2>
      <p className="muted">Signing needs a two-factor sign-in from the last five minutes ({SangamAcr.signature}). {satisfied ? "Yours qualifies." : "Sangam will ask you to sign in again first."}</p>
      <button onClick={() => void sign()} data-action="sign">Sign as approved</button>
      {result !== null && <><p className="ok" data-result="signed">Signed. The shared audit event (SGM-208), as buffered for the audit service:</p><pre>{JSON.stringify((result as { event: unknown }).event, null, 2)}</pre></>}
    </section>
  );
}

function App() {
  const { user, loading } = useSangam();
  return (
    <>
      <header>
        <span><b>Sangam sample</b> · React + Node</span>
        <SignedIn><span className="row">{user?.name} <SignOutButton className="ghost" /></span></SignedIn>
      </header>
      <main>
        {loading && <p className="muted">Loading…</p>}
        <SignedOut>
          <section>
            <h1>Sign in with Sangam</h1>
            <p className="muted">This sample keeps every token on its Node server; the browser only holds a session cookie.</p>
            <SignInButton />
          </section>
        </SignedOut>
        <SignedIn>
          <section><h1>Signed in as {user?.name}</h1><p className="muted">Sangam id <code>{user?.id}</code> · {user?.acr}</p></section>
          <Organisations />
          <PermissionCheck />
          <Signature />
        </SignedIn>
      </main>
    </>
  );
}

createRoot(document.getElementById("root")!).render(<SangamProvider><App /></SangamProvider>);
