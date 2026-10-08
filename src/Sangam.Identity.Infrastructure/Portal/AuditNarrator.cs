using System.Text.Json;
using Sangam.Identity.Application.Portal;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Identity.Infrastructure.Portal;

/// <summary>
/// Turns an <see cref="AuditEvent"/> into a sentence a person can read. Anything without a
/// phrasing falls back to its action name, so a new action shows up in the portal rather than
/// disappearing from it.
/// </summary>
internal static class AuditNarrator
{
    private static readonly HashSet<string> Sensitive = new(StringComparer.Ordinal)
    {
        AuditActions.UserLoginFail,
        AuditActions.UserPasswordChange,
        AuditActions.UserPasswordResetComplete,
        AuditActions.UserSessionRevoke,
        AuditActions.UserSessionRevokeAll,
        AuditActions.AppAccessRevoke,
        AuditActions.ConsentRevoke,
        AuditActions.AdminUserSuspend,
        AuditActions.AdminUserForceLogout,
        AuditActions.UserAccountDeletionRequest,
        AuditActions.AdminUserRead,
        AuditActions.UserMfaFail,
        AuditActions.UserMfaDisable,
        AuditActions.AppAdminGrant,
        AuditActions.AppAdminRevoke,
        AuditActions.AdminUserMfaResetRequest,
        AuditActions.AdminUserMfaReset,
    };

