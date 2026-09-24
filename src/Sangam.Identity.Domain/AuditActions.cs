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

    /// <summary>Organisation created.</summary>
    public const string OrgCreate = "org.create";

    /// <summary>Organisation updated.</summary>
    public const string OrgUpdate = "org.update";

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
}
