package in.sangamid.client;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
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
            String admin = exchange.getRequestHeaders().getFirst("sangam-acting-admin");
            seen.add(exchange.getRequestMethod() + " " + path + " " + exchange.getRequestHeaders().getFirst("authorization") + " "
                    + new String(exchange.getRequestBody().readAllBytes(), StandardCharsets.UTF_8) + (admin == null ? "" : " admin=" + admin));
            String body = answers.getOrDefault(path, "{\"ok\":true}");
            int status = path.endsWith("/forbidden") ? 403 : path.endsWith("/missing/merged") ? 404 : 200;
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
            client.upsertMembership("o1", "u1", "admin-token", "nurse", true, "2026-12-31T23:59:59+05:30");
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
    void changingAPlaceSendsTheAdminsOwnToken() throws Exception {
        // rc.13: as adding staff does; rc.14: setting a person's values too.
        List<String> seen = new ArrayList<>();
        HttpServer server = serve(Map.of("/connect/token", "{\"access_token\":\"t1\",\"expires_in\":3600}"), seen);
        try {
            ManagementClient client = new ManagementClient("http://127.0.0.1:" + server.getAddress().getPort() + "/", "app", "s", null);
            client.setRoles("o1", "u1", "admin-token", List.of("doctor"), null);
            client.removeMember("o1", "u1", "admin-token");
            client.setAttributes("u1", "admin-token", Map.of("badge", "S-77"));
            assertTrue(seen.get(1).startsWith("PUT /api/v1/orgs/o1/members/u1 Bearer t1"));
            assertTrue(seen.get(1).endsWith(" admin=admin-token"));
            assertTrue(seen.get(2).startsWith("DELETE /api/v1/orgs/o1/members/u1 Bearer t1"));
            assertTrue(seen.get(2).endsWith(" admin=admin-token"));
            assertTrue(seen.get(3).startsWith("PUT /api/v1/users/u1/attributes Bearer t1"));
            assertTrue(seen.get(3).endsWith(" admin=admin-token"));
        } finally {
            server.stop(0);
        }
    }

    @Test
    void lockingSendsThePersonsOwnToken() throws Exception {
        // rc.12 (T1): the idle timer ran out; the person's own token locks their Sangam session.
        List<String> seen = new ArrayList<>();
        HttpServer server = serve(Map.of(), seen);
        try {
            ManagementClient client = new ManagementClient("http://127.0.0.1:" + server.getAddress().getPort() + "/", "app", "s", null);
            assertTrue(client.lockSession("person-token"));
            assertEquals("POST /api/v1/sessions/lock Bearer person-token ", seen.get(0));
        } finally {
            server.stop(0);
        }
    }

    @Test
    void staffAreAddedForAnAdmin_WithTheirOwnToken() throws Exception {
        // rc.12: people who do not yet use the application are added as staff, for one of its admins.
        List<String> seen = new ArrayList<>();
        HttpServer server = serve(Map.of("/connect/token", "{\"access_token\":\"t1\",\"expires_in\":3600}"), seen);
        try {
            ManagementClient client = new ManagementClient("http://127.0.0.1:" + server.getAddress().getPort() + "/", "app", "s", null);
            Map<String, Object> addition = new java.util.LinkedHashMap<>();
            addition.put("joinCode", "K7Q2-M9XR");
            addition.put("roles", List.of("nurse"));
            client.addStaff("o1", "admin-token", addition);
            assertEquals("POST /api/v1/orgs/o1/staff Bearer t1 {\"joinCode\":\"K7Q2-M9XR\",\"roles\":[\"nurse\"]} admin=admin-token", seen.get(1));
        } finally {
            server.stop(0);
        }
    }

    @Test
    void theOrganisationChosenForThisSignIn_IsRead() {
        // rc.12 (A6).
        SangamUser.CurrentOrganisation org = SangamUser.currentOrganisation(Map.of("sangam_org",
                "{\"id\":\"0192a6b0-0000-7000-8000-000000000003\",\"name\":\"Apulki Pune\",\"type\":\"hospital\",\"path\":\"/g/p/\",\"roles\":[\"doctor\",\"administrator\"]}"));
        assertEquals("Apulki Pune", org.organisationName());
        assertEquals(List.of("doctor", "administrator"), org.roles());
        assertNull(SangamUser.currentOrganisation(Map.of("sangam_org", "not json")));
    }

    @Test
    void emergencyAccessIsRead_UntilItsEnd() {
        // rc.12 (M12).
        SangamUser.Emergency e = SangamUser.emergency(Map.of("sangam_emergency",
                "{\"id\":\"0192a6b0-0000-7000-8000-0000000000e1\",\"case\":\"LIPI-48213\",\"org\":\"0192a6b0-0000-7000-8000-000000000003\",\"until\":1792000000}"));
        assertEquals("LIPI-48213", e.caseReference());
        assertTrue(e.isActive(1791999999L));
        assertFalse(e.isActive(1792000000L));
        assertNull(SangamUser.emergency(Map.of("sangam_emergency", "{\"case\":\"x\"}")));
    }

    @Test
    void endingEmergencyAccessSendsThePersonsOwnTokenAndTheReason() throws Exception {
        List<String> seen = new ArrayList<>();
        HttpServer server = serve(Map.of(), seen);
        try {
            ManagementClient client = new ManagementClient("http://127.0.0.1:" + server.getAddress().getPort() + "/", "app", "s", null);
            assertTrue(client.endEmergency("person-token", "Care handed over"));
            assertEquals("POST /api/v1/sessions/emergency/end Bearer person-token {\"reason\":\"Care handed over\"}", seen.get(0));
        } finally {
            server.stop(0);
        }
    }

    @Test
    void mergedIntoGivesTheSurvivingId_AndNullForAnIdThatWasNotMerged() throws Exception {
        // rc.7: a 404 is an answer ("not merged, or not yours"), not an error.
        HttpServer server = serve(Map.of("/connect/token", "{\"access_token\":\"t1\",\"expires_in\":3600}",
                "/api/v1/users/u-old/merged", "{\"merged_into\":\"u-new\",\"merged_at\":\"2026-10-12T05:00:00Z\"}"), new ArrayList<>());
        try {
            ManagementClient client = new ManagementClient("http://127.0.0.1:" + server.getAddress().getPort() + "/", "app", "s", null);
            assertEquals("u-new", client.mergedInto("u-old"));
            assertNull(client.mergedInto("missing"));
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
