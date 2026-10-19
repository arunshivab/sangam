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
        return send(method, path, body, null);
    }

    /**
     * Any call, for one of your admins (rc.12): {@code adminAccessToken} is their own access token, sent as
     * {@code Sangam-Acting-Admin}; {@code null} for an ordinary call.
     */
    public JsonNode send(String method, String path, Object body, String adminAccessToken) throws IOException, InterruptedException {
        HttpRequest.Builder b = HttpRequest.newBuilder(URI.create(authority + "/api/v1/" + path.replaceAll("^/+", ""))).header("authorization", "Bearer " + token("sangam.manage"));
        if (adminAccessToken != null && !adminAccessToken.isEmpty()) {
            b.header("sangam-acting-admin", adminAccessToken);
        }
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

    /**
     * rc.12 (T1): locks the person's Sangam session with their own access token (from their sign-in, not client
     * credentials), when your idle timer runs out. Sangam tells the person's other applications in that session
     * ({@code session.locked}); on an enrolled shared ward device they come back with their PIN, anywhere else with a full
     * sign-in. Returns {@code false} when that sign-in had already ended.
     */
    public boolean lockSession(String personAccessToken) throws IOException, InterruptedException {
        HttpRequest request = HttpRequest.newBuilder(URI.create(authority + "/api/v1/sessions/lock"))
                .header("authorization", "Bearer " + personAccessToken)
                .POST(HttpRequest.BodyPublishers.noBody())
                .build();
        HttpResponse<String> r = http.send(request, HttpResponse.BodyHandlers.ofString());
        if (r.statusCode() == 404) {
            return false;
        }
        if (r.statusCode() >= 300) {
            throw new SangamApiException(r.statusCode(), "Sangam answered " + r.statusCode() + " to the lock: " + r.body());
        }
        return true;
    }

    /**
     * rc.12 (M12): ends the person's emergency access early, with a reason, with their own access token. Returns
     * {@code false} when none was in progress; tokens refreshed afterwards no longer carry it.
     */
    public boolean endEmergency(String personAccessToken, String reason) throws IOException, InterruptedException {
        if (reason == null || reason.isBlank()) {
            throw new IllegalArgumentException("Say why emergency access ends.");
        }
        HttpRequest request = HttpRequest.newBuilder(URI.create(authority + "/api/v1/sessions/emergency/end"))
                .header("authorization", "Bearer " + personAccessToken)
                .header("content-type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(JSON.writeValueAsString(Map.of("reason", reason))))
                .build();
        HttpResponse<String> r = http.send(request, HttpResponse.BodyHandlers.ofString());
        if (r.statusCode() == 404) {
            return false;
        }
        if (r.statusCode() >= 300) {
            throw new SangamApiException(r.statusCode(), "Sangam answered " + r.statusCode() + " to ending emergency access: " + r.body());
        }
        return true;
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

    /**
     * Gives a person a role at an organisation; {@code expiresAt} (ISO 8601) makes it time-limited. rc.5: only someone who
     * already uses your application; anyone else answers 404 — add them with {@link #addStaff} instead. rc.12: a role holds
     * only where it is given, so {@code appliesToDescendants} must be false. rc.13: for one of your admins (an admin of
     * that organisation): {@code adminAccessToken} is the token Sangam gave your application when they signed in.
     */
    public JsonNode upsertMembership(String orgId, String userId, String adminAccessToken, String role, boolean appliesToDescendants, String expiresAt) throws IOException, InterruptedException {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("role", role);
        body.put("appliesToDescendants", appliesToDescendants);
        if (expiresAt != null) {
            body.put("expiresAt", expiresAt);
        }
        return send("PUT", "orgs/" + orgId + "/members/" + userId, body, adminAccessToken);
    }

    /** rc.12: every role a person holds at an organisation; an empty list keeps them there as staff. rc.13: for one of your admins. */
    public JsonNode setRoles(String orgId, String userId, String adminAccessToken, List<String> roles, String expiresAt) throws IOException, InterruptedException {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("roles", roles);
        if (expiresAt != null) {
            body.put("expiresAt", expiresAt);
        }
        return send("PUT", "orgs/" + orgId + "/members/" + userId, body, adminAccessToken);
    }

    /** rc.13: ends a person's place at an organisation now, for one of your admins. */
    public JsonNode removeMember(String orgId, String userId, String adminAccessToken) throws IOException, InterruptedException {
        return send("DELETE", "orgs/" + orgId + "/members/" + userId, null, adminAccessToken);
    }

    /**
     * rc.12: the check before adding a person (which details match an existing account, never what Sangam holds), for one
     * of your admins. {@code person} has firstName, lastName, email, mobile (E.164 or null) and dateOfBirth (yyyy-mm-dd).
     */
    public JsonNode checkStaff(String orgId, String adminAccessToken, Map<String, Object> person) throws IOException, InterruptedException {
        return send("POST", "orgs/" + orgId + "/staff/check", person, adminAccessToken);
    }

    /**
     * rc.12: adds one person to one organisation for one of your admins (verified with DigiLocker). {@code addition} has
     * exactly one of person, joinCode, staffRef or userId, and optionally roles, startsOn, expiresAt and confirmed.
     */
    public JsonNode addStaff(String orgId, String adminAccessToken, Map<String, Object> addition) throws IOException, InterruptedException {
        return send("POST", "orgs/" + orgId + "/staff", addition, adminAccessToken);
    }

    /**
     * Where a person's account went if they merged it into another of theirs (rc.7): the surviving account's
     * {@code sub}, or {@code null} when the id was not merged or your application never knew it. Use it on
     * {@code account.merged}.
     */
    public String mergedInto(String userId) throws IOException, InterruptedException {
        try {
            JsonNode answer = send("GET", "users/" + userId + "/merged", null);
            return answer != null && answer.hasNonNull("merged_into") ? answer.get("merged_into").asText() : null;
        } catch (SangamApiException e) {
            if (e.status() == 404) {
                return null;
            }
            throw e;
        }
    }

    public JsonNode getAttributes(String userId) throws IOException, InterruptedException {
        return send("GET", "users/" + userId + "/attributes", null);
    }

    /**
     * Sets values your application keeps about a person; an empty value clears one. rc.14: done for one of your admins,
     * who administers your application or an organisation where the person works in it.
     */
    public JsonNode setAttributes(String userId, String adminAccessToken, Map<String, String> values) throws IOException, InterruptedException {
        return send("PUT", "users/" + userId + "/attributes", values, adminAccessToken);
    }
}
