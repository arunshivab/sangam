using Sangam.Identity.Application.Abstractions;

namespace Sangam.Identity.Infrastructure.Services;

/// <summary>Wall-clock implementation of <see cref="IClock"/>.</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
