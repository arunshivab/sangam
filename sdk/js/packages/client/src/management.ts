import { CLIENT_ASSERTION_TYPE, createClientAssertion, loadClientKey, type ClientKey } from "./clientkey.js";

export interface ManagementClientOptions {
  /** Sangam's address, for example "https://id.sangamid.in". */
  authority: string;
  clientId: string;
  /** The client secret; not needed when `privateKey` is set. */
  clientSecret?: string;
  /** rc.15: a private key (PKCS#8 PEM or private JWK) to sign in with instead of a secret (`private_key_jwt`). */
  privateKey?: string;
  /** A fetch to use instead of the global one. */
  fetch?: typeof fetch;
}

/** Thrown when the management API answers with an error. */
export class SangamApiError extends Error {
  constructor(readonly status: number, readonly body: string, message: string) {
    super(message);
    this.name = "SangamApiError";
  }
}

/** Drops trailing "/" characters (a loop, not a regular expression, so its time stays linear on any input). */
function trimTrailingSlashes(value: string): string {
  let end = value.length;
  while (end > 0 && value.charCodeAt(end - 1) === 47) {
    end--;
  }
  return value.slice(0, end);
}

/**
 * Sangam's management API for your application's back end, with client-credentials tokens fetched and cached. Needs a
 * confidential client allowed `sangam.manage`. Never use it from a browser.
 */
export class ManagementClient {
  readonly #options: ManagementClientOptions;
  readonly #tokens = new Map<string, { token: string; expires: number }>();
  readonly #fetch: typeof fetch;

  #key: Promise<ClientKey> | undefined;

  constructor(options: ManagementClientOptions) {
    if (!options.clientSecret && !options.privateKey) throw new Error("Sangam: set clientSecret or privateKey.");
    this.#options = { ...options, authority: trimTrailingSlashes(options.authority) };
    this.#fetch = options.fetch ?? fetch;
    this.#key = options.privateKey ? loadClientKey(options.privateKey) : undefined;
    // A key that cannot be read is reported on the first call; this stops an unhandled rejection before then.
    this.#key?.catch(() => undefined);
  }

