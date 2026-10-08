# Sangam SDK for Java

Java 17+ (R6, SGM-306). Two artifacts, the same concepts and rules as every Sangam SDK, proved by the shared
conformance vectors (`sdk/conformance/vectors.json`):

- **`in.sangamid:sangam-client`** — no framework: `SangamUser.fromClaims(...)`, `user.hasPermission(org, permission)`,
  `StepUp.satisfies(...)` and `StepUp.challenge(...)` (RFC 9470), `WebhookVerifier.verify(...)`, `TokenVerifier`
  (Nimbus JOSE + JWT), `ManagementClient`, and the shared audit event: `SangamAudit.build(...)`,
  `SangamAudit.validate(...)`, `AuditBuffer`.
- **`in.sangamid:sangam-spring`** — Spring Boot 3.5 and Spring Security 6.5: sign-in with PKCE, `@RequireStepUp`,
  `SangamUsers.current()` and `SangamAuditRecorder`.

```properties
spring.security.oauth2.client.registration.sangam.client-id=lims
spring.security.oauth2.client.registration.sangam.client-secret=${SANGAM_SECRET}
spring.security.oauth2.client.registration.sangam.client-authentication-method=client_secret_post
spring.security.oauth2.client.registration.sangam.scope=openid,profile,email,orgs.read
spring.security.oauth2.client.registration.sangam.redirect-uri={baseUrl}/auth/callback
spring.security.oauth2.client.provider.sangam.issuer-uri=https://id.sangamid.in/
sangam.app-id=lims
sangam.app-version=1.2.0
sangam.audit-buffer=/var/lib/lims/audit.jsonl
```

```java
@SpringBootApplication
@Import(SangamConfiguration.class)
class LimsApplication {
    @Bean
    SecurityFilterChain security(HttpSecurity http, ClientRegistrationRepository registrations, SangamProperties sangam) throws Exception {
        http.authorizeHttpRequests(a -> a.requestMatchers("/").permitAll().anyRequest().authenticated());
        return SangamSecurity.apply(http, registrations, sangam).build();   // /auth/login/sangam, /auth/callback, /auth/logout
    }
}

@PostMapping("/results/{id}/sign")
@RequireStepUp(acr = SangamAcr.SIGNATURE)
Map<String, Object> sign(@PathVariable String id, HttpServletRequest request) {
    if (!SangamUsers.current().hasPermission(wardPath, "results:sign")) throw new AccessDeniedException("…");
    return audit.record(request, AuditEntry.of("lims.result.sign", "sign", "result", id).signature(tokenId, "Approved", recordHash));
}
```

`mvn test` runs the shared vectors and the adapter's tests; `mvn package` builds the sample
(`samples/spring-boot`, port 5930).
