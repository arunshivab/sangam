namespace Sangam.Identity.Application.Accounts;

/// <summary>
/// rc.11 (founder decision, 13 Oct 2026): signing in with an email, a mobile number or a login ID, and the person's own
/// login ID. Resolving an identifier never says, to anyone, whether an account exists: the sign-in screens behave the
/// same either way.
/// </summary>
public interface ILoginIdService
{
    /// <summary>
    /// The email address of the one account <paramref name="identifier"/> names, or <see langword="null"/> when it names
    /// none. An email is looked up as it is; a mobile number only when it is verified on exactly one account; a login ID
    /// as stored. Deleted accounts are never found.
    /// </summary>
    /// <param name="identifier">What the person typed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> ResolveEmailAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>The person's login ID, and a free suggestion when they have none.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<LoginIdState> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Whether <paramref name="candidate"/> could be the person's login ID, without changing anything.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="candidate">What they typed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<LoginIdCheck> CheckAsync(Guid userId, string candidate, CancellationToken cancellationToken = default);

    /// <summary>Sets or changes the person's login ID; the previous one is held for <see cref="Domain.LoginIds.ReleaseHold"/>.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="candidate">What they typed.</param>
    /// <param name="ipAddress">Client address, for the audit trail.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<LoginIdCheck> SetAsync(Guid userId, string candidate, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Removes the person's login ID; it is held for <see cref="Domain.LoginIds.ReleaseHold"/>.</summary>
    /// <param name="userId">The person.</param>
    /// <param name="ipAddress">Client address, for the audit trail.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveAsync(Guid userId, string? ipAddress, CancellationToken cancellationToken = default);
}

/// <summary>The person's login ID.</summary>
/// <param name="Current">Their login ID, or <see langword="null"/>.</param>
/// <param name="Suggestion">A free login ID to offer (<c>first.last</c> first), or empty.</param>
/// <param name="CanSignInWithMobile">Whether their mobile number is verified, so it can be used to sign in.</param>
public sealed record LoginIdState(string? Current, string Suggestion, bool CanSignInWithMobile);

/// <summary>Whether a login ID can be used.</summary>
/// <param name="Value">The login ID as it would be stored (lower case).</param>
/// <param name="Problem">
/// <see langword="null"/> when it can; otherwise one of <c>length</c>, <c>characters</c>, <c>start</c>, <c>punctuation</c>,
/// <c>reserved</c> (see <see cref="Domain.LoginIds"/>), or <c>taken</c>.
/// </param>
/// <param name="Alternative">A free login ID close to it, when it is taken.</param>
public sealed record LoginIdCheck(string Value, string? Problem, string? Alternative)
{
    /// <summary>Why a login ID cannot be used: someone else has it, or gave it up recently.</summary>
    public const string ProblemTaken = "taken";

    /// <summary>Whether it can be used.</summary>
    public bool Ok => Problem is null;
}
