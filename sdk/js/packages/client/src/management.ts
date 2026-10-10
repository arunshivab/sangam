export interface ManagementClientOptions {
  /** Sangam's address, for example "https://id.sangamid.in". */
  authority: string;
  clientId: string;
  clientSecret: string;
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

  constructor(options: ManagementClientOptions) {
    this.#options = { ...options, authority: trimTrailingSlashes(options.authority) };
    this.#fetch = options.fetch ?? fetch;
  }

  /** A client-credentials token for the scope, reused until a minute before it expires. */
  async token(scope = "sangam.manage"): Promise<string> {
    const cached = this.#tokens.get(scope);
    if (cached && cached.expires > Date.now() + 60_000) return cached.token;
    const body = new URLSearchParams({ grant_type: "client_credentials", client_id: this.#options.clientId, client_secret: this.#options.clientSecret, scope });
    const response = await this.#fetch(`${this.#options.authority}/connect/token`, { method: "POST", body, headers: { "content-type": "application/x-www-form-urlencoded" } });
    const text = await response.text();
    if (!response.ok) throw new SangamApiError(response.status, text, `Sangam refused a token for ${scope} (${response.status})`);
    const json = JSON.parse(text) as { access_token: string; expires_in?: number };
    this.#tokens.set(scope, { token: json.access_token, expires: Date.now() + (json.expires_in ?? 300) * 1000 });
    return json.access_token;
  }

  /** Any call: `path` is relative to /api/v1/. */
  async send<T = unknown>(method: string, path: string, body?: unknown): Promise<T> {
    const headers: Record<string, string> = { authorization: `Bearer ${await this.token()}` };
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
   * your application; anyone else answers 404 — invite them with `invite` instead.
   */
  upsertMembership(orgId: string, userId: string, role: string, appliesToDescendants = false, expiresAt?: string) {
    return this.send("PUT", `orgs/${orgId}/members/${userId}`, expiresAt ? { role, appliesToDescendants, expiresAt } : { role, appliesToDescendants });
  }

  /** Invites someone by e-mail to a role at an organisation (rc.5); they are added when they accept. At most 200 a day. */
  invite(orgId: string, email: string, role: string, appliesToDescendants = false) {
    return this.send("POST", `orgs/${orgId}/invitations`, { email, role, appliesToDescendants });
  }

  getAttributes(userId: string) {
    return this.send<Record<string, string | null>>("GET", `users/${userId}/attributes`);
  }

  setAttributes(userId: string, values: Record<string, string | null>) {
    return this.send("PUT", `users/${userId}/attributes`, values);
  }
}
