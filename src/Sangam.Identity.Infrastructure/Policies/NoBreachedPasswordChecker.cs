using Sangam.Identity.Application.Security;

namespace Sangam.Identity.Infrastructure.Policies;

/// <summary>Used when the breach-check service is off: nothing is checked and nothing leaves Sangam.</summary>
public sealed class NoBreachedPasswordChecker : IBreachedPasswordChecker
{
    /// <inheritdoc />
    public bool Available => false;

    /// <inheritdoc />
    public Task<bool?> IsBreachedAsync(string password, CancellationToken cancellationToken = default)
        => Task.FromResult<bool?>(null);
}
