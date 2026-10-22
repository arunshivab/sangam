package in.sangamid.spring;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import com.nimbusds.jose.crypto.RSASSAVerifier;
import com.nimbusds.jose.jwk.RSAKey;
import com.nimbusds.jose.jwk.gen.RSAKeyGenerator;
import com.nimbusds.jwt.SignedJWT;
import com.sun.net.httpserver.HttpServer;
import in.sangamid.client.ClientKey;
import java.net.InetSocketAddress;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Test;
import org.springframework.security.oauth2.client.endpoint.OAuth2AuthorizationCodeGrantRequest;
import org.springframework.security.oauth2.client.registration.ClientRegistration;
import org.springframework.security.oauth2.core.AuthorizationGrantType;
import org.springframework.security.oauth2.core.ClientAuthenticationMethod;
import org.springframework.security.oauth2.core.endpoint.OAuth2AuthorizationExchange;
import org.springframework.security.oauth2.core.endpoint.OAuth2AuthorizationRequest;
import org.springframework.security.oauth2.core.endpoint.OAuth2AuthorizationResponse;

/** rc.15: Spring Security redeems the code with an assertion Sangam accepts, and no secret. */
class SangamClientKeysTest {

    @Test
    void theCodeIsRedeemedWithATypedAssertion_ForSangamsIssuer() throws Exception {
        RSAKey generated = new RSAKeyGenerator(2048).generate();
        SangamProperties properties = new SangamProperties();
        properties.setPrivateKey(generated.toJSONString());
        ClientKey key = SangamClientKeys.from(properties);
        assertThat(key.keyId()).isEqualTo(generated.computeThumbprint().toString());

        List<String> bodies = new ArrayList<>();
        HttpServer server = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        server.createContext("/connect/token", exchange -> {
            bodies.add(new String(exchange.getRequestBody().readAllBytes(), StandardCharsets.UTF_8));
            byte[] answer = "{\"access_token\":\"at\",\"token_type\":\"Bearer\",\"expires_in\":900}".getBytes(StandardCharsets.UTF_8);
            exchange.getResponseHeaders().add("content-type", "application/json");
            exchange.sendResponseHeaders(200, answer.length);
            exchange.getResponseBody().write(answer);
            exchange.close();
        });
        server.start();
        try {
            String base = "http://127.0.0.1:" + server.getAddress().getPort();
            ClientRegistration registration = ClientRegistration.withRegistrationId("sangam").clientId("lims")
                    .clientAuthenticationMethod(ClientAuthenticationMethod.PRIVATE_KEY_JWT)
                    .authorizationGrantType(AuthorizationGrantType.AUTHORIZATION_CODE).redirectUri("https://lims.example.in/auth/callback").scope("openid")
                    .authorizationUri(base + "/connect/authorize").tokenUri(base + "/connect/token").jwkSetUri(base + "/jwks").issuerUri(base + "/")
                    .build();
            OAuth2AuthorizationRequest request = OAuth2AuthorizationRequest.authorizationCode().clientId("lims").authorizationUri(base + "/connect/authorize")
                    .redirectUri("https://lims.example.in/auth/callback").state("s1").build();
            OAuth2AuthorizationResponse response = OAuth2AuthorizationResponse.success("code-1").redirectUri("https://lims.example.in/auth/callback").state("s1").build();
            SangamClientKeys.tokenResponseClient(key).getTokenResponse(new OAuth2AuthorizationCodeGrantRequest(registration, new OAuth2AuthorizationExchange(request, response)));

            String body = bodies.get(0);
            assertThat(body).contains("client_assertion_type=urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer").doesNotContain("client_secret");
            String assertion = URLDecoder.decode(body.replaceAll("^.*client_assertion=([^&]+).*$", "$1"), StandardCharsets.UTF_8);
            SignedJWT jwt = SignedJWT.parse(assertion);
            assertThat(jwt.verify(new RSASSAVerifier(generated.toPublicJWK()))).isTrue();
            assertThat(jwt.getHeader().getType().getType()).isEqualTo("client-authentication+jwt");
            assertThat(jwt.getHeader().getKeyID()).isEqualTo(key.keyId());
            assertThat(jwt.getJWTClaimsSet().getAudience()).containsExactly(base + "/");
            assertThat(jwt.getJWTClaimsSet().getIssuer()).isEqualTo("lims");
        } finally {
            server.stop(0);
        }
    }

    @Test
    void noKey_MeansASecret_AndAPublicKeyIsRefused() throws Exception {
        assertThat(SangamClientKeys.from(new SangamProperties())).isNull();
        SangamProperties properties = new SangamProperties();
        properties.setPrivateKey(new RSAKeyGenerator(2048).generate().toPublicJWK().toJSONString());
        assertThatThrownBy(() -> SangamClientKeys.from(properties)).isInstanceOf(IllegalArgumentException.class);
    }
}
