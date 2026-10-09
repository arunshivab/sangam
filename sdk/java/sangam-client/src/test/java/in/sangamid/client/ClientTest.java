package in.sangamid.client;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.nimbusds.jose.JOSEObjectType;
import com.nimbusds.jose.JWSAlgorithm;
import com.nimbusds.jose.JWSHeader;
import com.nimbusds.jose.crypto.RSASSASigner;
import com.nimbusds.jose.jwk.JWKSet;
import com.nimbusds.jose.jwk.RSAKey;
import com.nimbusds.jose.jwk.gen.RSAKeyGenerator;
import com.nimbusds.jwt.JWTClaimsSet;
import com.nimbusds.jwt.SignedJWT;
import com.sun.net.httpserver.HttpServer;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;

/** The token verifier against a published key set, and the management client against a recording server. */
class ClientTest {
    private static HttpServer serve(Map<String, String> answers, List<String> seen) throws IOException {
        HttpServer server = HttpServer.create(new InetSocketAddress("127.0.0.1", 0), 0);
        server.createContext("/", exchange -> {
            String path = exchange.getRequestURI().getPath();
            seen.add(exchange.getRequestMethod() + " " + path + " " + exchange.getRequestHeaders().getFirst("authorization") + " "
                    + new String(exchange.getRequestBody().readAllBytes(), StandardCharsets.UTF_8));
            String body = answers.getOrDefault(path, "{\"ok\":true}");
            int status = path.endsWith("/forbidden") ? 403 : 200;
            byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
            exchange.getResponseHeaders().add("content-type", "application/json");
            exchange.sendResponseHeaders(status, bytes.length);
            exchange.getResponseBody().write(bytes);
            exchange.close();
        });
        server.start();
        return server;
    }

    @Test
    void accessTokensAreCheckedAgainstThePublishedKeysIssuerAndAudience() throws Exception {
        RSAKey key = new RSAKeyGenerator(2048).keyID("k1").generate();
        HttpServer server = serve(Map.of("/jwks", new JWKSet(key.toPublicJWK()).toString()), new ArrayList<>());
        try {
            String base = "http://127.0.0.1:" + server.getAddress().getPort() + "/";
            TokenVerifier verifier = new TokenVerifier(base, base + "jwks", "his-api");
            List<Map<String, Object>> orgs = List.of(Map.of("id", "0192a6b0-0000-7000-8000-000000000002", "name", "H", "type", "hospital", "path", "/h/", "role", "nurse",
                    "permissions", List.of("vitals:read"), "inherits", true));
            TokenVerifier.Verified ok = verifier.verify(sign(key, base, "his-api", orgs));
            assertEquals("0192a6b0-0000-7000-8000-000000000042", ok.user().id());
            assertTrue(ok.user().hasPermission("/h/ward/", "vitals:read"));
            assertEquals(List.of("openid", "orgs.read"), ok.scopes());
            assertThrows(TokenVerifier.InvalidTokenException.class, () -> verifier.verify(sign(key, "https://evil.example/", "his-api", orgs)));
            assertThrows(TokenVerifier.InvalidTokenException.class, () -> verifier.verify(sign(key, base, "other-api", orgs)));
            RSAKey stranger = new RSAKeyGenerator(2048).keyID("k1").generate();
            assertThrows(TokenVerifier.InvalidTokenException.class, () -> verifier.verify(sign(stranger, base, "his-api", orgs)));
        } finally {
            server.stop(0);
        }
    }

    private static String sign(RSAKey key, String issuer, String audience, Object orgs) throws Exception {
        JWTClaimsSet claims = new JWTClaimsSet.Builder().issuer(issuer).audience(audience).subject("0192a6b0-0000-7000-8000-000000000042")
                .expirationTime(new Date(System.currentTimeMillis() + 300_000)).claim("scope", "openid orgs.read").claim("sangam_orgs", orgs).build();
        SignedJWT jwt = new SignedJWT(new JWSHeader.Builder(JWSAlgorithm.RS256).keyID("k1").type(JOSEObjectType.JWT).build(), claims);
        jwt.sign(new RSASSASigner(key));
        return jwt.serialize();
    }

    @Test
    void theManagementClientCachesItsTokenAndCallsApiV1() throws Exception {
        List<String> seen = new ArrayList<>();
        HttpServer server = serve(Map.of("/connect/token", "{\"access_token\":\"t1\",\"expires_in\":3600}"), seen);
        try {
            ManagementClient client = new ManagementClient("http://127.0.0.1:" + server.getAddress().getPort() + "/", "app", "s", null);
            client.upsertRole("nurse", "Nurse", List.of("vitals:read"));
            client.upsertMembership("o1", "u1", "nurse", true, "2026-12-31T23:59:59+05:30");
            assertEquals(1, seen.stream().filter(s -> s.startsWith("POST /connect/token")).count());
            assertTrue(seen.get(1).startsWith("PUT /api/v1/roles/nurse Bearer t1"));
            assertTrue(seen.get(2).contains("\"expiresAt\":\"2026-12-31T23:59:59+05:30\""));
            ManagementClient.SangamApiException e = assertThrows(ManagementClient.SangamApiException.class, () -> client.send("GET", "forbidden", null));
            assertEquals(403, e.status());
        } finally {
            server.stop(0);
        }
    }

    @Test
    void aWebhookTimestampTooLongForALong_IsRefused_NotThrown() {
        // rc.3 (CodeQL java/uncaught-number-format-exception): a crafted 25-digit timestamp is simply not valid.
        String secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
        assertFalse(WebhookVerifier.verify("msg_1", "1234567890123456789012345", "v1,AAAA", "{}", secret, 1614265330L));
        assertFalse(WebhookVerifier.verify("msg_1", "", "v1,AAAA", "{}", secret, 1614265330L));
    }
}
