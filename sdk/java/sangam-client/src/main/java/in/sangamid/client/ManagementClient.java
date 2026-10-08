package in.sangamid.client;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import java.io.IOException;
import java.net.URI;
import java.net.URLEncoder;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.time.Instant;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.stream.Collectors;

/** Sangam's management API for your back end, with client-credentials tokens cached (needs sangam.manage). */
public final class ManagementClient {
    private static final ObjectMapper JSON = new ObjectMapper();
    private final String authority;
    private final String clientId;
    private final String clientSecret;
    private final HttpClient http;
    private final Map<String, Map.Entry<String, Instant>> tokens = new HashMap<>();

    /** Thrown when the API answers with an error. */
    public static final class SangamApiException extends IOException {
        private static final long serialVersionUID = 1L;
        private final int status;

        public SangamApiException(int status, String message) {
            super(message);
            this.status = status;
        }

        public int status() {
            return status;
        }
    }

    public ManagementClient(String authority, String clientId, String clientSecret, HttpClient http) {
        this.authority = authority.replaceAll("/+$", "");
        this.clientId = clientId;
        this.clientSecret = clientSecret;
        this.http = http != null ? http : HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(10)).build();
    }

    /** A client-credentials token for the scope, reused until a minute before it expires. */
    public synchronized String token(String scope) throws IOException, InterruptedException {
        Map.Entry<String, Instant> cached = tokens.get(scope);
        if (cached != null && cached.getValue().isAfter(Instant.now().plusSeconds(60))) {
            return cached.getKey();
        }
        Map<String, String> form = new LinkedHashMap<>();
        form.put("grant_type", "client_credentials");
        form.put("client_id", clientId);
        form.put("client_secret", clientSecret);
        form.put("scope", scope);
        String body = form.entrySet().stream().map(e -> e.getKey() + "=" + URLEncoder.encode(e.getValue(), StandardCharsets.UTF_8)).collect(Collectors.joining("&"));
        HttpResponse<String> r = http.send(HttpRequest.newBuilder(URI.create(authority + "/connect/token")).header("content-type", "application/x-www-form-urlencoded")
                .POST(HttpRequest.BodyPublishers.ofString(body)).build(), HttpResponse.BodyHandlers.ofString());
        if (r.statusCode() >= 300) {
            throw new SangamApiException(r.statusCode(), "Sangam refused a token for " + scope + " (" + r.statusCode() + ")");
        }
        JsonNode json = JSON.readTree(r.body());
        String token = json.get("access_token").asText();
        tokens.put(scope, Map.entry(token, Instant.now().plusSeconds(json.path("expires_in").asLong(300))));
        return token;
    }

    /** Any call; {@code path} is relative to /api/v1/. */
    public JsonNode send(String method, String path, Object body) throws IOException, InterruptedException {
        HttpRequest.Builder b = HttpRequest.newBuilder(URI.create(authority + "/api/v1/" + path.replaceAll("^/+", ""))).header("authorization", "Bearer " + token("sangam.manage"));
        if (body != null) {
            b.header("content-type", "application/json").method(method, HttpRequest.BodyPublishers.ofString(JSON.writeValueAsString(body)));
        } else {
            b.method(method, HttpRequest.BodyPublishers.noBody());
        }
        HttpResponse<String> r = http.send(b.build(), HttpResponse.BodyHandlers.ofString());
        if (r.statusCode() >= 300) {
            throw new SangamApiException(r.statusCode(), "Sangam's management API answered " + r.statusCode() + " to " + method + " " + path + ": " + r.body());
        }
        return r.body().isEmpty() ? null : JSON.readTree(r.body());
    }

    public JsonNode upsertRole(String code, String displayName, List<String> permissions) throws IOException, InterruptedException {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("displayName", displayName);
        body.put("description", null);
        body.put("permissions", permissions);
        body.put("orgId", null);
        return send("PUT", "roles/" + URLEncoder.encode(code, StandardCharsets.UTF_8), body);
    }

    public JsonNode upsertOrganisation(String id, String name, String type, String parentId) throws IOException, InterruptedException {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("name", name);
        body.put("type", type);
        body.put("parentId", parentId);
        body.put("metadata", null);
        return send("PUT", "orgs/" + id, body);
    }

    /** Gives a person a role at an organisation; {@code expiresAt} (ISO 8601) makes it time-limited. */
    public JsonNode upsertMembership(String orgId, String userId, String role, boolean appliesToDescendants, String expiresAt) throws IOException, InterruptedException {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("role", role);
        body.put("appliesToDescendants", appliesToDescendants);
        if (expiresAt != null) {
            body.put("expiresAt", expiresAt);
        }
        return send("PUT", "orgs/" + orgId + "/members/" + userId, body);
    }

    public JsonNode getAttributes(String userId) throws IOException, InterruptedException {
        return send("GET", "users/" + userId + "/attributes", null);
    }

    public JsonNode setAttributes(String userId, Map<String, String> values) throws IOException, InterruptedException {
        return send("PUT", "users/" + userId + "/attributes", values);
    }
}
