# rc.12 design: hospitals inside applications, their admins, and adding staff safely

Status: **design for review** (founder decisions of 14 October 2026). No code yet. Where this file and earlier design files
disagree about organisations, administrators or invitations, this file wins once approved.

## 1. Who is who

Sangam's contracts are with **applications** (LiPi, Medimaze), never with hospitals. A hospital is a **customer inside an
application**. Organisations therefore stay owned by the application, as today, with one new level: the **customer**, the
top organisation of a hospital's tree.

```
Sangam
├── LiPi                       application (contract with Sangam)
│   ├── Apulki Mumbai          customer (its own admin)
│   │   ├── Branch 1           branch (may have its own admin)
│   │   └── Branch 2
│   └── Armoki                 customer
│       ├── Krishnagiri
│       └── Shrirampur
└── Medimaze                   application
    └── Apulki Pune            customer
        ├── Branch A
        └── Branch B
```

Two applications never see each other's trees, and two customers in one application never see each other's.

The three ways a customer uses Sangam, all supported:

| Customer | Sangam holds | The application holds |
|---|---|---|
| Apulki Pune on Medimaze | Sign-in only | Branch, staff, roles |
| Apulki Mumbai on LiPi | Sign-in and which branch | Roles |
| Armoki on LiPi | Sign-in, which clinic, roles | Nothing about access |

A person has **one Sangam account** and may be staff at several customers at once, in several applications. Each
application and each customer sees only its own part.

## 2. Administrators

| Who | Created by | Sees and does |
|---|---|---|
| Sangam operator | Sangam owner | The platform (operator console, unchanged) |
| Application admin (owner / admin) | A Sangam operator at enrolment, or another application admin | The whole application: every customer, credentials, rules (partner console, unchanged) |
| **Customer admin** (new) | An application admin, or another admin of the same customer | One customer's tree: staff, roles, admins of its branches, sign-in rules, access reviews, audit trail |
| **Branch admin** (new) | A customer admin, or another admin of the same branch | One branch and anything under it |

Admin rights are **Sangam console rights**, kept separately from roles. "Administrator" as a role inside the HIS (managing
things in LiPi) is a role like any other and grants nothing in Sangam. A person can hold either, both or neither.

Customer and branch admins work either in a **Sangam organisation console** (new, scoped to their trees) or inside the
application, which calls Sangam's management API on their behalf with their identity. Every action is recorded under the
admin's own name either way.

- **In an application** (LiPi, Medimaze), the admin adds a person to that application's organisations only. Apulki's admin
  adds Arun in LiPi, then in Medimaze: two additions, one per application.
