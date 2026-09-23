# Sangam — Legal Scaffolding (Draft)
## Founder Agreement, User T&C, and Privacy Policy Foundations

**Version:** v0.1 (working draft)
**Date:** May 2026

---

## ⚠️ Important Disclaimer Before Anyone Reads Further

**I am Claude, an AI assistant. I am not a lawyer. This document is not legal advice.**

This is a structured draft intended to:
- Capture the founders' intent in legible, well-organised form
- Save 60-70% of a lawyer's drafting time (and therefore their billing time)
- Surface the right questions to ask, not provide final answers

**Mandatory next step:** Take this draft to a qualified Indian lawyer with expertise in (a) technology partnerships, (b) data protection (DPDPA), and (c) healthcare data, before signing anything. Budget ₹40,000-1,50,000 for proper legal review and refinement. This is non-negotiable for an arrangement involving three companies, joint IP, healthcare-adjacent data, and user consent flows.

Anywhere you see [SQUARE BRACKETS] in this document, that's a placeholder you need to fill in or confirm with your lawyer.

---

# PART A — Founder Agreement / Memorandum of Understanding (MoU)

This is the agreement between the three founding partners and the joint entity they form.

## 1. Preamble and Parties

This Founder Agreement ("Agreement") is entered into on [DATE] between:

1. **Founder A:** [Full Name], [Designation], representing [Company A Legal Name], a [entity type] incorporated under the laws of India, having its registered office at [Address] ("Founder A")
2. **Founder B:** [Full Name], [Designation], representing [Company B Legal Name], a [entity type] incorporated under the laws of India, having its registered office at [Address] ("Founder B")
3. **Founder C:** [Full Name], [Designation], representing [Company C Legal Name], a [entity type] incorporated under the laws of India, having its registered office at [Address] ("Founder C")

Collectively referred to as the "Founders" or "Founding Partners."

## 2. Recitals

WHEREAS:

a. The Founders each operate distinct commercial software products in [list domains — healthcare information systems, compliance management, project management, nuclear medicine, etc.];

b. The Founders have agreed to jointly create, own, and operate a shared identity and tenancy software platform ("the Platform" or "Sangam") that will serve as the authentication and organisational backbone for the Founders' respective commercial applications;

c. The Platform will be developed and distributed as open-source software under the Apache License 2.0;

d. The Platform itself will not be monetised; each Founder will monetise their respective commercial application(s) independently;

e. The Founders wish to formalise their respective rights, obligations, contributions, and the governance of the Platform through this Agreement.

NOW THEREFORE, the parties agree as follows.

## 3. Definitions

- **"Platform" / "Sangam"** means the shared open-source identity and tenancy software platform jointly built and operated by the Founders, including all source code, documentation, configuration, deployment scripts, and associated intellectual property.
- **"Joint Entity"** means the legal entity formed by the Founders to hold ownership of the Platform's intellectual property, infrastructure assets, and operational accounts. Recommended form: **Limited Liability Partnership (LLP) registered under the LLP Act, 2008**.
- **"Partner Application"** means a software application owned by any Founder (or by a future admitted Partner) that integrates with the Platform for identity and organisational services.
- **"End User"** means any individual who registers an account with the Platform via any Partner Application.
- **"Identity Data"** means the data held by the Platform about End Users, including authentication credentials, profile information, organisational memberships, and consent records.
- **"Domain Data"** means data held by individual Partner Applications relating to their specific commercial domain (clinical records, project tasks, compliance documents, etc.). Domain Data is NOT held by the Platform.

## 4. Purpose of the Joint Entity

4.1 The Joint Entity is formed solely to:
- Own the intellectual property of the Platform
- Hold the Platform's hosting, domain, and operational accounts
- Receive contributions from the Founders for Platform operating costs
- Enter into contracts necessary for Platform operations (cloud hosting, legal, audit)
- Distribute any incidental revenue (if any) back to the Founders pro-rata

4.2 The Joint Entity shall NOT engage in:
- Direct sale of services to End Users
- Marketing or sales activities for the Partner Applications
- Holding Domain Data
- Any activity outside the scope of operating the Platform

## 5. Capital Contribution and Cost Sharing

5.1 **Initial capital.** Each Founder shall contribute INR [AMOUNT — recommend ₹50,000 each] as initial capital to the Joint Entity for incorporation costs, initial hosting setup, and legal expenses. Total initial capital: INR [TOTAL].

5.2 **Operating costs.** All ongoing operating costs of the Platform (hosting, backups, monitoring, security audits, domain renewals, legal/compliance, etc.) shall be borne equally by the Founders, each contributing one-third (1/3).

