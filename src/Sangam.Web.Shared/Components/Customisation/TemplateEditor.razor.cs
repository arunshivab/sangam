using Microsoft.AspNetCore.Components;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Web.Shared.Components.Customisation;

/// <summary>Edits one level's message templates (PR-19), language by language, with a preview of the e-mail.</summary>
public partial class TemplateEditor : ComponentBase
{
    private IReadOnlyList<TemplateRow>? _rows;
    private bool _loaded;
    private TemplateRow? _current;
    private string? _kind;
    private string _language = "en-IN";
    private string? _subject;
    private string? _body;
    private string? _dlt;
    private EmailPreview? _preview;
    private bool _busy;
    private bool _ok;
    private string? _message;

    /// <summary>The person editing.</summary>
    [Parameter]
    [EditorRequired]
    public Guid UserId { get; set; }

    /// <summary>The level.</summary>
    [Parameter]
    [EditorRequired]
    public CustomisationScope Scope { get; set; }

    /// <summary>The application or organisation; null for the platform.</summary>
    [Parameter]
    public Guid? ScopeId { get; set; }

    /// <summary>The panel heading, in the reader's language.</summary>
    [Parameter]
    public string Heading { get; set; } = string.Empty;

    /// <summary>The name in the e-mail header of a preview (the application, or Sangam).</summary>
    [Parameter]
    public string PreviewHeading { get; set; } = "Sangam";

    /// <summary>A prefix for element ids, unique on the page.</summary>
    [Parameter]
    public string Key { get; set; } = "templates";

    private bool IsSms => MessageTemplateKinds.Find(_kind)?.Sms == true;

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        _rows = await Customisation.ListTemplatesAsync(UserId, Scope, ScopeId);
        _loaded = true;
        Pick(_kind ?? (_rows is { Count: > 0 } rows ? rows[0].Kind : null), _language);
    }

    private void Pick(string? kind, string? language)
    {
        _kind = kind;
        _language = language ?? "en-IN";
        _current = _rows?.FirstOrDefault(r => r.Kind == _kind && r.Language == _language);
        _subject = _current?.Subject;
        _body = _current?.Body;
        _dlt = _current?.DltTemplateId;
        _preview = null;
        _message = null;
    }

    private async Task SaveAsync()
    {
        _busy = true;
        CustomisationResult result = await Customisation.SaveTemplateAsync(UserId, Scope, ScopeId, new TemplateInput(_kind!, _language, _subject, _body ?? string.Empty, _dlt), null);
        await ReloadAsync(result);
    }

    private async Task ResetAsync()
    {
        _busy = true;
        CustomisationResult result = await Customisation.ResetTemplateAsync(UserId, Scope, ScopeId, _kind!, _language, null);
        await ReloadAsync(result);
    }

    private async Task ReloadAsync(CustomisationResult result)
    {
        if (result.Succeeded)
        {
            _rows = await Customisation.ListTemplatesAsync(UserId, Scope, ScopeId);
            Pick(_kind, _language);
        }

        _busy = false;
        _ok = result.Succeeded;
        _message = L[result.Message ?? string.Empty];
    }

    private void PreviewNow()
        => _preview = Customisation.Preview(_kind!, _language, _subject, _body ?? string.Empty, PreviewHeading, null);

    private string KindName(string kind) => kind switch
    {
        MessageTemplateKinds.EmailVerification => L["E-mail: verification code for a new account"],
        MessageTemplateKinds.SignInCode => L["E-mail: sign-in code"],
        MessageTemplateKinds.PasswordReset => L["E-mail: password reset code"],
        MessageTemplateKinds.Invitation => L["E-mail: invitation to an organisation"],
        MessageTemplateKinds.EmailChangeCode => L["E-mail: code to confirm a new address"],
        MessageTemplateKinds.EmailChangedNotice => L["E-mail: notice that the address changed"],
        MessageTemplateKinds.TwoStepResetNotice => L["E-mail: notice that two-step sign-in was reset"],
        MessageTemplateKinds.RegistrationAttemptNotice => L["E-mail: someone tried to register with this address"],
        MessageTemplateKinds.PasswordAccountCodeNotice => L["E-mail: someone asked for a sign-in code, but the account signs in with a password"],
        MessageTemplateKinds.MobileAttemptNotice => L["E-mail: someone tried to register with this mobile"],
        MessageTemplateKinds.SmsSignIn => L["Text message: sign-in code"],
        MessageTemplateKinds.SmsMobileVerification => L["Text message: verify a mobile"],
        MessageTemplateKinds.SmsRegistrationNotice => L["Text message: someone tried to register with this mobile"],
        MessageTemplateKinds.SmsResetNotice => L["Text message: support was asked to reset two-step sign-in"],
        MessageTemplateKinds.SmsOperatorAlert => L["Text message: alert to the platform owner"],
        MessageTemplateKinds.TwoStepResetRequested => L["E-mail: support was asked to reset two-step sign-in (with the cancel link)"],
        MessageTemplateKinds.OperatorAlert => L["E-mail: alert to the platform owner"],
        MessageTemplateKinds.GrievanceAcknowledgement => L["E-mail: acknowledgement of a grievance"],
        MessageTemplateKinds.GrievanceResolution => L["E-mail: the answer to a grievance"],
        MessageTemplateKinds.IntegrationFailing => L["E-mail: an application's provisioning or webhook stopped working"],
        MessageTemplateKinds.PasswordChangedNotice => L["E-mail: notice that the password changed"],
        MessageTemplateKinds.AuthenticatorAddedNotice => L["E-mail: notice that an authenticator app was added"],
        MessageTemplateKinds.AuthenticatorRemovedNotice => L["E-mail: notice that the authenticator app was removed"],
        MessageTemplateKinds.PasskeyAddedNotice => L["E-mail: notice that a passkey was added"],
        MessageTemplateKinds.PasskeyRemovedNotice => L["E-mail: notice that a passkey was removed"],
        _ => L["Text message: confirm an action"],
    };

    private string SourceText(string source) => source switch
    {
        "here" => L["Set here."],
        "organisation" => L["From an organisation above this one."],
        "application" => L["From the application."],
        "platform" => L["From Sangam's own defaults."],
        "configuration" => L["Registered in configuration."],
        "none" => L["Not set yet: the English text is sent."],
        _ => L["Sangam's own text."],
    };
}
