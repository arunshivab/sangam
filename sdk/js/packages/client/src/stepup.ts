/** Sangam's authentication levels (`acr`), SGM-207. */
export const SangamAcr = {
  singleFactor: "urn:sangam:acr:1",
  twoFactor: "urn:sangam:acr:2",
  phishingResistant: "urn:sangam:acr:3",
  /** For an electronic signature: two factors, at most five minutes ago. */
  signature: "urn:sangam:acr:sign",
} as const;

/** A signature level is met only by an authentication at most this many seconds old. */
export const SIGNATURE_MAX_AGE_SECONDS = 300;

function rank(acr: string | undefined | null): number {
  switch (acr) {
    case SangamAcr.singleFactor:
      return 1;
    case SangamAcr.twoFactor:
    case SangamAcr.signature:
      return 2;
    case SangamAcr.phishingResistant:
      return 3;
    default:
      return 0;
  }
}

export interface StepUpRequirement {
  /** The level needed. */
  acr: string;
  /** How recent the authentication must be, in seconds. */
  maxAge?: number;
}

/**
 * Whether how the person authenticated meets a requirement: the level, or a stronger one, and recent enough. A signature
 * level is always capped at five minutes.
 */
export function satisfies(
  auth: { acr?: string | null | undefined; authTime?: number | null | undefined },
  requirement: StepUpRequirement,
  now: number = Math.floor(Date.now() / 1000),
): boolean {
  let maxAge = requirement.maxAge;
  if (requirement.acr === SangamAcr.signature) {
    maxAge = maxAge !== undefined && maxAge < SIGNATURE_MAX_AGE_SECONDS ? maxAge : SIGNATURE_MAX_AGE_SECONDS;
  }
  if (rank(auth.acr) < rank(requirement.acr)) return false;
  if (maxAge === undefined) return true;
  return typeof auth.authTime === "number" && now - auth.authTime <= maxAge;
}

/** The RFC 9470 `WWW-Authenticate` value an API returns when a request's token is not strong or recent enough. */
export function stepUpChallenge(acr: string, maxAge?: number): string {
  const header = `Bearer error="insufficient_user_authentication", error_description="A stronger or more recent authentication is required", acr_values="${acr}"`;
  return maxAge === undefined ? header : `${header}, max_age=${Math.floor(maxAge)}`;
}
