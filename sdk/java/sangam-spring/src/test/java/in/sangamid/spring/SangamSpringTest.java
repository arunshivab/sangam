package in.sangamid.spring;

import static org.assertj.core.api.Assertions.assertThat;
import static org.springframework.security.test.web.servlet.request.SecurityMockMvcRequestPostProcessors.csrf;
import static org.springframework.security.test.web.servlet.request.SecurityMockMvcRequestPostProcessors.oidcLogin;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

import in.sangamid.client.SangamAcr;
import in.sangamid.client.audit.AuditEntry;
import in.sangamid.client.audit.SangamAudit;
import jakarta.servlet.http.HttpServletRequest;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Instant;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.boot.test.autoconfigure.web.servlet.AutoConfigureMockMvc;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Import;
import org.springframework.mock.web.MockHttpSession;
import org.springframework.security.config.annotation.web.builders.HttpSecurity;
import org.springframework.security.oauth2.client.registration.ClientRegistration;
import org.springframework.security.oauth2.client.registration.ClientRegistrationRepository;
import org.springframework.security.oauth2.client.registration.InMemoryClientRegistrationRepository;
import org.springframework.security.oauth2.core.AuthorizationGrantType;
import org.springframework.security.web.SecurityFilterChain;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.MvcResult;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RestController;

/** Sign-in routes with PKCE and step-up parameters, the step-up interceptor, and the audit recorder. */
@SpringBootTest(classes = SangamSpringTest.App.class, properties = {"sangam.app-id=lims", "sangam.environment=development", "sangam.audit-buffer=target/test-audit/pending.jsonl"})
@AutoConfigureMockMvc
class SangamSpringTest {

    @SpringBootApplication
    @Import(SangamConfiguration.class)
    static class App {
        @Bean
        ClientRegistrationRepository registrations() {
            return new InMemoryClientRegistrationRepository(ClientRegistration.withRegistrationId("sangam").clientId("lims").clientSecret("secret")
                    .authorizationGrantType(AuthorizationGrantType.AUTHORIZATION_CODE).redirectUri("{baseUrl}/auth/callback").scope("openid", "orgs.read")
                    .authorizationUri("https://id.example.in/connect/authorize").tokenUri("https://id.example.in/connect/token").jwkSetUri("https://id.example.in/jwks")
                    .userNameAttributeName("sub").build());
        }

        @Bean
        SecurityFilterChain security(HttpSecurity http, ClientRegistrationRepository registrations, SangamProperties sangam) throws Exception {
            http.authorizeHttpRequests(a -> a.anyRequest().authenticated());
            return SangamSecurity.apply(http, registrations, sangam).build();
        }

        @RestController
        static class Api {
            private final SangamAuditRecorder audit;

            Api(SangamAuditRecorder audit) {
                this.audit = audit;
            }

            @GetMapping("/secure")
            String secure() {
                return SangamUsers.current().id();
            }

            @PostMapping("/sign")
            @RequireStepUp(acr = SangamAcr.SIGNATURE, returnTo = "/?resume=sign")
            Map<String, Object> sign(HttpServletRequest request) {
                return audit.record(request, AuditEntry.of("lims.result.sign", "sign", "result", "R-1").signature("sig-1", "Approved", "sha256:x"));
            }
        }
    }

    @Autowired
    MockMvc mvc;

    @Test
    void signInStartsWithPkceAndCarriesTheStepUpRequirement() throws Exception {
        MockHttpSession session = new MockHttpSession();
        MvcResult r = mvc.perform(get("/auth/login/sangam").param("returnTo", "/records/1").param("acr", SangamAcr.SIGNATURE).param("max_age", "300").session(session))
                .andExpect(status().is3xxRedirection()).andReturn();
        String location = r.getResponse().getRedirectedUrl();
        assertThat(location).startsWith("https://id.example.in/connect/authorize?").contains("code_challenge=").contains("code_challenge_method=S256")
                .contains("acr_values=urn:sangam:acr:sign").contains("max_age=300").contains("redirect_uri=http://localhost/auth/callback");
        assertThat(session.getAttribute(SangamAuthorizationRequestResolver.RETURN_TO)).isEqualTo("/records/1");
        mvc.perform(get("/auth/login/sangam").param("returnTo", "https://evil.example/").session(session)).andExpect(status().is3xxRedirection());
        assertThat(session.getAttribute(SangamAuthorizationRequestResolver.RETURN_TO)).isEqualTo("/");
    }

    @Test
    void signedOutPeopleAreSentToSignIn_OrToldWhereToGo() throws Exception {
        mvc.perform(get("/secure")).andExpect(status().is3xxRedirection());
        mvc.perform(get("/secure").header("Accept", "application/json")).andExpect(status().isUnauthorized()).andExpect(jsonPath("$.login").value("/auth/login/sangam?returnTo=%2Fsecure"));
    }

    @Test
    void stepUpIsRequiredForASignature_AndTheEventIsAudited() throws Exception {
        long now = Instant.now().getEpochSecond();
        mvc.perform(post("/sign").with(csrf()).header("Accept", "application/json").with(oidcLogin().idToken(t -> t.subject("0192a6b0-0000-7000-8000-000000000042")
                        .claim("acr", SangamAcr.SINGLE_FACTOR).claim("auth_time", Instant.ofEpochSecond(now)))))
                .andExpect(status().isUnauthorized()).andExpect(jsonPath("$.error").value("step_up_required"))
                .andExpect(jsonPath("$.login").value("/auth/login/sangam?returnTo=%2F%3Fresume%3Dsign&acr=urn%3Asangam%3Aacr%3Asign"));
        MvcResult ok = mvc.perform(post("/sign").with(csrf()).header("User-Agent", "spring-test").with(oidcLogin().idToken(t -> t.subject("0192a6b0-0000-7000-8000-000000000042")
                        .claim("acr", SangamAcr.SIGNATURE).claim("amr", List.of("pwd", "otp")).claim("auth_time", Instant.ofEpochSecond(now - 30)))))
                .andExpect(status().isOk()).andReturn();
        String body = ok.getResponse().getContentAsString();
        assertThat(body).contains("\"acr\":\"urn:sangam:acr:sign\"").contains("\"user_agent\":\"spring-test\"");
        Path buffer = Path.of("target/test-audit/pending.jsonl");
        assertThat(Files.readAllLines(buffer)).isNotEmpty();
        assertThat(SangamAudit.validate(new com.fasterxml.jackson.databind.ObjectMapper().readValue(body, Map.class))).isEmpty();
    }
}
