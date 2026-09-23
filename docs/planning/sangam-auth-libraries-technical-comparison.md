# Sangam — Auth Library Technical Comparison
## Supporting Document

**Reads alongside:** `sangam-strategic-document-v0.1.md` and `sangam-comparisons-and-decisions.md`
**Scope:** Technical, protocol-level comparison of identity server / OIDC provider options for a self-hosted, multi-tenant, .NET-anchored platform.
**Date:** May 2026
**Note on license accuracy:** Open-source licences shift. Always verify current licence on the project's official site before adopting.

---

## 0. Scope of Comparison

We're comparing **identity providers / OIDC servers** that you would self-host as the auth backbone for Sangam. Excluded:

- **SaaS-only products** (Auth0, Okta, Clerk, FusionAuth Cloud, AWS Cognito, Microsoft Entra ID). These solve a different problem — you rent identity from a vendor. For a jointly-owned open platform, this is the wrong shape.
- **Auth libraries that aren't OIDC servers** (e.g. raw `Microsoft.AspNetCore.Authentication.OpenIdConnect`) — these are clients, not servers.

The ten included options span four categories:

1. **.NET-native servers:** OpenIddict, Duende IdentityServer, ASP.NET Core Identity (caveat: not an OIDC server alone)
2. **Java/JVM servers:** Keycloak
3. **Go-based modern servers:** ZITADEL, Ory Hydra (+ Kratos), Authelia, Casdoor
4. **Other-stack servers:** Authentik (Python), SuperTokens (Node), Logto (Node), FusionAuth (Java)

---

## 1. At-a-Glance Comparison Matrix

| Library | Language | License | OIDC | OAuth 2.1 | SAML 2.0 | LDAP | SCIM | FAPI / DPoP | Built-in Admin UI | Multi-tenant Native | Memory Footprint (idle) | Container size | Maturity |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **OpenIddict** | C# / .NET 8+ | MIT | ✅ Certified | ✅ | ❌ (3rd party) | ❌ | ❌ | ✅ DPoP | ❌ (you build) | ⚠️ (you build) | ~150-250 MB | ~120 MB | High |
| **Duende IdentityServer** | C# / .NET 8+ | Custom (Community / Commercial) | ✅ Certified | ✅ | ⚠️ Paid add-on | ❌ | ⚠️ Paid add-on | ✅ DPoP, FAPI | ❌ (3rd party AdminUI) | ⚠️ (you build) | ~150-250 MB | ~120 MB | Very high |
| **Keycloak** | Java / Quarkus | Apache 2.0 | ✅ Certified | ✅ | ✅ Native | ✅ Native | ✅ Native | ✅ FAPI | ✅ Mature | ✅ Realms | ~600-900 MB | ~700 MB | Very high |
| **ZITADEL** | Go | **AGPL 3.0** (since 2025) | ✅ Certified | ✅ | ❌ (paid) | ❌ | ⚠️ Partial | ✅ DPoP | ✅ Modern | ✅ Organisations (first-class) | ~80-150 MB | ~50 MB | High |
| **Authentik** | Python + TypeScript | MIT (+ Enterprise tier) | ✅ | ✅ | ✅ Native | ✅ Server | ✅ | ⚠️ Partial | ✅ Flow Builder | ⚠️ (workarounds) | ~400-600 MB | ~700 MB | High |
| **Ory Hydra + Kratos** | Go | Apache 2.0 | ✅ Certified | ✅ | ❌ | ❌ | ❌ | ✅ DPoP, FAPI | ❌ (paid Ory Network) | ⚠️ (you build) | ~50-100 MB | ~40 MB | High |
| **Authelia** | Go | Apache 2.0 | ✅ | ✅ | ❌ (planned) | ✅ (auth backend) | ❌ | ⚠️ Partial | ⚠️ Minimal | ❌ | ~30-60 MB | ~30 MB | Medium-High |
| **Casdoor** | Go | Apache 2.0 | ✅ | ✅ | ✅ | ✅ | ✅ | ⚠️ | ✅ | ✅ | ~80-120 MB | ~80 MB | Medium |
| **SuperTokens** | Java + Node SDKs | Apache 2.0 (mostly) | ✅ | ✅ | ⚠️ Paid | ❌ | ❌ | ⚠️ | ✅ Modern | ✅ | ~300-500 MB | ~250 MB | Medium-High |
| **Logto** | Node.js (TS) | MPL 2.0 | ✅ Certified | ✅ | ⚠️ Paid | ❌ | ❌ | ⚠️ | ✅ Modern | ✅ | ~200-300 MB | ~250 MB | Medium |
| **FusionAuth (CE)** | Java | Custom (free CE, paid commercial) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ Excellent | ✅ Tenants | ~500-800 MB | ~700 MB | Very high |
| **ASP.NET Core Identity** | C# / .NET | MIT | ❌ (user store only) | n/a | n/a | n/a | n/a | n/a | ❌ | ❌ | n/a (library) | n/a | Very high |