  /** A client-credentials token for the scope, reused until a minute before it expires. */
  async token(scope = "sangam.manage"): Promise<string> {
    const cached = this.#tokens.get(scope);
    if (cached && cached.expires > Date.now() + 60_000) return cached.token;
    const body = new URLSearchParams({ grant_type: "client_credentials", client_id: this.#options.clientId, scope });
    if (this.#key) {
      // rc.15: a fresh key-signed assertion, for Sangam's issuer, instead of the secret.
      body.set("client_assertion_type", CLIENT_ASSERTION_TYPE);
      body.set("client_assertion", await createClientAssertion(await this.#key, this.#options.clientId, this.#options.authority + "/"));
    } else {
      body.set("client_secret", this.#options.clientSecret ?? "");
    }
    const response = await this.#fetch(`${this.#options.authority}/connect/token`, { method: "POST", body, headers: { "content-type": "application/x-www-form-urlencoded" } });
    const text = await response.text();
    if (!response.ok) throw new SangamApiError(response.status, text, `Sangam refused a token for ${scope} (${response.status})`);
    const json = JSON.parse(text) as { access_token: string; expires_in?: number };
    this.#tokens.set(scope, { token: json.access_token, expires: Date.now() + (json.expires_in ?? 300) * 1000 });
    return json.access_token;
  }

  /**
   * Any call: `path` is relative to /api/v1/. rc.12: `adminAccessToken` is one of your admins' own access token, sent as
   * `Sangam-Acting-Admin` on staff calls.
   */
  async send<T = unknown>(method: string, path: string, body?: unknown, adminAccessToken?: string): Promise<T> {
    const headers: Record<string, string> = { authorization: `Bearer ${await this.token()}` };
    if (adminAccessToken) headers["sangam-acting-admin"] = adminAccessToken;
    const init: RequestInit = { method, headers };
    if (body !== undefined) {
      headers["content-type"] = "application/json";
      init.body = JSON.stringify(body);
    }
    const response = await this.#fetch(`${this.#options.authority}/api/v1/${path.replace(/^\/+/, "")}`, init);
    const text = await response.text();
    if (!response.ok) throw new SangamApiError(response.status, text, `Sangam's management API answered ${response.status} to ${method} ${path}`);
    return (text.length === 0 ? undefined : JSON.parse(text)) as T;
  }

  upsertRole(code: string, displayName: string, permissions: string[]) {
    return this.send("PUT", `roles/${encodeURIComponent(code)}`, { displayName, description: null, permissions, orgId: null });
  }

  upsertOrganisation(id: string, name: string, type: string, parentId: string | null = null) {
    return this.send("PUT", `orgs/${id}`, { name, type, parentId, metadata: null });
  }

  /**
   * Gives a person a role at an organisation; `expiresAt` makes it time-limited. rc.5: only someone who already uses
   * your application; anyone else answers 404 — add them with `addStaff` instead. rc.12: a role holds only where it is
   * given, so `appliesToDescendants` must be false. rc.13: done for one of your admins (an admin of that organisation):
   * `adminAccessToken` is the token Sangam gave your application when they signed in.
   */
  upsertMembership(orgId: string, userId: string, adminAccessToken: string, role: string, appliesToDescendants = false, expiresAt?: string) {
    return this.send("PUT", `orgs/${orgId}/members/${userId}`, expiresAt ? { role, appliesToDescendants, expiresAt } : { role, appliesToDescendants }, adminAccessToken);
  }

  /**
   * rc.12: every role a person holds at an organisation; an empty list keeps them there as staff. rc.13: for one of your
   * admins (`adminAccessToken`), like `addStaff`.
   */
  setRoles(orgId: string, userId: string, adminAccessToken: string, roles: string[], expiresAt?: string) {
    return this.send("PUT", `orgs/${orgId}/members/${userId}`, expiresAt ? { roles, expiresAt } : { roles }, adminAccessToken);
  }

  /** rc.13: ends a person's place at an organisation now, for one of your admins (`adminAccessToken`). */
  removeMember(orgId: string, userId: string, adminAccessToken: string) {
    return this.send("DELETE", `orgs/${orgId}/members/${userId}`, undefined, adminAccessToken);
  }

  /**
   * rc.12: the check before adding a person — which details match an existing Sangam account, never what Sangam holds.
   * Done for one of your admins: `adminAccessToken` is the token Sangam gave your application when they signed in.
   */
  checkStaff(orgId: string, adminAccessToken: string, person: SangamStaffPerson) {
    return this.send<SangamStaffCheck>("POST", `orgs/${orgId}/staff/check`, person, adminAccessToken);
  }

  /**
   * rc.12: adds one person to one organisation for one of your admins (verified with DigiLocker). Send `person` (they get
   * an activation code your admin hands over, returned once), `joinCode`, `staffRef` from an earlier call, or `userId`.
   */
  addStaff(orgId: string, adminAccessToken: string, addition: SangamStaffAddition) {
    return this.send<SangamStaffResult>("POST", `orgs/${orgId}/staff`, addition, adminAccessToken);
  }

  /**
   * Where a person's account went if they merged it into another of theirs (rc.7): the surviving account's `sub`, or
   * `null` when the id was not merged or your application never knew it. Use it on `account.merged`.
   */
  async mergedInto(userId: string): Promise<string | null> {
    try {
      const answer = await this.send<{ merged_into: string; merged_at: string }>("GET", `users/${userId}/merged`);
      return answer.merged_into;
    } catch (error) {
      if (error instanceof SangamApiError && error.status === 404) return null;
      throw error;
    }
  }

  getAttributes(userId: string) {
    return this.send<Record<string, string | null>>("GET", `users/${userId}/attributes`);
  }

  /**
   * Sets values your application keeps about a person; an empty value clears one. rc.14: done for one of your admins,
   * who administers your application or an organisation where the person works in it.
   */
  setAttributes(userId: string, adminAccessToken: string, values: Record<string, string | null>) {
    return this.send("PUT", `users/${userId}/attributes`, values, adminAccessToken);
  }
}

/** rc.12: a person's details as your admin entered them. `dateOfBirth` is `yyyy-mm-dd`; `mobile` is E.164. */
export interface SangamStaffPerson {
  firstName: string;
  lastName: string;
  email: string;
  mobile?: string | null;
  dateOfBirth: string;
}

/** rc.12: one person to one organisation; set exactly one of `person`, `joinCode`, `staffRef` and `userId`. */
export interface SangamStaffAddition {
  person?: SangamStaffPerson;
  joinCode?: string;
  staffRef?: string;
  userId?: string;
  roles?: string[];
  startsOn?: string;
  expiresAt?: string;
  confirmed?: boolean;
}

/** rc.12: the check's answer. */
export interface SangamStaffCheck {
  outcome: "none" | "match" | "differs" | "similar";
  matching: string[];
  differing: string[];
  message: string;
  checksLeftToday: number;
}

/** rc.12: what adding did. */
export interface SangamStaffResult {
  status: "added" | "waiting" | "needs_confirmation" | "refused" | "not_found" | "too_many";
  message: string;
  staffRef: string | null;
  userId: string | null;
  activationCode: string | null;
  check: SangamStaffCheck | null;
  addedBeforeDigiLocker: boolean;
  /** rc.13: it waits for the person to verify with DigiLocker (the organisation takes only verified people). */
  waitsForDigiLocker?: boolean;
}
