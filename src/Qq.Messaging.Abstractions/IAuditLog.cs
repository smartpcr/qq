namespace Qq.Messaging.Abstractions;

/// <summary>
/// Persists an immutable audit trail of every human↔agent interaction.
/// </summary>
public interface IAuditLog
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