5.3 **Cost review.** The cost-sharing ratio shall be reviewed annually by the Founders. The ratio shall be renegotiated if:

a. Any single Partner Application accounts for more than 60% of the Platform's identity records for two consecutive quarters; or

b. Any single Partner Application accounts for more than 60% of the Platform's authentication traffic for two consecutive quarters; or

c. Any Founder requests a review and at least one other Founder concurs.

5.4 **Unusual expenses.** Any single expense exceeding INR [AMOUNT — recommend ₹50,000] requires unanimous prior approval by all Founders.

5.5 **Failure to contribute.** If any Founder fails to make their cost contribution for a period exceeding [60] days after written notice, the other Founders may, by unanimous decision:

a. Cover the shortfall pro-rata and treat it as a loan owed by the defaulting Founder at [10]% annual interest;

b. Initiate the Exit Provisions (Section 9) with respect to the defaulting Founder.

## 6. Intellectual Property

6.1 **Ownership.** All source code, documentation, designs, configurations, trademarks, and associated intellectual property of the Platform shall be owned by the Joint Entity.

6.2 **Licence to Founders.** The Joint Entity grants each Founder a perpetual, worldwide, royalty-free, non-exclusive licence to:

a. Use the Platform for integration with their Partner Application(s);

b. Modify the Platform for their internal use;

c. Continue using any version of the Platform existing at the time of their exit (subject to Section 9).

6.3 **Open Source Licence.** The Platform's source code shall be distributed publicly under the **Apache License, Version 2.0**. The Joint Entity is the copyright holder. Founders consent to all their contributions to the Platform being licensed under Apache 2.0.

6.4 **Trademarks.** The name "Sangam" (or the chosen final name), logo, and associated brand assets are owned by the Joint Entity. Use by Founders for marketing the Partner Applications requires written consent from the Joint Entity and shall not exceed [factual mention of platform integration].

6.5 **Contributions from third parties.** Public contributions to the Platform repository under Apache 2.0 are governed by a Contributor Licence Agreement (CLA) or Developer Certificate of Origin (DCO) to be adopted by the Joint Entity within [90] days of repository public release.

## 7. Roles and Decision-Making

7.1 **Tech Lead.** The Founders shall, by rotation every [six months], appoint one of themselves as the **Tech Lead**. The Tech Lead has authority to make day-to-day technical decisions including:

a. Code reviews and merge approvals
b. Routine infrastructure changes (within budget)
c. Hotfixes and security patches
d. Minor roadmap re-prioritisation

7.2 **Decisions requiring unanimous consent.** The following matters require unanimous written consent of all Founders:

a. Adoption of a new programming language, database engine, or major framework
b. Changing the open-source licence (Apache 2.0) to anything else
c. Admitting a new Partner (see Section 8)
d. Removing a Partner Application from the Platform
e. Any expense exceeding INR [AMOUNT]
f. Modifying this Agreement
g. Dissolution of the Joint Entity
h. Selling, licensing, or transferring Platform IP to any external party

7.3 **Decisions requiring majority consent.** All other operational decisions require simple majority (2 of 3 Founders).

7.4 **Tie-breaking.** In the event of a deadlock that prevents the Platform's operations:

a. The Founders shall first attempt resolution through a single round of mediation, jointly facilitated by [INDEPENDENT ADVISOR NAME — to be appointed by mutual agreement].

b. If mediation fails within [30] days, the status quo shall prevail until resolved.

c. No party may unilaterally take action contrary to a deadlocked decision.

7.5 **Meeting cadence.** The Founders shall meet, in person or by videoconference, at least once per calendar month to review Platform operations, costs, and roadmap. Minutes shall be maintained and circulated within [5] business days.

## 8. Admitting New Partners

8.1 The Joint Entity may admit additional Partners (and their applications) onto the Platform subject to:

a. Unanimous written consent of existing Founders
b. The new Partner's execution of an accession agreement with terms substantively equivalent to this Agreement
c. Contribution of equal share of accumulated capital, OR a one-time joining contribution as agreed by existing Founders
d. Commitment to the cost-sharing arrangement under Section 5
e. Disclosure to End Users via re-consent notification (see Section 6 of Part B — User T&C)

8.2 Existing Founders are not obligated to admit any new Partner.

## 9. Exit Provisions

9.1 **Voluntary exit.** Any Founder may exit the Joint Entity by serving written notice to the other Founders. The notice shall specify the desired exit date, which shall be not less than [90] days from the notice date.

9.2 **Data taken on exit.** Upon exit, the exiting Founder may take with them:

