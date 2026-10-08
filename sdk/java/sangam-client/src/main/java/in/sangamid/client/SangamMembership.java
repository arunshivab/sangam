package in.sangamid.client;

import java.util.List;
import java.util.Locale;

/**
 * One role a person holds at one organisation, from the {@code sangam_orgs} claim.
 *
 * @param organisationId the organisation
 * @param organisationName its name
 * @param organisationType its type, for example "hospital"
 * @param path its materialised path, ending with "/"
 * @param role the role code
 * @param permissions the permissions the role carries
 * @param appliesToDescendants whether the role reaches the organisation's descendants
 */
public record SangamMembership(String organisationId, String organisationName, String organisationType, String path, String role,
                               List<String> permissions, boolean appliesToDescendants) {

    public SangamMembership {
        permissions = List.copyOf(permissions);
    }

    /** The organisation itself, or a descendant when the role is inherited. Paths compare case-insensitively. */
    public boolean covers(String organisationPath) {
        String target = organisationPath.toLowerCase(Locale.ROOT);
        String own = path.toLowerCase(Locale.ROOT);
        if (target.equals(own)) {
            return true;
        }
        // Paths end with "/", so "/a/" never matches a sibling "/ab/".
        return appliesToDescendants && own.endsWith("/") && target.startsWith(own);
    }
}