---

## 2. Deep Dive — .NET-Native Options

### 2.1 OpenIddict

**Architecture.** A set of NuGet libraries layered on top of ASP.NET Core. Drops directly into your existing app or runs as a dedicated identity host. Uses Entity Framework Core for persistence (Postgres, SQL Server, SQLite, MongoDB). Token signing via X.509 certificates or ephemeral RSA/ECDSA keys with automatic rotation.

**License.** MIT, by Kévin Chalet (long-standing .NET community contributor). Funded via GitHub Sponsors and consulting — no commercial fork.

**Protocols.**
- OpenID Connect 1.0 (certified — see the OpenID Foundation conformance list)
- OAuth 2.0 with all standard flows: Authorization Code, Client Credentials, Refresh Token, Device Code, Resource Owner Password Credentials
- OAuth 2.1 features: PKCE mandatory, refresh token rotation, sender-constrained tokens
- DPoP (Demonstrating Proof-of-Possession) — RFC 9449
- Token Introspection (RFC 7662), Token Revocation (RFC 7009)
- OpenID Connect Discovery, Dynamic Client Registration
- **Not supported natively:** SAML 2.0, LDAP server, SCIM. These would need third-party libraries or external services.

**Storage backends.** EF Core Postgres / SQL Server / SQLite / MySQL / Cosmos DB / MongoDB. Schema is migration-managed.

**MFA support.** Inherits from ASP.NET Core Identity — TOTP, SMS, email, WebAuthn (via FIDO2.NET integration). All flows are wired in code.

**Multi-tenancy.** Not first-class. You build it via custom data partitioning, claim filtering, and a tenant resolver in your auth middleware. Doable but takes design care.

**Admin UI.** None provided. You build it. For Sangam this is *also* the self-service UI — same Blazor codebase doubles as both.

**Customisation.** Extremely deep. Every OIDC handler is overridable; you control claim transformation, consent UI, token contents, client validation rules.

**Deployment.** Standard ASP.NET Core — single binary `dotnet run`, or in Docker. Memory footprint ~150-250 MB at idle, scales linearly.

**Performance.** Excellent. Native ASP.NET Core means kestrel-grade throughput. Tens of thousands of token issuances per second on a small VM in published benchmarks.

**Maturity.** Started in 2014. Used in production by many .NET shops. ~3,500 GitHub stars. Active maintenance, frequent releases.

**Technical Pros:**
- True MIT, no usage thresholds, no telephone-home licence validation
- Idiomatic ASP.NET Core — feels native, not bolted on
- Smallest cognitive overhead for .NET developers
- Deeply customisable without forking
- Free admin UI by virtue of writing your own — which you wanted anyway for Sangam branding
- Lightweight (< 250 MB RAM)
- Fast (Kestrel-native)
- Works seamlessly with ASP.NET Core Identity for user management

