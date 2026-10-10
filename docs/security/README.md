# Security evidence (R7, v1.0.0-rc.1; updated to rc.5)

Sangam's own security work, ready for an outside tester and an auditor. None of it is a certification or a
penetration test: those are commissioned by the founder.

| File | What it is |
|---|---|
| [asvs-l2.md](asvs-l2.md), [asvs-l2.csv](asvs-l2.csv) | OWASP ASVS 4.0.3 Level 2: all 259 requirements, each with its status, the code or document that shows it, and a note. Mirrored in SGM-503. |
| [zap.md](zap.md) | OWASP ZAP baseline and authenticated full scans of the four hosts, before and after the R7 fixes. |
| [dependency-scan.md](dependency-scan.md) | How every dependency and image is scanned on every pull request (the `security` CI job), and the R7 results. |
| [pentest-scope.md](pentest-scope.md) | Scope, rules of engagement, test accounts, method and deliverables for the external penetration test (SGM-506). |

## Automated checks (rc.2)

| Check | When | Fails on | Required to merge |
|---|---|---|---|
| `security` (CI) | every pull request and push to main | a high or critical finding in a dependency, the repository or the identity image | yes |
| `migrations` (CI) | every pull request and push to main | an entity change without its migration, in Sangam's database or the imagiQa sample's | yes |
| CodeQL (`codeql.yml`, `security-extended` since rc.4) | every pull request, push to main, and weekly | through the ruleset's "Require code scanning results": a high or critical security alert | yes |
| Code-quality review (`tools/security/codeql-quality.sh`, rc.4) | once per release, by hand | nothing by itself: each finding is fixed or recorded below | before each release |
| `accessibility` (`nightly.yml`) | every night, and by hand | any WCAG 2.2 AA violation, policy violation or broken live connection, in three languages | no (nightly) |
| `zap-baseline` (`nightly.yml`) | every night, and by hand | any FAIL from the ZAP baseline scan of a host | no (nightly) |
| Dependabot (`dependabot.yml`) | weekly, and at once for an advisory | opens pull requests for NuGet, npm, pip, Maven, GitHub Actions and Docker updates | each runs the full CI |

## Code scanning: the first analysis (rc.3)

CodeQL's first run on main (rc.2) raised 35 alerts. None was in the hosts' own C# code. rc.3 fixed 29 (see the
CHANGELOG) and stopped analysing `docs/design` (3, design prototypes that are never served). Three are dismissed
on GitHub with these reasons:

| Alert | Where | Dismissed as | Why |
|---|---|---|---|
| `js/file-access-to-http` | `sdk/js/packages/node/src/audit.ts` | Won't fix | Reading the audit buffer from disk and sending it to Sangam over HTTPS is what the audit recorder is for. The buffer holds the application's own audit events. |
| `js/stack-trace-exposure` | `sdk/js/packages/node/test/flow.test.ts` | Used in tests | A test's stand-in identity provider; it is never shipped or run in production. |
| `js/xss-through-exception` | `sdk/js/packages/node/test/flow.test.ts` | Used in tests | The same test server. |

New alerts on a pull request fail it through the ruleset (high or critical security alerts). Review the Security
tab after each weekly run: new queries can find old code.

## Code scanning: rc.4

The full alert list after rc.3 (214 entries) held one critical alert in the hosts' own code, one medium, a Java
alert, and about 170 code-quality notes, many of them in compiler-generated code under `obj/`.

