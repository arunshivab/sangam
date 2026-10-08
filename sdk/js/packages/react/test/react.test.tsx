import { test } from "node:test";
import assert from "node:assert/strict";
import { renderToStaticMarkup } from "react-dom/server";
import { Can, createSessionClient, SangamProvider, SignedIn, SignedOut, useStepUp, type SangamUser } from "../src/index.js";

const nurse: SangamUser = {
  id: "u1", name: "Meera", acr: "urn:sangam:acr:1", authTime: Math.floor(Date.now() / 1000) - 60,
  memberships: [{ organisationId: "o", organisationName: "H", organisationType: "hospital", path: "/h/", role: "nurse", permissions: ["vitals:read"], appliesToDescendants: true }],
};

function StepUpProbe() {
  const { satisfied } = useStepUp({ acr: "urn:sangam:acr:sign" });
  return <span>{satisfied ? "may sign" : "must step up"}</span>;
}

test("components show what the person may see, with the shared permission rule", () => {
  const html = renderToStaticMarkup(
    <SangamProvider initialUser={nurse}>
      <SignedIn>in</SignedIn>
      <SignedOut>out</SignedOut>
      <Can organisation="/h/ward/" permission="vitals:read">vitals</Can>
      <Can organisation="/h/" permission="orders:write" fallback="no orders">orders</Can>
      <StepUpProbe />
    </SangamProvider>,
  );
  assert.equal(html, "invitalsno orders<span>must step up</span>");
  assert.equal(renderToStaticMarkup(<SangamProvider initialUser={null}><SignedIn>in</SignedIn><SignedOut>out</SignedOut></SangamProvider>), "out");
});

test("the session client follows sign-in and step-up answers from the back end", async () => {
  const went: string[] = [];
  const answers: Record<string, Response> = {
    "/auth/me": new Response(JSON.stringify({ user: nurse }), { status: 200 }),
    "/api/sign": new Response(JSON.stringify({ error: "step_up_required", login: "/auth/login?returnTo=%2F&acr=urn%3Asangam%3Aacr%3Asign" }), { status: 401 }),
    "/api/ok": new Response(JSON.stringify({ ok: true }), { status: 200 }),
  };
  const client = createSessionClient({ fetch: async (input) => answers[String(input)]!, navigate: (u) => went.push(u) });
  assert.equal((await client.me())?.id, "u1");
  assert.equal(await client.fetchJson("/api/sign", { method: "POST" }), null);
  assert.deepEqual(await client.fetchJson("/api/ok"), { ok: true });
  client.stepUp({ acr: "urn:sangam:acr:sign", maxAge: 300 }, "/records/1");
  assert.deepEqual(went, ["/auth/login?returnTo=%2F&acr=urn%3Asangam%3Aacr%3Asign", "/auth/login?returnTo=%2Frecords%2F1&acr=urn%3Asangam%3Aacr%3Asign&max_age=300"]);
});
