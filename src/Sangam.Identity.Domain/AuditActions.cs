namespace Sangam.Identity.Domain;

/// <summary>
/// The audit action taxonomy. Dotted, lowercase, <c>noun.verb</c> or <c>noun.sub.verb</c>.
/// Add here first; an <see cref="Entities.AuditEvent.Action"/> outside this list is a bug.
/// </summary>
public static class AuditActions
{
    /// <summary>A new account was created.</summary>
    public const string UserRegister = "user.register";

    /// <summary>The email address was verified.</summary>
    public const string UserEmailVerify = "user.email.verify";

    /// <summary>The person asked to change their e-mail address; a code went to the new address (OI-022).</summary>
    public const string UserEmailChangeRequest = "user.email.change.request";

    /// <summary>The person confirmed the new address with its code; the old address was told (OI-022).</summary>
    public const string UserEmailChange = "user.email.change";

    /// <summary>Someone tried to register with an address or mobile that already has an account, while existing accounts are concealed (V-09).</summary>
    public const string UserRegisterDuplicate = "user.register.duplicate";

    /// <summary>A passkey was added to the account (PR-14).</summary>
    public const string UserPasskeyAdd = "user.passkey.add";

    /// <summary>A passkey was removed from the account (PR-14).</summary>
    public const string UserPasskeyRemove = "user.passkey.remove";

    /// <summary>A passkey sign-in was refused: unknown credential, bad signature or a counter that went backwards (PR-14).</summary>
    public const string UserPasskeyFail = "user.passkey.fail";

    /// <summary>An SMS was handed to the provider, or refused by it (template and outcome in metadata; never the number or the code) (PR-15).</summary>
    public const string UserSmsSend = "user.sms.send";

    /// <summary>The person proved control of their mobile number with an SMS code (PR-15).</summary>
    public const string UserMobileVerify = "user.mobile.verify";

    /// <summary>The day's SMS volume reached the configured alert threshold (PR-15, cost and SMS-pumping watch).</summary>
    public const string SmsVolumeAlert = "sms.volume.alert";

    /// <summary>An application asked for a stronger or more recent sign-in than the session had; the person was asked to authenticate again (PR-17).</summary>
    public const string UserStepUpRequired = "user.stepup.required";

    /// <summary>An application's step-up request (acr_values or max_age) was met (PR-17).</summary>
    public const string UserStepUpSuccess = "user.stepup.success";

    /// <summary>A step-up could not be met: the level asked for is not available to this person (PR-17).</summary>
    public const string UserStepUpFail = "user.stepup.fail";

    /// <summary>The person signed a record in an application's signature ceremony (PR-17).</summary>
    public const string UserSignatureSign = "user.signature.sign";

    /// <summary>The person declined to sign in an application's signature ceremony (PR-17).</summary>
    public const string UserSignatureDecline = "user.signature.decline";

    /// <summary>Successful sign-in.</summary>
    public const string UserLoginSuccess = "user.login.success";

    /// <summary>Failed sign-in (wrong password, locked out, or unknown email — see actor type).</summary>
    public const string UserLoginFail = "user.login.fail";

    /// <summary>Sign-out.</summary>
    public const string UserLogout = "user.logout";

    /// <summary>Password changed by the user.</summary>
    public const string UserPasswordChange = "user.password.change";

    /// <summary>Password reset requested (email sent).</summary>
    public const string UserPasswordResetRequest = "user.password.reset.request";

    /// <summary>Password reset completed.</summary>
    public const string UserPasswordResetComplete = "user.password.reset.complete";

    /// <summary>A one-time code was issued (purpose in metadata).</summary>
    public const string UserOtpIssue = "user.otp.issue";

    /// <summary>A one-time code was refused (wrong, expired or exhausted; reason in metadata).</summary>
    public const string UserOtpFail = "user.otp.fail";

    /// <summary>The user changed their sign-in preference.</summary>
    public const string UserSignInPreferenceChange = "user.signin.preference.change";

