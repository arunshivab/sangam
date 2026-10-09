package in.sangamid.client.audit;

import in.sangamid.client.SangamUser;
import java.security.SecureRandom;
import java.time.Instant;
import java.time.ZoneOffset;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.regex.Pattern;

/** Builds and checks events in the shared audit schema 1.0 (SGM-208), with the same rules as every Sangam SDK. */
public final class SangamAudit {
    public static final String SCHEMA_VERSION = "1.0";
    public static final String MASKED = "[masked]";

    private static final SecureRandom RANDOM = new SecureRandom();
    private static final DateTimeFormatter RFC3339 = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'").withZone(ZoneOffset.UTC);
    private static final Set<String> ENVIRONMENTS = Set.of("production", "staging", "development");
    private static final Set<String> ACTORS = Set.of("user", "admin", "api", "system", "anonymous");
    private static final Set<String> CATEGORIES = Set.of("access", "create", "update", "delete", "approve", "sign", "export", "admin", "security");
    private static final Set<String> OUTCOMES = Set.of("success", "failure", "denied");
    private static final Set<String> CLASSES = Set.of("public", "internal", "personal", "sensitive-personal", "health");
    private static final Pattern UUID7 = Pattern.compile("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");
    private static final Pattern UUID = Pattern.compile("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
    private static final Pattern TIME = Pattern.compile("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(\\.\\d+)?(Z|[+-]\\d{2}:\\d{2})$");
    private static final Pattern ACTION = Pattern.compile("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*){2,}$");
    private static final Pattern VERSION = Pattern.compile("^1\\.[0-9]+$");

    private SangamAudit() {
    }

    /** A time-ordered UUID (version 7, RFC 9562). */
    public static String uuid7(long epochMillis) {
        byte[] b = new byte[16];
        RANDOM.nextBytes(b);
        for (int i = 0; i < 6; i++) {
            b[i] = (byte) (epochMillis >>> (8 * (5 - i)));
        }
        b[6] = (byte) ((b[6] & 0x0f) | 0x70);
        b[8] = (byte) ((b[8] & 0x3f) | 0x80);
        StringBuilder hex = new StringBuilder();
        for (byte x : b) {
            hex.append(String.format("%02x", x));
        }
        return hex.substring(0, 8) + "-" + hex.substring(8, 12) + "-" + hex.substring(12, 16) + "-" + hex.substring(16, 20) + "-" + hex.substring(20);
    }

    /** An event: source from config, actor from the user, client from the request; sensitive fields masked. */
    public static Map<String, Object> build(AuditConfig config, SangamUser user, String ip, String userAgent, AuditEntry entry, Instant now) {
        String type = entry.actorType != null ? entry.actorType : user != null && !user.id().isEmpty() ? "user" : "anonymous";
        Map<String, Object> actor = new LinkedHashMap<>();
        actor.put("type", type);
        if (user != null && !user.id().isEmpty() && (type.equals("user") || type.equals("admin"))) {
            actor.put("sub", user.id());
            put(actor, "session_id", user.sessionId());
            put(actor, "acr", user.acr());
            if (!user.amr().isEmpty()) {
                actor.put("amr", user.amr());
            }
        }
        put(actor, "client_id", entry.clientId);
        Map<String, Object> e = new LinkedHashMap<>();
        e.put("schema_version", SCHEMA_VERSION);
        e.put("event_id", uuid7(now.toEpochMilli()));
        e.put("occurred_at", RFC3339.format(now));
        e.put("source", ordered("app_id", config.appId(), "app_version", config.appVersion(), "environment", config.environment()));
        if (entry.orgId != null) {
            Map<String, Object> tenant = ordered("org_id", entry.orgId);
            put(tenant, "org_path", entry.orgPath);
            e.put("tenant", tenant);
        }
        e.put("actor", actor);
        e.put("action", entry.action);
        e.put("category", entry.category);
        Map<String, Object> target = ordered("type", entry.targetType, "id", entry.targetId);
        put(target, "display", entry.targetDisplay);
        e.put("target", target);
        e.put("outcome", entry.outcome);
        put(e, "reason", entry.reason);
        if (entry.signature != null) {
            e.put("signature", ordered("token_id", entry.signature[0], "meaning", entry.signature[1], "record_hash", entry.signature[2]));
        }
        if (!entry.changes.isEmpty()) {
            List<Map<String, Object>> changes = new ArrayList<>();
            for (Map<String, Object> c : entry.changes) {
                boolean masked = entry.sensitive.contains(String.valueOf(c.get("field")));
                Map<String, Object> m = new LinkedHashMap<>();
                m.put("field", c.get("field"));
                m.put("before", masked ? MASKED : c.get("before"));
                m.put("after", masked ? MASKED : c.get("after"));
                changes.add(m);
            }
            e.put("changes", changes);
        }
        if (ip != null && !ip.isEmpty()) {
            Map<String, Object> client = ordered("ip", ip);
            put(client, "user_agent", userAgent);
            e.put("client", client);
        }
        put(e, "correlation_id", entry.correlationId);
        e.put("data_classification", entry.dataClassification);
        return e;
    }

    /** The problems with an event against schema 1.0; empty when it is valid. */
    public static List<String> validate(Object event) {
        if (!(event instanceof Map<?, ?> o)) {
            return List.of("not an object");
        }
        List<String> p = new ArrayList<>();
        if (!matches(VERSION, o.get("schema_version"))) p.add("schema_version must be 1.x");
        if (!matches(UUID7, o.get("event_id"))) p.add("event_id must be a UUID version 7");
        if (!matches(TIME, o.get("occurred_at"))) p.add("occurred_at must be RFC 3339");
        if (!(o.get("source") instanceof Map<?, ?> s) || !filled(s.get("app_id")) || !filled(s.get("app_version")) || !ENVIRONMENTS.contains(s.get("environment"))) {
            p.add("source needs app_id, app_version and an environment");
        }
        if (o.containsKey("tenant")) {
            boolean ok = o.get("tenant") instanceof Map<?, ?> t && matches(UUID, t.get("org_id"))
                    && (!t.containsKey("org_path") || (t.get("org_path") instanceof String path && path.startsWith("/") && path.endsWith("/")));
            if (!ok) p.add("tenant needs a UUID org_id and an org_path that starts and ends with /");
        }
        Object type = null;
        if (!(o.get("actor") instanceof Map<?, ?> a) || !ACTORS.contains(type = a.get("type"))) {
            p.add("actor.type must be user, admin, api, system or anonymous");
        } else {
            boolean person = "user".equals(type) || "admin".equals(type);
            if (person && !filled(a.get("sub"))) p.add("a person's event needs actor.sub");
            if (a.get("sub") instanceof String sub && sub.contains("@")) p.add("actor.sub is the Sangam subject id, never an e-mail address");
            if ("api".equals(type) && !filled(a.get("client_id"))) p.add("an api event needs actor.client_id");
        }
        if (!matches(ACTION, o.get("action"))) p.add("action must be domain.object.verb in lowercase");
        Object category = o.get("category");
        if (!CATEGORIES.contains(category)) p.add("category is not one of the nine");
        if (!(o.get("target") instanceof Map<?, ?> t) || !filled(t.get("type")) || !filled(t.get("id"))) p.add("target needs type and id");
        if (!OUTCOMES.contains(o.get("outcome"))) p.add("outcome must be success, failure or denied");
        if ("sign".equals(category) && (!(o.get("signature") instanceof Map<?, ?> s) || !filled(s.get("token_id")) || !filled(s.get("meaning")) || !filled(s.get("record_hash")))) {
            p.add("a sign event needs signature.token_id, meaning and record_hash");
        }
        if (o.containsKey("changes") && (!(o.get("changes") instanceof List<?> list) || list.stream().anyMatch(c -> !(c instanceof Map<?, ?> m) || !filled(m.get("field"))))) {
            p.add("each change needs its field");
        }
        if (("user".equals(type) || "admin".equals(type)) && (!(o.get("client") instanceof Map<?, ?> c) || !filled(c.get("ip")))) p.add("a person's event needs client.ip");
        if (!CLASSES.contains(o.get("data_classification"))) p.add("data_classification is not one of the five");
        for (String f : List.of("recorded_at", "prev_hash", "hash")) {
            if (o.containsKey(f)) p.add(f + " is set by the audit service, never by the application");
        }
        return p;
    }

    private static boolean matches(Pattern pattern, Object value) {
        return value instanceof String s && pattern.matcher(s).matches();
    }

    private static boolean filled(Object value) {
        return value instanceof String s && !s.isEmpty();
    }

    private static void put(Map<String, Object> m, String key, String value) {
        if (value != null && !value.isEmpty()) {
            m.put(key, value);
        }
    }

    private static Map<String, Object> ordered(Object... pairs) {
        if (pairs.length % 2 != 0) {
            throw new IllegalArgumentException("ordered() takes name and value pairs.");
        }
        Map<String, Object> m = new LinkedHashMap<>();
        for (int i = 0; i + 1 < pairs.length; i += 2) {
            m.put((String) pairs[i], pairs[i + 1]);
        }
        return m;
    }
}
