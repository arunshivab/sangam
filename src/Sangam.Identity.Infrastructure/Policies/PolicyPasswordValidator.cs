using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Sangam.Identity.Application.Security;
using Sangam.Identity.Domain;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Policies;

/// <summary>
/// Applies the policy rules whenever a password is set — registration, reset and replacement (PR-16): the longest
/// minimum over every application and organisation the person belongs to, and, when the breach-check service is
/// on, a password that appears in known breaches is refused.
/// </summary>
public sealed class PolicyPasswordValidator : IPasswordValidator<SangamUser>
{
    /// <summary>Error code: shorter than a policy requires.</summary>
    public const string TooShortCode = "PasswordTooShortForPolicy";

    /// <summary>Error code: found in known breaches.</summary>
    public const string BreachedCode = "PasswordBreached";

    /// <summary>The message for a breached password.</summary>
    public const string BreachedMessage = "This password has appeared in a data breach elsewhere, so it is not safe to use. Choose a different one.";

    private readonly ISecurityPolicyService _policies;
    private readonly IBreachedPasswordChecker _breaches;

    /// <summary>Initialises the validator.</summary>
    /// <param name="policies">Policies.</param>
    /// <param name="breaches">Breach checker.</param>
    public PolicyPasswordValidator(ISecurityPolicyService policies, IBreachedPasswordChecker breaches)
    {
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _breaches = breaches ?? throw new ArgumentNullException(nameof(breaches));
    }

    /// <inheritdoc />
    public async Task<IdentityResult> ValidateAsync(UserManager<SangamUser> manager, SangamUser user, string? password)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(user);
        if (password is null)
        {
            return IdentityResult.Success;
        }

        SecurityPolicy policy = await _policies.ForPasswordAsync(user.Id).ConfigureAwait(false);
        if (password.Length < policy.MinPasswordLength)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = TooShortCode,
                Description = string.Create(CultureInfo.InvariantCulture, $"An organisation you belong to requires a password of at least {policy.MinPasswordLength} characters."),
            });
        }

        return await _breaches.IsBreachedAsync(password).ConfigureAwait(false) == true
            ? IdentityResult.Failed(new IdentityError { Code = BreachedCode, Description = BreachedMessage })
            : IdentityResult.Success;
    }
}
