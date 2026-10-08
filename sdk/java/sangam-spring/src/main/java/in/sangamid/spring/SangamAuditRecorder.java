package in.sangamid.spring;

import in.sangamid.client.SangamUser;
import in.sangamid.client.audit.AuditBuffer;
import in.sangamid.client.audit.AuditConfig;
import in.sangamid.client.audit.AuditEntry;
import in.sangamid.client.audit.SangamAudit;
import jakarta.servlet.http.HttpServletRequest;
import java.nio.file.Path;
import java.time.Instant;
import java.util.List;
import java.util.Map;

/** Records shared audit events (SGM-208) for the signed-in person and the request, into the durable buffer. */
public final class SangamAuditRecorder {
    private final SangamProperties properties;
    private final AuditBuffer buffer;

    public SangamAuditRecorder(SangamProperties properties) {
        this.properties = properties;
        this.buffer = new AuditBuffer(Path.of(properties.getAuditBuffer()));
    }

    public Map<String, Object> record(HttpServletRequest request, AuditEntry entry) {
        SangamUser user = SangamUsers.current();
        String forwarded = request.getHeader("X-Forwarded-For");
        String ip = properties.isTrustProxy() && forwarded != null && !forwarded.isEmpty() ? forwarded.split(",")[0].trim() : request.getRemoteAddr();
        Map<String, Object> event = SangamAudit.build(new AuditConfig(properties.getAppId(), properties.getAppVersion(), properties.getEnvironment()), user, ip,
                request.getHeader("User-Agent"), entry, Instant.now());
        buffer.append(event);
        return event;
    }

    public List<Map<String, Object>> pending() {
        return buffer.pending();
    }

    public AuditBuffer buffer() {
        return buffer;
    }
}
