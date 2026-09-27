# Who may do what in Sangam

Four planes of authority, each enforced in a different place. Nothing in a user interface
decides permission: every check below lives in a service and is proved by a test.

## The planes

| Plane | Who | Stored in | Enforced in |
|---|---|---|---|
| **Platform** | imagiQa staff running Sangam | `platform_operators.role` | `IAdminService` |
| **Application** — human | A partner's own administrators | `app_admins` | *not built yet (PR-07)* |
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
| Enable or disable an application | — | ✓ | ✓ | ✓ |
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

## Application plane

| Capability | App admin (human) | Application (machine) |
|---|:--:|:--:|
| Create or retire the application's roles | *PR-07* | ✓ `PUT /api/v1/roles/{code}` |
| Register organisations and build the tree | *PR-07* | ✓ `PUT /api/v1/orgs/{id}` |
| Grant and revoke memberships | *PR-07* | ✓ `PUT` / `DELETE /api/v1/orgs/{id}/members/{user}` |
| Edit branding, redirect URIs, sign-in policy | *PR-07* | — |
| See a user's other applications, sessions or audit trail | **never** | **never** |

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
| imagiQa's own support engineer | Platform | `Support` rank |
| LiPi's Super Admin and Admin | Application — human | `app_admins`, PR-07 |
| The hospital administrator who creates users | Organisation | `org_admin` at the hospital |
| A department head | Organisation | App role on the department, `applies_to_descendants` |
| Doctors and nurses | Organisation | App roles with LiPi-defined permissions |
| View-only staff | Organisation | An app role with read-only permissions |

A hospital's Director, CFO or IT head never holds a platform rank: a platform rank is power over
every tenant, including other hospitals.
