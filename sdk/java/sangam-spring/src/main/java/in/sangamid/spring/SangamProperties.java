package in.sangamid.spring;

import org.springframework.boot.context.properties.ConfigurationProperties;

/** {@code sangam.*}: the Spring Security registration to use, where the routes live, and the audit helper's settings. */
@ConfigurationProperties("sangam")
public class SangamProperties {
    /** The Spring Security client registration id for Sangam. */
    private String registrationId = "sangam";
    /** Where sign-in lives: {base}/login/{registration}, {base}/callback, {base}/logout. */
    private String basePath = "/auth";
    private String appId = "";
    private String appVersion = "0.0.0";
    private String environment = "production";
    /** A durable JSON Lines file where audit events wait for the audit service. */
    private String auditBuffer = "sangam-audit/pending.jsonl";
    /** The audit service's POST /v1/events address; empty until it exists. */
    private String auditEndpoint = "";
    /** Trust X-Forwarded-For for audit client addresses (behind your own proxy only). */
    private boolean trustProxy;

    public String getRegistrationId() { return registrationId; }
    public void setRegistrationId(String value) { registrationId = value; }
    public String getBasePath() { return basePath; }
    public void setBasePath(String value) { basePath = value.replaceAll("/+$", ""); }
    public String getAppId() { return appId; }
    public void setAppId(String value) { appId = value; }
    public String getAppVersion() { return appVersion; }
    public void setAppVersion(String value) { appVersion = value; }
    public String getEnvironment() { return environment; }
    public void setEnvironment(String value) { environment = value; }
    public String getAuditBuffer() { return auditBuffer; }
    public void setAuditBuffer(String value) { auditBuffer = value; }
    public String getAuditEndpoint() { return auditEndpoint; }
    public void setAuditEndpoint(String value) { auditEndpoint = value; }
    public boolean isTrustProxy() { return trustProxy; }
    public void setTrustProxy(boolean value) { trustProxy = value; }

    /** Where to start a sign-in. */
    public String loginPath() {
        return basePath + "/login/" + registrationId;
    }
}