a. Identity records of End Users who registered into the Platform **via** the exiting Founder's Partner Application(s), as evidenced by the `registered_via_app_id` field in the Identity database. **They do NOT take identity records of users registered via other Partner Applications.**

b. Organisation records that were created **via** the exiting Founder's Partner Application(s).

c. Audit logs pertaining to the exiting Founder's Partner Application(s).

d. The exiting Founder may NOT take or copy any data relating to End Users or Organisations registered via other Partner Applications.

9.3 **User notification.** Upon a Founder's exit:

a. End Users whose Identity Data is being taken shall receive notification not less than [60] days before transfer, detailing what data is being transferred, to which entity, and offering an option to refuse and have their data deleted from the exiting Founder's records.

b. End Users whose data remains on the Platform shall be notified that one Partner has exited.

9.4 **Platform code post-exit.** The exiting Founder retains the perpetual licence granted under Section 6.2. They may fork the public Apache 2.0 repository at any time and continue independent operation of their own platform instance.

9.5 **Liability allocation on exit.** The exiting Founder remains liable for:

a. Their share of accrued operating costs up to the exit date
b. Their share of any liabilities (regulatory penalties, breach damages, etc.) arising from events occurring before their exit date

9.6 **Involuntary exit.** A Founder may be removed from the Joint Entity by unanimous decision of the remaining Founders only on grounds of:

a. Material breach of this Agreement unremedied within [60] days of written notice
b. Insolvency, bankruptcy, or dissolution of the Founder's underlying company
c. Conviction of any offence involving fraud, dishonesty, or breach of trust
d. Action causing material damage to the Platform's reputation or operations

Involuntary exit follows the same data and code provisions as voluntary exit (Sections 9.2-9.5).

## 10. Confidentiality

10.1 Each Founder shall keep confidential all non-public information about the other Founders' Partner Applications, business strategies, customers, financials, and Domain Data, encountered in the course of operating the Platform.

10.2 This obligation survives termination of this Agreement and any Founder's exit, for a period of [3] years.

10.3 Standard exceptions apply: information that is publicly available, was independently known, is required by law to be disclosed, or is disclosed with prior written consent.

## 11. Liability Allocation

11.1 The Joint Entity carries its own liability for Platform operations. Each Founder's liability is limited to:

a. Their unpaid capital contributions
b. Their share of operating costs as agreed
c. Liabilities arising directly from their own gross negligence or wilful misconduct

11.2 No Founder is liable for the commercial operations, customer relationships, or product quality of another Founder's Partner Application.

11.3 The Joint Entity shall maintain professional indemnity / cyber liability insurance once annual revenue or scale exceeds [SCALE THRESHOLD — to be determined with insurance broker]. Premium costs shared equally.

## 12. Dispute Resolution

12.1 Any dispute arising under this Agreement shall first be addressed through good-faith negotiation between the Founders.

12.2 If negotiation fails within [30] days, the dispute shall be referred to mediation by [INDEPENDENT ADVISOR NAME].

12.3 If mediation fails within a further [30] days, the dispute shall be referred to arbitration under the Arbitration and Conciliation Act, 1996, with a sole arbitrator appointed by mutual agreement, or failing such agreement, by [APPOINTING AUTHORITY]. Seat of arbitration: [CITY — recommend Mumbai, Bengaluru, or Pune]. Language: English.

12.4 Courts of [CITY] shall have exclusive jurisdiction for any matter not subject to arbitration.

## 13. Governing Law

This Agreement is governed by and construed in accordance with the laws of India.

## 14. Term and Termination

14.1 This Agreement is effective from the date of execution and continues until terminated by:

a. Unanimous written agreement of all Founders to dissolve the Joint Entity
b. Reduction of Founders to fewer than [2] through exits
c. Order of a court or arbitrator

14.2 Upon termination, the Joint Entity's assets (including Platform IP) shall be dealt with as the Founders unanimously agree, failing which by arbitration. The default position: the Platform IP is donated to a recognised open-source foundation, ensuring continuity for the End Users.

## 15. Miscellaneous

15.1 **Entire agreement.** This Agreement, together with all schedules and exhibits, constitutes the entire understanding between the Founders.

15.2 **Amendment.** Amendments require written consent of all Founders.

15.3 **Severability.** If any clause is held invalid, the rest of the Agreement remains in effect.

15.4 **Notices.** Notices shall be sent by email with acknowledgement, AND by registered post or courier to the addresses above.

15.5 **No partnership beyond stated scope.** This Agreement does not create a partnership, joint venture, or agency relationship between the Founders' commercial businesses; it strictly governs the Joint Entity and the Platform.

**EXECUTED on the date first above written:**

