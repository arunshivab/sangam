# Who may do what in Sangam

Four planes of authority, each enforced in a different place. Nothing in a user interface
decides permission: every check below lives in a service and is proved by a test.

## The planes

| Plane | Who | Stored in | Enforced in |
|---|---|---|---|
| **Platform** | the Sangam team running Sangam | `platform_operators.role` | `IAdminService` |
| **Application** — human | A partner's own administrators | `app_admins.role` | `IPartnerService` |
| **Application** — machine | A partner's own code | OAuth client + `sangam.manage` | `/api/v1`, app id taken from the token |
| **Organisation** | Clinical and admin staff inside one application | `org_memberships` → `roles` | The application, from the `sangam_orgs` claim |
| **The person** | Every user, over their own account | — | `IPortalService` |

The only point where planes meet: an Owner can disable an *application*, which stops every sign-in
to it. They cannot reach inside that application's tenancy to change a role or a membership.

## Platform ranks

| Capability | Viewer | AppManager | Support | Owner |
|---|:--:|:--:|:--:|:--:|
| Search users | ✓ | ✓ | ✓ | ✓ |
| Open a user's record — audited, the user sees it | ✓ | ✓ | ✓ | ✓ |
| List applications | ✓ | ✓ | ✓ | ✓ |
| Enable or disable a partner's application | — | ✓ | ✓ | ✓ |
| Make someone a partner application's owner | — | ✓ | ✓ | ✓ |
| Disable, or give owners to, one of Sangam's own applications | refused | refused | refused | refused |
| Suspend or reinstate an account | — | — | ✓ | ✓ |
| Sign a user out everywhere | — | — | ✓ | ✓ |
| Place or clear a deletion hold | — | — | ✓ | ✓ |
| Delete an account immediately | — | — | — | ✓ |
| List, grant and revoke operators | — | — | — | ✓ |
| Revoke the last owner | refused | refused | refused | refused |
| Sign in as a user | **never** | **never** | **never** | **never** |
| Read a password | impossible | impossible | impossible | impossible |
| Change a user's name, date of birth or email | — | — | — | — |

Every operator must have an authenticator app, whatever their rank. See ADR-0005.

### Named rights (rc.16, ASVS V1.4.4 and V1.4.5)

The console's code never compares ranks. Each action asks for one named right through a single check,
`OperatorAccess` (Infrastructure/Admin), which writes every refusal to the audit log as `access.denied` with the
operation, the right and the rank it needs. Ranks are how rights are given (`OperatorRights` in the domain); they stay
cumulative, so who may do what is exactly the table above. A test (`OperatorAccessTests`) fails the build when a public
service method that acts for an operator does not go through `OperatorAccess`, and when a right is missing here.

| Right | What it allows | Viewer | AppManager | Support | Owner |
|---|---|:--:|:--:|:--:|:--:|
| `UseConsole` | Use the console: its pages, the work queue, the grievance list | ✓ | ✓ | ✓ | ✓ |
| `SearchUsers` | Search and list people | ✓ | ✓ | ✓ | ✓ |
| `OpenUserRecord` | Open a person's record (audited; the person sees it) | ✓ | ✓ | ✓ | ✓ |
| `ListApplications` | List applications (and SAML service providers) | ✓ | ✓ | ✓ | ✓ |
| `ReadGrievances` | Read the grievance log | ✓ | ✓ | ✓ | ✓ |
| `ViewMonitoring` | See the monitoring page | ✓ | ✓ | ✓ | ✓ |
| `ManageApplications` | Enable or disable a partner's application, its logout addresses and DigiLocker exception; give it an owner | — | ✓ | ✓ | ✓ |
| `ManageSamlProviders` | Register and change SAML service providers | — | ✓ | ✓ | ✓ |
| `EditPlatformBranding` | Change Sangam's own branding and messages | — | ✓ | ✓ | ✓ |
| `SuspendAccounts` | Suspend or reinstate an account | — | — | ✓ | ✓ |
| `SignOutEverywhere` | Sign a person out everywhere | — | — | ✓ | ✓ |
| `HoldDeletion` | Place or clear a deletion hold | — | — | ✓ | ✓ |
| `ReadAuditTrail` | Read a person's audit trail | — | — | ✓ | ✓ |
| `ReviewRecoveries` | Review an account recovery waiting for a decision | — | — | ✓ | ✓ |
| `ActOnGrievances` | Log, acknowledge, note and close grievances | — | — | ✓ | ✓ |
| `WithdrawTwoStepReset` | Withdraw a two-step reset before it takes effect | — | — | ✓ | ✓ |
| `SeeOverview` | See the platform overview | — | — | — | ✓ |
| `DeleteAccounts` | Delete an account at once | — | — | — | ✓ |
| `ManageOperators` | List, grant and revoke console access | — | — | — | ✓ |
| `ApplyTwoStepResetNow` | Apply a two-step reset without its waiting period | — | — | — | ✓ |
| `ReviewPrivilegedRecoveries` | Review the recovery of a privileged account | — | — | — | ✓ |

