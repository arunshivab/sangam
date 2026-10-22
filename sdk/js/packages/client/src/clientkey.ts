import { base64url, calculateJwkThumbprint, exportJWK, importJWK, importPKCS8, SignJWT, type CryptoKey, type JWK } from "jose";

/** rc.15: the `client_assertion_type` sent with each assertion. */
export const CLIENT_ASSERTION_TYPE = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

/** rc.15: the JOSE type of each assertion. */
export const CLIENT_ASSERTION_TOKEN_TYPE = "client-authentication+jwt";

/**
 * rc.15: a private key your back end signs in to Sangam with, instead of a client secret (`private_key_jwt`, RFC 7523).
 * Add the matching public key on the partner console (Credentials, Keys); `kid` is its RFC 7638 thumbprint, the key ID
 * the console shows.
 */
export interface ClientKey {
  key: CryptoKey;
  kid: string;
  alg: "RS256" | "ES256" | "ES384" | "ES512";
}

/**
 * Reads a private key: PKCS#8 PEM (`-----BEGIN PRIVATE KEY-----`; convert others with `openssl pkcs8 -topk8 -nocrypt`)
 * or a private JSON Web Key. RSA of 2048 bits or more, or elliptic curve P-256, P-384 or P-521. Never use it in a browser.
 */
export async function loadClientKey(privateKey: string): Promise<ClientKey> {
  const text = privateKey.trim();
  if (text.startsWith("{")) {
    const jwk = JSON.parse(text) as JWK;
    if (!jwk.d) throw new Error("Sangam: that is a public key. Your back end signs with the private key; the public key goes on the partner console.");
    const alg = algorithmOf(jwk);
    return finish((await importJWK(jwk, alg, { extractable: true })) as CryptoKey, alg);
  }
  if (!text.includes("-----BEGIN PRIVATE KEY-----")) {
    throw new Error("Sangam: give the private key in PKCS#8 PEM (-----BEGIN PRIVATE KEY-----); convert with: openssl pkcs8 -topk8 -nocrypt -in key.pem");
  }
  for (const alg of ["RS256", "ES256", "ES384", "ES512"] as const) {
    try {
      return await finish(await importPKCS8(text, alg, { extractable: true }), alg);
    } catch (error) {
      if (error instanceof Error && error.message.startsWith("Sangam:")) throw error;
      // Not this kind of key: try the next.
    }
  }
  throw new Error("Sangam: that is not an RSA or elliptic-curve (P-256, P-384, P-521) private key.");
}

async function finish(key: CryptoKey, alg: ClientKey["alg"]): Promise<ClientKey> {
  const jwk = await exportJWK(key);
  if (jwk.kty === "RSA" && base64url.decode(jwk.n!).length * 8 < 2048) throw new Error("Sangam: the RSA key must have at least 2048 bits.");
  const pub: JWK = jwk.kty === "RSA" ? { kty: "RSA", n: jwk.n!, e: jwk.e! } : { kty: "EC", crv: jwk.crv!, x: jwk.x!, y: jwk.y! };
  return { key, kid: await calculateJwkThumbprint(pub, "sha256"), alg };
}

function algorithmOf(jwk: JWK): ClientKey["alg"] {
  if (jwk.kty === "RSA") return "RS256";
  if (jwk.kty === "EC" && jwk.crv === "P-256") return "ES256";
  if (jwk.kty === "EC" && jwk.crv === "P-384") return "ES384";
  if (jwk.kty === "EC" && jwk.crv === "P-521") return "ES512";
  throw new Error("Sangam: use an RSA key, or an elliptic-curve key on P-256, P-384 or P-521.");
}

/**
 * A new assertion for one request to Sangam's token endpoint: `iss` and `sub` are the client id, `aud` is Sangam's issuer
 * (`authority`), a fresh `jti`, two minutes to live, typed `client-authentication+jwt`. Sangam accepts each one once.
 */
export async function createClientAssertion(clientKey: ClientKey, clientId: string, authority: string): Promise<string> {
  const id = new Uint8Array(16);
  crypto.getRandomValues(id);
  return new SignJWT({})
    .setProtectedHeader({ alg: clientKey.alg, kid: clientKey.kid, typ: CLIENT_ASSERTION_TOKEN_TYPE })
    .setIssuer(clientId)
    .setSubject(clientId)
    .setAudience(authority)
    .setJti(base64url.encode(id))
    .setIssuedAt()
    .setNotBefore("0s")
    .setExpirationTime("2m")
    .sign(clientKey.key);
}
