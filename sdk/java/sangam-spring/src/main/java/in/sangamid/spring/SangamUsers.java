package in.sangamid.spring;

import in.sangamid.client.SangamUser;
import org.springframework.security.core.Authentication;
import org.springframework.security.core.context.SecurityContextHolder;
import org.springframework.security.oauth2.core.oidc.user.OidcUser;

/** The signed-in person, from Spring Security's OIDC sign-in. */
public final class SangamUsers {
    private SangamUsers() {
    }

    /** The person behind an authentication, or null. */
    public static SangamUser from(Authentication authentication) {
        if (authentication != null && authentication.getPrincipal() instanceof OidcUser oidc) {
            return SangamUser.fromClaims(oidc.getClaims());
        }
        return null;
    }

    /** The person signed in on this request, or null. */
    public static SangamUser current() {
        return from(SecurityContextHolder.getContext().getAuthentication());
    }
}
