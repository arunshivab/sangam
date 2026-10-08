import type { IncomingMessage, ServerResponse } from "node:http";
import * as oidc from "openid-client";
import {
  can,
  createTokenVerifier,
  ManagementClient,
  satisfies,
  stepUpChallenge,
  userFromClaims,
  type AuditEntry,
  type AuditEvent,
  type SangamUser,
  type StepUpRequirement,
} from "@sangam/client";
import { AuditRecorder, type AuditOptions } from "./audit.js";
import { cookies, MemorySessionStore, newId, setCookie, signId, unsignId, type SessionStore } from "./session.js";

export * from "@sangam/client";
export { AuditRecorder, clientIp, type AuditOptions } from "./audit.js";
export { MemorySessionStore, type SessionStore } from "./session.js";

/** What a session holds: never sent to the browser, which only gets a signed session id. */
export interface SangamSession {
  user?: SangamUser;
  idToken?: string;
  pending?: { state: string; nonce: string; verifier: string; returnTo: string };
}

export interface SangamNodeOptions {
  /** Sangam's address, for example "https://id.sangamid.in". */
  authority: string;
  clientId: string;
  clientSecret: string;
  /** Your application's own address, for example "https://lims.example.in": the callback is {baseUrl}{basePath}/callback. */
  baseUrl: string;
  /** Where the sign-in routes live. */
  basePath?: string;
  scopes?: string[];
  /** 32 characters or more: signs the session cookie. */
  cookieSecret: string;
  sessionTtlSeconds?: number;
  store?: SessionStore<SangamSession>;
  /** Allow http for Sangam itself (development only). */
  allowInsecureRequests?: boolean;
  /** Trust X-Forwarded-For for audit client addresses (behind your own proxy only). */
  trustProxy?: boolean;
  /** The shared audit helper's settings; without them `audit` throws. */
  audit?: AuditOptions;
  /** For tests: an already-discovered configuration. */
  configuration?: oidc.Configuration;
}

type Next = (error?: unknown) => void;
type Handler = (req: IncomingMessage, res: ServerResponse, next: Next) => void | Promise<void>;

declare module "node:http" {
  interface IncomingMessage {
    /** Set by `requireToken`: the caller's identity from their Sangam access token. */
    sangamToken?: { user: SangamUser; claims: Record<string, unknown>; scopes: string[] };
  }
}

const COOKIE = "sangam.sid";

function redirect(res: ServerResponse, location: string): void {
  res.statusCode = 302;
  res.setHeader("location", location);
  res.end();
}

function json(res: ServerResponse, status: number, body: unknown, headers: Record<string, string> = {}): void {
  res.statusCode = status;
  res.setHeader("content-type", "application/json; charset=utf-8");
  for (const [k, v] of Object.entries(headers)) res.setHeader(k, v);
  res.end(JSON.stringify(body));
}

function wantsJson(req: IncomingMessage): boolean {
  return (req.headers.accept ?? "").includes("application/json") || req.headers["x-requested-with"] === "fetch";
}

/** Only local paths are followed after sign-in, so the login route cannot be used to send people elsewhere. */
function safeReturn(value: string | null | undefined): string {
  return value && value.startsWith("/") && !value.startsWith("//") && !value.startsWith("/\\") ? value : "/";
}

/**
 * Sign in with Sangam for a Node back end. The browser never holds a token: the authorization code with PKCE is
 * exchanged server-side and the session lives in `store`, the browser getting only a signed, http-only session cookie
 * (the backend-for-frontend pattern, SGM-306 §2).
 *
 * Routes under `basePath` (default /auth): GET login (returnTo, acr, max_age), GET callback, GET logout, GET me.
 */
