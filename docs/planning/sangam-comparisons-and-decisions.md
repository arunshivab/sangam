# Sangam — Comparisons & Decisions Companion Doc
## Strategic Document — Supporting Material

**Reads alongside:** `sangam-strategic-document-v0.1.md`
**Purpose:** Detailed comparisons on the six items raised in the founder review.
**Note on prices:** All prices are May 2026 list prices. Cloud and SaaS prices change; treat as ±15%.

---

## 1. Domain Extensions for "Sangam"

You're right that `.com` is mostly gone for short common Sanskrit/Hindi names — they got swept up years ago. Here's the practical ranking for a platform like Sangam.

### Recommended (in order)

**`.in`** — *Strongly recommended.*
- Indian sovereign TLD, perfect identity signal for an India-based, DPDPA-compliant platform serving Indian healthcare and construction companies.
- ~₹500-800/year. No restrictions for registration.
- High availability for common names compared to `.com`.

**`.co.in`** — Backup option.
- Also Indian. Slightly older / more business-oriented feel.
- ~₹500-800/year.
- Use if `.in` is taken.

**`.org`** — *Strong fit for the open-source angle.*
- "Org" signals non-commercial / community / foundation. Aligns with Apache 2.0 + joint open-source ethos.
- ~₹1,000-1,500/year.
- Very strong choice if you envision public contributors.

**`.dev`** — Developer-facing TLD.
- Google-owned, HTTPS-mandatory (free SSL trust signal baked in).
- ~₹1,200-1,800/year.
- Good if the integration audience (the three apps and any future partner developers) is the primary audience.

### Acceptable

**`.io`** — Tech startup default. Expensive (~₹3,000-5,000/year) and a bit overused. Skip unless others unavailable.

**`.tech`** — Newer, often available, ~₹800-2,500/year. Acceptable but less recognized.

**`.app`** — Google-owned, HTTPS-mandatory. ~₹1,500-2,500/year. Good fallback.

### Avoid

**`.xyz`, `.online`, `.site`, `.one`** — Cheap (~₹100-500/year) but look spammy. Especially bad for healthcare-adjacent platforms where trust matters.

**`.co`** — Colombian TLD that masquerades as ".company". Confusing, expensive. Skip.

### Suggested approach

Try in this order and grab the first 2-3 that are available so nobody else squats them later:

1. `sangam.in` / `sangam.org`
2. `sangamid.in` / `sangam-id.in` (id = "identity")
3. `sangamauth.in`
4. `usesangam.in` (the "use[product]" pattern is common — usefathom, useblossom, etc.)
5. `sangamhq.in` (hq = headquarters, common for platform brands)
6. `getsangam.in`

Register the top choice on `.in` AND `.org` together — costs less than ₹2,000/year combined and prevents future confusion or impersonation.

---

## 2. PostgreSQL — One Instance for All Apps?

**Short answer: yes, technically fine, and DPDPA-compliant — but with conditions, and only sensible for early stage.**

### What DPDPA actually requires

DPDP Rules 2025 (notified November 2025) require "reasonable security safeguards" — specifically:

- Encryption of personal data at rest and in transit
- Access controls (only authorised personnel)
- Logical separation of data between purposes
- Audit logs of access and modifications
- Documented breach response process

**DPDPA does not mandate physical database separation.** It mandates *effective* separation. Logical separation via separate databases within one PostgreSQL instance, with separate users/roles per database and no cross-database access, satisfies the requirement.

### The recommended setup

One PostgreSQL 16 instance, with separate logical databases:

```
PostgreSQL Instance
├── sangam_identity      ← owned by Sangam entity, user: sangam_user
├── app_a_his            ← owned by Company A,    user: app_a_user
├── app_b_compliance     ← owned by Company B,    user: app_b_user
└── app_c_nucmed         ← owned by Company C,    user: app_c_user
```

