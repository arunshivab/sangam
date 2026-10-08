# @sangam/react

Sangam for React (R6, SGM-306). Pairs with `@sangam/node`: the browser asks your back end who is signed in
(`/auth/me`) and never holds a token.

```tsx
import { SangamProvider, SignedIn, SignedOut, SignInButton, Can, useSangam, useStepUp, SangamAcr } from "@sangam/react";

<SangamProvider basePath="/auth">
  <SignedOut><SignInButton /></SignedOut>
  <SignedIn>
    <Can organisation={ward.path} permission="vitals:write" fallback={<p>Read only</p>}><VitalsForm /></Can>
  </SignedIn>
</SangamProvider>

const { client } = useSangam();
const { satisfied, stepUp } = useStepUp({ acr: SangamAcr.signature });
await client.fetchJson("/results/R-1/sign", { method: "POST" });   // follows a step-up answer to Sangam
```

Permission and step-up checks here are for what to show; your back end enforces them.
