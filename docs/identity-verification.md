# Verifying identity with DigiLocker (PR-26, SGM-201, SGM-204)

A person can confirm their name, date of birth and gender against their government record in the account portal
(*Personal details → Verified identity*). Sangam uses DigiLocker's OAuth 2.0 flow with PKCE, as DigiLocker's Requester
API Specification describes it.

## What happens

1. On the profile page the person reads what will happen and ticks that they agree: **their Sangam profile takes
   DigiLocker's values and they stay locked**. Only then does *Verify with DigiLocker* start anything.
2. The portal sends them to DigiLocker with a fresh `state` and an S256 `code_challenge`, kept for ten minutes in an
   encrypted, http-only cookie bound to the signed-in person.
3. DigiLocker sends them back to `/verify/digilocker/callback`. The portal checks the state and the person, exchanges
   the code (client secret plus verifier), and reads `digilockerid`, `name`, `dob` and `gender` from the token response,
   or from the user-details endpoint when the response leaves them out. The access token is used once and dropped.
4. The profile's name (the last word becomes the family name; a one-word name stays whole), date of birth and gender
   are set to the record's, exactly as written there, and locked. DigiLocker's `T` is recorded as *Other*, Sangam's
   nearest value.

Every outcome returns to the profile page with a message: verified, cancelled, could not confirm, took too long, or
already used by another account.

## What Sangam keeps

| Kept | Never kept |
|---|---|
| The name, date of birth and gender on the record | The Aadhaar number (never requested) |
| When, and that it was DigiLocker | Any document, the e-Aadhaar XML, DigiLocker's `reference_key` |
| HMAC-SHA256 of the DigiLocker id, keyed with `Sangam:DigiLocker:SubjectKey` | The DigiLocker id itself, DigiLocker's tokens |

The keyed hash is what makes **one DigiLocker identity verify one Sangam account**: a second account presenting the same
DigiLocker identity is refused (and the attempt audited as `identity.verify.refused`).

## Afterwards

- The name and gender cannot be changed in the portal, and the date of birth, already fixed, now comes from the record.
- Applications that ask for the `profile` scope receive `"sangam_identity_verified": true` in the access token and at
  `/connect/userinfo`. The claim is absent for people who are not verified.
- The person can *Remove the verification*: the record and the hash are deleted and the fields unlock (their values
  stay). They can verify again later.
- Verifying and removing are audited (`identity.verify`, `identity.unverify`), narrated in the person's activity, and
  raise `user.updated` for SCIM and webhooks.
- Deleting the account deletes the verification with it, so the same person can verify a new account.

## Switching it on

DigiLocker is off unless `Sangam:DigiLocker:Enabled` is true on the account portal. Outside Development the portal
refuses to start without `ClientId`, `ClientSecret`, https endpoints and a `SubjectKey` of 32 characters or more. In
production, use `deploy/production/docker-compose.digilocker.yml` and register
`https://account.sangamid.in/verify/digilocker/callback` with DigiLocker. The endpoints default to
`api.digitallocker.gov.in/public/oauth2/1/…` and can be pointed at the sandbox or the MeriPehchaan host.

**To confirm with DigiLocker during onboarding** (OI-046): the production host names, whether `purpose=verification`
is the right value for this use, and whether the partner agreement requires showing DigiLocker's own consent text.
