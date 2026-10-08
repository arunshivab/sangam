package in.sangamid.client;

import java.time.Instant;

/** Step-up (SGM-207): is the person's sign-in strong and recent enough for this action? */
public final class StepUp {
    /** A signature level is met only by an authentication at most this many seconds old. */
    public static final long SIGNATURE_MAX_AGE_SECONDS = 300;

    private StepUp() {
    }

    private static int rank(String acr) {
        if (acr == null) {
            return 0;
        }
        return switch (acr) {
            case SangamAcr.SINGLE_FACTOR -> 1;
            case SangamAcr.TWO_FACTOR, SangamAcr.SIGNATURE -> 2;
            case SangamAcr.PHISHING_RESISTANT -> 3;
            default -> 0;
        };
    }

    /** The level or a stronger one, and recent enough; a signature level is capped at five minutes. */
    public static boolean satisfies(String acr, Long authTime, String level, Long maxAge, long now) {
        Long limit = maxAge;
        if (SangamAcr.SIGNATURE.equals(level)) {
            limit = limit != null && limit < SIGNATURE_MAX_AGE_SECONDS ? limit : SIGNATURE_MAX_AGE_SECONDS;
        }
        if (rank(acr) < rank(level)) {
            return false;
        }
        return limit == null || (authTime != null && now - authTime <= limit);
    }

    /** As {@link #satisfies(String, Long, String, Long, long)}, for the person, now. */
    public static boolean satisfies(SangamUser user, String level, Long maxAge) {
        return user != null && satisfies(user.acr(), user.authTime(), level, maxAge, Instant.now().getEpochSecond());
    }

    /** The RFC 9470 {@code WWW-Authenticate} value for an API whose caller must authenticate again. */
    public static String challenge(String level, Long maxAge) {
        String header = "Bearer error=\"insufficient_user_authentication\", error_description=\"A stronger or more recent authentication is required\", acr_values=\"" + level + "\"";
        return maxAge == null ? header : header + ", max_age=" + maxAge;
    }
}
