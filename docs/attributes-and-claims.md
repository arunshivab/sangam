# Custom attributes, claims and time-limited roles (PR-25, SGM-209, SGM-202)

Sangam's profile holds what every application needs: name, e-mail, mobile, date of birth, gender. An application that
needs a little more about the people who use it — an employee number, a pay grade, a shift — defines **attributes** on
the partner console (*your application → Attributes & claims*), and chooses which of them, if any, reach its tokens as
**custom claims**.

## Attributes

| Field | Rule |
|---|---|
| Key | 2 to 40 lowercase letters, digits and underscores, starting with a letter (`employee_no`) |
| Label | Up to 80 characters, shown to administrators and, in the account portal, to the person |
| Kind of value | `text`, `number`, `date` (`yyyy-mm-dd`), `boolean` (`true`/`false`) or `choice` (two or more, comma-separated) |
| Who may change it | `admin` (administrators and the management API) or `person` (the person too, in their account portal) |
| Applies to | The whole application, or one organisation: then only people with a live role there have a value |

At most 20 attributes per application, values of at most 200 characters, and values only for people who have linked the
application. Retiring an attribute stops it being shown or released at once.

**No health data.** Sangam is an identity platform, not a clinical record (SGM-203, SGM-204). An attribute whose key or
label reads like a condition, treatment, result or anything about a person's body or mind (*blood group*, *HIV status*,
*medication*, *diagnosis*, …) is refused, and whoever adds an attribute must declare that it is not health data; the
declaration is in the audit record. Keep clinical facts in the clinical application.

The person sees everything an application keeps about them on *Connected apps* in their account portal, and changes
the rows marked theirs. Every change is audited (`attribute.values.set`, with who made it) and raises `user.updated` for
SCIM and webhooks, with the changed keys.

## Custom claims

A claim takes its value from one attribute, or from what Sangam already knows about the person in the application:

| Source | Value |
|---|---|
| `attribute` | The attribute's value, typed: text and dates as strings, numbers as numbers, yes/no as booleans |
| `roles` | The person's role codes in the application, as an array |
| `permissions` | The permissions those roles carry, as an array |
| `org_names` | The names of their organisations, as an array |

At most 10 claims. A claim may not take the name of one Sangam issues or a token needs (`sub`, `email`, `roles`,
`acr`, any `sangam_` name, …).

Custom claims are released **only under the `attributes` scope**: in the access token and at `/connect/userinfo`, never
in the ID token. The consent screen lists them as *Details {application} keeps about you*, with the values, so the
person sees exactly what will be sent before allowing it. Without the scope, nothing is released.

## Time-limited roles

A role can be given *until* a date — for a contractor, a locum or an auditor. On the partner console, pick the date under
*Until*: the role ends at the end of that day, India time. Through the management API, send `expiresAt` (ISO 8601, in the
future and at most five years away) with the membership:

```http
PUT /api/v1/orgs/{orgId}/members/{userId}
{"role": "auditor", "appliesToDescendants": false, "expiresAt": "2026-12-31T23:59:59+05:30"}
```

From that moment the role no longer counts anywhere — tokens, consoles, policies. Within a minute Sangam revokes it for
good, audits `org_membership.expire`, and tells SCIM and webhooks (`membership.revoked` with `"reason": "expired"`, and
`user.deactivated` when it was the person's last role in the application). Sending the membership again without
`expiresAt` makes it permanent.

## Management API

```http
GET /api/v1/users/{userId}/attributes        → {"employee_no": "E-1042", "shift": "night", "on_call": null}
PUT /api/v1/users/{userId}/attributes        {"employee_no": "E-1043", "on_call": ""}
```

Both need the `sangam.manage` scope and a person who has linked the calling application (`404` otherwise). A `PUT` sets
only the keys it names; an empty value clears one. A value of the wrong kind, an unknown key, or an organisation's
attribute for someone without a role there is refused with `400` and the reason.
