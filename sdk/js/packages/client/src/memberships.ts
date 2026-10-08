/** One role a person holds at one organisation, from the `sangam_orgs` claim. */
export interface SangamMembership {
  organisationId: string;
  organisationName: string;
  organisationType: string;
  /** Materialised path, ending with "/" (for example "/group/hospital/"). */
  path: string;
  role: string;
  permissions: readonly string[];
  /** Whether the role reaches the organisation's descendants. */
  appliesToDescendants: boolean;
}

/** Who signed in, as Sangam's tokens describe them. */
export interface SangamUser {
  /** The Sangam subject id (`sub`): stable, and the only identifier to store. */
  id: string;
  name: string;
  email?: string;
  memberships: readonly SangamMembership[];
  /** How and when they authenticated (`acr`, `amr`, `auth_time`), for step-up. */
  acr?: string;
  amr?: readonly string[];
  authTime?: number;
  /** The Sangam session (`sid`). */
  sessionId?: string;
  /** True when the name, date of birth and gender were verified with DigiLocker. */
  identityVerified?: boolean;
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Reads memberships from `sangam_orgs`: a JSON string, an array, or one object. Anything malformed is skipped rather
 * than failing the sign-in, as in every Sangam SDK.
 */
export function parseMemberships(claim: unknown): SangamMembership[] {
  let value: unknown = claim;
  if (typeof claim === "string") {
    try {
      value = JSON.parse(claim);
    } catch {
      return [];
    }
  }
  const items = Array.isArray(value) ? value : value === null || value === undefined ? [] : [value];
  const result: SangamMembership[] = [];
  for (const item of items) {
    if (typeof item !== "object" || item === null) continue;
    const o = item as Record<string, unknown>;
    const id = typeof o.id === "string" ? o.id : "";
    const path = typeof o.path === "string" ? o.path : "";
    const role = typeof o.role === "string" ? o.role : "";
    if (!UUID.test(id) || path.length === 0 || role.length === 0) continue;
    result.push({
      organisationId: id,
      organisationName: typeof o.name === "string" ? o.name : "",
      organisationType: typeof o.type === "string" ? o.type : "",
      path,
      role,
      permissions: Array.isArray(o.permissions) ? o.permissions.filter((p): p is string => typeof p === "string") : [],
      appliesToDescendants: o.inherits === true,
    });
  }
  return result;
}

/** Builds the user from token claims (an ID token, an access token, or userinfo). */
export function userFromClaims(claims: Record<string, unknown>): SangamUser {
  const sub = typeof claims.sub === "string" ? claims.sub : "";
  const email = typeof claims.email === "string" ? claims.email : undefined;
  const user: SangamUser = {
    id: sub,
    name: typeof claims.name === "string" ? claims.name : email ?? "",
    memberships: parseMemberships(claims.sangam_orgs),
  };
  if (email !== undefined) user.email = email;
  if (typeof claims.acr === "string") user.acr = claims.acr;
  if (Array.isArray(claims.amr)) user.amr = claims.amr.filter((a): a is string => typeof a === "string");
  if (typeof claims.auth_time === "number") user.authTime = claims.auth_time;
  if (typeof claims.sid === "string") user.sessionId = claims.sid;
  if (claims.sangam_identity_verified === true) user.identityVerified = true;
  return user;
}

/** Whether a membership covers an organisation: the organisation itself, or a descendant when the role is inherited. */
export function covers(membership: SangamMembership, organisationPath: string): boolean {
  const target = organisationPath.toLowerCase();
  const own = membership.path.toLowerCase();
  if (target === own) return true;
  // Paths end with "/", so "/a/" never matches a sibling "/ab/".
  return membership.appliesToDescendants && own.endsWith("/") && target.startsWith(own);
}

/**
 * The one permission rule (SGM-306): granted at the organisation, or at an ancestor whose membership inherits. Deny by
 * default. Applications enforce; Sangam only says who holds what.
 */
export function can(user: SangamUser | null | undefined, organisationPath: string, permission: string): boolean {
  return !!user && user.memberships.some((m) => covers(m, organisationPath) && m.permissions.includes(permission));
}

/** Whether the person holds the role at the organisation (directly or inherited). */
export function hasRole(user: SangamUser | null | undefined, organisationPath: string, role: string): boolean {
  return !!user && user.memberships.some((m) => m.role === role && covers(m, organisationPath));
}

/** The roles the person holds at an organisation, sorted. */
export function rolesIn(user: SangamUser | null | undefined, organisationPath: string): string[] {
  if (!user) return [];
  return [...new Set(user.memberships.filter((m) => covers(m, organisationPath)).map((m) => m.role))].sort();
}
