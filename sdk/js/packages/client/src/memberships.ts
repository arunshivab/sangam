/**
 * One role a person holds at one organisation, from the `sangam_orgs` claim. Since rc.12 one claim entry carries every
 * role held there (`roles`), and each becomes one membership; a place with no role in Sangam (the application keeps its
 * roles itself) becomes one membership with an empty `role`.
 */
export interface SangamMembership {
  organisationId: string;
  organisationName: string;
  organisationType: string;
  /** Materialised path, ending with "/" (for example "/group/hospital/"). */
  path: string;
  /** The role's code; empty when the person belongs here without a role in Sangam. */
  role: string;
  /** The permissions of every role held at this organisation. */
  permissions: readonly string[];
  /** Whether the role reaches the organisation's descendants. Always false since rc.12: a role holds where it was given. */
  appliesToDescendants: boolean;
}

/** rc.12 (A6): the organisation the person chose to work at for this sign-in ("Working at which branch?"). */
export interface SangamCurrentOrganisation {
  organisationId: string;
  organisationName: string;
  organisationType: string;
  path: string;
  /** Every role held there; empty when your application keeps roles itself. */
  roles: readonly string[];
  permissions: readonly string[];
}

/**
 * rc.12 (M12): emergency access in progress, from `sangam_emergency`. Show a banner, apply your own limits, and stop at
 * `until` (Unix seconds) even if the token lives longer.
 */
export interface SangamEmergency {
  id: string;
  /** Your own case reference, as you sent it. */
  caseReference: string;
  organisationId: string;
  until: number;
}

/** Who signed in, as Sangam's tokens describe them. */
export interface SangamUser {
  /** The Sangam subject id (`sub`): stable, and the only identifier to store. */
  id: string;
  name: string;
  email?: string;
  memberships: readonly SangamMembership[];
  /** rc.12: where they are working for this sign-in, from `sangam_org` (switch with `prompt=select_org`). */
  currentOrganisation?: SangamCurrentOrganisation;
  /** rc.12 (M12): emergency access in progress for this sign-in, from `sangam_emergency`. */
  emergency?: SangamEmergency;
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
    if (!UUID.test(id) || path.length === 0) continue;
    const strings = (v: unknown): string[] =>
      Array.isArray(v) ? v.filter((x): x is string => typeof x === "string" && x.length > 0) : [];
    let roles = strings(o.roles);
    if (roles.length === 0 && typeof o.role === "string" && o.role.length > 0) roles = [o.role];
    if (roles.length === 0) roles = [""];
    const permissions = strings(o.permissions);
    for (const role of new Set(roles)) {
      result.push({
        organisationId: id,
        organisationName: typeof o.name === "string" ? o.name : "",
        organisationType: typeof o.type === "string" ? o.type : "",
        path,
        role,
        permissions,
        appliesToDescendants: o.inherits === true,
      });
    }
  }
  return result;
}

/** Reads `sangam_org`: a JSON string or one object. Anything malformed is no organisation. */
export function parseCurrentOrganisation(claim: unknown): SangamCurrentOrganisation | undefined {
  let value: unknown = claim;
  if (typeof claim === "string") {
    try {
      value = JSON.parse(claim);
    } catch {
      return undefined;
    }
  }
  if (typeof value !== "object" || value === null || Array.isArray(value)) return undefined;
  const o = value as Record<string, unknown>;
  const id = typeof o.id === "string" ? o.id : "";
  const path = typeof o.path === "string" ? o.path : "";
  if (!UUID.test(id) || path.length === 0) return undefined;
  const strings = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string" && x.length > 0) : []);
  const roles = strings(o.roles);
  if (roles.length === 0 && typeof o.role === "string" && o.role.length > 0) roles.push(o.role);
  return {
    organisationId: id,
    organisationName: typeof o.name === "string" ? o.name : "",
    organisationType: typeof o.type === "string" ? o.type : "",
    path,
    roles,
    permissions: strings(o.permissions),
  };
}

/** Reads `sangam_emergency`: a JSON string or one object. Anything malformed is no emergency access. */
export function parseEmergency(claim: unknown): SangamEmergency | undefined {
  let value: unknown = claim;
  if (typeof claim === "string") {
    try {
      value = JSON.parse(claim);
    } catch {
      return undefined;
    }
  }
  if (typeof value !== "object" || value === null || Array.isArray(value)) return undefined;
  const o = value as Record<string, unknown>;
  if (typeof o.id !== "string" || !UUID.test(o.id) || typeof o.org !== "string" || !UUID.test(o.org)) return undefined;
  if (typeof o.case !== "string" || o.case.length === 0 || typeof o.until !== "number" || !Number.isFinite(o.until)) return undefined;
  return { id: o.id, caseReference: o.case, organisationId: o.org, until: o.until };
}

/** Whether emergency access is still in force (`now` in Unix seconds). */
export function emergencyActive(emergency: SangamEmergency | undefined, now: number = Math.floor(Date.now() / 1000)): boolean {
  return emergency !== undefined && now < emergency.until;
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
  const current = parseCurrentOrganisation(claims.sangam_org);
  if (current) user.currentOrganisation = current;
  const emergency = parseEmergency(claims.sangam_emergency);
  if (emergency) user.emergency = emergency;
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
  return !!user && role.length > 0 && user.memberships.some((m) => m.role === role && covers(m, organisationPath));
}

/** The roles the person holds at an organisation, sorted. */
export function rolesIn(user: SangamUser | null | undefined, organisationPath: string): string[] {
  if (!user) return [];
  return [...new Set(user.memberships.filter((m) => m.role.length > 0 && covers(m, organisationPath)).map((m) => m.role))].sort();
}
