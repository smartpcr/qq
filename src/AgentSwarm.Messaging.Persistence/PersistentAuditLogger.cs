using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// EF Core-backed implementation of <see cref="IAuditLogger"/>. Persists rows
/// into the shared <c>AuditLog</c> table (architecture.md §3.1 / §4.10).
/// Connector-specific identifiers (Discord guild/channel/interaction/thread)
/// are stored verbatim in the <see cref="AuditEntry.Details"/> JSON column so
/// the schema stays platform-neutral while preserving full per-platform
/// provenance.
/// </summary>
public sealed class PersistentAuditLogger : IAuditLogger
{
    private readonly IDbContextFactory<MessagingDbContext> _contextFactory;

    /// <summary>Creates a new audit logger.</summary>
    public PersistentAuditLogger(IDbContextFactory<MessagingDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc />
    public async Task LogAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var row = new AuditLogEntry
        {
            // Id is assigned by the property initializer (Guid.NewGuid).
            Platform = entry.Platform,
            ExternalUserId = entry.ExternalUserId,
            MessageId = entry.MessageId,
            Details = entry.Details,
            Timestamp = entry.Timestamp,
            CorrelationId = entry.CorrelationId,
        };

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        context.AuditLog.Add(row);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // We persist human responses into the same AuditLog table by encoding
        // the human-decision-specific fields into the Details JSON. The
        // schema stays platform-neutral, downstream readers project the
        // typed columns back out via the JSON path. Architecture.md §4.10
        // explicitly permits this collapse provided the extra fields remain
        // queryable -- AuditLog.Details is a TEXT column SQLite can json_extract.
        var details = HumanResponseDetailsEncoder.Combine(
            entry.Details,
            entry.QuestionId,
            entry.SelectedActionId,
            entry.ActionValue,
            entry.Comment);

        var row = new AuditLogEntry
        {
            Platform = entry.Platform,
            ExternalUserId = entry.ExternalUserId,
            MessageId = entry.MessageId,
            Details = details,
            Timestamp = entry.Timestamp,
            CorrelationId = entry.CorrelationId,
        };

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        context.AuditLog.Add(row);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