Rules to enforce:
- Each database user has access **only** to its own database. No `SUPERUSER`, no cross-database `GRANT`s.
- Connection strings stored as secrets per app — no app can read another app's connection string.
- Encryption at rest (disk-level) and in transit (TLS).
- Daily backups taken at the *instance* level, restored selectively if needed.
- Audit logging via `pgAudit` extension, retained 1-5 years per data category.

This setup is widely used and DPDPA-defensible.

### What you give up

**Single point of failure.** Instance down = all four databases down. For 1,000 users in Year 1 this is acceptable. As Companies A/B/C onboard paying customers, you'll want to separate the *commercial app* databases from the *identity* database, possibly:

- One PG instance for Sangam (shared, lower-risk identity data)
- Each company runs its own PG for their app's domain data on their own infrastructure

**Resource contention.** If App C runs an expensive query, App A's query slows down. At low scale, irrelevant. At scale, painful.

**Coordination tax.** Maintenance windows, version upgrades, backups, and security patches affect all four databases. Requires the three founders to agree on downtime windows — small but real governance overhead.

**Trust requirement.** Whoever has DB instance admin access can technically read any of the four databases (though not via the app users you defined). For three friends starting out, fine. As partners or employees join, this needs hardening — encryption at the app layer for sensitive columns, key management separated from DB admin, etc.

### Recommendation

**For Year 1-2 (under ~5,000 users across all apps):** Single PostgreSQL instance, four logical databases, separate users. Hosted on whoever owns the joint cloud account (Sangam entity). Each company connects via their own DB user.

**For Year 3+ or when any one app gets > 1,000 active paying users:** Migrate that app's database to its own dedicated instance owned by that company. Sangam identity stays where it is.

**Always-on, from Day 1:**
- Encryption at rest (disk-level on the cloud provider, automatic)
- TLS for all connections, no cleartext
- `pgAudit` enabled
- Daily backups, weekly restore drills
- Separate Postgres user per database, principle of least privilege

---

## 3. Auth Library Comparison

I've narrowed this to the five realistic options for a .NET-based shared identity platform. SaaS options (Auth0, Clerk, etc.) are excluded — they make sense for single apps, not for an open-source shared backbone you control.

### Comparison table

| Library | License | Cost | .NET-native | Maturity | Customisable | Operational Burden | Verdict |
|---|---|---|---|---|---|---|---|
| **OpenIddict** | MIT | ₹0 forever | Yes | High (since 2014) | Very high | Medium (you build admin UI) | **Recommended primary** |
| **Duende IdentityServer** (Community Edition) | Source-available + Community licence | ₹0 while < $1M USD revenue, max 4 clients | Yes | Very high | Very high | Medium | Strong alternative |
| **Duende IdentityServer** (Starter) | Commercial | $1,500/yr (~₹1.25 lakh/yr), 2 clients | Yes | Very high | Very high | Medium | Avoid for our case |
| **Keycloak** | Apache 2.0 | ₹0 forever | No (Java) | Very high | High | High (separate JVM stack, more memory) | Skip — stack mismatch |
| **ASP.NET Core Identity alone** | MIT | ₹0 forever | Yes | Very high | Medium | Low | Skip — not OIDC out of the box |

### Detail by option

**OpenIddict** — *Recommended*

Pros:
- MIT licensed, no revenue thresholds, no client-count limits, no future surprise pricing.
- Pure .NET 8/9. Integrates with ASP.NET Core Identity for user management out of the box.
- Excellent documentation, active maintenance, OIDC-certified.
- Used by Microsoft samples, popular in .NET community.
- You're never at the mercy of a vendor changing pricing or licence terms.

Cons:
- You build the admin UI (Blazor) yourself. No off-the-shelf admin console.
- Slightly more wiring required upfront vs Duende's batteries-included approach. Maybe 1-2 weeks extra in v0.
- Smaller ecosystem of paid support vs Duende.

**Duende IdentityServer — Community Edition**

Pros:
- Same engine as their paid product. Same features as Enterprise. Free.
- Best documentation of any .NET auth library, by a margin.
- Free admin UI partner (RSK AdminUI Community Edition) — saves you building it.
- Direct line to the original IdentityServer4 team if you ever need consulting.