**Technical Cons:**
- No SAML — if any future partner needs SAML SSO for their enterprise customers (banks, hospitals on Microsoft AD FS), you'd add a separate SAML bridge (e.g. `Sustainsys.Saml2`)
- No LDAP / SCIM — for enterprise provisioning, this is a future gap
- No built-in admin UI — must build, ~2-4 weeks of work
- Multi-tenancy you architect yourself; no "realm" concept out of the box
- Smaller commercial-support ecosystem than Duende or Keycloak
- Documentation is good but spread across blog posts + samples; less polished than Duende's

---

### 2.2 Duende IdentityServer

**Architecture.** Direct successor to IdentityServer4 (which was open-source MIT until late 2020). Same authors, now a commercial product with an open-source-style codebase. NuGet packages for ASP.NET Core; deployment identical to OpenIddict.

**License.** Source-available with three commercial tiers + Community Edition:
- **Community Edition:** Free if organisation gross revenue < $1M USD AND ≤ 4 client applications.
- **Starter:** $1,500/year, 2 clients.
- **Business:** $6,000/year, 10 clients (likely).
- **Enterprise:** $18,000+/year, 25 clients, advanced features.

License validation happens at runtime in your application — if you misconfigure the licence, you get logged warnings but the server still runs. No phone-home; validation is local.

**Protocols.**
- Full OIDC + OAuth 2.1 (certified)
- DPoP (RFC 9449)
- **FAPI 1.0 / FAPI 2.0** support — relevant for banking, healthcare, government scenarios needing high-assurance auth
- Token Exchange (RFC 8693)
- CIBA (Client-Initiated Backchannel Authentication) — Enterprise tier
- Mutual TLS client auth (mTLS)
- Dynamic Federation — Enterprise tier
- **SAML, SCIM:** Available via Rock Solid Knowledge add-ons (commercial, paid separately, ~₹50,000+/yr each)

**Storage.** EF Core (same backends as OpenIddict) or fully custom via interface implementations.

**MFA.** Same as OpenIddict — leverages ASP.NET Core Identity.

**Multi-tenancy.** Not first-class out of the box. Same as OpenIddict — you architect it.

**Admin UI.** Not included. Rock Solid Knowledge sells **AdminUI** as a separate annual licence (~$1,500-3,000/year). A free Community Edition of AdminUI exists with reduced features. Alternatively, you build your own.

**Customisation.** Deepest of any .NET option. The Duende team are the recognised authorities on OIDC in .NET; their abstractions are the gold standard.

**Standout features:**
- The cleanest token issuance pipeline of any .NET option
- Best-in-class documentation (genuinely comprehensive, tutorial-driven)
- DPoP, FAPI, CIBA are implemented to spec
- Optional commercial support contracts with the original IdentityServer4 authors

**Maturity.** Same code lineage as IdentityServer4 (since 2015). Used by major .NET shops worldwide.

**Technical Pros:**
- Most comprehensive OIDC feature set in the .NET ecosystem
- FAPI / DPoP / CIBA matter if any partner app ever touches banking, regulated finance, or open-banking-style APIs
- Vendor-backed support available
- Documentation quality is unmatched
- Same operational profile as OpenIddict (small, fast, ASP.NET-native)
- Community Edition is feature-complete (not a stripped-down version) — you get everything for free if you qualify

**Technical Cons:**
- **Licence ambiguity for shared platforms** — if Sangam is a free open-source platform but the three founding companies' apps using it have combined revenue > $1M USD, the "organisation" definition becomes contestable. Get written clarification from Duende sales before adopting.
- 4-client limit on Community Edition. You have 3 apps. One new partner = breach.
- Upgrade path is expensive ($1,500-18,000/year)
- AdminUI is a separate licence or extra build work
- Licence-validation log noise if you misconfigure
- Source-available is not the same as open-source by OSI definition — purists may object
- Adds a software-supplier relationship to what is otherwise a fully open stack

---

### 2.3 ASP.NET Core Identity (for completeness)