- **In the Sangam console**, the admin sees every tree in which they are an admin, across applications (LiPi's Apulki and
  Medimaze's Apulki), and may tick organisations in several at once, with a warning when more than one organisation is
  chosen ("Add Arun to 5 organisations?"). The person then gets access to each chosen application. Nothing outside the
  admin's own trees is ever shown.
- **Through the API**, each call adds one person to one organisation, with that organisation's roles. An application with
  a multi-select sends one call per organisation. The first call identifies or creates the person (with the check in 4.3)
  and returns their Sangam id; later calls send that id, not the details again, so there is one check, one code and one
  e-mail. Calls are independent (one failing leaves the others) and repeating a call changes nothing.

### Adding people needs DigiLocker (decision 2)

Only an admin whose own account is **verified with DigiLocker** may add a person or link an existing account. An admin
who is not verified keeps every other right (audit trail, sign-in rules, access reviews, ending roles) and asks a verified
admin above them to add the person. The rule applies all the way up: an application admin must be verified before creating
customer admins.

**Until DigiLocker is live** (MeitY onboarding, expected within about two months of October 2026), a Sangam operator may
switch on a temporary exception per application. While it is on, unverified admins of that application may add people;
each such addition is marked "added before DigiLocker". When DigiLocker goes live, admins have 30 days to verify, after
which unverified admins lose the right to add. Earlier additions stay valid and stay marked.

## 3. Memberships

- **One organisation per addition.** A head office ("Apulki Corporate") is an organisation like any branch and is added to
  the same way. Being in a head office gives nothing below it: a person is in every branch the admin chose, and only those.
  The "applies to everything below" switch is **removed** (decision of 14 Oct). Migration expands each existing membership
  that had it into one explicit membership per organisation below it, so nobody loses access they have today; branches
  opened later are never included automatically.
- **Several roles per person per branch** (doctor and administrator, say). Today Sangam allows one.
- **The role is optional.** A membership without a role means "staff at this branch"; the application decides what they may
  do.
- **Per customer: "Roles are kept in Sangam: yes / no".** When no (Apulki Mumbai), the consoles hide role choices, the API
  refuses a role, and access reviews ask about membership only.
- Roles still belong to the application and are shown as the application named them.

## 4. Adding a person

### 4.1 The person gives the admin a code (pull)

In the portal the person chooses **Join an organisation**. Sangam shows a short one-time code and a QR code, valid for a few
hours and for one use. The admin enters or scans it, in the console or in the application. That links exactly that account
(it is the person's own act), and they are added with the branch and roles the admin chose. This is the normal way for
anyone who already has Sangam.

### 4.2 The admin creates the person and gives them a code (push)

The admin enters first name, last name, email, mobile and date of birth (from the HIS through the API, or in the console).
Before anything is created or sent, Sangam checks (4.3). If the admin proceeds, Sangam:

1. creates an account **waiting for the person**, or an invitation to an existing account;
2. shows the admin a one-time **activation code** to hand to the person in person;
3. e-mails the person a link.

One code covers every organisation added for that person, in every application; additions made before the person
activates wait and start together when they do. Opening the link, the person must first enter the admin's code. Without it nothing happens: no account, no access. No SMS
or WhatsApp is used (cost). A wrong recipient of the e-mail is stopped at the code.

### 4.3 The check before anything is sent

Sangam compares what the admin entered with existing accounts. It tells the admin **which details match and which do not,
never what Sangam holds**: a different record may belong to a different person, and its contents are that person's.

| Found | The admin is told | Then |
|---|---|---|
| An account with this email or mobile, all details match | "This person already has a Sangam account. They will enter your code to join." | Invitation to that account |
| An account with this email or mobile, some details differ | e.g. "A person with this last name, date of birth and email is already registered in Sangam, but the first name is different. Are you sure you have checked the name against their records?" | The admin corrects the details, asks the person for their join code (4.1), or proceeds |
| No account with this email or mobile, but one with the same name and date of birth | "A person with this name and date of birth may already be registered in Sangam. Ask them for their join code." | The admin asks for the code, or proceeds to create a new account |
| Nothing similar | — | New account (4.2) |

Limits: at most **5 checks per person per admin per day**; every check is audited under the admin's name with which fields
matched (never the values). The likelihood score (each mismatch lowers it) only chooses the row and the wording; it never
links or grants anything.

### 4.4 Joining needs the right person

- **Existing account:** after entering the admin's code, Sangam also compares the account's own name and date of birth with
  what the admin entered. If they differ and the admin did not confirm that difference in 4.3, joining stops and the admin
  is told.
- **New account:** the person enters the code, verifies the e-mail, sets a password. The details the admin typed are kept as
  the hospital's staff record; the person's own account details always win where they differ (decision 2 of 14 Oct).

## 5. Ending

- **A person leaves a branch:** the admin ends the membership (today's "Change"); access to that branch's applications ends
  at once; the account stays the person's.
- **A person leaves the hospital (G2, leaver):** a customer admin ends **all of that person's roles and memberships in the
  customer's tree, across its branches, on one date** (today or a chosen day), with one reason. The applications are told
  through the existing webhook and SCIM events; the person sees it in Activity; the account stays theirs.
- **A joiner may start on a later date** (G2): an addition can carry a start date, and access begins that day.
- **A customer leaves the application:** an application admin **closes the customer**. Every membership and admin right
  under it ends at once, the people are told, and it is audited. Their accounts stay as personal accounts; former admins
  simply lose console access.

## 6. Working for which branch (A6)

A person with more than one branch in an application is asked once at sign-in, **"Working at which branch?"**, and the token
carries that one organisation (`org_id`, its name, and their roles there if Sangam keeps them). The application may say
which (a ward computer always at Branch 1); Sangam honours it only if the person is a member there. Switching goes back
through Sangam. The full list stays available to applications that ask for it, so nothing that works today breaks.

- The choice is shown only to someone with two or more organisations in that application. Choices are cards (the whole
  card is the button), ordered **last used first, then A to Z**, with a search above six.
- "Remember on this device for 12 hours" keeps the choice **for that person on that device** (never for the next person on
  a shared ward device).
- An application offers "Switch organisation" through the Sangam SDK, which sends the person back to Sangam to choose
  again (`prompt=select_org`).
- Consent (A7) names the organisation chosen, and its "Will not be shared" list includes **"Other organisations' roles"**.
- The portal home may then show "Working as {role} · {organisation} in {application} · Switch" (P1), now that switching
  means something; decided at build time.

## 7. Records

The hospital's staff records (what the admin entered, when and by whom) are kept for **7 years with the audit archive**, then
deleted (decision 1). A closed customer's records follow the same rule.

## 8. Shared ward devices (T1)

For a tablet or computer at a nursing station, used by many people in turn.

- **Enrolment.** A customer or branch admin opens the organisation console **on the device** and enrols it ("Ward 4B ·
  Nursing station"). Sangam stores a device key in that browser. The admin sees every enrolled device and can remove one at
  once (lost, stolen, moved). Any other computer can still be marked shared by its user (rc.11's "This device's list"); there
  everyone signs in fully every time.
- **"Who's using this tablet?"** On an enrolled device: the last 6 people who signed in on it, as large tiles (name and
  their role at this organisation), and "Someone else, sign in". Only that hospital's staff appear.
- **The application decides when to lock or sign out.** LiPi, Medimaze and others keep their own idle times in their own
  settings, because only they see the person working. When their timer runs out they call Sangam **lock**; when the person
  leaves or someone else takes over they call **sign out** (today's sign-out). Sangam keeps no idle setting for applications;
  it locks only its own screens (portal, consoles). The Sangam SDK provides the idle timer and both calls so each
  application need not write them.
- **Sangam decides how you get back in.** On an enrolled device, after a lock:
  - within **2 hours** of the lock, the person's tile and a **6-digit PIN** (set at their first full sign-in on an enrolled
    device);
  - after 2 hours, a full sign-in (password, passkey or code);
  - a full sign-in at least once every **12 hours**, however many PIN unlocks;
  - **5 wrong PINs** cancel the PIN until the next full sign-in.

  An application or a customer may make these **stricter, never looser**. A PIN works only on the enrolled device where that
  person last signed in fully.
- **One person, one device, one state.** When one application locks or signs out, Sangam tells the person's other
  applications on that device to do the same (it already does so for sign-out).
- **Badges** (tap a staff card) come later, once a hospital chooses a reader: a badge will replace choosing the tile, never
  the PIN.

## 9. Emergency access, "break-glass" (M12)

Decisions of 14 October 2026. Sangam holds no patient data, so the work is split.

**The application** (LiPi, Medimaze) knows the patient. It keeps patient-level restrictions (a restricted ward patient seen
only by the treating doctor and those referred), decides when to offer "Emergency access", decides what may be done under
it (viewing, prescribing, ordering), and records which records were opened and what was done.

**Sangam** provides the break-glass itself, the same for every application:

- **Only staff of that hospital (customer).** People outside the organisation get no emergency access at all; a visiting
  doctor with no role there works through the staff on duty (verbal orders entered under their own login, as the hospital's
  own policy says).
- **One person.** The staff member uses their own login; no second person or sponsor is needed.
- **A reason is required**, and identity is checked **again on the spot** with a passkey or authenticator app.
- **Up to 60 minutes.** The application may end it earlier at any time with a reason (the patient left the ward, care
  handed over, the emergency is over); otherwise it ends by itself at 60 minutes.
- **At most 3 uses per person per day.**
- The application sends an **opaque reference** to the case (its own number, never the patient's name or details), so the
  review can say "emergency access to LiPi case 48213, 14:02 to 14:41, reason ..." without Sangam holding health data.
- The token carries the emergency marker, the case reference and the end time, so the application can show a banner and
  apply its own limits.
- **Recorded and told at once:** the person's Activity, the audit trail, the hospital's compliance evidence, and an e-mail
  to the organisation's emergency access reviewers.
- **Reviewed by an "emergency access reviewer"** (a role the hospital gives, for example the Medical Superintendent or
  Quality; not the IT admin), who marks each use **justified** or **not justified** with a note. In the organisation console
  and the access review: **green** under 24 hours, **orange** 24 to 72 hours, **red** after 72 hours unreviewed.
- **Switched on per organisation** by the hospital's admin; off by default.

## 10. Smaller fixes from the v2 handoff

- **A5:** the two-step screen's heading becomes "One more step" (page title "Two-step sign-in").
- **A9:** the sign-out screen lists the applications that will be signed out on this device (Sangam already signs them out).
- **A10:** first name and last name stay separate (the duplicate check compares them); the hint "As on your government ID"
  goes under them.
- **R2:** the application overview shows an access-review banner ("5 roles waiting for a decision") linking to R6.

Where the handoff and earlier decisions differ, the decisions stand: no Turnstile; no "Welcome back, {first}" or
person-specific method list before the password (accounts are never revealed); "This device's list" instead of one
remembered account; no "Test sign-in"; no Merges tab; no application review state; no "Resend verification".

## 11. What changes in code

| Area | Change |
|---|---|
| Data | Customer marker on top organisations; `org_admins` (customer and branch admin rights, separate from roles); several roles per membership (membership roles table); optional role; "roles kept in Sangam" setting; join codes; activation codes; staff records; check log; DigiLocker exception per application |
| Migration | Today's `org_admin` role memberships become admin rights; every existing top organisation becomes a customer; existing single roles carry over unchanged; memberships that applied to everything below are expanded into explicit ones and the switch removed |
| API | Add / check a person (4.3) returns per-field match only; join-code linking; close customer; admin rights; organisation hint at sign-in |
| Consoles | New organisation console for customer and branch admins; partner console gains customers, closing, admin rights, the DigiLocker exception |
| Portal | Join an organisation (code and QR); entering an activation code; "Working at which branch?" |
| Sign-in | The branch choice and `org_id` claim (A6); device enrolment, "Who's using this tablet?", PIN set and unlock, lock and its rules (T1) |
| SDK | Idle timer, lock and sign-out calls, honouring a lock from another application (T1); start and end emergency access (M12) |
| Emergency access | Request (reason, fresh check, case reference), 60-minute token, early end with reason, daily limit, reviewer role, review queue with 24 h / 72 h colours, per-organisation switch (M12) |
| Audit | Checks, adds, joins, closes, admin grants, exception use |
| Docs | Threat model rows (wrong-person invitation, enumeration through checks, admin abuse); ASVS; go-live checklist |

## 12. Not in this release

- The hospital's own logo on the sign-in screen when an organisation is named (later, small).
- A group organisation shared by several applications (Apulki head office across Medimaze and LiPi): no need yet, and it
  would weaken the wall between applications.
