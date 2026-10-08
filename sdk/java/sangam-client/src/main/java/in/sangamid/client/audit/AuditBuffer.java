package in.sangamid.client.audit;

import com.fasterxml.jackson.core.type.TypeReference;
import com.fasterxml.jackson.databind.ObjectMapper;
import java.io.IOException;
import java.io.UncheckedIOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.nio.file.StandardOpenOption;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.function.Supplier;

/** A durable JSON Lines file where events wait for the audit service (SGM-208 §7). */
public final class AuditBuffer {
    private static final ObjectMapper JSON = new ObjectMapper();
    private final Path path;

    public AuditBuffer(Path path) {
        this.path = path;
    }

    public Path path() {
        return path;
    }

    /** Checks the event against the schema and appends it. */
    public synchronized void append(Map<String, Object> event) {
        List<String> problems = SangamAudit.validate(event);
        if (!problems.isEmpty()) {
            throw new IllegalArgumentException("The audit event breaks the shared schema: " + String.join("; ", problems));
        }
        try {
            Path parent = path.toAbsolutePath().getParent();
            if (parent != null) {
                Files.createDirectories(parent);
            }
            Files.writeString(path, JSON.writeValueAsString(event) + "\n", StandardCharsets.UTF_8, StandardOpenOption.CREATE, StandardOpenOption.APPEND);
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }

    /** Events waiting, oldest first. */
    public synchronized List<Map<String, Object>> pending() {
        if (!Files.exists(path)) {
            return List.of();
        }
        try {
            List<Map<String, Object>> events = new ArrayList<>();
            for (String line : Files.readAllLines(path, StandardCharsets.UTF_8)) {
                if (!line.isBlank()) {
                    events.add(JSON.readValue(line, new TypeReference<Map<String, Object>>() { }));
                }
            }
            return events;
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }

    /** Sends up to 500 events with a token for audit.write; keeps them when the service refuses or is away. */
    public synchronized int flush(String endpoint, Supplier<String> token, HttpClient http) throws IOException, InterruptedException {
        if (endpoint == null || endpoint.isEmpty()) {
            return 0;
        }
        List<Map<String, Object>> events = pending();
        List<Map<String, Object>> batch = events.subList(0, Math.min(500, events.size()));
        if (batch.isEmpty()) {
            return 0;
        }
        HttpRequest request = HttpRequest.newBuilder(URI.create(endpoint)).header("authorization", "Bearer " + token.get()).header("content-type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(JSON.writeValueAsString(Map.of("events", batch)))).build();
        if (http.send(request, HttpResponse.BodyHandlers.discarding()).statusCode() >= 300) {
            return 0;
        }
        Path temporary = path.resolveSibling(path.getFileName() + ".tmp");
        StringBuilder rest = new StringBuilder();
        for (Map<String, Object> e : events.subList(batch.size(), events.size())) {
            rest.append(JSON.writeValueAsString(e)).append('\n');
        }
        Files.writeString(temporary, rest.toString(), StandardCharsets.UTF_8);
        Files.move(temporary, path, StandardCopyOption.REPLACE_EXISTING);
        return batch.size();
    }
}
