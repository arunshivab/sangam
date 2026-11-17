# Set up roles locally

A Sangam you have just started on your laptop is empty: no application of yours, no roles, no organisations, and
nobody placed anywhere. Until someone holds a role, your application's sign-ins carry no `sangam_org.roles`. This page
takes you through the same steps your customers' admins follow in production, with you playing every part. It works
the same for every application.

You need Sangam running locally as the README describes (`Sangam.Identity.Server` on 5100, the operator console on
5300, the partner console on 5400). In Development, Sangam keeps outgoing e-mail instead of sending it: read
verification codes at `http://localhost:5100/dev/outbox`.

## 1. Two accounts

Register two accounts at `http://localhost:5100/register` and verify both:

- **yours**, the one that runs things (operator, application owner, organisation admin);
- **a test person**, the one who will hold a role.

## 2. Make yourself the operator

Only once per database:

```powershell
dotnet run --project src/Sangam.Admin.Web -- create-operator you@example.in
```

Then add an authenticator app (or a passkey) to your account in the portal (`http://localhost:5200`, **Sign-in &
security**). The operator and partner consoles open only with one.

## 3. Register your application

Applications are registered from settings when the identity server starts. Use user secrets for the identity server
(the key, `myapp` here, is any short name of yours):

```powershell
dotnet user-secrets --project src/Sangam.Identity.Server set "Sangam:Clients:myapp:ClientId" "myapp"
dotnet user-secrets --project src/Sangam.Identity.Server set "Sangam:Clients:myapp:Kind" "application"
dotnet user-secrets --project src/Sangam.Identity.Server set "Sangam:Clients:myapp:BaseUrl" "http://localhost:5050/"
dotnet user-secrets --project src/Sangam.Identity.Server set "Sangam:Clients:myapp:Secret" "<32 or more random characters>"
dotnet user-secrets --project src/Sangam.Identity.Server set "Sangam:Clients:myapp:DisplayName" "My application"
```

- `BaseUrl` is where your application runs; its callbacks are `<BaseUrl>signin-sangam` and `<BaseUrl>signout-sangam`.
  Locally it may be `http://localhost`; anywhere else it must be `https`.
- The secret must be at least 32 characters and must not contain `change-me`.
- Add `Sangam:Clients:myapp:ManagementApi` = `true` only if you will use the API path below.

Restart the identity server. Your application is registered.

## 4. Operator console: owner and the DigiLocker exception

At `http://localhost:5300` → **Applications**, on your application's row:

1. **Assign owner** → your e-mail → **Make owner**.
2. **DigiLocker exception** → a day a few months ahead → **Save**.

Only admins verified with DigiLocker may add people. A laptop has no DigiLocker, so the exception lets unverified
admins add people until that day. Never switch it on in production except as the go-live checklist says.

## 5. Partner console: roles and an organisation

At `http://localhost:5400` → your application:

1. **Roles**: for each role, its code (what your application reads, for example `nurse`), its name and its
   permissions, separated by commas → **Save role**. Sangam passes permissions through without reading them.
2. **Organisations & people** → **Add organisation**: a made-up test organisation (say, "Test Hospital"), kind
   `hospital` or whichever fits.

## 6. The test person uses your application once

Sign in to your application as the test person and allow it on the consent screen. A person can be given a role only
in an application they have linked.

## 7. Give the test person a role

Partner console → your application → **Organisations & people** → Test Hospital → **Give someone a role here**: search
for the test person by name or e-mail, choose the role, optionally an end date → **Give role**.

## 8. Sign in again

Sign in to your application as the test person again, asking for `org.current` (and `orgs.read` if you show all their
places). `sangam_org.roles` now carries the role.

When you wipe your local database, repeat from step 1.

## The same with the API

Step 5 and step 7 can be done by your own code or test set-up through the management API, exactly as in production
(`ManagementApi` = `true` in step 3). Steps 1, 2, 4 and 6 stay as above.

1. A token for your application: `POST /connect/token` with `grant_type=client_credentials`, your client id and
   secret, and `scope=sangam.manage`.
2. Each role: `PUT /api/v1/roles/{code}` with
   `{"displayName": "Nurse", "description": null, "permissions": ["patient:read"], "orgId": null}`.
3. The organisation: `PUT /api/v1/orgs/{a new id of yours}` with
   `{"name": "Test Hospital", "type": "hospital", "parentId": null, "metadata": null}`.
4. The place: `PUT /api/v1/orgs/{orgId}/members/{sub}` with `{"roles": ["nurse"]}`, where `{sub}` is the test person's
   `sub` in your application. Sangam places someone only when an admin asks, so send **your own** access token, from
   your own sign-in to your application, in the `Sangam-Acting-Admin` header. As the application's owner you are an
   admin of every one of its organisations.

Every call uses the token from 1 as `Authorization: Bearer`. Every change is in Sangam's audit log, as in production.
