# Native, mobile and device applications (PR-21, SGM-219)

How an application that is not a web site signs people in with Sangam. Everything here follows *OAuth 2.0 for Native
Apps* (RFC 8252): the person signs in in the **system browser**, never in a web view the app controls, so the app never
sees the password, a passkey works, and the person can see they are on `id.sangamid.in`.

## Registering

Native and device applications are **public clients**: an installed app cannot keep a secret, so it has none. They are
registered from the identity server's settings, like the deployment's own applications (R4):

    Sangam__Clients__ward__ClientId: lipi-ward-android
    Sangam__Clients__ward__Kind: native
    Sangam__Clients__ward__RedirectUris: https://ward.lipi.in/app/callback,in.lipi.ward:/callback

    Sangam__Clients__tv__ClientId: lipi-queue-display
    Sangam__Clients__tv__Kind: device

Redirect addresses a native application may use (refused otherwise, at start-up):

| Kind of address | Example | When |
|---|---|---|
| Claimed https (App Links, Universal Links) | `https://ward.lipi.in/app/callback` | Preferred on Android and iOS |
| Private-use scheme, reverse-domain form | `in.lipi.ward:/callback` | When a claimed link is not possible |
| Loopback by IP literal | `http://127.0.0.1/callback` | Desktop applications; any port at sign-in |

Never `localhost` by name, never plain http elsewhere, never a fragment, never a short scheme such as `myapp:`.

## Signing in (Android, iOS, Flutter, React Native)

1. Authorization code with PKCE (S256) — required for every client.
2. Open the system browser: Custom Tabs on Android, `ASWebAuthenticationSession` on iOS. Use AppAuth for Android or
   iOS, `flutter_appauth` for Flutter, `react-native-app-auth` for React Native; they do all of this.
3. Discovery: `https://id.sangamid.in/.well-known/openid-configuration`.
4. Scopes: `openid profile` and what the app needs; `offline_access` for a refresh token.
5. Keep the refresh token in the platform keystore (Android Keystore, iOS Keychain); the access token in memory only.
6. **Refresh tokens rotate.** Every refresh returns a new refresh token; store it and throw the old one away. Using an
   old one again (after a short grace period for network retries, `Sangam:Tokens:RefreshReuseLeewaySeconds`, 30 s by
   default) is treated as theft: the whole sign-in is revoked and the person signs in again.
7. Sensitive actions: ask for step-up with `acr_values` and `max_age` (SGM-207); passkeys work in the system browser.
8. Sign out: the end-session endpoint with `id_token_hint`, and the app's own redirect address as
   `post_logout_redirect_uri`.

## Devices without a keyboard: TVs, kiosks, command lines (RFC 8628)

    POST https://id.sangamid.in/connect/device
      client_id=lipi-queue-display&scope=openid profile offline_access

The answer has a `user_code`, a `verification_uri` (`https://id.sangamid.in/device`) and a `verification_uri_complete`
(show it as a QR code). The device shows the code and the address, then polls:

    POST https://id.sangamid.in/connect/token
      grant_type=urn:ietf:params:oauth:grant-type:device_code&device_code=…&client_id=lipi-queue-display

— `authorization_pending` until the person has acted, then the tokens, or `access_denied` if they refused. Poll no
faster than the `interval` in the answer, if one is given (every five seconds otherwise); the code expires after ten
minutes. On their phone, the person opens the
address, signs in as usual (the application's sign-in rules apply), checks the code matches, sees what will be shared,
and allows or refuses. Allowing is their consent; it appears in the portal's connected apps, from where they can end it.

## Pushed authorization requests (RFC 9126)

Any registered web application may push its authorization request to `/connect/par` (authenticated with its secret)
and send the browser to `/connect/authorize?client_id=…&request_uri=…`: the
parameters never travel through the browser. An application can be held to it with
`Sangam__Clients__<key>__RequirePushedAuthorization: "true"`.

## Token exchange between applications (RFC 8693)

For one application's back end to call another Sangam application's API on the person's behalf — LiPi HIS reading
from the laboratory system, say. The calling application must be confidential and registered with
`Sangam__Clients__<key>__ExchangeAudiences: <target client id>`. It sends the access token the person's sign-in gave
it:

    POST https://id.sangamid.in/connect/token
      grant_type=urn:ietf:params:oauth:grant-type:token-exchange
      client_id=…&client_secret=…
      subject_token=<the person's access token>&subject_token_type=urn:ietf:params:oauth:token-type:access_token
      audience=<target client id>&scope=profile

Sangam refuses unless the token was issued to the caller, the person has consented to the target application, and
the scopes are no more than the original. The new token is addressed only to the target (`aud`), names the caller in
`act`, lives ten minutes, and cannot be refreshed. Every exchange is audited (`token.exchange`).