    /// <summary>Account deletion requested (soft delete starts).</summary>
    public const string UserAccountDeletionRequest = "user.account.deletion.request";

    /// <summary>Account hard-deleted after the grace period.</summary>
    public const string UserAccountDeletionComplete = "user.account.deletion.complete";

    /// <summary>A pending deletion was cancelled (the user signed in, or asked to keep the account).</summary>
    public const string UserAccountDeletionCancel = "user.account.deletion.cancel";

    /// <summary>The user changed their own profile from the portal.</summary>
    public const string UserProfileUpdate = "user.profile.update";

    /// <summary>The user enrolled an authenticator app.</summary>
    public const string UserMfaEnable = "user.mfa.enable";

    /// <summary>The user removed their authenticator app.</summary>
    public const string UserMfaDisable = "user.mfa.disable";

    /// <summary>A second-factor code was refused.</summary>
    public const string UserMfaFail = "user.mfa.fail";

    /// <summary>An operator opened a user's record. Reads are audited, not only writes.</summary>
    public const string AdminUserRead = "admin.user.read";

    /// <summary>A partner owner made someone an administrator of their application.</summary>
    public const string AppAdminGrant = "app.admin.grant";

    /// <summary>A partner owner removed one of their application's administrators.</summary>
    public const string AppAdminRevoke = "app.admin.revoke";

    /// <summary>An application administrator invited someone by e-mail to an organisation and role (PR-13).</summary>
    public const string AppInvitationCreate = "app.invitation.create";

    /// <summary>The invited person accepted, linking the application and taking the role (PR-13).</summary>
    public const string AppInvitationAccept = "app.invitation.accept";

    /// <summary>A partner changed their application's branding or sign-in policy.</summary>
    public const string AppSettingsUpdate = "app.settings.update";

    /// <summary>An operator changed an application registry entry.</summary>
    public const string AdminAppUpdate = "admin.app.update";

    /// <summary>An operator lifted a suspension.</summary>
    public const string AdminUserReinstate = "admin.user.reinstate";

    /// <summary>An operator was granted console access.</summary>
    public const string AdminOperatorGrant = "admin.operator.grant";

    /// <summary>An operator's console access was revoked.</summary>
    public const string AdminOperatorRevoke = "admin.operator.revoke";

    /// <summary>The user downloaded their personal data (DPDPA portability).</summary>
    public const string UserDataExport = "user.data.export";

    /// <summary>A single session was ended from the portal.</summary>
    public const string UserSessionRevoke = "user.session.revoke";

    /// <summary>Every session was ended from the portal.</summary>
    public const string UserSessionRevokeAll = "user.session.revoke_all";

    /// <summary>The user revoked an application's access from the portal.</summary>
    public const string AppAccessRevoke = "app.access.revoke";

    /// <summary>A platform operator placed a hold that blocks the scheduled purge.</summary>
    public const string AdminUserHoldPlace = "admin.user.hold.place";

    /// <summary>A platform operator cleared a hold.</summary>
    public const string AdminUserHoldClear = "admin.user.hold.clear";

    /// <summary>
    /// Support reset a person's two-step sign-in after proving who they are, because they lost their authenticator
    /// and recovery codes (PR-16, CAP-019). Method and reference in metadata; never an identity-document number.
    /// </summary>
    public const string AdminUserMfaReset = "admin.user.mfa.reset";

    /// <summary>
    /// Support asked to reset a person's two-step sign-in (D-K). Metadata records how the operator verified the
    /// person's identity (required), the reference, whether the account is privileged, and when it takes effect.
    /// </summary>
    public const string AdminUserMfaResetRequest = "admin.user.mfa.reset.request";

    /// <summary>An operator applied a two-step reset at once, skipping the cooling-off period (D-K); the reason is in metadata.</summary>
    public const string AdminUserMfaResetUrgent = "admin.user.mfa.reset.urgent";

