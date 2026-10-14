package in.sangamid.client;

import com.fasterxml.jackson.core.JsonProcessingException;
import com.fasterxml.jackson.databind.ObjectMapper;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Collection;
import java.util.Date;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.TreeSet;
import java.util.regex.Pattern;

/**
 * The signed-in person, from Sangam's token claims. Identify people by {@link #id()} (the Sangam subject id) only.
 *
 * @param id the Sangam subject id ({@code sub})
 * @param name the display name
 * @param email the e-mail address, when the token carries it
 * @param memberships roles per organisation
 * @param acr how strongly they authenticated
 * @param amr how they authenticated
 * @param authTime when they authenticated (epoch seconds), when known
 * @param sessionId the Sangam session ({@code sid})
 * @param identityVerified whether name, date of birth and gender were verified with DigiLocker
 */
public record SangamUser(String id, String name, String email, List<SangamMembership> memberships, String acr, List<String> amr,
                         Long authTime, String sessionId, boolean identityVerified) {

    private static final Pattern UUID = Pattern.compile("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");
    private static final ObjectMapper JSON = new ObjectMapper();

    public SangamUser {
        memberships = List.copyOf(memberships);
        amr = amr == null ? List.of() : List.copyOf(amr);
    }

    /** The one permission rule: granted at the organisation, or at an ancestor whose membership inherits. Deny by default. */
    public boolean hasPermission(String organisationPath, String permission) {
        return memberships.stream().anyMatch(m -> m.covers(organisationPath) && m.permissions().contains(permission));
    }

    /** Whether the person holds the role at the organisation, directly or inherited. */
    public boolean hasRole(String organisationPath, String role) {
        return !role.isEmpty() && memberships.stream().anyMatch(m -> m.role().equals(role) && m.covers(organisationPath));
    }

    /** The roles held at an organisation, sorted. */
    public List<String> rolesIn(String organisationPath) {
        TreeSet<String> roles = new TreeSet<>();
        memberships.stream().filter(m -> !m.role().isEmpty() && m.covers(organisationPath)).forEach(m -> roles.add(m.role()));
        return List.copyOf(roles);
    }

    /** The person from token claims (an ID token, an access token or userinfo). */
    public static SangamUser fromClaims(Map<String, ?> claims) {
        Object email = claims.get("email");
        Object name = claims.get("name");
        Object amr = claims.get("amr");
        List<String> methods = new ArrayList<>();
        if (amr instanceof Collection<?> list) {
            list.stream().filter(String.class::isInstance).map(String.class::cast).forEach(methods::add);
        }
        return new SangamUser(
                String.valueOf(claims.get("sub") == null ? "" : claims.get("sub")),
                name instanceof String s ? s : email instanceof String e ? e : "",
                email instanceof String e ? e : null,
                parseMemberships(claims.get("sangam_orgs")),
                claims.get("acr") instanceof String a ? a : null,
                methods,
                epochSeconds(claims.get("auth_time")),
                claims.get("sid") instanceof String s ? s : null,
                Boolean.TRUE.equals(claims.get("sangam_identity_verified")));
    }

    private static Long epochSeconds(Object value) {
        if (value instanceof Number n) {
            return n.longValue();
        }
        if (value instanceof Instant i) {
            return i.getEpochSecond();
        }
        if (value instanceof Date d) {
            return d.toInstant().getEpochSecond();
        }
        return null;
    }

    /** Reads {@code sangam_orgs}: a JSON string, a list or one object. Anything malformed is skipped, never fatal. */
    public static List<SangamMembership> parseMemberships(Object claim) {
        Object value = claim;
        if (claim instanceof String text) {
            try {
                value = JSON.readValue(text, Object.class);
            } catch (JsonProcessingException e) {
                return List.of();
            }
        }
        List<?> items = value instanceof List<?> list ? list : value == null ? List.of() : List.of(value);
        List<SangamMembership> result = new ArrayList<>();
        for (Object item : items) {
            if (!(item instanceof Map<?, ?> o)) {
                continue;
            }
            if (!(o.get("id") instanceof String id) || !UUID.matcher(id).matches() || !(o.get("path") instanceof String path) || path.isEmpty()) {
                continue;
            }
            // rc.12: one entry carries every role held there ("roles"; older tokens carry only "role"). Each role is one
            // membership; a place with no role in Sangam (the application keeps roles itself) is one with an empty role.
            LinkedHashSet<String> roles = new LinkedHashSet<>(strings(o.get("roles")));
            if (roles.isEmpty() && o.get("role") instanceof String single && !single.isEmpty()) {
                roles.add(single);
            }
            if (roles.isEmpty()) {
                roles.add("");
            }
            List<String> permissions = strings(o.get("permissions"));
            for (String role : roles) {
                result.add(new SangamMembership(id, o.get("name") instanceof String n ? n : "", o.get("type") instanceof String t ? t : "", path, role, permissions,
                        Boolean.TRUE.equals(o.get("inherits"))));
            }
        }
        return result;
    }

    /** rc.12 (A6): the organisation the person chose to work at for this sign-in. */
    public record CurrentOrganisation(String organisationId, String organisationName, String organisationType, String path,
                                      List<String> roles, List<String> permissions) {
    }

    /**
     * rc.12 (A6): reads {@code sangam_org} (a JSON string or one object) from token claims; {@code null} when absent or
     * malformed. Send the person back with {@code prompt=select_org} to choose again.
     */
    public static CurrentOrganisation currentOrganisation(Map<String, ?> claims) {
        Object value = claims.get("sangam_org");
        if (value instanceof String text) {
            try {
                value = JSON.readValue(text, Object.class);
            } catch (JsonProcessingException e) {
                return null;
            }
        }
        if (!(value instanceof Map<?, ?> o) || !(o.get("id") instanceof String id) || !UUID.matcher(id).matches()
                || !(o.get("path") instanceof String path) || path.isEmpty()) {
            return null;
        }
        List<String> roles = strings(o.get("roles"));
        if (roles.isEmpty() && o.get("role") instanceof String single && !single.isEmpty()) {
            roles.add(single);
        }
        return new CurrentOrganisation(id, o.get("name") instanceof String n ? n : "", o.get("type") instanceof String t ? t : "", path,
                List.copyOf(roles), List.copyOf(strings(o.get("permissions"))));
    }

    /** rc.12 (M12): emergency access in progress. Show a banner, apply your own limits, stop at {@code until} (Unix seconds). */
    public record Emergency(String id, String caseReference, String organisationId, long until) {
        /** Whether it is still in force at {@code nowSeconds}. */
        public boolean isActive(long nowSeconds) {
            return nowSeconds < until;
        }
    }

    /** rc.12 (M12): reads {@code sangam_emergency} (a JSON string or one object); {@code null} when absent or malformed. */
    public static Emergency emergency(Map<String, ?> claims) {
        Object value = claims.get("sangam_emergency");
        if (value instanceof String text) {
            try {
                value = JSON.readValue(text, Object.class);
            } catch (JsonProcessingException e) {
                return null;
            }
        }
        if (!(value instanceof Map<?, ?> o) || !(o.get("id") instanceof String id) || !UUID.matcher(id).matches()
                || !(o.get("org") instanceof String org) || !UUID.matcher(org).matches()
                || !(o.get("case") instanceof String caseRef) || caseRef.isEmpty()
                || !(o.get("until") instanceof Number until) || until instanceof Double || until instanceof Float) {
            return null;
        }
        return new Emergency(id, caseRef, org, until.longValue());
    }

    private static List<String> strings(Object value) {
        List<String> result = new ArrayList<>();
        if (value instanceof Collection<?> list) {
            list.stream().filter(String.class::isInstance).map(String.class::cast).filter(s -> !s.isEmpty()).forEach(result::add);
        }
        return result;
    }
}
