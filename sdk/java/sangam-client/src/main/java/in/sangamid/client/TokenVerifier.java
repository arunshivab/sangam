package in.sangamid.client;

import com.nimbusds.jose.JOSEException;
import com.nimbusds.jose.JWSAlgorithm;
import com.nimbusds.jose.jwk.source.JWKSource;
import com.nimbusds.jose.jwk.source.JWKSourceBuilder;
import com.nimbusds.jose.proc.BadJOSEException;
import com.nimbusds.jose.proc.JWSVerificationKeySelector;
import com.nimbusds.jose.proc.SecurityContext;
import com.nimbusds.jwt.JWTClaimsSet;
import com.nimbusds.jwt.proc.DefaultJWTClaimsVerifier;
import com.nimbusds.jwt.proc.DefaultJWTProcessor;
import java.net.MalformedURLException;
import java.net.URI;
import java.text.ParseException;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/** Checks Sangam access tokens for your API: signature against Sangam's published keys, issuer, expiry, audience. */
public final class TokenVerifier {
    private final DefaultJWTProcessor<SecurityContext> processor = new DefaultJWTProcessor<>();

    /** The outcome: the person, the claims and the granted scopes. */
    public record Verified(SangamUser user, Map<String, Object> claims, List<String> scopes) {
    }

    /** Thrown when a token is not Sangam's, has expired, or is not for this API. */
    public static final class InvalidTokenException extends Exception {
        private static final long serialVersionUID = 1L;

        public InvalidTokenException(String message, Throwable cause) {
            super(message, cause);
        }
    }

    /**
     * @param issuer Sangam's issuer, for example "https://id.sangamid.in/"
     * @param jwksUri its JWKS address (from discovery: issuer + ".well-known/jwks")
     * @param audience the audience your API expects, or null when its tokens carry none
     */
    public TokenVerifier(String issuer, String jwksUri, String audience) {
        String iss = issuer.endsWith("/") ? issuer : issuer + "/";
        JWKSource<SecurityContext> keys;
        try {
            keys = JWKSourceBuilder.create(URI.create(jwksUri).toURL()).retrying(true).build();
        } catch (MalformedURLException e) {
            throw new IllegalArgumentException("Not a JWKS address: " + jwksUri, e);
        }
        processor.setJWSKeySelector(new JWSVerificationKeySelector<>(Set.of(JWSAlgorithm.RS256, JWSAlgorithm.PS256, JWSAlgorithm.ES256), keys));
        processor.setJWSTypeVerifier((type, context) -> { });
        JWTClaimsSet.Builder exact = new JWTClaimsSet.Builder().issuer(iss);
        DefaultJWTClaimsVerifier<SecurityContext> claims = new DefaultJWTClaimsVerifier<>(audience == null ? null : Set.of(audience), exact.build(), Set.of("exp", "sub"), null);
        claims.setMaxClockSkew(30);
        processor.setJWTClaimsSetVerifier(claims);
    }

    public Verified verify(String token) throws InvalidTokenException {
        try {
            JWTClaimsSet set = processor.process(token, null);
            Map<String, Object> claims = new HashMap<>(set.toJSONObject());
            Object scope = claims.get("scope");
            List<String> scopes = scope instanceof String s ? List.of(s.split(" ")) : List.of();
            return new Verified(SangamUser.fromClaims(claims), claims, scopes);
        } catch (ParseException | BadJOSEException | JOSEException e) {
            throw new InvalidTokenException(e.getMessage(), e);
        }
    }
}