    public static AuditLine Describe(AuditEvent e, IReadOnlyDictionary<Guid, string> appNames)
    {
        // An administrator acting through an application is the partner's own staff; one acting
        // with no application is a Sangam operator. The two must never be confused in a person's log.
        string actor = e.ActorType switch
        {
            AuditActorType.User => "You", // i18n-key
            AuditActorType.Admin when e.ActorAppId is not null => $"An administrator of {AppName(e.ActorAppId, appNames)}", // i18n-key
            AuditActorType.Admin => "A Sangam operator", // i18n-key
            AuditActorType.Api => AppName(e.ActorAppId, appNames),
            AuditActorType.System => "Sangam",
            _ => "Someone", // i18n-key
        };

        string app = AppName(e.ActorAppId, appNames);
        string summary = e.Action switch
        {
            AuditActions.UserRegister => "You created your Sangam account.",
            AuditActions.UserEmailVerify => "You verified your email address.",
            AuditActions.UserEmailChangeRequest => "You asked to change your email address; a code went to the new address.",
            AuditActions.UserEmailChange => "You changed your email address. The old address was told.",
            AuditActions.UserPasskeyAdd => "You added a passkey.",
            AuditActions.UserPasskeyRemove => "You removed a passkey.",
            AuditActions.UserPasskeyFail => "A passkey sign-in was refused.",
            AuditActions.UserSmsSend => SmsSentence(e.Metadata),
            AuditActions.UserSignatureSign => $"You signed “{Read(e.Metadata, "meaning")}” for {app} (record {Read(e.Metadata, "record_id")}).",
            AuditActions.UserSignatureDecline => $"You declined to sign a record for {app}.",
            AuditActions.UserStepUpRequired => $"{app} asked you to sign in again, with a stronger or more recent sign-in.",
            AuditActions.UserStepUpSuccess => $"Your sign-in met {app}'s request for a stronger or more recent sign-in.",
            AuditActions.UserMobileVerify => "You verified your mobile number.",
            AuditActions.AppInvitationAccept => "You accepted an invitation and joined an organisation in an application.",
            AuditActions.UserLoginSuccess => SignInSentence(e.Metadata),
            AuditActions.UserLoginFail => FailedSignInSentence(e.Metadata),
            AuditActions.UserLogout => "You signed out.",
            AuditActions.UserLogoutApp => $"You signed out from {app}.",
            AuditActions.UserPasswordChange => "You changed your password.",
            AuditActions.UserPasswordResetRequest => "A password reset was requested for your account.",
            AuditActions.UserPasswordResetComplete => "Your password was reset. All devices were signed out.",
            AuditActions.UserOtpIssue => CodeEmailedSentence(e.Metadata),
            AuditActions.UserOtpFail => CodeRefusedSentence(e.Metadata),
            AuditActions.UserSignInPreferenceChange => "You changed how you sign in.",
            AuditActions.UserProfileUpdate => Read(e.Metadata, "mobile_changed") is "true"
                ? "You changed your personal details, including your mobile number (it is unverified again)."
                : "You changed your personal details.",
            AuditActions.UserSessionRevoke => "You ended one of your sessions.",
            AuditActions.UserSessionRevokeAll => "You signed out of all devices.",
            AuditActions.UserDataExport => "You downloaded a copy of your data.",
            AuditActions.UserAccountDeletionRequest => "You asked for your account to be deleted.",
            AuditActions.UserAccountDeletionCancel => "The deletion of your account was cancelled.",
            AuditActions.ConsentGrant => Read(e.Metadata, "basis") == "first_party_implicit"
                ? $"You signed in to {app}, part of Sangam itself, so no separate permission was asked."
                : $"You allowed {app} to use your Sangam account.",
            AuditActions.ConsentDeny => $"You declined to share your account with {app}.",
            AuditActions.ConsentRevoke => $"Your consent for {app} was withdrawn.",
            AuditActions.AppAccessRevoke => $"You revoked {app}'s access to your account.",
            AuditActions.TokenIssue => $"{app} received access to your account.",
            AuditActions.OrgMembershipGrant => Read(e.Metadata, "role") is string role
                ? $"{ByWhom(e, actor, app)} gave you a role in an organisation ({role})."
                : $"{ByWhom(e, actor, app)} gave you a role in an organisation.",
            AuditActions.OrgMembershipRevoke => $"{ByWhom(e, actor, app)} removed one of your organisation roles.",
            AuditActions.AppAdminGrant => Read(e.Metadata, "role") is "owner"
                ? $"{actor} made you an owner of {Read(e.Metadata, "app") ?? app}."
                : $"{actor} made you an administrator of {Read(e.Metadata, "app") ?? app}.",
            AuditActions.AppAdminRevoke => $"{actor} removed you as an administrator of {app}.",
            AuditActions.AdminUserRead => "A Sangam operator opened your account record.",
            AuditActions.AdminUserReinstate => "A Sangam operator lifted the suspension on your account.",
            AuditActions.AdminUserDeleteNow => "A Sangam operator deleted your account.",
            AuditActions.AdminOperatorGrant => "You were given Sangam console access.",
            AuditActions.AdminOperatorRevoke => "Your Sangam console access was removed.",
            AuditActions.UserMfaEnable => "You set up an authenticator app for two-step sign-in.",
            AuditActions.UserMfaDisable => "You removed your authenticator app.",
            AuditActions.UserMfaFail => "An authenticator code was refused.",
            AuditActions.AdminUserSuspend => "A Sangam operator suspended your account.",
            AuditActions.AdminUserForceLogout => "A Sangam operator signed you out everywhere.",
            AuditActions.AdminUserHoldPlace => "A Sangam operator placed a hold on your account.",
            AuditActions.AdminUserHoldClear => "A Sangam operator cleared the hold on your account.",
            AuditActions.AdminUserMfaResetRequest => "Sangam support was asked to reset your two-step sign-in. It waits out a cooling-off period, and you were told so you could cancel it.",
            AuditActions.AdminUserMfaReset => "Your two-step sign-in was reset by Sangam support. Your authenticator was removed and every device was signed out.",
            AuditActions.AdminUserMfaResetUrgent => "A Sangam operator applied the reset of your two-step sign-in at once, without the cooling-off period.",
            AuditActions.AdminUserMfaResetWithdraw => "A Sangam operator withdrew the reset of your two-step sign-in.",
            AuditActions.UserMfaResetCancel => "You cancelled a reset of your two-step sign-in.",
            _ => e.Action,
        };

        return new AuditLine(e.Id, e.OccurredAt, e.Action, summary, actor, e.IpAddress, Sensitive.Contains(e.Action));
    }

