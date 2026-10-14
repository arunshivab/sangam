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

    /// <summary>The e-mail that someone asked to recover the account (reset two-step sign-in), with the cancel link (D-K; rc.6).</summary>
    public const string TwoStepResetRequested = "two_step_reset_requested";

    /// <summary>An alert e-mail to operators (D-H, D-K).</summary>
    public const string OperatorAlert = "operator_alert";

    /// <summary>The acknowledgement of a grievance, with its reference and the date it will be answered by (D-D).</summary>
    public const string GrievanceAcknowledgement = "grievance_acknowledgement";

    /// <summary>The answer to a grievance (D-D).</summary>
    public const string GrievanceResolution = "grievance_resolution";

    /// <summary>To an application's owners: its SCIM provisioning or a webhook endpoint stopped working (PR-23, PR-24).</summary>
    public const string IntegrationFailing = "integration_failing";

    /// <summary>To the person: their password was changed or reset (R7, ASVS V2.5.5).</summary>
    public const string PasswordChangedNotice = "password_changed_notice";

    /// <summary>To the person: an authenticator app was added to their account (R7, ASVS V2.5.5).</summary>
    public const string AuthenticatorAddedNotice = "authenticator_added_notice";

    /// <summary>To the person: the authenticator app was removed from their account (R7, ASVS V2.5.5).</summary>
    public const string AuthenticatorRemovedNotice = "authenticator_removed_notice";

    /// <summary>To the person: an authenticator code that had already been used was tried again (rc.8, ASVS V2.8.5).</summary>
    public const string AuthenticatorCodeReusedNotice = "authenticator_code_reused_notice";

    /// <summary>To the person: a passkey was added to their account (R7, ASVS V2.5.5).</summary>
    public const string PasskeyAddedNotice = "passkey_added_notice";

    /// <summary>To the person: a passkey was removed from their account (R7, ASVS V2.5.5).</summary>
    public const string PasskeyRemovedNotice = "passkey_removed_notice";

    /// <summary>To the person: someone signed in to their account from a browser not used before (rc.9, ASVS V2.2.3).</summary>
    public const string NewDeviceSignInNotice = "new_device_sign_in_notice";

    /// <summary>To the person: they asked for a sign-in code, but their sign-in uses a password (rc.2).</summary>
    public const string PasswordAccountCodeNotice = "password_account_code_notice";

    /// <summary>To the person: a recovery with DigiLocker did not match their account and waits for an operator (rc.6, SGM-914).</summary>
    public const string RecoveryReviewWaiting = "recovery_review_waiting";

    /// <summary>To the person: an operator refused a recovery with DigiLocker (rc.6, SGM-914).</summary>
    public const string RecoveryRefused = "recovery_refused";

    /// <summary>To the person: their connection to an application will end for inactivity (rc.6, SGM-910 section 7).</summary>
    public const string AppInactivityNotice = "app_inactivity_notice";

    /// <summary>To the person: their connection to an application ended for inactivity (rc.6, SGM-910 section 7).</summary>
    public const string AppConnectionEnded = "app_connection_ended";

    /// <summary>To the person: their unused account will be deleted (rc.6, SGM-910 section 7).</summary>
    public const string AccountInactivityNotice = "account_inactivity_notice";

    /// <summary>To both accounts: a merge was asked for, with the date it takes effect and a link to cancel (rc.7, SGM-914 section 9).</summary>
    public const string AccountMergeRequested = "account_merge_requested";

    /// <summary>To the surviving account: the merge is done (rc.7).</summary>
    public const string AccountMerged = "account_merged";

    /// <summary>To an application's other owners: one of its administrators merged accounts and the role moved (rc.7).</summary>
    public const string AppAdminMerged = "app_admin_merged";

    /// <summary>To the person: they left an organisation, and which of their roles ended (rc.10, P4).</summary>
    public const string OrganisationLeft = "organisation_left";

    /// <summary>To an organisation's organisation admins: someone left it, and which of their roles ended (rc.10, P4).</summary>
    public const string OrganisationMemberLeft = "organisation_member_left";

    /// <summary>
    /// To a person an admin added (rc.12): the activation link. It works only with the code the admin hands over in person,
    /// so the e-mail never carries the code.
    /// </summary>
    public const string StaffActivation = "staff_activation";

    /// <summary>To the admin who added someone (rc.12): joining stopped because the account's details differ from theirs.</summary>
    public const string StaffJoinStopped = "staff_join_stopped";

    /// <summary>To each person at a customer that left an application (rc.12): their places there have ended; the account stays theirs.</summary>
    public const string OrganisationClosed = "organisation_closed";

    /// <summary>
    /// To an organisation's emergency access reviewers (rc.12, M12): someone used emergency access. The case is the
    /// application's own reference; Sangam holds no patient details.
    /// </summary>
    public const string EmergencyAccessUsed = "emergency_access_used";

    /// <summary>Every kind, in the order the consoles list them.</summary>
    public static IReadOnlyList<MessageTemplateKind> All { get; } =
    [
        new(EmailVerification, false, true, ["name", "code", "minutes", "application"], ["code"]),
        new(SignInCode, false, true, ["name", "code", "minutes", "application"], ["code"]),
        new(PasswordReset, false, true, ["name", "code", "minutes", "application"], ["code"]),
        new(Invitation, false, true, ["application", "organisation", "role", "link", "days"], ["link"]),
        new(StaffActivation, false, true, ["name", "application", "organisations", "admin", "link", "days"], ["link"]),
        new(StaffJoinStopped, false, false, ["name", "member", "organisations", "fields"], ["member", "fields"]),
        new(EmailChangeCode, false, false, ["name", "code", "minutes"], ["code"]),
        new(EmailChangedNotice, false, false, ["name", "new_email"], []),
        new(TwoStepResetNotice, false, false, ["name"], []),
        new(PasswordChangedNotice, false, false, ["name"], []),
        new(AuthenticatorAddedNotice, false, false, ["name"], []),
        new(AuthenticatorRemovedNotice, false, false, ["name"], []),
        new(AuthenticatorCodeReusedNotice, false, false, ["name"], []),
        new(PasskeyAddedNotice, false, false, ["name"], []),
        new(PasskeyRemovedNotice, false, false, ["name"], []),
        new(NewDeviceSignInNotice, false, false, ["name", "browser", "address", "time", "link"], ["link"]),
        new(PasswordAccountCodeNotice, false, false, ["name"], []),
        new(TwoStepResetRequested, false, false, ["name", "hours", "effective", "link"], ["link"]),
        new(RecoveryReviewWaiting, false, false, ["name", "link"], ["link"]),
        new(RecoveryRefused, false, false, ["name"], []),
        new(AppInactivityNotice, false, false, ["name", "application", "date"], ["application", "date"]),
        new(AppConnectionEnded, false, false, ["name", "application"], ["application"]),
        new(AccountInactivityNotice, false, false, ["name", "date"], ["date"]),
        new(AccountMergeRequested, false, false, ["name", "survivor", "closed", "hours", "effective", "link"], ["survivor", "closed", "link"]),
        new(AccountMerged, false, false, ["name", "closed"], ["closed"]),
        new(AppAdminMerged, false, false, ["name", "application", "admin"], ["application", "admin"]),
        new(OrganisationLeft, false, false, ["name", "organisation", "roles"], ["organisation", "roles"]),
        new(OrganisationClosed, false, false, ["name", "organisation", "application"], ["organisation", "application"]),
        new(EmergencyAccessUsed, false, false, ["name", "member", "organisation", "application", "case", "reason", "start", "end", "link"], ["member", "case", "reason"]),
        new(OrganisationMemberLeft, false, false, ["name", "member", "organisation", "application", "roles"], ["member", "organisation", "roles"]),
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
