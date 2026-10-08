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
    };

    public static AuditLine Describe(AuditEvent e, IReadOnlyDictionary<Guid, string> appNames)
    {
        // An administrator acting through an application is the partner's own staff; one acting
        // with no application is a Sangam operator. The two must never be confused in a person's log.
        string actor = e.ActorType switch
        {
            AuditActorType.User => "You",
            AuditActorType.Admin when e.ActorAppId is not null => "An administrator of " + AppName(e.ActorAppId, appNames),
            AuditActorType.Admin => "A Sangam operator",
            AuditActorType.Api => AppName(e.ActorAppId, appNames),
            AuditActorType.System => "Sangam",
            _ => "Someone",
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
            AuditActions.UserMobileVerify => "You verified your mobile number.",
            AuditActions.AppInvitationAccept => "You accepted an invitation and joined an organisation in an application.",
            AuditActions.UserLoginSuccess => "You signed in" + ModeSuffix(e.Metadata) + ".",
            AuditActions.UserLoginFail => "A sign-in attempt failed" + ReasonSuffix(e.Metadata) + ".",
            AuditActions.UserLogout => "You signed out.",
            AuditActions.UserLogoutApp => $"You signed out from {app}.",
            AuditActions.UserPasswordChange => "You changed your password.",
            AuditActions.UserPasswordResetRequest => "A password reset was requested for your account.",
            AuditActions.UserPasswordResetComplete => "Your password was reset. All devices were signed out.",
            AuditActions.UserOtpIssue => "A one-time code was emailed to you" + PurposeSuffix(e.Metadata) + ".",
            AuditActions.UserOtpFail => "A one-time code was refused" + ReasonSuffix(e.Metadata) + ".",
            AuditActions.UserSignInPreferenceChange => "You changed how you sign in.",
            AuditActions.UserProfileUpdate => "You changed your personal details" + MobileSuffix(e.Metadata) + ".",
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
            AuditActions.OrgMembershipGrant => $"{ByWhom(e, actor, app)} gave you a role in an organisation{RoleSuffix(e.Metadata)}.",
            AuditActions.OrgMembershipRevoke => $"{ByWhom(e, actor, app)} removed one of your organisation roles.",
            AuditActions.AppAdminGrant => $"{actor} made you {AdminNoun(e.Metadata)} of {Read(e.Metadata, "app") ?? app}.",
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
            _ => e.Action,
        };

        return new AuditLine(e.Id, e.OccurredAt, e.Action, summary, actor, e.IpAddress, Sensitive.Contains(e.Action));
    }

    private static string AppName(Guid? appId, IReadOnlyDictionary<Guid, string> names)
        => appId is Guid id && names.TryGetValue(id, out string? name) ? name : "An application";

    private static string ModeSuffix(string metadata) => (Read(metadata, "mode"), Read(metadata, "channel")) switch
    {
        ("password_and_otp", "sms") => " with your password and a texted code",
        ("otp_only", "sms") => " with a texted code",
        ("password_and_otp", _) => " with your password and an emailed code",
        ("otp_only", _) => " with an emailed code",
        ("password", _) => " with your password",
        ("passkey", _) => " with a passkey",
        _ => string.Empty,
    };

    private static string SmsSentence(string metadata)
    {
        string what = Read(metadata, "template") switch
        {
            "mobile_verification" => "to verify your mobile",
            "sign_in" => "to sign in",
            _ => "to confirm an action",
        };
        return Read(metadata, "outcome") switch
        {
            "sent" => $"A code was texted to your mobile {what}.",
            "limited" => $"A code {what} was not texted: too many were asked for in the last hour.",
            _ => $"A code {what} could not be texted to your mobile.",
        };
    }

    private static string ReasonSuffix(string metadata) => Read(metadata, "reason") switch
    {
        "wrong_password" => " (wrong password)",
        "unknown_email" => " (no account with that email)",
        "locked_out" => " (the account was locked)",
        "Invalid" => " (wrong code)",
        "Expired" => " (the code had expired)",
        null => string.Empty,
        string other => " (" + other.Replace('_', ' ') + ")",
    };

    private static string PurposeSuffix(string metadata) => Read(metadata, "purpose") switch
    {
        "email_verification" => " to verify your email",
        "password_reset" => " to reset your password",
        "sign_in" => " to sign in",
        "email_change" => " to confirm your new address",
        _ => string.Empty,
    };

    private static string MobileSuffix(string metadata) => Read(metadata, "mobile_changed") is "true" ? ", including your mobile number (it is unverified again)" : string.Empty;

    /// <summary>A person when a person acted; otherwise the application itself (its backend, via the API).</summary>
    private static string ByWhom(AuditEvent e, string actor, string app) => e.ActorType == AuditActorType.Admin ? actor : app;

    private static string AdminNoun(string metadata) => Read(metadata, "role") is "owner" ? "an owner" : "an administrator";

    private static string RoleSuffix(string metadata) => Read(metadata, "role") is string role ? $" ({role})" : string.Empty;

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