Cons:
- **The hard cap is the issue.** Free only if your organisation revenue is under $1M USD/year. The ambiguity: whose revenue counts? Sangam entity? Each of the three founding companies separately? Duende's terms say "you" qualify if your organisation makes < $1M. If Sangam is its own legal entity with no revenue (it's free to users), Sangam qualifies forever. But this needs a written clarification from Duende, in writing, before you commit. If they later decide your three companies' combined revenue counts, you owe back-licensing fees.
- 4-client limit on Community Edition. You have three apps. Adds zero headroom for a fourth partner without paying.
- License validation happens at runtime — software phones home logically (the validation is local, but warnings appear if out of compliance). Small ideological mismatch with "fully open source."
- If you exceed $1M or 4 clients, you upgrade to paid: **$1,500/yr (Starter, 2 clients), $6,000/yr (Business, 10 clients), $18,000/yr (Enterprise, 25 clients)**. The Starter tier wouldn't even fit you (only 2 clients vs your 3 apps).

**Duende — Paid tiers**

Skip unless you absolutely need a specific Duende-exclusive feature. The Business tier at $6,000/year (~₹5 lakh) is excessive for a free-to-user platform whose total infra costs would otherwise be ₹30-50k/year. The economics don't work.

**Keycloak**

Pros:
- Fully Apache 2.0, no limits ever.
- Battle-tested at massive scale (Red Hat customers, governments, banks).
- Most feature-complete option — has everything: federation, SAML, LDAP, social login, custom flows, themes.
- Free admin UI built in.