____________________     ____________________     ____________________
Founder A                Founder B                Founder C

---

# PART B — End User Terms & Conditions (Draft)

These are the terms shown to End Users at registration on any Partner Application.

## 1. Acceptance

By creating an account on this application, you agree to:

a. These Terms & Conditions of the **Sangam Identity Platform** (the "Platform")

b. The Terms of Service of the specific Partner Application you are registering through ("Partner Application Terms")

c. The Privacy Policy of the Platform

If you do not agree, you may not create an account or use the application.

## 2. About the Platform

2.1 The Sangam Identity Platform is a **shared identity service** operated by [JOINT ENTITY NAME], a Limited Liability Partnership registered in India.

2.2 The Platform provides:

a. Your login credentials (email, password, etc.)
b. Your basic profile (name, contact details, verification status)
c. A record of which organisations you belong to
d. A record of which Partner Applications you have access to

2.3 The Platform does NOT hold:

a. Your medical records, clinical data, project data, or any domain-specific information held by a Partner Application
b. Your payment or billing information (held by individual Partner Applications)
c. Any information beyond what is needed for identity and access management

## 3. Current Partner Applications

At the time of your registration, the Platform serves the following Partner Applications:

- [Partner Application A — purpose] operated by [Company A]
- [Partner Application B — purpose] operated by [Company B]
- [Partner Application C — purpose] operated by [Company C]