    /// <summary>An operator withdrew a pending two-step reset (D-K).</summary>
    public const string AdminUserMfaResetWithdraw = "admin.user.mfa.reset.withdraw";

    /// <summary>The account's owner cancelled a pending two-step reset — "this wasn't me" (D-K).</summary>
    public const string UserMfaResetCancel = "user.mfa.reset.cancel";

    /// <summary>An alert was sent to the platform's owner (D-H, D-K).</summary>
    public const string PlatformAlert = "platform.alert";

    /// <summary>A platform operator deleted an account immediately, without waiting for the grace period.</summary>
    public const string AdminUserDeleteNow = "admin.user.delete_now";

    /// <summary>Organisation created.</summary>
    public const string OrgCreate = "org.create";

    /// <summary>Organisation updated.</summary>
    public const string OrgUpdate = "org.update";

    /// <summary>An application administrator changed an organisation's security policy (PR-16; before and after in metadata).</summary>
    public const string OrgPolicyUpdate = "org.policy.update";

    /// <summary>An application administrator changed the application's password or second-factor policy (PR-16).</summary>
    public const string AppPolicyUpdate = "app.policy.update";

    /// <summary>Sign-in page branding (logo, accent, welcome line, links) changed at a level (PR-19).</summary>
    public const string CustomisationBrandingUpdate = "customisation.branding.update";

    /// <summary>An e-mail or SMS template changed or reset at a level (PR-19).</summary>
    public const string CustomisationTemplateUpdate = "customisation.template.update";

    /// <summary>Membership granted.</summary>
    public const string OrgMembershipGrant = "org_membership.grant";

    /// <summary>Membership revoked.</summary>
    public const string OrgMembershipRevoke = "org_membership.revoke";

    /// <summary>App registered.</summary>
    public const string AppRegister = "app.register";

    /// <summary>App client secret rotated.</summary>
    public const string AppSecretRotate = "app.secret.rotate";

    /// <summary>App disabled.</summary>
    public const string AppDisable = "app.disable";

    /// <summary>Role created or updated by the app.</summary>
    public const string RoleUpsert = "role.upsert";

    /// <summary>Role retired by the app.</summary>
    public const string RoleRetire = "role.retire";

    /// <summary>Consent granted.</summary>
    public const string ConsentGrant = "consent.grant";

    /// <summary>The user declined consent on the consent screen.</summary>
    public const string ConsentDeny = "consent.deny";

    /// <summary>Tokens issued to an app for a user (grant type in metadata).</summary>
    public const string TokenIssue = "token.issue";

    /// <summary>An application exchanged a person's access token for one addressed to another application (PR-21).</summary>
    public const string TokenExchange = "token.exchange";

    /// <summary>A person approved a device's sign-in at /device (PR-21).</summary>
    public const string DeviceApprove = "device.approve";

    /// <summary>A person refused a device's sign-in at /device (PR-21).</summary>
    public const string DeviceDeny = "device.deny";

    /// <summary>A SAML assertion was issued to a service provider (PR-22).</summary>
    public const string SamlAssertion = "saml.assertion";

    /// <summary>A SAML service provider asked Sangam to sign the person out (PR-22).</summary>
    public const string SamlLogout = "saml.logout";

    /// <summary>A SAML service provider was registered in the console (PR-22).</summary>
    public const string SamlProviderRegister = "saml.provider.register";

    /// <summary>A SAML service provider's settings were changed (PR-22).</summary>
    public const string SamlProviderUpdate = "saml.provider.update";

    /// <summary>An app-initiated sign-out ended the session.</summary>
    public const string UserLogoutApp = "user.logout.app";

    /// <summary>Consent revoked.</summary>
    public const string ConsentRevoke = "consent.revoke";

    /// <summary>A platform operator suspended a user.</summary>
    public const string AdminUserSuspend = "admin.user.suspend";

