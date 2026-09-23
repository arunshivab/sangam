namespace Sangam.Identity.Application.Abstractions;

/// <summary>Appends to the audit log. Writes are never batched with, or rolled back by, the caller's unit of work.</summary>
public interface IAuditWriter
{
    /// <summary>Appends one event.</summary>
    /// <param name="entry">The event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
