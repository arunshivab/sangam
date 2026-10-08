package in.sangamid.client.audit;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/** What happened; the helper adds who, where and from which device (SGM-208 §7). Build with {@link #of}. */
public final class AuditEntry {
    String action;
    String category;
    String targetType;
    String targetId;
    String outcome = "success";
    String targetDisplay;
    String orgId;
    String orgPath;
    String dataClassification = "internal";
    String reason;
    String[] signature;
    final List<Map<String, Object>> changes = new ArrayList<>();
    Set<String> sensitive = Set.of();
    String actorType;
    String clientId;
    String correlationId;

    private AuditEntry() {
    }

    /** An entry: action as domain.object.verb, one of the nine categories, and what was acted on. */
    public static AuditEntry of(String action, String category, String targetType, String targetId) {
        AuditEntry e = new AuditEntry();
        e.action = action;
        e.category = category;
        e.targetType = targetType;
        e.targetId = targetId;
        return e;
    }

    public AuditEntry outcome(String value) { outcome = value; return this; }
    public AuditEntry display(String value) { targetDisplay = value; return this; }
    public AuditEntry tenant(String organisationId, String organisationPath) { orgId = organisationId; orgPath = organisationPath; return this; }
    public AuditEntry classification(String value) { dataClassification = value; return this; }
    public AuditEntry reason(String value) { reason = value; return this; }
    public AuditEntry signature(String tokenId, String meaning, String recordHash) { signature = new String[] {tokenId, meaning, recordHash}; return this; }
    public AuditEntry sensitive(Set<String> fields) { sensitive = Set.copyOf(fields); return this; }
    public AuditEntry actor(String type, String client) { actorType = type; clientId = client; return this; }
    public AuditEntry correlation(String value) { correlationId = value; return this; }

    /** A field-level change; masked when the field is declared sensitive. */
    public AuditEntry change(String field, Object before, Object after) {
        Map<String, Object> c = new LinkedHashMap<>();
        c.put("field", field);
        c.put("before", before);
        c.put("after", after);
        changes.add(c);
        return this;
    }
}
