package in.sangamid.client;

import com.nimbusds.jose.JOSEException;
import com.nimbusds.jose.JOSEObjectType;
import com.nimbusds.jose.JWSAlgorithm;
import com.nimbusds.jose.JWSHeader;
import com.nimbusds.jose.JWSSigner;
import com.nimbusds.jose.crypto.ECDSASigner;
import com.nimbusds.jose.crypto.RSASSASigner;
import com.nimbusds.jose.jwk.Curve;
import com.nimbusds.jose.jwk.ECKey;
import com.nimbusds.jose.jwk.JWK;
import com.nimbusds.jose.jwk.RSAKey;
import com.nimbusds.jwt.JWTClaimsSet;
import com.nimbusds.jwt.SignedJWT;
import java.security.GeneralSecurityException;
import java.security.KeyFactory;
import java.security.SecureRandom;
import java.security.interfaces.RSAPrivateCrtKey;
import java.security.interfaces.RSAPublicKey;
import java.security.spec.PKCS8EncodedKeySpec;
import java.security.spec.RSAPublicKeySpec;
import java.text.ParseException;
import java.util.Base64;
import java.util.Date;
import java.util.Set;

/**
 * rc.15: signs your application in to Sangam with a private key instead of a client secret ({@code private_key_jwt},
 * RFC 7523). Add the matching public key on the partner console (Credentials, Keys). Each {@link #assertion} is new:
 * {@code iss} and {@code sub} are the client id, {@code aud} is Sangam's issuer, a fresh {@code jti}, two minutes to live,
 * typed {@code client-authentication+jwt}, with the key's RFC 7638 thumbprint as {@code kid} — the key ID the console shows.
 */
public final class ClientKey {
    /** The {@code client_assertion_type} sent with each assertion. */
    public static final String ASSERTION_TYPE = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    /** The JOSE type of each assertion. */
    public static final String TOKEN_TYPE = "client-authentication+jwt";
    private static final SecureRandom RANDOM = new SecureRandom();
    private static final Set<Curve> CURVES = Set.of(Curve.P_256, Curve.P_384, Curve.P_521);

    private final JWK jwk;
    private final JWSSigner signer;
    private final JWSAlgorithm algorithm;

    private ClientKey(JWK jwk, JWSSigner signer, JWSAlgorithm algorithm) {
        this.jwk = jwk;
        this.signer = signer;
        this.algorithm = algorithm;
    }

    /**
     * An RSA private key in PEM (PKCS#8, {@code -----BEGIN PRIVATE KEY-----}), 2048 bits or more. For an elliptic-curve
     * key, use {@link #fromJwk} (Java cannot work out the public point from a PEM private key alone).
     */
    public static ClientKey fromPem(String pem) {
        if (pem == null || !pem.contains("-----BEGIN PRIVATE KEY-----")) {
            throw new IllegalArgumentException("Sangam: give the private key in PKCS#8 PEM (-----BEGIN PRIVATE KEY-----); convert with: openssl pkcs8 -topk8 -nocrypt -in key.pem");
        }
        String base64 = pem.replaceAll("-----(BEGIN|END) PRIVATE KEY-----", "").replaceAll("\\s", "");
        try {
            KeyFactory factory = KeyFactory.getInstance("RSA");
            RSAPrivateCrtKey key = (RSAPrivateCrtKey) factory.generatePrivate(new PKCS8EncodedKeySpec(Base64.getDecoder().decode(base64)));
            RSAPublicKey pub = (RSAPublicKey) factory.generatePublic(new RSAPublicKeySpec(key.getModulus(), key.getPublicExponent()));
            return fromJwk(new RSAKey.Builder(pub).privateKey(key).build().toJSONString());
        } catch (GeneralSecurityException | ClassCastException e) {
            throw new IllegalArgumentException("Sangam: that is not an RSA private key Java can read. For an elliptic-curve key, use ClientKey.fromJwk.", e);
        }
    }

    /** A private key as a JSON Web Key: RSA (2048 bits or more), or EC on P-256, P-384 or P-521, with its private part. */
    public static ClientKey fromJwk(String json) {
        try {
            JWK parsed = JWK.parse(json);
            if (!parsed.isPrivate()) {
                throw new IllegalArgumentException("Sangam: that is a public key. Your servers sign with the private key; the public key goes on the partner console.");
            }
            if (parsed instanceof RSAKey rsa) {
                if (rsa.size() < 2048) {
                    throw new IllegalArgumentException("Sangam: the RSA key must have at least 2048 bits.");
                }
                RSAKey withKid = new RSAKey.Builder(rsa).keyID(rsa.computeThumbprint().toString()).build();
                return new ClientKey(withKid, new RSASSASigner(withKid), JWSAlgorithm.RS256);
            }
            if (parsed instanceof ECKey ec && CURVES.contains(ec.getCurve())) {
                ECKey withKid = new ECKey.Builder(ec).keyID(ec.computeThumbprint().toString()).build();
                JWSAlgorithm alg = ec.getCurve() == Curve.P_256 ? JWSAlgorithm.ES256 : ec.getCurve() == Curve.P_384 ? JWSAlgorithm.ES384 : JWSAlgorithm.ES512;
                return new ClientKey(withKid, new ECDSASigner(withKid), alg);
            }
            throw new IllegalArgumentException("Sangam: use an RSA key, or an elliptic-curve key on P-256, P-384 or P-521.");
        } catch (ParseException | JOSEException e) {
            throw new IllegalArgumentException("Sangam: that private key cannot be read.", e);
        }
    }

    /** The key's ID, as the partner console shows it. */
    public String keyId() {
        return jwk.getKeyID();
    }

    /** The key as a JSON Web Key, private part included, for Spring Security's {@code private_key_jwt}. */
    public JWK jwk() {
        return jwk;
    }

    /** A new assertion for one request to Sangam's token endpoint. */
    public String assertion(String clientId, String authority) {
        byte[] id = new byte[16];
        RANDOM.nextBytes(id);
        long now = System.currentTimeMillis();
        JWTClaimsSet claims = new JWTClaimsSet.Builder()
                .issuer(clientId)
                .subject(clientId)
                .audience(authority)
                .jwtID(Base64.getUrlEncoder().withoutPadding().encodeToString(id))
                .issueTime(new Date(now))
                .notBeforeTime(new Date(now))
                .expirationTime(new Date(now + 120_000))
                .build();
        SignedJWT jwt = new SignedJWT(new JWSHeader.Builder(algorithm).type(new JOSEObjectType(TOKEN_TYPE)).keyID(keyId()).build(), claims);
        try {
            jwt.sign(signer);
        } catch (JOSEException e) {
            throw new IllegalStateException("Sangam: the assertion could not be signed.", e);
        }
        return jwt.serialize();
    }
}
