# Sangam.Client

Sign in with Sangam for ASP.NET Core and Blazor, and know which roles each person holds in which
of your organisations.

## Register your application

Ask imagiQa to register your application on Sangam. You receive a client id and secret, and
register two addresses: `https://your-app/signin-sangam` and `https://your-app/signout-sangam`.
imagiQa then makes one of your staff the application's owner; from then on your own team
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
