namespace Sangam.Identity.Application.Abstractions;

/// <summary>Source of the current time, so use cases can be tested at a fixed instant.</summary>
public interface IClock
{
    /// <summary>Gets the current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
}