    /// <summary>A platform operator forced a user's sessions to end.</summary>
    public const string AdminUserForceLogout = "admin.user.force_logout";

    /// <summary>Reference data or the bootstrap app was seeded.</summary>
    public const string SystemSeed = "system.seed";

    /// <summary>An application was registered, or its registration corrected, from the deployment's settings (R4).</summary>
    public const string SystemClientRegistered = "system.client.registered";

    /// <summary>Events older than a year moved into an encrypted archive file, anonymised (D-A).</summary>
    public const string AuditArchive = "audit.archive";

    /// <summary>An archive file older than seven years was deleted (D-A).</summary>
    public const string AuditArchivePurge = "audit.archive.purge";

    /// <summary>A grievance was logged (D-D).</summary>
    public const string GrievanceLog = "grievance.log";

    /// <summary>A grievance was acknowledged (D-D).</summary>
    public const string GrievanceAcknowledge = "grievance.acknowledge";

    /// <summary>A note was added to a grievance (D-D).</summary>
    public const string GrievanceNote = "grievance.note";

    /// <summary>A grievance was closed, resolved or declined (D-D).</summary>
    public const string GrievanceClose = "grievance.close";

    /// <summary>A partner changed their application's SCIM provisioning settings (PR-23).</summary>
    public const string ScimSettings = "scim.settings";

    /// <summary>A time-limited membership ended by itself (PR-25).</summary>
    public const string OrgMembershipExpire = "org_membership.expire";

    /// <summary>A person verified their identity through DigiLocker (PR-26); the profile took the record's values.</summary>
    public const string IdentityVerify = "identity.verify";

    /// <summary>A verification was refused: that DigiLocker identity already verifies another account (PR-26).</summary>
    public const string IdentityVerifyRefused = "identity.verify.refused";

    /// <summary>A person removed their identity verification (PR-26).</summary>
    public const string IdentityUnverify = "identity.unverify";

    /// <summary>A partner defined a custom user attribute (PR-25).</summary>
    public const string AttributeDefine = "attribute.define";

    /// <summary>A partner retired a custom user attribute.</summary>
    public const string AttributeRetire = "attribute.retire";

    /// <summary>A person's custom attribute values were set (by them, an administrator, or the application).</summary>
    public const string AttributeValuesSet = "attribute.values.set";

    /// <summary>A partner added a custom claim.</summary>
    public const string ClaimMappingAdd = "claim.mapping.add";

    /// <summary>A partner removed a custom claim.</summary>
    public const string ClaimMappingRemove = "claim.mapping.remove";

    /// <summary>A SCIM reconciliation ran.</summary>
    public const string ScimReconcile = "scim.reconcile";

    /// <summary>A SCIM delivery gave up after its retries; the target is marked failing.</summary>
    public const string ScimFailing = "scim.failing";

    /// <summary>A partner added or changed a webhook endpoint (PR-24).</summary>
    public const string WebhookEndpointSave = "webhook.endpoint.save";

    /// <summary>A partner removed a webhook endpoint.</summary>
    public const string WebhookEndpointDelete = "webhook.endpoint.delete";

    /// <summary>A partner rotated a webhook endpoint's signing secret.</summary>
    public const string WebhookSecretRotate = "webhook.secret.rotate";

    /// <summary>A webhook delivery gave up after its retries; the endpoint is marked failing.</summary>
    public const string WebhookFailing = "webhook.failing";

    /// <summary>An administrator downloaded an application's evidence pack (PR-32).</summary>
    public const string EvidenceExport = "evidence.export";

    /// <summary>A signed-in caller was refused by an access check — the management API (R7, ASVS V7.2.2).</summary>
    public const string AccessDenied = "access.denied";

    /// <summary>The token, introspection or revocation endpoint refused a client or a grant — a wrong client secret, or
    /// a spent, expired or replayed code or refresh token (R7, ASVS V7.2.1).</summary>
    public const string TokenRefused = "token.refused";
}
