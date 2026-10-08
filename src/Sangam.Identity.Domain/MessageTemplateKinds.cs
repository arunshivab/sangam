namespace Sangam.Identity.Domain;

/// <summary>One kind of message Sangam sends, and what its template may and must contain (SGM-209 §4).</summary>
/// <param name="Code">Stable code, stored with each template.</param>
/// <param name="Sms">Whether it is a text message (otherwise an e-mail).</param>
/// <param name="PartnerEditable">Whether an application or organisation may override it; otherwise only the platform.</param>
/// <param name="Variables">The variables it may use.</param>
/// <param name="Required">The variables it must use.</param>
public sealed record MessageTemplateKind(string Code, bool Sms, bool PartnerEditable, IReadOnlyList<string> Variables, IReadOnlyList<string> Required);

/// <summary>Every kind of message, and the variables each template may use.</summary>
public static class MessageTemplateKinds
{
    /// <summary>The code that verifies a new account's e-mail address.</summary>
    public const string EmailVerification = "email_verification";

    /// <summary>A sign-in code by e-mail.</summary>
    public const string SignInCode = "sign_in_code";

    /// <summary>A password-reset code.</summary>
    public const string PasswordReset = "password_reset";

    /// <summary>The code that confirms a new e-mail address.</summary>
    public const string EmailChangeCode = "email_change_code";

    /// <summary>The notice to the old address after a change.</summary>
    public const string EmailChangedNotice = "email_changed_notice";

    /// <summary>An invitation to an organisation and role.</summary>
    public const string Invitation = "invitation";

    /// <summary>The security notice after support reset two-step sign-in.</summary>
    public const string TwoStepResetNotice = "two_step_reset_notice";

    /// <summary>The notice to an existing account when someone registers with its address (V-09).</summary>
    public const string RegistrationAttemptNotice = "registration_attempt_notice";

    /// <summary>The e-mail notice to an existing account when someone registers with its mobile number (D-L), sent when it cannot be texted.</summary>
    public const string MobileAttemptNotice = "mobile_attempt_notice";

    /// <summary>A sign-in code by SMS.</summary>
    public const string SmsSignIn = "sms_sign_in";

    /// <summary>The SMS that verifies a mobile number.</summary>
    public const string SmsMobileVerification = "sms_mobile_verification";

    /// <summary>The SMS that confirms an action.</summary>
    public const string SmsStepUp = "sms_step_up";

    /// <summary>The SMS to an existing account's mobile when someone registers with it (D-L).</summary>
    public const string SmsRegistrationNotice = "sms_registration_notice";

    /// <summary>The SMS that support was asked to reset two-step sign-in (D-K).</summary>
    public const string SmsResetNotice = "sms_reset_notice";

    /// <summary>The SMS alert to operators (D-H, D-K).</summary>
    public const string SmsOperatorAlert = "sms_operator_alert";

    /// <summary>The e-mail that support was asked to reset two-step sign-in, with the cancel link (D-K).</summary>
    public const string TwoStepResetRequested = "two_step_reset_requested";

    /// <summary>An alert e-mail to operators (D-H, D-K).</summary>
    public const string OperatorAlert = "operator_alert";

    /// <summary>The acknowledgement of a grievance, with its reference and the date it will be answered by (D-D).</summary>
    public const string GrievanceAcknowledgement = "grievance_acknowledgement";

    /// <summary>The answer to a grievance (D-D).</summary>
    public const string GrievanceResolution = "grievance_resolution";

    /// <summary>To an application's owners: its SCIM provisioning or a webhook endpoint stopped working (PR-23, PR-24).</summary>
    public const string IntegrationFailing = "integration_failing";

    /// <summary>Every kind, in the order the consoles list them.</summary>
    public static IReadOnlyList<MessageTemplateKind> All { get; } =
    [
        new(EmailVerification, false, true, ["name", "code", "minutes", "application"], ["code"]),
        new(SignInCode, false, true, ["name", "code", "minutes", "application"], ["code"]),
        new(PasswordReset, false, true, ["name", "code", "minutes", "application"], ["code"]),
        new(Invitation, false, true, ["application", "organisation", "role", "link", "days"], ["link"]),
        new(EmailChangeCode, false, false, ["name", "code", "minutes"], ["code"]),
        new(EmailChangedNotice, false, false, ["name", "new_email"], []),
        new(TwoStepResetNotice, false, false, ["name"], []),
        new(TwoStepResetRequested, false, false, ["name", "hours", "effective", "link"], ["link"]),
        new(OperatorAlert, false, false, ["summary", "details"], ["summary"]),
        new(GrievanceAcknowledgement, false, false, ["name", "reference", "received", "resolve_by"], ["reference"]),
        new(GrievanceResolution, false, false, ["name", "reference", "resolution"], ["reference", "resolution"]),
        new(IntegrationFailing, false, false, ["name", "application", "integration", "error", "link"], ["integration", "link"]),
        new(RegistrationAttemptNotice, false, false, [], []),
        new(MobileAttemptNotice, false, false, [], []),
        new(SmsSignIn, true, false, [], []),
        new(SmsMobileVerification, true, false, [], []),
        new(SmsStepUp, true, false, [], []),
        new(SmsRegistrationNotice, true, false, [], []),
        new(SmsResetNotice, true, false, [], []),
        new(SmsOperatorAlert, true, false, [], []),
    ];

    /// <summary>The kind with this code, or null.</summary>
    /// <param name="code">The code.</param>
    public static MessageTemplateKind? Find(string? code) => All.FirstOrDefault(k => k.Code == code);
}
