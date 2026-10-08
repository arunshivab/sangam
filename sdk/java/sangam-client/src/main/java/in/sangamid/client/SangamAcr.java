package in.sangamid.client;

/** Sangam's authentication levels ({@code acr}, SGM-207). */
public final class SangamAcr {
    public static final String SINGLE_FACTOR = "urn:sangam:acr:1";
    public static final String TWO_FACTOR = "urn:sangam:acr:2";
    public static final String PHISHING_RESISTANT = "urn:sangam:acr:3";
    /** For an electronic signature: two factors, at most five minutes ago. */
    public static final String SIGNATURE = "urn:sangam:acr:sign";

    private SangamAcr() {
    }
}
