package in.sangamid.spring;

import in.sangamid.client.ClientKey;
import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import org.springframework.security.oauth2.client.endpoint.NimbusJwtClientAuthenticationParametersConverter;
import org.springframework.security.oauth2.client.endpoint.OAuth2AuthorizationCodeGrantRequest;
import org.springframework.security.oauth2.client.endpoint.RestClientAuthorizationCodeTokenResponseClient;
import org.springframework.security.oauth2.client.registration.ClientRegistration;
import org.springframework.security.oauth2.core.ClientAuthenticationMethod;
import org.springframework.security.oauth2.jose.jws.SignatureAlgorithm;

/**
 * rc.15: Sign in with Sangam using a key instead of a client secret ({@code private_key_jwt}). Set
 * {@code sangam.private-key} (a PKCS#8 RSA PEM, or a private JSON Web Key) or {@code sangam.private-key-path}, and the
 * registration's {@code client-authentication-method=private_key_jwt} with no secret; {@link SangamSecurity#apply} then
 * redeems codes with an assertion Sangam accepts: typed {@code client-authentication+jwt}, its audience Sangam's issuer.
 */
public final class SangamClientKeys {
    private SangamClientKeys() {
    }

    /** The key in {@code properties}, or {@code null} when the application signs in with a secret. */
    public static ClientKey from(SangamProperties properties) {
        String text = properties.getPrivateKey();
        if ((text == null || text.isBlank()) && properties.getPrivateKeyPath() != null && !properties.getPrivateKeyPath().isBlank()) {
            try {
                text = Files.readString(Path.of(properties.getPrivateKeyPath()), StandardCharsets.UTF_8);
            } catch (IOException e) {
                throw new UncheckedIOException("Sangam: cannot read sangam.private-key-path " + properties.getPrivateKeyPath(), e);
            }
        }
        if (text == null || text.isBlank()) {
            return null;
        }
        return text.trim().startsWith("{") ? ClientKey.fromJwk(text) : ClientKey.fromPem(text);
    }

    /** A token client that redeems authorization codes with an assertion signed by {@code key}. */
    public static RestClientAuthorizationCodeTokenResponseClient tokenResponseClient(ClientKey key) {
        NimbusJwtClientAuthenticationParametersConverter<OAuth2AuthorizationCodeGrantRequest> assertions =
                new NimbusJwtClientAuthenticationParametersConverter<>(registration -> {
                    if (!ClientAuthenticationMethod.PRIVATE_KEY_JWT.equals(registration.getClientAuthenticationMethod())) {
                        throw new IllegalStateException("Sangam: set the registration's client-authentication-method to private_key_jwt when sangam.private-key is set.");
                    }
                    return key.jwk();
                });
        assertions.setJwtClientAssertionCustomizer(context -> {
            ClientRegistration registration = context.getAuthorizationGrantRequest().getClientRegistration();
            String issuer = registration.getProviderDetails().getIssuerUri();
            context.getHeaders().type(ClientKey.TOKEN_TYPE);
            context.getHeaders().algorithm(algorithm(key));
            context.getClaims().audience(List.of(issuer != null ? issuer : authorityOf(registration.getProviderDetails().getTokenUri())));
        });
        RestClientAuthorizationCodeTokenResponseClient client = new RestClientAuthorizationCodeTokenResponseClient();
        client.addParametersConverter(assertions);
        return client;
    }

    private static SignatureAlgorithm algorithm(ClientKey key) {
        String alg = switch (key.jwk().getKeyType().getValue()) {
            case "EC" -> switch (((com.nimbusds.jose.jwk.ECKey) key.jwk()).getCurve().getName()) {
                case "P-384" -> "ES384";
                case "P-521" -> "ES512";
                default -> "ES256";
            };
            default -> "RS256";
        };
        return SignatureAlgorithm.from(alg);
    }

    /** Sangam's issuer from its token endpoint, when the registration names no issuer. */
    private static String authorityOf(String tokenUri) {
        int at = tokenUri.indexOf("/connect/token");
        return at > 0 ? tokenUri.substring(0, at) + "/" : tokenUri;
    }
}
