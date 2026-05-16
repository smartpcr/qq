// -----------------------------------------------------------------------
// <copyright file="PersistentAuditLogger.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// EF Core-backed <see cref="IAuditLogger"/>. Persists every audit
/// entry passed through either the general-purpose
/// <see cref="IAuditLogger.LogAsync"/> path or the typed
/// <see cref="IAuditLogger.LogHumanResponseAsync"/> path to a single
/// <c>audit_log_entries</c> table discriminated by
/// <see cref="AuditEntryKind"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stage 3.2 scope (iter-2 evaluator item 5).</b> The default
/// in-process registration of <c>NullAuditLogger</c> dropped both audit
/// writes silently, violating the story brief's "persist every human
/// response" guarantee and the Stage 3.2 handoff audit requirement.
/// This logger gives Stage 3.2 a real persistent path while staying
/// schema-compatible with Stage 5.3's planned tenant / platform
/// columns (those are additive — <see cref="AuditLogEntry"/> can grow
/// nullable columns without breaking this writer).
/// </para>
/// <para>
/// <b>Lifetime + scope.</b> Singleton dependency that opens a fresh
/// <see cref="IServiceScope"/> per call to acquire the scoped
/// <see cref="MessagingDbContext"/>. Mirrors
/// <see cref="PersistentOutboundMessageIdIndex"/>,
/// <see cref="PersistentOutboundDeadLetterStore"/>,
/// <see cref="PersistentTaskOversightRepository"/>.
/// </para>
/// </remarks>
public sealed class PersistentAuditLogger : IAuditLogger
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PersistentAuditLogger> _logger;

    public PersistentAuditLogger(
        IServiceScopeFactory scopeFactory,
        ILogger<PersistentAuditLogger> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task LogAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var row = new AuditLogEntry
        {
            EntryId = entry.EntryId,
            EntryKind = AuditEntryKind.General,
            MessageId = entry.MessageId,
            UserId = entry.UserId,
            AgentId = entry.AgentId,
            Action = entry.Action,
            Timestamp = entry.Timestamp,
            CorrelationId = entry.CorrelationId,
            Details = entry.Details,
            QuestionId = null,
            ActionValue = null,
            Comment = null,
        };

        await WriteAsync(row, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var row = new AuditLogEntry
        {
            EntryId = entry.EntryId,
            EntryKind = AuditEntryKind.HumanResponse,
            MessageId = entry.MessageId,
            UserId = entry.UserId,
            AgentId = entry.AgentId,
            Action = "human.response",
            Timestamp = entry.Timestamp,
            CorrelationId = entry.CorrelationId,
            Details = null,
            QuestionId = entry.QuestionId,
            ActionValue = entry.ActionValue,
            Comment = entry.Comment,
        };

        await WriteAsync(row, ct).ConfigureAwait(false);
    }

    private async Task WriteAsync(AuditLogEntry row, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        db.AuditLogEntries.Add(row);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Audit writes are never the hot path; failing loud here
            // could mask the original business event with an unrelated
            // persistence error. Log and continue — the upstream caller
            // already responded to the operator.
            _logger.LogError(
                ex,
                "PersistentAuditLogger failed to persist audit row. EntryId={EntryId} EntryKind={EntryKind} CorrelationId={CorrelationId}",
                row.EntryId,
                row.EntryKind,
                row.CorrelationId);
            throw;
        }
    }
}