## Application plane

On the partner console (`partners.sangamid.in`) for people, the management API for code.
See ADR-0006.

| Capability | Admin | Owner | Application (machine) |
|---|:--:|:--:|:--:|
| Create or retire the application's roles | ✓ | ✓ | ✓ `PUT /api/v1/roles/{code}` |
| Register organisations and build the tree | ✓ | ✓ | ✓ `PUT /api/v1/orgs/{id}` |
| Grant and revoke memberships — linked people only | ✓ | ✓ | ✓ `PUT` / `DELETE /api/v1/orgs/{id}/members/{user}` (rc.5: linked people only here too; rc.13: for an admin of that organisation, named in `Sangam-Acting-Admin`) |
| Add someone who does not use the application yet | ✓ | ✓ | ✓ `POST /api/v1/orgs/{id}/staff` (rc.12, for an admin named in `Sangam-Acting-Admin`; the e-mail invitation answers 410) |
| Find people — only those who linked this application | ✓ | ✓ | — |
| Edit description, brand colour, tile letter | ✓ | ✓ | — |
| Sign-in policy: each person's choice, or always two-step | ✓ | ✓ | — |
| Add, promote, demote, remove administrators | — | ✓ | — |
| Remove or demote the last owner | refused | refused | — |
| Password-only or email-code-only sign-in, redirect URIs, secrets, name | — | — | — *(Sangam team)* |
| See a user's other applications, sessions or audit trail | **never** | **never** | **never** |

Every application administrator must have an authenticator app, like every operator.

## The person, over their own accounts (rc.7)

Only the person merges two of their accounts, signed in to both within 15 minutes, each with its own second step; the
merge waits 24 hours (72 when either account administers an application or organisation) with a cancel link to both.
No operator, application administrator or application can merge someone's accounts. An application administrator's
role moves with the account, and the application's other owners are told; operators' accounts are never merged.

## Organisation plane

| | Defined by | Sangam's part |
|---|---|---|
| `org_admin` | Sangam, `is_system = true` | Cannot be retired or re-scoped |
| `doctor`, `nurse`, `receptionist`, … | The application | Stored, scoped, returned in `sangam_orgs` |
| Permissions such as `patient:read` | The application | **Opaque** — never interpreted |

An organisation-scoped role beats an application-wide role of the same code, and
`applies_to_descendants` decides whether a role flows down the organisation tree. Sangam reports
these in the token; the application enforces them.

## Worked example — a hospital on LiPi HIS

| Person | Plane | How |
|---|---|---|
| Sangam's own support engineer | Platform | `Support` rank |
| LiPi's Super Admin and Admin | Application — human | `app_admins`: Owner and Admin |
| The hospital administrator who creates users | Organisation | `org_admin` at the hospital |
| A department head | Organisation | App role on the department, `applies_to_descendants` |
| Doctors and nurses | Organisation | App roles with LiPi-defined permissions |
| View-only staff | Organisation | An app role with read-only permissions |

A hospital's Director, CFO or IT head never holds a platform rank: a platform rank is power over
every tenant, including other hospitals.
