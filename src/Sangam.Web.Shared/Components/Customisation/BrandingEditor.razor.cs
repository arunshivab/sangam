using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Sangam.Identity.Application.Customisation;
using Sangam.Identity.Domain.Enums;

namespace Sangam.Web.Shared.Components.Customisation;

/// <summary>Edits one level's sign-in page branding (PR-19): logo, accent, welcome line per language and links.</summary>
public partial class BrandingEditor : ComponentBase
{
    private static readonly (string Culture, string Name)[] Languages = [("en-IN", "English"), ("hi-IN", "हिन्दी"), ("ml-IN", "മലയാളം")];
    private readonly Dictionary<string, string?> _welcome = new(StringComparer.Ordinal);
    private BrandingSettings? _settings;
    private bool _loaded;
    private string? _accent;
    private string? _help;
    private string? _terms;
    private string? _privacy;
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

    /// <summary>The identity server's address, to show the logo.</summary>
    [Parameter]
    public string IdentityBaseUrl { get; set; } = string.Empty;

    /// <summary>Where "Preview the sign-in page" goes, or null for no preview.</summary>
    [Parameter]
    public string? PreviewUrl { get; set; }

    /// <summary>A prefix for element ids, unique on the page.</summary>
    [Parameter]
    public string Key { get; set; } = "branding";

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        _settings = await Customisation.GetBrandingAsync(UserId, Scope, ScopeId);
        _loaded = true;
        if (_settings is null)
        {
            return;
        }

        _accent = _settings.Accent;
        _help = _settings.HelpUrl;
        _terms = _settings.TermsUrl;
        _privacy = _settings.PrivacyUrl;
        _welcome.Clear();
        foreach ((string culture, string text) in _settings.Welcome)
        {
            _welcome[culture] = text;
        }
    }

    private async Task SaveAsync()
    {
        _busy = true;
        CustomisationResult result = await Customisation.SaveBrandingAsync(UserId, Scope, ScopeId, new BrandingInput(_accent, _welcome, _help, _terms, _privacy), null);
        _busy = false;
        Show(result);
        if (result.Succeeded)
        {
            await LoadAsync();
        }
    }

    private async Task UploadAsync(InputFileChangeEventArgs e)
    {
        _busy = true;
        try
        {
            IBrowserFile file = e.File;
            using MemoryStream buffer = new();
            await using (Stream stream = file.OpenReadStream(maxAllowedSize: 200 * 1024))
            {
                await stream.CopyToAsync(buffer);
            }

            Show(await Customisation.SaveLogoAsync(UserId, Scope, ScopeId, file.ContentType, buffer.ToArray(), null));
            await LoadAsync();
        }
        catch (IOException)
        {
            Show(CustomisationResult.Refused(L["The logo can be at most {0} KB.", 200]));
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RemoveLogoAsync()
    {
        _busy = true;
        Show(await Customisation.RemoveLogoAsync(UserId, Scope, ScopeId, null));
        await LoadAsync();
        _busy = false;
    }

    private void Show(CustomisationResult result)
    {
        _ok = result.Succeeded;
        _message = L[result.Message ?? string.Empty];
    }
}