A current and up-to-date list is always available at [https://sangam.in/partners].

## 4. Identity Sharing — Important Consent

4.1 By creating an account, you give consent for the Platform to share your basic Identity Data (name, email, phone, verification status, organisation memberships) with **the specific Partner Application you are registering through**.

4.2 Your Identity Data is **NOT** automatically shared with other Partner Applications. You will be asked for explicit consent each time you choose to use another Partner Application.

4.3 **Future partners.** If a new Partner Application is added to the Platform after your registration:

a. We will notify you the next time you log in
b. Your data will NOT be shared with that new Partner Application unless you explicitly opt in
c. You may always view the current list of partners at [https://sangam.in/partners]

## 5. Your Account

5.1 You must provide accurate, current, and complete information when registering.

5.2 You are responsible for safeguarding your password and for any activities under your account.

5.3 You must promptly notify us of any unauthorised access or use.

5.4 You may not:
- Create accounts for others without their permission
- Use false identity information
- Attempt to access another user's account
- Resell, transfer, or sublicense your account

## 6. Your Rights Under DPDPA

Under the Digital Personal Data Protection Act, 2023 ("DPDPA") and DPDP Rules 2025, you have the following rights with respect to your Identity Data held by the Platform:

6.1 **Right to access** — request a copy of your Identity Data and information about how it has been processed.

6.2 **Right to correction** — correct or update inaccurate Identity Data.

6.3 **Right to deletion ("erasure")** — request deletion of your account and Identity Data. We will comply within 30 days, subject to legal retention requirements (e.g., audit logs for healthcare-related identity events may be retained for 5 years as per applicable regulation).

6.4 **Right to withdraw consent** — withdraw your consent to share Identity Data with any specific Partner Application at any time, by visiting your account at [https://sangam.in/account].

6.5 **Right to grievance redressal** — raise concerns with our Data Protection Officer at [dpo@sangam.in].

6.6 **Right to nominate** — nominate another person to exercise your rights in the event of your death or incapacity.

To exercise any of these rights, write to [privacy@sangam.in] or visit your account settings.

## 7. Account Deletion

7.1 You may delete your Platform account at any time from your account settings.

7.2 **What happens when you delete:**

a. Your Identity Data on the Platform is deleted within 30 days, except for audit log entries which may be retained for legal compliance.

b. Your access to all Partner Applications via the Platform is revoked.

c. **Important:** Your data held by individual Partner Applications (e.g., your clinical records in a hospital information system) is NOT automatically deleted. You must request deletion separately from each Partner Application, as they are independent data controllers for their domain data.

7.3 Once deleted, your account cannot be recovered. You may register a new account with the same email after [30] days.

## 8. Service Availability

8.1 We aim to provide the Platform with high availability but make no guarantee of uninterrupted service.

8.2 We may suspend or restrict access for maintenance, security, or legal reasons, with reasonable notice where possible.

8.3 The Platform is provided "as is." To the maximum extent permitted by Indian law, we disclaim all warranties beyond those that cannot be excluded by law.

## 9. Liability

9.1 To the maximum extent permitted by law, our liability to you for any matter arising under these Terms is limited to ₹[1,000] in aggregate.

9.2 The Platform is not liable for the operations, services, content, or actions of any Partner Application. Your claims with respect to a Partner Application's services are governed by the Partner Application's own terms.

## 10. Indemnity

You agree to indemnify the Platform and its operating entity from any claim arising out of:

a. Your violation of these Terms
b. Your provision of false information at registration
c. Misuse of your account credentials due to your negligence

## 11. Changes to These Terms

11.1 We may modify these Terms with [30] days' prior notice via email and in-account notification.

11.2 If you do not agree with the changes, you may delete your account before the changes take effect. Continued use after the effective date constitutes acceptance.

## 12. Governing Law and Jurisdiction

These Terms are governed by the laws of India. Disputes shall be subject to the exclusive jurisdiction of the courts of [CITY].

## 13. Contact

Operating Entity: [JOINT ENTITY NAME, LLP]
Registered Address: [ADDRESS]
Grievance Officer / DPO: [NAME, EMAIL]
General Contact: [hello@sangam.in]

---

# PART C — Privacy Policy Foundations

This is a structural outline. The final Privacy Policy must be drafted in close consultation with a DPDPA-experienced lawyer.

## Essential Sections (per DPDPA Rules 2025)

1. **Identity of Data Fiduciary** — Name and contact of the Joint Entity; name of the Data Protection Officer (DPO).

2. **Description of Personal Data collected** — Email, phone, name, password (hashed), verification status, organisation memberships, IP address, device info, consent records, audit events.

3. **Purposes of Processing** — Specifically:
   - Authentication (login, password reset, MFA)
   - Identity verification across Partner Applications
   - Service security (rate limiting, fraud prevention)
   - Compliance with legal obligations
   - Audit and breach response

4. **Legal Basis** — Consent (primary) and legal obligation (for retention of audit logs).

5. **Data Sharing** —
   - With Partner Applications: only as per explicit user consent (Section 4 of Part B)
   - With cloud hosting provider [NAME]: as data processor, under DPDPA-compliant contract
   - With third-party services (email provider, etc.): as data processors with no marketing rights
   - **No** data sale, **no** marketing data sharing

6. **Cross-Border Transfers** — None as default; data is hosted in India. If ever exported (e.g., backup to non-Indian region), explicit notification and consent.

7. **Retention** —
   - Active Identity Data: retained while account is active
   - Audit logs: 5 years minimum (healthcare-linked), 1 year (other identity events)
   - Deleted-account remnants: purged within 30 days, except audit references with hashed pseudonyms

8. **Security Measures** — Encryption at rest and in transit, MFA for admin access, regular audits, principle of least privilege, breach response process.

9. **User Rights** — As listed in Section 6 of Part B.

10. **Children's Data** — The Platform is not intended for users below [18]. We do not knowingly collect data from minors. (Adjust to 14 / 16 / 18 based on Partner App requirements.)

11. **Breach Notification** — Procedure if a breach affecting user data is detected. Notify Data Protection Board of India within 72 hours; notify affected users without undue delay.

12. **Grievance Redressal** — DPO contact, complaint procedure, escalation to Data Protection Board.

13. **Updates** — How and when the Privacy Policy is updated.

14. **Effective Date** — Last revised on [DATE].

---

# PART D — Schedule of Items to Confirm with Your Lawyer

Items where I (Claude) have made placeholder assumptions or where Indian legal nuance requires expert input:

1. The exact structure and registration process for the LLP, and whether LLP is indeed optimal (vs. Section 8 company for non-profit signaling, or Private Limited Company)
2. Specific addresses, names, designations to fill in
3. Capital contribution amount (₹50,000 is illustrative)
4. The 60% trigger for cost-sharing review (could be 50% or 70%)
5. Notice periods throughout (60/30/90 days are common but adjustable)
6. Indemnity caps (₹1,000 user liability cap is conservative but legally defensible)
7. Minimum user age (18, 16, or 14 per Indian DPDPA interpretations for healthcare)
8. Specific arbitration mechanism (sole arbitrator, panel, seat, language)
9. Insurance threshold and provider
10. Data Protection Officer — can it be fractional/shared? At what scale becomes mandatory under DPDPA?
11. Whether your three companies' contracts with their own enterprise customers create any obligations on the shared Platform (e.g., audit rights flow-through)
12. Specific exit data definitions (registered_via_app_id is a technical proxy; legal phrasing may differ)
13. Healthcare-specific compliance overlay if any Partner App handles ABDM/ABHA data, NDHM integration, or hospital data
14. Tax treatment of cost contributions from companies to the LLP
15. GST applicability (likely yes on any commercial element)

---

*End of legal scaffolding draft.*

*This document and the next two (sprint plan, data model) form a single deliverable set. Read in sequence.*
