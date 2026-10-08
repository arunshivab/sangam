import type { SangamUser, StepUpRequirement } from "@sangam/client";

export interface SessionClientOptions {
  /** Where @sangam/node's routes are mounted. */
  basePath?: string;
  fetch?: typeof fetch;
  /** How to leave the page (tests replace it). */
  navigate?: (url: string) => void;
}

/** Thrown by `fetchJson` when the server answers with an error other than a sign-in or step-up request. */
export class HttpError extends Error {
  constructor(readonly status: number, readonly body: unknown) {
    super(`HTTP ${status}`);
    this.name = "HttpError";
  }
}

/**
 * Talks to your back end's Sangam routes. The browser never sees a token: it asks the back end who is signed in, and
 * follows the back end's answer when a sign-in or a step-up is needed.
 */
export function createSessionClient(options: SessionClientOptions = {}) {
  const basePath = (options.basePath ?? "/auth").replace(/\/+$/, "");
  const doFetch = options.fetch ?? ((input, init) => fetch(input, init));
  const navigate = options.navigate ?? ((url: string) => window.location.assign(url));
  const here = () => (typeof window === "undefined" ? "/" : window.location.pathname + window.location.search);

  function loginUrl(returnTo: string = here(), requirement?: StepUpRequirement): string {
    const q = new URLSearchParams({ returnTo });
    if (requirement) {
      q.set("acr", requirement.acr);
      if (requirement.maxAge !== undefined) q.set("max_age", String(requirement.maxAge));
    }
    return `${basePath}/login?${q.toString()}`;
  }

  return {
    basePath,
    loginUrl,
    /** The signed-in person, or null. */
    async me(): Promise<SangamUser | null> {
      const response = await doFetch(`${basePath}/me`, { headers: { accept: "application/json" }, credentials: "same-origin" });
      if (response.status === 401) return null;
      if (!response.ok) throw new HttpError(response.status, await response.text());
      return ((await response.json()) as { user: SangamUser }).user;
    },
    signIn(returnTo?: string): void {
      navigate(loginUrl(returnTo));
    },
    signOut(): void {
      navigate(`${basePath}/logout`);
    },
    /** Sends the person to Sangam to authenticate again at a level, coming back here. */
    stepUp(requirement: StepUpRequirement, returnTo?: string): void {
      navigate(loginUrl(returnTo, requirement));
    },
    /**
     * A JSON call to your back end that follows a sign-in or step-up answer (401 with `login`, as @sangam/node gives)
     * by going to Sangam; returns null then.
     */
    async fetchJson<T = unknown>(input: string, init: RequestInit = {}): Promise<T | null> {
      const headers = new Headers(init.headers);
      headers.set("accept", "application/json");
      headers.set("x-requested-with", "fetch");
      const response = await doFetch(input, { ...init, headers, credentials: "same-origin" });
      const text = await response.text();
      const body = text.length > 0 ? (JSON.parse(text) as unknown) : null;
      if (response.status === 401 && body && typeof (body as { login?: unknown }).login === "string") {
        navigate((body as { login: string }).login);
        return null;
      }
      if (!response.ok) throw new HttpError(response.status, body);
      return body as T;
    },
  };
}

export type SessionClient = ReturnType<typeof createSessionClient>;
