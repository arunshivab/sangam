package in.sangamid.client;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.fasterxml.jackson.core.type.TypeReference;
import com.fasterxml.jackson.databind.ObjectMapper;
import in.sangamid.client.audit.AuditConfig;
import in.sangamid.client.audit.AuditEntry;
import in.sangamid.client.audit.SangamAudit;
import java.io.IOException;
import java.nio.file.Path;
import java.time.Instant;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.stream.Stream;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

/** The shared conformance vectors every Sangam SDK runs (sdk/conformance/vectors.json). */
class ConformanceTest {
    private static final ObjectMapper JSON = new ObjectMapper();
    private static final Map<String, Object> V = load();
    private static final SangamUser USER = new SangamUser("x", "Test", null, SangamUser.parseMemberships(section("permissions").get("sangam_orgs")), null, List.of(), null, null, false);

    @SuppressWarnings("unchecked")
    private static Map<String, Object> section(String name) {
        return (Map<String, Object>) V.get(name);
    }

    @SuppressWarnings("unchecked")
    private static List<Map<String, Object>> list(Map<String, Object> m, String name) {
        return (List<Map<String, Object>>) m.get(name);
    }

    private static Map<String, Object> load() {
        try {
            return JSON.readValue(Path.of("..", "..", "conformance", "vectors.json").toFile(), new TypeReference<Map<String, Object>>() { });
        } catch (IOException e) {
            throw new IllegalStateException(e);
        }
    }

    static Stream<Map<String, Object>> permissions() { return list(section("permissions"), "has_permission").stream(); }
    static Stream<Map<String, Object>> stepUp() { return list(section("step_up"), "cases").stream(); }
    static Stream<Map<String, Object>> webhooks() { return list(section("webhooks"), "cases").stream(); }
    static Stream<Map<String, Object>> audit() { return list(section("audit"), "validation").stream(); }

    @ParameterizedTest
    @MethodSource("permissions")
    void permission(Map<String, Object> c) {
        assertEquals(c.get("expect"), USER.hasPermission((String) c.get("org_path"), (String) c.get("permission")), (String) c.get("why"));
    }

    @Test
    void rolesAndMalformedClaims() {
        for (Map<String, Object> c : list(section("permissions"), "has_role")) {
            assertEquals(c.get("expect"), USER.hasRole((String) c.get("org_path"), (String) c.get("role")));
        }
        for (Map<String, Object> c : list(section("permissions"), "roles_in")) {
            assertEquals(c.get("expect"), USER.rolesIn((String) c.get("org_path")));
        }
        for (Map<String, Object> c : list(section("permissions"), "malformed_claims")) {
            assertEquals(((Number) c.get("memberships")).intValue(), SangamUser.parseMemberships(c.get("claim")).size());
        }
    }

    @ParameterizedTest
    @MethodSource("stepUp")
    void stepUp(Map<String, Object> c) {
        long now = ((Number) section("step_up").get("now")).longValue();
        Long authTime = c.get("auth_time") == null ? null : ((Number) c.get("auth_time")).longValue();
        Long maxAge = c.get("max_age") == null ? null : ((Number) c.get("max_age")).longValue();
        assertEquals(c.get("expect"), StepUp.satisfies((String) c.get("acr"), authTime, (String) c.get("level"), maxAge, now), (String) c.get("why"));
    }

    @Test
    @SuppressWarnings("unchecked")
    void challenge() {
        for (Map<String, Object> c : (List<Map<String, Object>>) V.get("challenge")) {
            Long maxAge = c.get("max_age") == null ? null : ((Number) c.get("max_age")).longValue();
            assertEquals(c.get("header"), StepUp.challenge((String) c.get("level"), maxAge));
        }
    }

    @ParameterizedTest
    @MethodSource("webhooks")
    void webhook(Map<String, Object> c) {
        assertEquals(c.get("expect"), WebhookVerifier.verify((String) c.get("id"), (String) c.get("timestamp"), (String) c.get("signature"), (String) c.get("body"),
                (String) c.get("secret"), ((Number) c.get("now")).longValue()), (String) c.get("why"));
    }

    @ParameterizedTest
    @MethodSource("audit")
    void auditValidation(Map<String, Object> c) {
        List<String> problems = SangamAudit.validate(c.get("event"));
        assertEquals(c.get("valid"), problems.isEmpty(), c.get("why") + ": " + problems);
    }

    @Test
    @SuppressWarnings("unchecked")
    void auditBuilder() {
        Map<String, Object> b = (Map<String, Object>) section("audit").get("builder");
        Map<String, Object> config = (Map<String, Object>) b.get("config");
        Map<String, Object> u = (Map<String, Object>) b.get("user");
        Map<String, Object> request = (Map<String, Object>) b.get("request");
        Map<String, Object> i = (Map<String, Object>) b.get("input");
        Map<String, Object> target = (Map<String, Object>) i.get("target");
        Map<String, Object> tenant = (Map<String, Object>) i.get("tenant");
        SangamUser user = new SangamUser((String) u.get("sub"), "", null, List.of(), (String) u.get("acr"), (List<String>) u.get("amr"), null, (String) u.get("sid"), false);
        AuditEntry entry = AuditEntry.of((String) i.get("action"), (String) i.get("category"), (String) target.get("type"), (String) target.get("id"))
                .display((String) target.get("display")).outcome((String) i.get("outcome")).tenant((String) tenant.get("org_id"), (String) tenant.get("org_path"))
                .classification((String) i.get("data_classification")).sensitive(new HashSet<>((List<String>) i.get("sensitive")));
        for (Map<String, Object> ch : (List<Map<String, Object>>) i.get("changes")) {
            entry.change((String) ch.get("field"), ch.get("before"), ch.get("after"));
        }
        Map<String, Object> e = SangamAudit.build(new AuditConfig((String) config.get("app_id"), (String) config.get("app_version"), (String) config.get("environment")),
                user, (String) request.get("ip"), (String) request.get("user_agent"), entry, Instant.now());
        assertEquals(List.of(), SangamAudit.validate(e));
        for (Map.Entry<String, Object> x : ((Map<String, Object>) b.get("expect")).entrySet()) {
            assertEquals(JSON.valueToTree(x.getValue()), JSON.valueToTree(e.get(x.getKey())), x.getKey());
        }
        for (Object k : (List<Object>) b.get("absent")) {
            assertFalse(e.containsKey(k));
        }
    }

    @Test
    void uuid7IsTimeOrdered() {
        String a = SangamAudit.uuid7(1_700_000_000_000L);
        String b = SangamAudit.uuid7(1_700_000_000_001L);
        assertTrue(a.compareTo(b) < 0);
        assertEquals('7', a.charAt(14));
    }
}