**Not an OIDC server.** This is a user management library — handles password hashing, user storage, lockout, email confirmation, 2FA, roles, claims, external login providers. It's the *substrate* that OpenIddict and Duende sit on top of for the user store.

You cannot use it alone for Sangam. If you tried, you'd be writing your own OIDC protocol layer — exactly the mistake you must not make.

**Use case:** Always present underneath either OpenIddict or Duende. Not an alternative to them.

---

## 3. Deep Dive — Java / JVM Options

### 3.1 Keycloak

**Architecture.** Java application running on Quarkus (since v17+). Built and maintained by Red Hat, donated to CNCF as a graduated project. Single binary or container.

**License.** Apache 2.0. No usage limits, no commercial gates, no telephone-home.

**Protocols.**
- OpenID Connect 1.0 (certified)
- OAuth 2.0 / 2.1
- SAML 2.0 — first-class, both IdP and SP roles
- LDAP — both as auth backend AND can act as LDAP server
- SCIM 2.0 — provisioning support
- WS-Federation
- FAPI 1.0
- DPoP, mTLS, CIBA, Token Exchange, Device Code
- **Most protocol-complete option in this list.**

**Storage backends.** PostgreSQL, MariaDB, MySQL, MSSQL, Oracle. PostgreSQL is the most-tested production setup.

**MFA.** TOTP, WebAuthn (passkeys), recovery codes, SMS via third-party gateway. Conditional flows let you require MFA per realm, per role, per risk score.

**Multi-tenancy.** **Realms** are first-class. Each realm is an isolated tenant with its own users, clients, roles, themes, identity providers. This is the most mature multi-tenancy model in the open-source IdP world.

**Admin UI.** Mature, comprehensive web admin console. Self-service "Account Console" for end users. Both customisable via themes.

**Customisation.**
- **Themes** for UI (HTML + FreeMarker templates)
- **SPI (Service Provider Interface)** for behavioural extension — write Java code, deploy as JAR, Keycloak loads it
- **Authentication Flows** are graph-configurable in the admin UI
- **Mappers** translate user attributes into token claims
- Webhook-style **Event Listeners** for external integrations

**Deployment.** Single JVM process. Container image ~700 MB. Memory ~600-900 MB idle, scales with realm count and user count. Clusters via Infinispan (cache) for HA.

