package in.sangamid.client;

import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.time.Instant;
import java.util.Base64;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

/** Verifies Sangam's signed webhooks (Standard Webhooks, SGM-217). */
public final class WebhookVerifier {
    public static final String SECRET_PREFIX = "whsec_";
    public static final long TOLERANCE_SECONDS = 300;

    private WebhookVerifier() {
    }

    /** HMAC-SHA256 of {@code id.timestamp.body} under the secret's bytes, and a timestamp within five minutes. */
    public static boolean verify(String id, String timestamp, String signature, String body, String secret, long now) {
        if (id == null || id.isEmpty() || signature == null || signature.isEmpty() || timestamp == null || !timestamp.matches("\\d+")
                || secret == null || !secret.startsWith(SECRET_PREFIX)) {
            return false;
        }
        if (Math.abs(now - Long.parseLong(timestamp)) > TOLERANCE_SECONDS) {
            return false;
        }
        try {
            byte[] key = Base64.getDecoder().decode(secret.substring(SECRET_PREFIX.length()));
            Mac mac = Mac.getInstance("HmacSHA256");
            mac.init(new SecretKeySpec(key, "HmacSHA256"));
            byte[] expected = ("v1," + Base64.getEncoder().encodeToString(mac.doFinal((id + "." + timestamp + "." + body).getBytes(StandardCharsets.UTF_8))))
                    .getBytes(StandardCharsets.US_ASCII);
            for (String s : signature.split(" ")) {
                if (!s.isEmpty() && MessageDigest.isEqual(s.getBytes(StandardCharsets.US_ASCII), expected)) {
                    return true;
                }
            }
            return false;
        } catch (IllegalArgumentException | GeneralSecurityException e) {
            return false;
        }
    }

    /** As {@link #verify(String, String, String, String, String, long)}, now. */
    public static boolean verify(String id, String timestamp, String signature, String body, String secret) {
        return verify(id, timestamp, signature, body, secret, Instant.now().getEpochSecond());
    }
}
