import { createRemoteJWKSet, jwtVerify, type JWTPayload } from "jose";
import { userFromClaims, type SangamUser } from "./memberships.js";

export interface TokenVerifierOptions {
  /** Sangam's issuer, for example "https://id.sangamid.in/". */
  issuer: string;
  /** The audience your API expects, when its tokens carry one. */
  audience?: string;
  /** The JWKS address; read from discovery when omitted. */
  jwksUri?: string;
}

/**
 * Checks Sangam access tokens for your API: signature against Sangam's published keys, issuer, expiry and, when given,
 * audience. Returns the person (with their memberships, acr and auth_time) and the token's claims.
 */
export function createTokenVerifier(options: TokenVerifierOptions): (token: string) => Promise<{ user: SangamUser; claims: JWTPayload; scopes: string[] }> {
  const issuer = options.issuer.endsWith("/") ? options.issuer : options.issuer + "/";
  let jwks: ReturnType<typeof createRemoteJWKSet> | undefined;
  async function keys() {
    if (jwks) return jwks;
    let uri = options.jwksUri;
    if (!uri) {
      const response = await fetch(new URL(".well-known/openid-configuration", issuer));
      if (!response.ok) throw new Error(`Sangam discovery answered ${response.status}`);
      uri = ((await response.json()) as { jwks_uri: string }).jwks_uri;
    }
    jwks = createRemoteJWKSet(new URL(uri));
    return jwks;
  }
  return async (token: string) => {
    const { payload } = await jwtVerify(token, await keys(), options.audience ? { issuer, audience: options.audience } : { issuer });
    const scope = typeof payload.scope === "string" ? payload.scope.split(" ") : Array.isArray(payload.scp) ? (payload.scp as string[]) : [];
    return { user: userFromClaims(payload as Record<string, unknown>), claims: payload, scopes: scope.filter((s) => s.length > 0) };
  };
}
