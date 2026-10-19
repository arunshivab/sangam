# Sangam SDK for Java

Java 17+ (R6, SGM-306). Two artifacts, the same concepts and rules as every Sangam SDK, proved by the shared
conformance vectors (`sdk/conformance/vectors.json`):

- **`in.sangamid:sangam-client`** — no framework: `SangamUser.fromClaims(...)`, `user.hasPermission(org, permission)`,
  `StepUp.satisfies(...)` and `StepUp.challenge(...)` (RFC 9470), `WebhookVerifier.verify(...)`, `TokenVerifier`
  (Nimbus JOSE + JWT), `ManagementClient`, and the shared audit event: `SangamAudit.build(...)`,
  `SangamAudit.validate(...)`, `AuditBuffer`.
- **`in.sangamid:sangam-spring`** — Spring Boot 4.1 and Spring Security 7.1 (Java 17 or later): sign-in with PKCE, `@RequireStepUp`,
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

rc.12: `SangamUser.currentOrganisation(claims)` reads `sangam_org`; `SangamUser.emergency(claims)` reads
`sangam_emergency` (`caseReference()`, `until()`, `isActive(now)`). `ManagementClient` gains `checkStaff`, `addStaff`,
`setRoles` (the invitation call is gone), `lockSession(personToken)` and `endEmergency(personToken, reason)`. To ask for
emergency access, add `sangam_emergency=<your case number>` to the authorization request.

rc.13: `upsertMembership`, `setRoles` and the new `removeMember` take the acting admin's own access token third
(`setRoles(orgId, userId, adminAccessToken, roles, expiresAt)`), as `addStaff` does; Sangam checks they are an admin of
that organisation and records who asked.

rc.14: `setAttributes(userId, adminAccessToken, values)` takes the acting admin too, who must administer your application
or an organisation where the person works. Every application knows a person by its own id (the `sub` of its tokens, and the id the management API, webhooks and
SCIM use); another application's id for the same person is unrelated, and the API answers *not found* to an id that is
not yours (ADR-0017).

`mvn test` runs the shared vectors and the adapter's tests; `mvn package` builds the sample
(`samples/spring-boot`, port 5930).
