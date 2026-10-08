# Sangam.Client

Sign in with Sangam for ASP.NET Core and Blazor, and know which roles each person holds in which
of your organisations.

## Register your application

Ask the Sangam team (help@sangamid.in) to register your application on Sangam. You receive a client id and secret, and
register two addresses: `https://your-app/signin-sangam` and `https://your-app/signout-sangam`.
The Sangam team then makes one of your staff the application's owner; from then on your own team
manages roles, organisations and people on the partner console.

## Add sign-in

```csharp
builder.Services.AddSangam(o =>
{
    o.Authority = "https://id.sangamid.in";
    o.ClientId = builder.Configuration["Sangam:ClientId"]!;
    o.ClientSecret = builder.Configuration["Sangam:ClientSecret"];
});
builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);

// …
app.UseAuthentication();
app.UseAuthorization();
app.MapSangamSignOut();          // GET /signout ends the session here and at Sangam
```

Any page that needs a signed-in person now sends the visitor to Sangam and brings them back.

## Know who signed in, and what they may do

```csharp
SangamUser? user = httpContext.User.GetSangamUser();

user.Id                                   // stable Sangam id: store against this, not the email
user.Organisations                        // where they hold a role
user.HasRole("doctor", hospital.Path)     // includes roles inherited from a parent organisation
user.HasPermission("patients:write", ward.Path)
```

Roles and permissions are your application's own vocabulary — define them on the partner
console. Sangam reports them; your application decides what they allow.

Roles are as of sign-in. Someone given a new role sees it the next time they sign in.

## What Sangam never gives you

Other applications a person uses, their sessions elsewhere, or anyone who has not signed in to
your application and allowed it access.

## Ask for a stronger sign-in before a sensitive action

Sangam decides how strongly people authenticate; your application decides when that matters.

```csharp
if (!User.Satisfies(SangamAcr.TwoFactor, maxAge: TimeSpan.FromMinutes(5)))
{
    await SangamStepUp.ChallengeAsync(HttpContext, SangamAcr.TwoFactor, returnUrl: Request.Path, maxAge: TimeSpan.FromMinutes(5));
    return;
}
```

Levels: `SangamAcr.SingleFactor`, `TwoFactor`, `PhishingResistant` (a passkey) and `Signature` (two factors
within the last five minutes). Every Sangam token carries `acr`, `amr` and `auth_time`. An API that needs
more answers `401` with `WWW-Authenticate: SangamStepUp.Challenge(SangamAcr.TwoFactor)` (RFC 9470).

## Electronic signatures

With your client-credentials token (`scope=sangam.manage`):

```http
POST /api/v1/signatures
{ "recordId": "SOP-QA-014/3", "recordHash": "sha256:<hex of the record>", "meaning": "Approved",
  "displayText": "SOP-QA-014 Handling of radioactive waste, revision 3",
  "returnUrl": "https://your-app/signin-sangam", "signer": "<optional Sangam subject>" }
```

Send the person to `{Authority}{ceremonyPath}`. They sign in again with two factors, see the record and the
meaning, and sign or decline; they come back to `returnUrl?signature_request={id}&status=signed|declined`.
`GET /api/v1/signatures/{id}` then returns the signature token: a JWS with `typ` `sangam-signature+jwt`,
signed with Sangam's published keys, binding `sub`, `name`, `sig_record_id`, `sig_record_hash`,
`sig_meaning`, `sig_time`, `acr`, `amr` and `auth_time`. Verify it like any Sangam token (issuer, your client
id as audience, the keys at `/.well-known/jwks`), check the record hash against your record, and store it
with the record. `returnUrl` must be one of your registered redirect addresses.


## Open Sangam in the person's language, and with your organisation's branding

Two optional parameters on the authorization request:

```http
GET /connect/authorize?client_id=...&ui_locales=hi&sangam_org=<organisation id>&...
```

- `ui_locales` (OpenID Connect): space-separated languages in order of preference. Sangam's screens are in
  English (`en`), Hindi (`hi`) and Malayalam (`ml`). A language the person picked on Sangam itself, or set in
  their profile, comes first.
- `sangam_org`: the Sangam id of the organisation this sign-in is for. The sign-in screens then show that
  organisation's logo, welcome line and accent (set in the partner console), falling back to your application's.
  An organisation that is not yours is ignored.

Your sign-in page branding and the wording of the e-mails Sangam sends for you (verification, sign-in and reset
codes, invitations) are set in the partner console: Settings → *Sign-in page*, and *Messages*.

## Be told when a person's Sangam session ends

Ask the Sangam team to register either or both addresses for your application (operator console → Applications →
*Logout*):

- **Back-channel** (OpenID Connect Back-Channel Logout 1.0): Sangam POSTs `logout_token=<JWT>` to your address
  when a session in which your application was used ends — the person signs out, or ends the session from their
  account portal. The token has `typ` `logout+jwt`, your client id as `aud`, `sub`, `sid` and the logout `events`
  claim. Verify it with Sangam's keys, end every local session with that `sid` (or `sub`), and answer `200`.
  Failed deliveries are retried for a while; this is the dependable channel.
- **Front-channel** (Front-Channel Logout 1.0): Sangam loads your page in a hidden frame with `iss` and `sid` on
  the query string as the person signs out. Your page must allow being framed by Sangam's origin
  (`frame-ancestors`), and browsers that block third-party cookies may not send your cookie inside the frame.

Discovery advertises `backchannel_logout_supported` and `frontchannel_logout_supported`, both with sessions.

## Check or revoke a token from your back end

With your client id and secret (HTTP Basic or form fields):

- `POST /connect/introspect` with `token=...` (RFC 7662) answers `{ "active": true|false, ... }`.
- `POST /connect/revoke` with `token=...` (RFC 7009) revokes an access or refresh token.

## Webhooks, the management API and the shared audit event (R6)

```csharp
// Signed webhooks (Standard Webhooks, SGM-217): check every message on the body exactly as received.
bool genuine = SangamWebhook.Verify(Request.Headers["webhook-id"], Request.Headers["webhook-timestamp"], Request.Headers["webhook-signature"], body, secret);

// The management API from your back end (needs sangam.manage); tokens are fetched and cached for you.
SangamManagementClient sangam = services.GetRequiredService<SangamManagementClient>();
await sangam.UpsertMembershipAsync(wardId, personId, "nurse", appliesToDescendants: false, expiresAt: contractEnd);

// The shared audit event (SGM-208): who, where and from which device are filled in from the request.
builder.Services.AddSangamAudit(o => { o.AppId = "lims"; o.AppVersion = "1.2.0"; o.BufferPath = "/var/lib/lims/audit.jsonl"; });
await audit.RecordAsync(new SangamAuditEntry("lims.result.sign", "sign", "result", resultId)
{
    Signature = (tokenId, "Approved", recordHash),
    OrganisationId = ward.OrganisationId,
    OrganisationPath = ward.Path,
});
```

Events are checked against schema 1.0 and buffered in a JSON Lines file; once the audit service exists, set
`SangamAuditOptions.Endpoint` and the forwarder sends them in batches. The same rules — permissions, step-up, the RFC
9470 challenge, webhook signatures and the audit schema — are in the JavaScript, Python and Java SDKs, and every SDK
runs the shared conformance vectors in `sdk/conformance/vectors.json`. Sample: `samples/Sangam.Sample.AspNetCore`.