- **Fixed:** the critical `cs/xml/xpath-injection` in the SAML signature check (#36), the Java timestamp parse
  (#35), two `cs/constant-condition` notes in the SAML endpoint (#200, #201), and the quality findings that were
  real faults (a certificate leaked on each SIEM reconnect, certificates left undisposed when start-up fails). See
  the CHANGELOG.
- **The gate now runs `security-extended`.** The quality notes leave the Security tab after the first analysis of
  main with rc.4, and `obj/` is no longer analysed.
- **Code quality is reviewed once per release** with `tools/security/codeql-quality.sh` (the `security-and-quality`
  suite, every language). The founder or Claude runs it before tagging; its summary is read in full.

Dismissed on GitHub after rc.4, with these reasons:

| Alert | Where | Dismissed as | Why |
|---|---|---|---|
| #37 `cs/xml/missing-validation` | `src/Sangam.Identity.Infrastructure/Saml/SamlProtocol.cs` (`Load`) | Won't fix | SAML messages are not validated against the XML schema, by design: Sangam reads only the elements it needs and checks each one. The reader prohibits DTDs, has no resolver (no external entities) and caps the size; the signature is verified before any value is trusted. |
| #30 `js/file-access-to-http` | `sdk/js/packages/node/src/audit.ts` | Won't fix | As in rc.3: sending the audit buffer to Sangam is the recorder's purpose. |
| #23, #22 `js/stack-trace-exposure`, `js/xss-through-exception` | `sdk/js/packages/node/test/flow.test.ts` | Used in tests | As in rc.3: a test's stand-in identity provider. |

rc.4's own quality review (C#, after the fixes) left these, none of them a fault:

| Rule | Count | Why it stays |
|---|---|---|
| `cs/path-combine` | 43 | `Path.Combine` drops its first part when a later part is absolute. Every call joins a configured directory with a name Sangam builds itself (or a test's temporary folder); none takes a name from a request. |
| `cs/linq/missed-where`, `cs/linq/missed-select` | 24 | Style: a `foreach` with an `if` could be a LINQ `Where`. The loops are kept where they read more plainly. |
| `cs/useless-upcast` | 17 | `(string?)null` and `(Guid?)null` in tuples, ternaries and anonymous objects, where the cast names the type. |
| `cs/local-not-disposed` | 6 | Disposed through `await using (x.ConfigureAwait(false))`, in a `finally`, or returned to the caller; CodeQL does not follow these. |
| `cs/complex-condition` | 3 | Validation checks that read as one rule. |
| `cs/catch-of-all-exceptions` | 1 | The account purge logs and carries on with the next account; marked with a pragma and a comment. |
| `cs/static-field-written-by-instance` | 1 | `AccountService`'s dummy hash for timing-safe sign-in, computed once; writing it twice is harmless. |
| `cs/equality-on-floats` | 1 | A comparison with an exact sentinel value, not a computed one. |
| `cs/xml/missing-validation` | 1 | #37 above. |

JavaScript held only the three alerts above; Python, Java and the workflows were clean.

## ASVS decisions (rc.5)

The founder decided the six requirements that were Not met on 10 October 2026 (A1 to A6). rc.5 builds them:

| Decision | Requirement | Now |
|---|---|---|
| A1: 12 characters, no character-type rules (organisations may opt in); breached-password check online by k-anonymity with a built-in 10,000-password fallback | V2.1.1, V2.1.9, V2.1.7 | Met |
| A2: a pepper, as Argon2id's secret input, in a secret file; one offline copy with the founder | V2.4.5 | Met |
| A3: `__Host-` session and anti-forgery cookies, now | V3.4.4 | Met |
| A4: consent first — the API adds only existing users; others are invited | V4.2.1 | Met (pairwise ids still to discuss) |
| A5: logos scanned by Anjal's ClamAV; refused while it does not answer | V12.4.2 | Met |
| A6: keys as files on the VM for the pilot and controlled launch, compensating controls in SGM-908 | V6.4.2, V6.4.1 | Accepted; review before the public launch |

ASVS L2 now: 161 met, 75 partly, 1 not met (accepted), 8 for the founder, 14 not applicable ([asvs-l2.md](asvs-l2.md)).

Related:

- [../siem.md](../siem.md): streaming the audit log to a SIEM.
- [../evidence-packs.md](../evidence-packs.md): evidence for connected applications' own audits.
- [../accessibility.md](../accessibility.md): WCAG 2.2 AA in three languages.
- [../../deploy/kubernetes/README.md](../../deploy/kubernetes/README.md): the high-availability path.
- [../go-live-checklist.md](../go-live-checklist.md): the founder's decisions and actions ("Added by R7").

The ISMS drafts are in the document set, not here:

- SGM-907, statement of applicability;
- SGM-908, risk assessment and treatment plan;
- SGM-909, core policies.

To report a vulnerability, see [SECURITY.md](../../SECURITY.md).