    private static string AppName(Guid? appId, IReadOnlyDictionary<Guid, string> names)
        => appId is Guid id && names.TryGetValue(id, out string? name) ? name : "An application"; // i18n-key

    // Whole sentences, never English fragments joined together: each one is translated as it stands (PR-18).
    private static string SignInSentence(string metadata) => (Read(metadata, "mode"), Read(metadata, "channel")) switch
    {
        ("password_and_otp", "sms") => "You signed in with your password and a texted code.",
        ("otp_only", "sms") => "You signed in with a texted code.",
        ("password_and_otp", _) => "You signed in with your password and an emailed code.",
        ("otp_only", _) => "You signed in with an emailed code.",
        ("password", _) => "You signed in with your password.",
        ("passkey", _) => "You signed in with a passkey.",
        _ => "You signed in.",
    };

    private static string FailedSignInSentence(string metadata) => Read(metadata, "reason") switch
    {
        "wrong_password" => "A sign-in attempt failed (wrong password).",
        "unknown_email" => "A sign-in attempt failed (no account with that email).",
        "locked_out" => "A sign-in attempt failed (the account was locked).",
        "Invalid" => "A sign-in attempt failed (wrong code).",
        "Expired" => "A sign-in attempt failed (the code had expired).",
        null => "A sign-in attempt failed.",
        string other => $"A sign-in attempt failed ({other.Replace('_', ' ')}).",
    };

    private static string CodeRefusedSentence(string metadata) => Read(metadata, "reason") switch
    {
        "wrong_password" => "A one-time code was refused (wrong password).",
        "unknown_email" => "A one-time code was refused (no account with that email).",
        "locked_out" => "A one-time code was refused (the account was locked).",
        "Invalid" => "A one-time code was refused (wrong code).",
        "Expired" => "A one-time code was refused (the code had expired).",
        null => "A one-time code was refused.",
        string other => $"A one-time code was refused ({other.Replace('_', ' ')}).",
    };

    private static string CodeEmailedSentence(string metadata) => Read(metadata, "purpose") switch
    {
        "email_verification" => "A one-time code was emailed to you to verify your email.",
        "password_reset" => "A one-time code was emailed to you to reset your password.",
        "sign_in" => "A one-time code was emailed to you to sign in.",
        "email_change" => "A one-time code was emailed to you to confirm your new address.",
        _ => "A one-time code was emailed to you.",
    };

    private static string SmsSentence(string metadata) => (Read(metadata, "template"), Read(metadata, "outcome")) switch
    {
        ("mobile_verification", "sent") => "A code was texted to your mobile to verify your mobile.",
        ("sign_in", "sent") => "A code was texted to your mobile to sign in.",
        (_, "sent") => "A code was texted to your mobile to confirm an action.",
        ("mobile_verification", "limited") => "A code to verify your mobile was not texted: too many were asked for in the last hour.",
        ("sign_in", "limited") => "A code to sign in was not texted: too many were asked for in the last hour.",
        (_, "limited") => "A code to confirm an action was not texted: too many were asked for in the last hour.",
        ("mobile_verification", _) => "A code to verify your mobile could not be texted to your mobile.",
        ("sign_in", _) => "A code to sign in could not be texted to your mobile.",
        _ => "A code to confirm an action could not be texted to your mobile.",
    };

    /// <summary>A person when a person acted; otherwise the application itself (its backend, via the API).</summary>
    private static string ByWhom(AuditEvent e, string actor, string app) => e.ActorType == AuditActorType.Admin ? actor : app;

    private static string? Read(string metadata, string property)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(metadata);
            if (!document.RootElement.TryGetProperty(property, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
