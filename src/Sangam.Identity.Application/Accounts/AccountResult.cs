namespace Sangam.Identity.Application.Accounts;

/// <summary>Outcome of an account operation: success, or field-keyed errors the screen can show in place.</summary>
public sealed class AccountResult
{
    private AccountResult(bool succeeded, IReadOnlyList<AccountError> errors)
    {
        Succeeded = succeeded;
        Errors = errors;
    }

    /// <summary>Whether the operation completed.</summary>
    public bool Succeeded { get; }

    /// <summary>Errors, empty on success.</summary>
    public IReadOnlyList<AccountError> Errors { get; }

    /// <summary>A successful result.</summary>
    public static AccountResult Success { get; } = new(true, []);

    /// <summary>A failed result.</summary>
    /// <param name="errors">One or more errors.</param>
    /// <returns>The result.</returns>
    public static AccountResult Failed(params AccountError[] errors) => new(false, errors);
}

/// <summary>One error, attached to a field when it belongs to one.</summary>
/// <param name="Field">Field name the error belongs to, or <see langword="null"/> for a page-level error.</param>
/// <param name="Message">Human-readable message in the user's language.</param>
public sealed record AccountError(string? Field, string Message);
