using AgentSwarm.Messaging.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="IAuditLogger"/> that persists every audit
/// entry to the append-only <c>AuditLog</c> table via <see cref="AuditLogDbContext"/>.
/// Implements <c>implementation-plan.md</c> §5.2 step 1 ("Implement
/// <c>SqlAuditLogger : IAuditLogger</c> writing to an append-only <c>AuditLog</c>
/// table…").
/// </summary>
/// <remarks>
/// <para>
/// <b>Insert-only contract</b>: <see cref="LogAsync"/> calls
/// <c>ctx.AuditLog.Add(entity)</c> and <c>SaveChangesAsync</c>. No update or delete
/// surface is exposed on the store; the database-level <c>INSTEAD OF UPDATE</c> /
/// <c>INSTEAD OF DELETE</c> triggers installed by the migration (and the equivalent
/// SQLite <c>BEFORE</c> triggers used by the test fixture) make any attempt to mutate
/// a row fail loudly — defense in depth against both compromised application code
/// and direct privileged-database mutation.
/// </para>
/// <para>
/// <b>Checksum responsibility</b>: callers populate
/// <see cref="AuditEntry.Checksum"/> via
/// <see cref="AuditEntry.ComputeChecksum"/> before passing the entry to
/// <see cref="LogAsync"/>. The store does <b>not</b> recompute and overwrite the
/// caller's checksum — that would silently mask a caller-side computation bug and
/// defeat the tamper-detection guarantee for downstream auditors. Instead, the store
/// <b>recomputes</b> the canonical SHA-256 over the supplied row fields and
/// <b>verifies</b> the result matches <see cref="AuditEntry.Checksum"/> before the
/// INSERT. A mismatch throws <see cref="InvalidOperationException"/> — the row is
/// rejected and the caller surfaces the integrity failure to operators. This is the
/// last line of defence against in-flight tampering (e.g. an in-memory mutation of
/// the <see cref="AuditEntry"/> between checksum computation and the SQL INSERT)
/// without trusting any single caller's computation in isolation.
/// </para>
/// <para>
/// <b>Argument validation</b>: a <see langword="null"/> <see cref="AuditEntry"/>
/// throws <see cref="ArgumentNullException"/>. Unlike <see cref="NoOpAuditLogger"/>,
/// the SQL store is strict: an audit entry that cannot be persisted is a compliance
/// signal the caller must surface (typically by logging the failure and continuing —
/// see <c>CardActionHandler</c> for the canonical try/catch pattern).
/// </para>
/// </remarks>
public sealed class SqlAuditLogger : IAuditLogger
{
    private readonly IDbContextFactory<AuditLogDbContext> _contextFactory;

    /// <summary>
    /// Construct the logger with the DI-bound EF context factory.
    /// </summary>
    /// <param name="contextFactory">EF Core context factory bound by DI.</param>
    public SqlAuditLogger(IDbContextFactory<AuditLogDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc />
    public async Task LogAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Stage 5.2 iter-2 evaluator feedback item #5 — verify the caller-supplied
        // Checksum matches a recomputed canonical SHA-256 over the row's canonical
        // fields BEFORE the INSERT. A mismatch is a tamper / in-flight-mutation
        // signal and is rejected outright. This sits on the persistence boundary so
        // every audit row that lands in the table is provably consistent with its
        // stored Checksum at the moment of insert; downstream auditors who later
        // recompute ComputeChecksum() over the stored columns are checking against
        // a value the store itself has already verified.
        var recomputed = AuditEntry.ComputeChecksum(
            timestamp: entry.Timestamp,
            correlationId: entry.CorrelationId,
            eventType: entry.EventType,
            actorId: entry.ActorId,
            actorType: entry.ActorType,
            tenantId: entry.TenantId,
            agentId: entry.AgentId,
            taskId: entry.TaskId,
            conversationId: entry.ConversationId,
            action: entry.Action,
            payloadJson: entry.PayloadJson,
            outcome: entry.Outcome);

        if (!string.Equals(recomputed, entry.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SqlAuditLogger refused to persist AuditEntry with CorrelationId='{entry.CorrelationId}', " +
                $"EventType='{entry.EventType}': supplied Checksum does not match the canonical SHA-256 " +
                $"recomputed from the entry fields. Supplied='{entry.Checksum}', Recomputed='{recomputed}'. " +
                $"This indicates either a caller-side ComputeChecksum bug, an in-flight mutation of the " +
                $"AuditEntry between checksum computation and the SQL INSERT, or a deliberate tamper " +
                $"attempt. The row was NOT persisted so the AuditLog table preserves its " +
                $"checksum-consistency invariant.");
        }

        await using var ctx = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var entity = new AuditLogEntity
        {
            Timestamp = entry.Timestamp,
            CorrelationId = entry.CorrelationId,
            EventType = entry.EventType,
            ActorId = entry.ActorId,
            ActorType = entry.ActorType,
            TenantId = entry.TenantId,
            AgentId = entry.AgentId,
            TaskId = entry.TaskId,
            ConversationId = entry.ConversationId,
            Action = entry.Action,
            PayloadJson = entry.PayloadJson,
            Outcome = entry.Outcome,
            Checksum = entry.Checksum,
        };

        ctx.AuditLog.Add(entity);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
