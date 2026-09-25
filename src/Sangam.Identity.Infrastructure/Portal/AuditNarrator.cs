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
    };

    public static AuditLine Describe(AuditEvent e, IReadOnlyDictionary<Guid, string> appNames)
    {
        string actor = e.ActorType switch
        {
            AuditActorType.User => "You",
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
            AuditActions.ConsentGrant => $"You allowed {app} to use your Sangam account.",
            AuditActions.ConsentDeny => $"You declined to share your account with {app}.",
            AuditActions.ConsentRevoke => $"Your consent for {app} was withdrawn.",
            AuditActions.AppAccessRevoke => $"You revoked {app}'s access to your account.",
            AuditActions.TokenIssue => $"{app} received access to your account.",
            AuditActions.OrgMembershipGrant => $"{app} gave you a role in an organisation{RoleSuffix(e.Metadata)}.",
            AuditActions.OrgMembershipRevoke => $"{app} removed one of your organisation roles.",
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

    private static string ModeSuffix(string metadata) => Read(metadata, "mode") switch
    {
        "password_and_otp" => " with your password and an emailed code",
        "otp_only" => " with an emailed code",
        "password" => " with your password",
        _ => string.Empty,
    };

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
        _ => string.Empty,
    };

    private static string MobileSuffix(string metadata) => Read(metadata, "mobile_changed") is "true" ? ", including your mobile number (it is unverified again)" : string.Empty;

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
