package in.sangamid.sample;

import in.sangamid.client.SangamAcr;
import in.sangamid.client.SangamMembership;
import in.sangamid.client.SangamUser;
import in.sangamid.client.StepUp;
import in.sangamid.client.audit.AuditEntry;
import in.sangamid.spring.RequireStepUp;
import in.sangamid.spring.SangamAuditRecorder;
import in.sangamid.spring.SangamProperties;
import in.sangamid.spring.SangamUsers;
import jakarta.servlet.http.HttpServletRequest;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.stream.Collectors;
import org.springframework.http.MediaType;
import org.springframework.security.web.csrf.CsrfToken;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;
import org.springframework.web.util.HtmlUtils;

@RestController
class SampleController {
    private final SangamAuditRecorder audit;
    private final SangamProperties sangam;
    private final String page;

    SampleController(SangamAuditRecorder audit, SangamProperties sangam) throws IOException {
        this.audit = audit;
        this.sangam = sangam;
        try (InputStream in = SampleController.class.getResourceAsStream("/page.html")) {
            this.page = new String(in.readAllBytes(), StandardCharsets.UTF_8);
        }
    }

    private static String e(String s) {
        return HtmlUtils.htmlEscape(s == null ? "" : s);
    }

    @GetMapping(value = "/", produces = MediaType.TEXT_HTML_VALUE)
    String home(HttpServletRequest request) {
        SangamUser user = SangamUsers.current();
        CsrfToken csrf = (CsrfToken) request.getAttribute(CsrfToken.class.getName());
        String token = csrf == null ? "" : e(csrf.getToken());
        if (user == null) {
            String body = "<section><h1>Sign in with Sangam</h1><p class=\"muted\">This sample keeps every token on its Spring Boot server; the browser only holds a session cookie.</p>"
                    + "<a class=\"button\" href=\"" + sangam.loginPath() + "?returnTo=/\">Sign in with Sangam</a></section>";
            return page.replace("{{user}}", "").replace("{{body}}", body).replace("{{csrf}}", token);
        }
        String memberships = user.memberships().stream().map(m -> "<li><b>" + e(m.role()) + "</b> at " + e(m.organisationName()) + " <code>" + e(m.path()) + "</code>"
                + (m.appliesToDescendants() ? " (and below)" : "") + ": " + e(String.join(", ", m.permissions())) + "</li>").collect(Collectors.joining());
        String options = user.memberships().stream().map(m -> "<option value=\"" + e(m.path()) + "\">" + e(m.organisationName()) + "</option>").collect(Collectors.joining());
        String body = "<section><h1>Signed in as " + e(user.name()) + "</h1><p class=\"muted\">Sangam id <code>" + e(user.id()) + "</code> · " + e(user.acr()) + "</p></section>"
                + "<section data-panel=\"organisations\"><h2>Your organisations in this application</h2><ul>" + (memberships.isEmpty() ? "<li class=\"muted\">No role yet.</li>" : memberships) + "</ul></section>"
                + "<section data-panel=\"permission\"><h2>Check a permission</h2><div class=\"row\"><select id=\"org\" aria-label=\"Organisation\">" + options
                + "<option value=\"/0192a6b0-0000-7000-8000-00000000ffff/\">An organisation you have no role in</option></select>"
                + "<input id=\"permission\" value=\"vitals:read\" aria-label=\"Permission\"><button class=\"ghost\" id=\"ask\">Ask the server</button></div><p id=\"answer\"></p></section>"
                + "<section data-panel=\"signature\"><h2>Sign record SOP-114</h2><p class=\"muted\">Signing needs a two-factor sign-in from the last five minutes (" + SangamAcr.SIGNATURE + "). "
                + (StepUp.satisfies(user, SangamAcr.SIGNATURE, null) ? "Yours qualifies." : "Sangam will ask you to sign in again first.")
                + "</p><button data-action=\"sign\" id=\"sign\">Sign as approved</button><div id=\"signed\"></div></section>";
        String header = "<span class=\"row\">" + e(user.name()) + " <a class=\"button ghost\" href=\"" + sangam.getBasePath() + "/logout\">Sign out</a></span>";
        return page.replace("{{user}}", header).replace("{{body}}", body).replace("{{csrf}}", token);
    }

    @GetMapping("/api/check")
    Map<String, Object> check(@RequestParam String org, @RequestParam String permission) {
        SangamUser user = SangamUsers.current();
        return Map.of("allowed", user != null && user.hasPermission(org, permission));
    }

    @PostMapping("/api/records/{id}/sign")
    @RequireStepUp(acr = SangamAcr.SIGNATURE, returnTo = "/?resume=sign")
    Map<String, Object> sign(@PathVariable String id, HttpServletRequest request) {
        SangamUser user = SangamUsers.current();
        AuditEntry entry = AuditEntry.of("sample.record.sign", "sign", "record", id).display("Record " + id).signature("sig-" + id, "Approved", "sha256:" + "0".repeat(64));
        if (user != null && !user.memberships().isEmpty()) {
            SangamMembership m = user.memberships().get(0);
            entry.tenant(m.organisationId(), m.path());
        }
        Map<String, Object> result = new LinkedHashMap<>();
        result.put("signed", id);
        result.put("event", audit.record(request, entry));
        return result;
    }
}
