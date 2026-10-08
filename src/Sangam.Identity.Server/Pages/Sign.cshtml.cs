using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using Sangam.Identity.Application.Accounts;
using Sangam.Identity.Application.Signatures;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Enums;
using Sangam.Identity.Server.Authentication;
using Sangam.Identity.Server.Authorization;
using Sangam.Identity.Server.Pages.Account;

namespace Sangam.Identity.Server.Pages;

/// <summary>
/// The signature ceremony (PR-17, SGM-207 §5): a fresh two-factor authentication, then the record, its hash and
/// the meaning of the signature, and the person's own decision to sign or decline.
/// </summary>
public sealed class SignModel : AuthPageModel
{
    private readonly ISignatureService _signatures;
    private readonly IAccountService _accounts;
    private readonly IOptionsMonitor<OpenIddictServerOptions> _server;

    /// <summary>Initialises the page.</summary>
    /// <param name="signatures">Signatures.</param>
    /// <param name="accounts">Accounts.</param>
    /// <param name="server">OpenIddict options, for the issuer name.</param>
    public SignModel(ISignatureService signatures, IAccountService accounts, IOptionsMonitor<OpenIddictServerOptions> server)
    {
        _signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }

    /// <summary>The request.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid RequestId { get; set; }

    /// <summary>The request as shown, when it can be answered.</summary>
    public SignatureCeremony? Ceremony { get; private set; }

    /// <summary>Why it cannot be answered, when it cannot.</summary>
    public string? Problem { get; private set; }

    /// <summary>The signer's name.</summary>
    public string SignerName { get; private set; } = string.Empty;

    /// <summary>Shows the request, after making sure the person has just signed in with two factors.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
        => await PrepareAsync(cancellationToken) ?? Page();

    /// <summary>Signs.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostSignAsync(CancellationToken cancellationToken)
    {
        IActionResult? detour = await PrepareAsync(cancellationToken);
        if (detour is not null || Ceremony is null)
        {
            return detour ?? Page();
        }

        AuthenticationProof proof = AuthenticationProof.FromSession(User);
        string issuer = _server.CurrentValue.Issuer?.AbsoluteUri ?? $"{Request.Scheme}://{Request.Host}{Request.PathBase}/";
        SignatureResult result = await _signatures.SignAsync(
            RequestId, CurrentUserId(), SignerName, issuer, proof.Acr, proof.Methods, proof.AuthenticatedAt!.Value, ClientIp, cancellationToken);
        if (!result.Succeeded)
        {
            Problem = L[result.Message ?? string.Empty];
            Ceremony = null;
            return Page();
        }

        return Redirect(result.RedirectUrl!);
    }

    /// <summary>Declines.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IActionResult> OnPostDeclineAsync(CancellationToken cancellationToken)
    {
        IActionResult? detour = await PrepareAsync(cancellationToken);
        if (detour is not null || Ceremony is null)
        {
            return detour ?? Page();
        }

        SignatureResult result = await _signatures.DeclineAsync(RequestId, CurrentUserId(), ClientIp, cancellationToken);
        if (!result.Succeeded)
        {
            Problem = L[result.Message ?? string.Empty];
            Ceremony = null;
            return Page();
        }

        return Redirect(result.RedirectUrl!);
    }

    private Guid CurrentUserId() => SangamAuthentication.UserId(User) ?? Guid.Empty;

    /// <summary>Loads the request; returns a redirect when the person must first authenticate afresh.</summary>
    private async Task<IActionResult?> PrepareAsync(CancellationToken cancellationToken)
    {
        SignatureCeremony? ceremony = await _signatures.GetAsync(RequestId, cancellationToken);
        if (ceremony is null)
        {
            Problem = L["There is no such signature request. Go back to the application and start again."];
            return null;
        }

        if (ceremony.Status != SignatureStatus.Pending)
        {
            Problem = L["This request has already been answered."];
            return null;
        }

        if (ceremony.Expired)
        {
            Problem = L["This request has expired. Go back to the application and start again."];
            return null;
        }

        // 11.200: two distinct components, and recent. Otherwise authenticate again before anything is shown.
        bool signedIn = User.Identity?.IsAuthenticated == true;
        AuthenticationProof proof = AuthenticationProof.FromSession(User);
        bool fresh = signedIn
            && AuthenticationAssurance.Level(proof.Acr) >= 2
            && proof.AuthenticatedAt is DateTimeOffset at
            && DateTimeOffset.UtcNow - at <= AuthenticationAssurance.SignatureFreshness;
        if (!fresh)
        {
            if (signedIn)
            {
                await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            }

            return Redirect("/login?returnUrl=" + Uri.EscapeDataString("/sign/" + RequestId.ToString("D")));
        }

        if (ceremony.Signer is Guid named && named != CurrentUserId())
        {
            Problem = L["This request is for someone else to sign. Sign out and let them sign in."];
            return null;
        }

        UserSummary? user = await _accounts.FindByIdAsync(CurrentUserId(), cancellationToken);
        if (user is null)
        {
            return Redirect("/login");
        }

        SignerName = user.DisplayName;
        Ceremony = ceremony;
        return null;
    }
}