**Performance.** Excellent at scale. Used by governments (Italy's national ID), banks, large SaaS platforms. Slower cold start than Go/.NET (5-15 seconds) but steady-state throughput is high.

**Maturity.** Originated 2014 at Red Hat. ~20,000 GitHub stars. CNCF graduated project (2024). Backed by Red Hat with commercial support available.

**Technical Pros:**
- Most feature-complete OSS IdP — supports every protocol you might ever need
- Production-proven at massive scale
- Multi-tenancy via realms is the cleanest model available
- Free LDAP + SAML + SCIM = enterprise-ready out of the box
- Apache 2.0, no strings
- Free admin UI is excellent
- Largest community of any OSS IdP — answers on Stack Overflow, books, conference talks

**Technical Cons:**
- **JVM operational model** — separate stack to monitor, profile, tune. JVM GC tuning is a real skill.
- **Memory hungry** — 600-900 MB idle is 3-6× the .NET options
- **Customisation requires Java** — for a C#-only team, every SPI extension is a context switch
- Container image is ~700 MB (vs ~50 MB for Go options)
- Cold start is noticeably slower than .NET or Go
- Realms can become unwieldy if you don't design them correctly (every realm has its own DB tables internally)
- Major version upgrades have historically required care; Keycloak X (Quarkus rewrite) was a non-trivial migration

---

## 4. Deep Dive — Go-Based Modern Options

### 4.1 ZITADEL

**Architecture.** Go-based monolith built on event sourcing. State stored as event streams in PostgreSQL or CockroachDB. Single binary, gRPC + REST APIs, Kubernetes-first design.

**License.** **AGPL 3.0 (since mid-2025; previously Apache 2.0).**
- AGPL is strong copyleft for **network use**. If you modify ZITADEL and offer it as a service (which Sangam essentially does for its partner apps), you may be required to publish your modified source under AGPL.
- For most Sangam use cases — running stock ZITADEL without modifications — AGPL is fine.
- Commercial licence is available if you want to keep modifications proprietary.

**Protocols.**
- OpenID Connect (certified)
- OAuth 2.0 / 2.1
- DPoP
- Token Exchange
- SAML 2.0 — **paid feature in some editions**
- **No native LDAP server, no SCIM client (SCIM endpoint partial)**

**Storage.** PostgreSQL, CockroachDB. Event-sourced — auditable by design, you can replay history.

**MFA.** TOTP, WebAuthn/passkeys, U2F, OTP via email/SMS.

**Multi-tenancy.** First-class. **Organisations** are core to the data model. Best multi-tenancy of any modern OSS IdP — purpose-built for SaaS-style multi-org scenarios.

**Admin UI.** Modern, polished web console. Also: full gRPC + REST API for programmatic management.

**Customisation.** "Actions" — JavaScript executed at lifecycle events (post-login, pre-token, etc.). No language compilation required; deploy via API.

**Deployment.** Single binary. Container ~50 MB. Kubernetes operator available. **Requires HTTP/2 on reverse proxy for gRPC** — some setups (Cloudflare Tunnel without config) won't work out of the box.

**Performance.** Designed for cloud-scale. Used at SaaS scale by some Y-Combinator companies. Cold start sub-second.

**Maturity.** Public since 2020, growing fast. ~10,000 GitHub stars. Backed by Swiss company ZITADEL AG.

**Technical Pros:**
- Best multi-tenancy model in the comparison — organisations are first-class
- Event sourcing = built-in audit trail (DPDPA-relevant)
- Modern API-first design — gRPC + REST means easy automation
- Very small footprint, very fast
- Passkeys, WebAuthn, MFA are excellent
- Cloud-native deployment is easy
- Active development, well-funded company

**Technical Cons:**
- **AGPL 3.0 risk** — for a shared platform among three companies' commercial apps, the AGPL interaction needs legal review. Even using stock ZITADEL is fine; modifying it could pull your apps into AGPL obligations. Conservative interpretation: avoid for a closed-source commercial app integration.
- SAML is a paid feature in some configurations
- HTTP/2 reverse-proxy requirement adds setup friction
- Customisation in JavaScript only — power-user features need careful design
- Smaller community than Keycloak (but growing)
- Some major upgrades have caused breaking changes (per the comparison source)

### 4.2 Ory Hydra (+ Kratos + Keto)

**Architecture.** Microservice approach. **Hydra** = OAuth2 + OIDC server only (no user management). **Kratos** = user management / signup / login flows. **Keto** = authorization / permissions. You compose them yourself.

**License.** Apache 2.0 across the stack.

**Protocols.**
- OIDC + OAuth 2.0/2.1 (Hydra) — certified
- FAPI, DPoP, mTLS, Token Exchange — Hydra
- No SAML, no LDAP, no SCIM — by design (out of scope)

**Storage.** PostgreSQL, MySQL, CockroachDB for each component.

**MFA.** Via Kratos — TOTP, WebAuthn, lookup secrets.

**Multi-tenancy.** Not native — you compose it via Hydra clients + Kratos identity schemas.

**Admin UI.** **None included.** Ory sells "Ory Network" as a hosted product and "Ory Console" admin UI; self-hosting means you build your own UI on top of the APIs. This is a significant build effort.

**Customisation.** API-first; you write whatever UI and flow logic you want. Maximum flexibility, maximum responsibility.

**Deployment.** Each service is a Go binary. Tiny containers (~40 MB). Memory ~50-100 MB per service.

**Performance.** Excellent. Used at very high scale (Hydra is used by some large fintechs).

**Maturity.** Hydra since 2015. Kratos since 2019. Both stable.

**Technical Pros:**
- Strict separation of concerns — each component does one thing well
- Apache 2.0, no commercial gates
- Smallest possible footprint
- High customisability
- Excellent security posture; the team is known for cryptographic care

**Technical Cons:**
- **You build everything user-facing** — login UI, signup UI, password reset UI, admin UI. Hydra and Kratos are headless. For Sangam this means significantly more frontend work than OpenIddict alone.
- The "compose three services" model adds operational complexity
- No built-in admin UI of any kind for self-hosters
- For a small team, the upfront effort is higher than OpenIddict + your own Blazor admin
- Ory's commercial model pulls more features into their hosted "Ory Network" over time — self-host parity is something to watch

### 4.3 Authelia

**Architecture.** Single Go binary. Designed primarily as an **authentication proxy** for HTTP services (used heavily in homelab and self-hosted-app communities). Acts as an OIDC provider as a secondary feature.

**License.** Apache 2.0.

**Protocols.**
- OIDC 1.0 — added in v4.36+
- OAuth 2.0
- No SAML (planned), no SCIM, no LDAP server (LDAP client only)

**Storage.** SQLite, MySQL, PostgreSQL.

**MFA.** TOTP, WebAuthn, Duo push, Mobile push.

**Multi-tenancy.** None.

**Admin UI.** Minimal end-user portal. Configuration is YAML files — no admin web UI for ops.

**Technical Pros:**
- Tiny footprint (~30 MB RAM)
- Fast, simple
- Excellent for protecting non-SSO-aware web services as a reverse proxy

**Technical Cons:**
- **Not designed as a multi-tenant SaaS-style IdP**
- No admin UI — config via YAML edits and restart
- OIDC support is newer and less battle-tested than the primary use case
- No multi-tenancy = not a fit for Sangam

**Verdict for Sangam:** wrong tool for the job. Excellent for protecting Plex/Jellyfin/Nextcloud at home; not for a multi-tenant identity platform.

### 4.4 Casdoor

**Architecture.** Go backend with React frontend. Single binary deployment.

**License.** Apache 2.0.

**Protocols.** OIDC, OAuth 2.0, SAML, LDAP server + client, SCIM, CAS, WebAuthn. Surprisingly feature-rich.

**Storage.** MySQL, PostgreSQL, SQL Server, Oracle, CockroachDB.

**MFA.** TOTP, SMS, email.

**Multi-tenancy.** First-class organisations and applications.

**Admin UI.** Functional but less polished than ZITADEL or Authentik.

**Technical Pros:**
- Apache 2.0, full feature set including SAML/LDAP/SCIM at zero cost
- Multi-tenant by design
- Lightweight Go deployment

**Technical Cons:**
- **Community is heavily Chinese-developer-led** — docs sometimes thin in English, GitHub issues often in Chinese. For an India-based team, this is friction.
- Smaller English-speaking community than Keycloak, ZITADEL, Authentik
- Less production track record outside East Asia
- Some features feel like checkboxes rather than fully polished

**Verdict for Sangam:** technically credible but the community/documentation gap makes operations harder for an Indian team.

---

## 5. Deep Dive — Other-Stack Options

### 5.1 Authentik

**Architecture.** Python (Django) backend with TypeScript/Lit frontend. Multi-process: server + worker + PostgreSQL. (Note: Redis was dropped in v2025.10 in favour of Postgres-based task processing.)

**License.** **MIT** for the core, with a separate paid Enterprise tier for advanced features (RAC, audit log retention extensions, etc.).

**Protocols.** OIDC, OAuth 2.0, SAML 2.0, LDAP server, LDAP client, RADIUS, SCIM, Kerberos via proxy. Most protocol-rich after Keycloak.

**Storage.** PostgreSQL.

**MFA.** TOTP, WebAuthn, Duo, SMS, email.

**Multi-tenancy.** Possible via flows and policies; not as first-class as ZITADEL or Keycloak realms.

**Admin UI.** **Standout feature: the Flow Builder.** Auth flows are visually composed in the admin UI from stages, policies, and providers. Very approachable.

**Customisation.** Flow Builder for most needs; Python for deeper extensions. Policies can be expressed in Python expressions.

**Deployment.** Docker Compose or Kubernetes (Helm). Multiple containers (server, worker). ~400-600 MB RAM total.

**Technical Pros:**
- Best UX for building custom authentication flows
- MIT-licensed core
- Strong protocol coverage (LDAP server is a standout)
- Active community, friendly maintainers
- Particularly good for environments mixing SaaS apps with legacy non-SSO services (proxy mode)

**Technical Cons:**
- **Python + Django + worker process** = bigger surface than Go or .NET
- Breaking changes reported in major upgrades (2025.4, 2025.12, 2026.2)
- Multi-tenancy less mature than Keycloak/ZITADEL
- Python customisation in a .NET shop = context switch

### 5.2 FusionAuth Community Edition

**Architecture.** Java application. Single binary or Docker.

**License.** Custom: free for self-hosting up to "reasonable use" thresholds (10,000 MAU on Community Edition for most features). Commercial tiers above that. **Not open-source under OSI definition** — be careful.

**Protocols.** OIDC, OAuth 2.0, SAML 2.0, LDAP, SCIM, FAPI.

**Multi-tenancy.** First-class (tenants).

**Admin UI.** Excellent — arguably the most polished admin console of any option here.

**Technical Pros:**
- Best out-of-the-box admin experience
- Comprehensive protocol support
- Multi-tenancy native

**Technical Cons:**
- **License is not true open source.** Restrictions on what you can do; commercial obligations above usage thresholds. For an open-source-committed platform, this is a values mismatch.
- JVM operational model
- For Sangam's specifically *open-source* stance, FusionAuth is philosophically misaligned

### 5.3 SuperTokens

Self-hostable, Apache 2.0 (core), with SDK-based integration model. More common as a single-app auth solution than as a multi-tenant central platform. Multi-tenancy exists but is less mature than ZITADEL/Keycloak. **Probably not the right shape for Sangam** — better suited to a single SaaS embedding auth than to a shared identity backbone.

### 5.4 Logto

Node.js-based, MPL 2.0. Modern UX, fast-growing. Hosted SaaS plus self-host. Multi-tenant by design. **Newer (2022) — less production proven** than Keycloak or even ZITADEL. Worth watching, probably not yet the bet for a 10-year platform commitment.

---

## 6. Decision Matrix — Which Wins on Which Axis

| Axis | Winner | Why |
|---|---|---|
| **Smallest footprint** | Authelia / Ory Hydra | ~30-60 MB RAM |
| **Most protocols supported** | Keycloak | OIDC + SAML + LDAP + SCIM + WS-Fed + everything |
| **Best multi-tenancy** | ZITADEL (then Keycloak) | Organisations are first-class data model |
| **Best admin UI out of the box** | FusionAuth / ZITADEL / Authentik | Modern, polished, no extra build |
| **Best .NET integration** | OpenIddict / Duende | Native ASP.NET Core, idiomatic C# |
| **Most permissive licence** | OpenIddict / Keycloak / Ory / Casdoor / Authelia | MIT or Apache 2.0, no strings |
| **Highest FAPI / banking-grade compliance** | Duende Enterprise / Keycloak | FAPI 2.0, CIBA, deep mTLS |
| **Best documentation** | Duende | Genuinely tutorial-grade |
| **Best community/Q&A support** | Keycloak | Largest installed base |
| **Lowest total cost of ownership** (no licence, no hidden fees) | OpenIddict | MIT forever, no thresholds |
| **Best for visual flow composition** | Authentik | Flow Builder is a category-leading UX |
| **Best event sourcing / audit trail** | ZITADEL | Built-in by architecture |
| **Best long-term political neutrality** | OpenIddict / Keycloak | Pure OSS, no commercial overhang |

---

## 7. Scenario-Based Recommendations

### If Sangam stays C#/.NET-only and identity stays simple
**→ OpenIddict.** Smallest footprint, native fit, no licence fees ever.

### If Sangam needs SAML for enterprise customers of partner apps
**→ Keycloak.** SAML is native, multi-tenant via realms is excellent, free.

### If Sangam ever needs banking-grade FAPI / regulated finance compliance
**→ Duende Enterprise** (if you'll pay for it) **or Keycloak**.

### If your team has Go expertise and AGPL is acceptable
**→ ZITADEL.** Best multi-tenancy model, modern stack, beautiful APIs.

### If you want the best admin UX and don't mind the JVM
**→ FusionAuth** (commercially) **or Keycloak** (free).

### If "we will never depend on a vendor" is non-negotiable
**→ OpenIddict, Keycloak, Ory, Authentik, or Casdoor.** All Apache 2.0 / MIT, none with commercial gates.

### If you want a visual flow builder for non-technical configuration
**→ Authentik.** Unique strength.

---

## 8. Final Recommendation for Sangam — Reconfirmed

After this fuller comparison, **OpenIddict remains the best fit**, with **Keycloak as the meaningful secondary option to keep in mind.**

**Why OpenIddict wins for Sangam specifically:**

1. **Stack coherence.** Your team is .NET. Every other option (Keycloak/Java, ZITADEL/Go, Authentik/Python, Ory/Go) adds a second language to your operational stack. For a three-founder team, that cost is real.

2. **True MIT, no licence fragility.** OpenIddict has no revenue caps (Duende), no AGPL obligations (ZITADEL), no enterprise feature gates (Authentik, FusionAuth), no community-edition limits (Duende).

3. **Footprint matches your scale.** 150-250 MB RAM fits comfortably inside Oracle Cloud Free Tier or a tiny E2E VM. Keycloak's 600-900 MB would force you up an instance size immediately.

4. **You wanted a custom Blazor admin UI anyway.** OpenIddict's lack of bundled admin UI is a feature, not a bug, in your context — the admin UI is *also* your branded self-service portal, written once in your preferred stack.

5. **Migration paths remain open.** If Sangam grows into needing SAML or enterprise federation, you can stand up Keycloak alongside OpenIddict for those specific flows, or migrate. OIDC's portability protects you either way.

**When to revisit this choice:**

- If a future partner app *requires* SAML SSO with their enterprise customers → add Keycloak alongside, or migrate.
- If you hit a feature gap in OpenIddict that's first-class in Keycloak (advanced authorisation services, fine-grained policies) → evaluate Keycloak.
- If multi-tenancy in your custom OpenIddict + Blazor build becomes architecturally painful → ZITADEL deserves a second look (with legal review on AGPL).

**Plan B if OpenIddict surprises you:** **Keycloak.** Not Duende — the licence ambiguity isn't worth it for a free shared platform. Not ZITADEL — AGPL is a no-go for your three companies' commercial apps unless you commit to never modifying it.

---

## 9. Honest Reality Check

A few uncomfortable truths worth saying:

- **No identity library is perfect.** Every choice involves trade-offs. The choice is less about "best" and more about "best fit for our specific shape."

- **Migration is rarer than people think.** Most teams pick an IdP and keep it for 5-10 years. The cost of choosing slightly wrong is usually less than the cost of analysis paralysis. Pick, ship, iterate.

- **The bigger risk is not the IdP — it's how you design your own data model on top of it.** Multi-tenancy mistakes, role-model mistakes, consent-model mistakes — these are where projects fail. Spend your design time there.

- **The library is roughly 20% of the work.** The other 80% is your admin UI, your integration SDK, your operational runbooks, your migration tools, your support processes, your data model. None of these are decided by the choice of OpenIddict vs Keycloak.

---

*End of auth library technical comparison.*
