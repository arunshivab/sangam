using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sangam.Identity.Application.Passkeys;
using Sangam.Identity.Server.Authentication;

namespace Sangam.Identity.Server.Pages.Account;

/// <summary>A person's passkeys: list, add, remove (PR-14). Kept on the identity server, the only origin passkeys are bound to.</summary>
[Authorize]
public sealed class PasskeysModel : PageModel
{
    /// <summary>The text catalogue, in the request's language (PR-18).</summary>
    private Microsoft.Extensions.Localization.IStringLocalizer L => PageText.For(HttpContext);

    private readonly IPasskeyService _passkeys;

    /// <summary>Initialises the page.</summary>
    /// <param name="passkeys">Passkeys.</param>
    public PasskeysModel(IPasskeyService passkeys)
    {
        _passkeys = passkeys ?? throw new ArgumentNullException(nameof(passkeys));
    }

    /// <summary>The person's passkeys.</summary>
    public IReadOnlyList<PasskeyRow> Passkeys { get; private set; } = [];

    /// <summary>A confirmation to show.</summary>
    [TempData]
    public string? Notice { get; set; }

    /// <summary>Shows the passkeys.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!_passkeys.Enabled)
        {
            return NotFound();
        }

        Passkeys = await _passkeys.ListAsync(UserId(), cancellationToken);
        return Page();
    }

    /// <summary>Removes a passkey.</summary>
    /// <param name="id">The passkey.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnPostRemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await _passkeys.RemoveAsync(UserId(), id, cancellationToken))
        {
            Notice = L["Passkey removed."];
        }

        return RedirectToPage();
    }

    /// <summary>Starts adding a passkey.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnPostAddOptionsAsync(CancellationToken cancellationToken)
    {
        PasskeyCeremony ceremony = await _passkeys.BeginRegistrationAsync(UserId(), cancellationToken);
        return Content($"{{\"challengeId\":\"{ceremony.ChallengeId:D}\",\"options\":{ceremony.OptionsJson}}}", "application/json");
    }

    /// <summary>Stores the new passkey.</summary>
    /// <param name="answer">The browser's answer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IActionResult> OnPostAddVerifyAsync([FromBody] PasskeyAnswer answer, CancellationToken cancellationToken)
    {
        if (answer is null)
        {
            // A body that does not bind is a broken or forged request, not a server fault.
            return new BadRequestObjectResult(new { error = L["That passkey answer could not be read. Please try again."].Value });
        }

        PasskeyResult result = await _passkeys.CompleteRegistrationAsync(UserId(), answer.ChallengeId, answer.Credential, answer.Name ?? string.Empty, cancellationToken);
        if (!result.Succeeded)
        {
            return new JsonResult(new { error = L[result.Message ?? string.Empty].Value });
        }

        Notice = L[result.Message ?? string.Empty];
        return new JsonResult(new { redirect = "/account/passkeys" }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private Guid UserId() => SangamAuthentication.UserId(User) ?? throw new InvalidOperationException("Signed-in user has no id.");
}
