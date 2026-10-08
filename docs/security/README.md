# Security evidence (R7, v1.0.0-rc.1)

Sangam's own security work, ready for an outside tester and an auditor. None of it is a certification or a
penetration test: those are commissioned by the founder.

| File | What it is |
|---|---|
| [asvs-l2.md](asvs-l2.md), [asvs-l2.csv](asvs-l2.csv) | OWASP ASVS 4.0.3 Level 2: all 259 requirements, each with its status, the code or document that shows it, and a note. Mirrored in SGM-503. |
| [zap.md](zap.md) | OWASP ZAP baseline and authenticated full scans of the four hosts, before and after the R7 fixes. |
| [dependency-scan.md](dependency-scan.md) | How every dependency and image is scanned on every pull request (the `security` CI job), and the R7 results. |
| [pentest-scope.md](pentest-scope.md) | Scope, rules of engagement, test accounts, method and deliverables for the external penetration test (SGM-506). |

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