Cons:
- **Java stack.** Adds JVM operational complexity to an otherwise .NET shop.
- Memory-hungry (1-2 GB JVM minimum even at idle, vs OpenIddict's ~200 MB).
- Customisation requires either Keycloak SPI plugins (Java) or external services. Awkward in a .NET-native team.
- Two stacks to maintain, monitor, upgrade.

Verdict: technically excellent, but stack mismatch makes it the wrong choice given your C#/.NET preference.

**ASP.NET Core Identity alone**

Not an OIDC server. It's a user management library. You'd be writing your own protocol layer on top, which is exactly what you should not do — auth protocols are subtle and one mistake creates security holes. Use it *underneath* OpenIddict or Duende, not instead of them.

### Final recommendation on auth library

**Primary choice: OpenIddict + ASP.NET Core Identity.**

Reasoning:
- MIT-licensed, no future commercial surprise.
- Fully .NET-native, no stack split.
- Sufficient maturity and community for production use.
- Building the admin UI is work you'd partially do anyway (your branded self-service UI).
- Worst case if you outgrow it: you can migrate to Keycloak or Duende later. Migration is real work but not catastrophic — the OIDC standard means your apps are loosely coupled.

**Hedge:** start a development spike on Duende Community Edition in parallel for the first month. If OpenIddict feels too sparse on admin tooling, switch. The OIDC layer is portable; only your custom admin UI would need rework.

---

## 4. Cloud Provider Comparison

Constraints:
- **Data residency:** India only (DPDPA + healthcare data + Indian customers)
- **Scale:** 1,000 users Y1, 2,000 Y2, modest growth Y3+
- **Stack:** Linux + Docker + PostgreSQL + ASP.NET Core
- **Budget sensitivity:** very high

### Cloud comparison table

| Provider | India region(s) | Lowest viable VM | ~Monthly cost | Pros | Cons |
|---|---|---|---|---|---|
| **Oracle Cloud (OCI) Free Tier** | Mumbai, Hyderabad | 2× Ampere A1 (ARM), 24 GB combined RAM | **₹0** (Always Free) | Free forever within limits; mature; in India | Onboarding bureaucracy; ARM architecture; account suspension risk if rules misread |
| **Azure** | Central India (Pune), South India (Chennai), West India (Mumbai) | B2s (2 vCPU, 4 GB) | ~₹2,500-2,800 | Industry standard; great .NET tooling; mature India presence | Most expensive; complex pricing; egress costs |
| **AWS** | Mumbai, Hyderabad | t3.small (2 vCPU, 2 GB) | ~₹1,200-1,800 | Largest ecosystem; reliable | Complex pricing; less .NET-friendly than Azure |
| **GCP** | Mumbai, Delhi | e2-small (2 vCPU, 2 GB) | ~₹1,500-2,000 | Good engineering; clean console | Smaller .NET ecosystem; less Indian SI support |
| **E2E Networks** | Delhi NCR, Mumbai | C3.4GB (2 vCPU, 4 GB) | ~₹1,300-1,500 | Indian provider; INR billing (no FX risk); 30-50% cheaper than hyperscalers; DPDPA-aligned by design | Smaller ecosystem; fewer managed services; less ops automation |
| **DigitalOcean** | Bangalore | Basic 2 vCPU / 4 GB | ~₹2,000-2,200 ($24) | Simple pricing; clean UX; good for devs | Smaller India presence; USD billing |
| **Hetzner** | — | CX22 (2 vCPU, 4 GB) | ~₹420 (€4.59) | Cheapest globally | **No India region — DPDPA risk; rule out** |

### Detail by serious option

**Oracle Cloud Free Tier — the dark horse for v0**

Oracle's Always Free Tier in Mumbai includes:
- 2× Ampere A1 Arm-based VMs with combined 4 OCPU and **24 GB RAM** (split however you want)
- 2× AMD x86 VMs (1/8 OCPU, 1 GB RAM each — too small to use)
- 200 GB total block storage
- 10 GB object storage
- 10 TB outbound data transfer per month
- Free Autonomous Database (20 GB)

For your scale, **the Free Tier could literally cover everything in Year 1.** Two ARM VMs at 12 GB RAM each is comfortable for Sangam + supporting services.

Caveats:
- ARM architecture means some Docker images need ARM variants (most popular images now publish ARM builds, but check).
- Oracle's onboarding is more bureaucratic — credit card required (not charged), occasional manual verification.
- If Oracle ever decides your account violates their fair use, they can suspend. Low risk but non-zero.
- Move to paid before you outgrow the limits — paid tier is fine, similar to AWS/Azure pricing.

**Azure — the safe choice**

If you want zero surprises, deep .NET tooling integration, and India's biggest enterprise cloud presence, Azure is the obvious pick. Pune (Central India) data centre is closest for many Indian users. Cost-wise it's the most expensive of the realistic options, but the time saved on tooling familiarity for a .NET shop offsets some of that.

Reserved instances (1-year commit) cut costs ~30%. Worth doing after 6 months of stable load.

**E2E Networks — the Indian-pride pick**

INR billing, no foreign exchange risk, Indian data centre by design, support team in Indian timezones, prices 30-50% lower than Azure/AWS. The trade-off is fewer managed services (no equivalent of Azure App Service or AWS Fargate) and a smaller ecosystem. For Docker-on-VM deployment — which is your plan — that doesn't matter.

If the founder agreement values keeping the technology stack Indian-supported, E2E is the strongest fit.

**AWS / GCP**

Both are credible but neither has strong advantages for this specific workload over Azure or E2E in India. AWS Mumbai is mature; GCP Mumbai is solid. Pick if a founder already has account experience.

### Final recommendation on cloud

**Primary recommendation: start on Oracle Cloud Free Tier for v0 build and beta (months 1-6).**

- Costs: ₹0/month for compute, ₹0 for DB (Autonomous DB free) or ~₹0 for self-hosted PG on the same free VM.
- Risk is tiny at this stage — if Oracle suspends or you outgrow, you migrate to paid in a weekend. Docker portability is the safety net.
- You get to "pay zero" during the riskiest phase of the project (when you're still figuring out if you've built the right thing).

**Switch to paid tier when:**
- Combined RAM usage exceeds 18 GB sustained (75% of free tier)
- You need a managed service Oracle Free Tier doesn't include
- Reliability concerns push you to dedicated resources

**Migration target when leaving free tier:**
- *Option A:* Stay on Oracle paid tier — same provider, same VMs, just billed.
- *Option B:* Move to **E2E Networks** for INR-stable pricing and Indian support, especially if the joint entity is Indian-incorporated. This is my mid-term favorite for the long haul.
- *Option C:* Azure if any of you has strong existing Azure expertise or enterprise discounts.

**Skip:** AWS, GCP, DigitalOcean, Hetzner for this specific use case. Not worse, just no clear advantage.

---

## 5. Approximate Cost — Both Cloud Options

For comparison, I'm running the same workload (Sangam platform serving 1,000-2,000 users) on three setups: Oracle Free Tier, E2E Networks paid, and Azure paid. Excludes one-time setup, only run cost.

### Year 1 (1,000 users, 500 × 5min daily)

| Line item | Oracle Free Tier | E2E Networks | Azure India |
|---|---|---|---|
| Compute (1 VM, ~2 vCPU / 4 GB) | ₹0 | ₹1,350/mo | ₹2,600/mo |
| Storage (50 GB) | ₹0 (within 200 GB free) | ₹250/mo | ₹400/mo |
| Backup storage (Blob, ~10 GB) | ₹0 | ₹100/mo | ₹200/mo |
| Bandwidth | ₹0 (within 10 TB free) | ₹0 (typically generous) | ~₹200/mo |
| Domain (₹800/year, amortised) | ₹70/mo | ₹70/mo | ₹70/mo |
| SSL | ₹0 (Let's Encrypt) | ₹0 | ₹0 |
| Email (free tier) | ₹0 | ₹0 | ₹0 |
| Monitoring (free tier) | ₹0 | ₹0 | ₹0 |
| **Monthly total** | **₹70** | **₹1,770** | **₹3,470** |
| **Annual total** | **₹840** | **₹21,240** | **₹41,640** |
| **Per company / year** | **~₹280** | **~₹7,080** | **~₹13,880** |

### Year 2 (2,000 users, 300 full-day active)

| Line item | Oracle Free Tier | E2E Networks | Azure India |
|---|---|---|---|
| Compute (2 vCPU / 8 GB) | ₹0 (use 8GB of free 24GB) | ₹2,200/mo (C3.8GB) | ₹5,000/mo (B2ms) |
| Storage (100 GB) | ₹0 | ₹500/mo | ₹800/mo |
| Backup storage (~20 GB) | ₹0 | ₹200/mo | ₹400/mo |
| Bandwidth | ₹0 | ₹0 | ~₹400/mo |
| Domain | ₹70/mo | ₹70/mo | ₹70/mo |
| Monitoring (paid tier if needed) | ₹0 | ₹500/mo | ₹500/mo |
| **Monthly total** | **₹70** | **₹3,470** | **₹7,170** |
| **Annual total** | **₹840** | **₹41,640** | **₹86,040** |
| **Per company / year** | **~₹280** | **~₹13,880** | **~₹28,680** |

### Year 3+ steady state (3,000-5,000 users)

| Line item | Oracle (now paid) | E2E Networks | Azure India |
|---|---|---|---|
| Compute (2 vCPU / 8 GB) | ~₹3,500/mo | ₹2,200/mo | ₹5,000/mo |
| Storage (200 GB) | ₹600/mo | ₹1,000/mo | ₹1,600/mo |
| Backup + bandwidth + misc | ₹500/mo | ₹500/mo | ₹1,000/mo |
| Domain | ₹70/mo | ₹70/mo | ₹70/mo |
| Monitoring | ₹500/mo | ₹500/mo | ₹500/mo |
| **Monthly total** | **₹5,170** | **₹4,270** | **₹8,170** |
| **Annual total** | **₹62,040** | **₹51,240** | **₹98,040** |
| **Per company / year** | **~₹20,680** | **~₹17,080** | **~₹32,680** |

### Headline takeaway

| Stage | Cheapest | Per company / month |
|---|---|---|
| Year 1 | Oracle Free Tier | ~₹25/month |
| Year 2 | Oracle Free Tier | ~₹25/month |
| Year 3+ | E2E Networks | ~₹1,400/month |

Even the *expensive* option (Azure) at peak scale is ~₹2,700 per founder per month. This is pocket change relative to the apps' commercial revenue. **Don't over-optimise — pick what's operationally easiest, not what saves ₹500/month.**

---

## 6. Email Recommendation (Until Your Email Client Is Ready)

Since your own email client service is ~2 months away, you need a transactional email provider for: registration verification, password reset, login alerts, MFA codes (later), notifications.

### Realistic options

| Provider | Free tier | Pros | Cons |
|---|---|---|---|
| **Brevo** (ex-Sendinblue) | 300/day, 9,000/month | Generous free tier; SMTP and API; transactional + marketing in one; good deliverability India | Free tier brand label on emails (small) |
| **Resend** | 100/day, 3,000/month, 1 domain | Modern API; cleanest developer experience; React Email integration | Lower volume; newer (2023+) |
| **Amazon SES** | 200/day free (only if sending FROM EC2); ₹8 per 1000 thereafter | Very cheap at any volume; rock-solid | Manual sender verification; setup is fiddly; deliverability needs DKIM/SPF/DMARC tuning |
| **SendGrid** | 100/day free | Established; recognised brand | Increasingly aggressive about pushing paid tiers; deliverability has slipped in some regions |
| **Mailgun** | None free now (was 5K, removed) | Strong reputation | No free tier anymore |
| **Postmark** | 100 free for trial only | Best deliverability in industry | No production free tier |

### Recommendation

**Use Brevo as the primary, with Resend as the backup.**

Reasoning:
- Brevo's 300/day = 9,000/month free is comfortably more than your Year 1 usage. 1,000 users × maybe 1-2 emails per month per active user = 500-2,000/month. Easy fit.
- Brevo supports both SMTP and HTTP API; works seamlessly with MailKit (your .NET stack).
- Indian deliverability is good — they have Mumbai infrastructure.
- Decent admin UI to see what's been sent and bounced.
- When your own email client launches in 2 months, swap by changing the SMTP host config — no code changes.

**Setup tasks (one-time, ~30 minutes):**
- Sign up Brevo with the Sangam domain email
- Set up SPF, DKIM, DMARC records on the domain (Brevo provides exact values)
- Test delivery to Gmail, Outlook, and a corporate domain (your three companies)
- Wire up MailKit in ASP.NET Core with Brevo SMTP credentials in env vars

**When your email client is ready:** drop Brevo, point SMTP config at your service. Zero downtime if done during low-traffic window.

---

## Summary of Decisions to Take to Your Friends

| Decision | My recommendation | Why |
|---|---|---|
| Domain | `sangam.in` + `sangam.org` together | India sovereignty + open-source signal; ~₹2,000/year total |
| Database setup | One PostgreSQL instance, 4 logical DBs, separate users | DPDPA-compliant, lean, sufficient for ≤ 5,000 users |
| Auth library | OpenIddict + ASP.NET Core Identity | MIT-licensed, .NET-native, no revenue cap, no client cap |
| Cloud provider (v0) | Oracle Cloud Free Tier (Mumbai) | ₹0 cost during riskiest phase; Indian region |
| Cloud provider (long-term) | E2E Networks | INR billing, Indian support, 30-50% cheaper than hyperscalers |
| Email (until your service ships) | Brevo | 9,000 emails/month free, good Indian deliverability, swappable |

---

## What's NOT in this doc (still pending)

These are unchanged from the strategy doc's "Open Decisions" section and need founder discussion:

- Final platform name (we're using "Sangam" provisionally)
- Legal entity registration (LLP recommended)
- First app to integrate
- Tech lead for Year 1
- Lawyer engagement for founder agreement and T&C
- Independent advisor for tie-breaks

Once these are decided, the next doc will be the **founder agreement and T&C draft** — the legal scaffolding for the joint entity, exit clauses, and user consent language.

---

*End of comparison companion document.*