export function createSangam(options: SangamNodeOptions) {
  if (options.cookieSecret.length < 32) throw new Error("cookieSecret must be at least 32 characters.");
  const basePath = (options.basePath ?? "/auth").replace(/\/+$/, "");
  const baseUrl = options.baseUrl.replace(/\/+$/, "");
  const redirectUri = `${baseUrl}${basePath}/callback`;
  const secure = baseUrl.startsWith("https://");
  const ttl = options.sessionTtlSeconds ?? 8 * 3600;
  const store = options.store ?? new MemorySessionStore<SangamSession>();
  const scopes = options.scopes ?? ["openid", "profile", "email", "orgs.read"];
  const authority = options.authority.replace(/\/+$/, "");
  let configuration: Promise<oidc.Configuration> | undefined = options.configuration ? Promise.resolve(options.configuration) : undefined;

  function config(): Promise<oidc.Configuration> {
    configuration ??= oidc
      .discovery(new URL(authority + "/"), options.clientId, options.clientSecret, oidc.ClientSecretPost(options.clientSecret), options.allowInsecureRequests ? { execute: [oidc.allowInsecureRequests] } : undefined)
      .catch((error: unknown) => {
        configuration = undefined;
        throw error;
      });
    return configuration;
  }

  async function load(req: IncomingMessage): Promise<{ id: string | undefined; session: SangamSession }> {
    const id = unsignId(cookies(req)[COOKIE], options.cookieSecret);
    return { id, session: (id ? await store.get(id) : undefined) ?? {} };
  }

  async function save(res: ServerResponse, id: string | undefined, session: SangamSession): Promise<string> {
    const sid = id ?? newId();
    await store.set(sid, session, ttl);
    setCookie(res, COOKIE, signId(sid, options.cookieSecret), { secure, maxAge: ttl });
    return sid;
  }

  /** The signed-in person, or undefined. */
  async function user(req: IncomingMessage): Promise<SangamUser | undefined> {
    return (await load(req)).session.user;
  }

  /** The address that starts a sign-in (with a step-up requirement, when given). */
  function loginUrl(returnTo: string, requirement?: StepUpRequirement): string {
    const q = new URLSearchParams({ returnTo });
    if (requirement) {
      q.set("acr", requirement.acr);
      if (requirement.maxAge !== undefined) q.set("max_age", String(requirement.maxAge));
    }
    return `${basePath}/login?${q.toString()}`;
  }

  async function login(req: IncomingMessage, res: ServerResponse, url: URL): Promise<void> {
    const c = await config();
    const verifier = oidc.randomPKCECodeVerifier();
    const state = oidc.randomState();
    const nonce = oidc.randomNonce();
    const parameters: Record<string, string> = {
      redirect_uri: redirectUri,
      scope: scopes.join(" "),
      code_challenge: await oidc.calculatePKCECodeChallenge(verifier),
      code_challenge_method: "S256",
      state,
      nonce,
    };
    const acr = url.searchParams.get("acr");
    const maxAge = url.searchParams.get("max_age");
    if (acr) parameters.acr_values = acr;
    if (maxAge && /^\d+$/.test(maxAge)) parameters.max_age = maxAge;
    const { id, session } = await load(req);
    session.pending = { state, nonce, verifier, returnTo: safeReturn(url.searchParams.get("returnTo")) };
    await save(res, id, session);
    redirect(res, oidc.buildAuthorizationUrl(c, parameters).href);
  }

  async function callback(req: IncomingMessage, res: ServerResponse, url: URL): Promise<void> {
    const { id, session } = await load(req);
    const pending = session.pending;
    if (!pending) {
      redirect(res, "/");
      return;
    }
    delete session.pending;
    if (url.searchParams.get("error")) {
      await save(res, id, session);
      redirect(res, pending.returnTo);
      return;
    }
    const tokens = await oidc.authorizationCodeGrant(await config(), new URL(redirectUri + url.search), {
      pkceCodeVerifier: pending.verifier,
      expectedState: pending.state,
      expectedNonce: pending.nonce,
      idTokenExpected: true,
    });
    const claims = tokens.claims() as Record<string, unknown> | undefined;
    if (!claims) throw new Error("Sangam returned no ID token.");
    session.user = userFromClaims(claims);
    if (tokens.id_token) session.idToken = tokens.id_token;
    // A new session id after sign-in, so an id planted before it is worth nothing.
    if (id) await store.delete(id);
    await save(res, undefined, session);
    redirect(res, pending.returnTo);
  }

  async function logout(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const { id, session } = await load(req);
    if (id) await store.delete(id);
    setCookie(res, COOKIE, "", { secure, maxAge: 0 });
    const c = await config();
    const parameters: Record<string, string> = { post_logout_redirect_uri: baseUrl + "/" };
    if (session.idToken) parameters.id_token_hint = session.idToken;
    redirect(res, oidc.buildEndSessionUrl(c, parameters).href);
  }

  /** Handles the sign-in routes; passes everything else on. Mount it before your own routes. */
  const router: Handler = async (req, res, next) => {
    const url = new URL(req.url ?? "/", baseUrl);
    if (req.method !== "GET" || !url.pathname.startsWith(basePath + "/")) {
      next();
      return;
    }
    try {
      switch (url.pathname.slice(basePath.length)) {
        case "/login":
          await login(req, res, url);
          return;
        case "/callback":
          await callback(req, res, url);
          return;
        case "/logout":
          await logout(req, res);
          return;
        case "/me": {
          const u = await user(req);
          if (u) json(res, 200, { user: u, loginUrl: loginUrl("/"), logoutUrl: `${basePath}/logout` });
          else json(res, 401, { user: null, loginUrl: loginUrl("/") });
          return;
        }
        default:
          next();
      }
    } catch (error) {
      next(error);
    }
  };

  /** Lets signed-in people through; sends others to sign in (or answers 401 to a fetch). */
  function requireSignIn(): Handler {
    return async (req, res, next) => {
      if (await user(req)) {
        next();
        return;
      }
      const login = loginUrl(req.url ?? "/");
      if (wantsJson(req)) json(res, 401, { error: "sign_in_required", login });
      else redirect(res, login);
    };
  }

  /**
   * Lets through people whose sign-in meets the requirement (level and age, SGM-207); sends others to Sangam to
   * authenticate again at that level, coming back to the same page (or answers 401 with where to go, to a fetch).
   */
  function requireStepUp(requirement: StepUpRequirement, returnTo?: (req: IncomingMessage) => string): Handler {
    return async (req, res, next) => {
      const u = await user(req);
      if (u && satisfies({ acr: u.acr, authTime: u.authTime }, requirement)) {
        next();
        return;
      }
      const login = loginUrl(returnTo ? returnTo(req) : req.url ?? "/", requirement);
      if (wantsJson(req)) json(res, 401, { error: "step_up_required", acr: requirement.acr, max_age: requirement.maxAge ?? null, login });
      else redirect(res, login);
    };
  }

  /** Lets through people who hold the permission at the organisation the request names; 403 otherwise. */
  function requirePermission(organisationPath: (req: IncomingMessage) => string | undefined, permission: string): Handler {
    return async (req, res, next) => {
      const u = await user(req);
      const path = organisationPath(req);
      if (u && path && can(u, path, permission)) next();
      else json(res, u ? 403 : 401, { error: u ? "forbidden" : "sign_in_required", permission });
    };
  }

  const verifyToken = createTokenVerifier({ issuer: authority + "/" });

  /**
   * For an API called with a Sangam access token: checks it, and the step-up requirement when given (answering with
   * the RFC 9470 challenge when short); sets `req.sangamToken`.
   */
  function requireToken(requirement?: StepUpRequirement & { scope?: string }, verifier = verifyToken): Handler {
    return async (req, res, next) => {
      const header = req.headers.authorization ?? "";
      if (!header.startsWith("Bearer ")) {
        json(res, 401, { error: "invalid_token" }, { "www-authenticate": 'Bearer realm="sangam"' });
        return;
      }
      try {
        const verified = await verifier(header.slice(7));
        if (requirement?.scope && !verified.scopes.includes(requirement.scope)) {
          json(res, 403, { error: "insufficient_scope" }, { "www-authenticate": `Bearer error="insufficient_scope", scope="${requirement.scope}"` });
          return;
        }
        if (requirement?.acr && !satisfies({ acr: verified.user.acr, authTime: verified.user.authTime }, requirement)) {
          json(res, 401, { error: "insufficient_user_authentication" }, { "www-authenticate": stepUpChallenge(requirement.acr, requirement.maxAge) });
          return;
        }
        req.sangamToken = { user: verified.user, claims: verified.claims as Record<string, unknown>, scopes: verified.scopes };
        next();
      } catch {
        json(res, 401, { error: "invalid_token" }, { "www-authenticate": 'Bearer error="invalid_token"' });
      }
    };
  }

  const management = new ManagementClient({ authority, clientId: options.clientId, clientSecret: options.clientSecret });
  const recorder = options.audit ? new AuditRecorder(options.audit, options.trustProxy ?? false) : undefined;

  const audit = {
    /** Records a shared audit event for the request's signed-in person (SGM-208). */
    async record(req: IncomingMessage, entry: AuditEntry): Promise<AuditEvent> {
      if (!recorder) throw new Error("Pass `audit` options to createSangam to record audit events.");
      return recorder.record(req, req.sangamToken?.user ?? (await user(req)), entry);
    },
    /** Events waiting for the audit service. */
    pending: () => (recorder ? recorder.pending() : Promise.resolve([])),
    /** Sends waiting events once `audit.endpoint` is set. */
    flush: () => (recorder ? recorder.flush(() => management.token("audit.write")) : Promise.resolve(0)),
    bufferPath: recorder?.bufferPath,
  };

  return { router, user, loginUrl, requireSignIn, requireStepUp, requirePermission, requireToken, audit, management, redirectUri };
}

export type Sangam = ReturnType<typeof createSangam>;
