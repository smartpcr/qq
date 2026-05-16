// -----------------------------------------------------------------------
// <copyright file="PersistentOutboundDeadLetterStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// EF Core-backed <see cref="IOutboundDeadLetterStore"/>. Persists
/// the dead-letter ledger written by <c>TelegramMessageSender</c> on
/// retry exhaustion (iter-4 evaluator item 4) so the operator audit
/// trail survives a worker restart.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime + scope.</b> Registered as a singleton; the impl uses
/// <see cref="IServiceScopeFactory"/> to create a fresh scope per call,
/// bridging the singleton sender to the scoped
/// <see cref="MessagingDbContext"/>. Matches
/// <see cref="PersistentOutboundMessageIdIndex"/>'s pattern.
/// </para>
/// </remarks>
internal sealed class PersistentOutboundDeadLetterStore : IOutboundDeadLetterStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PersistentOutboundDeadLetterStore> _logger;

    public PersistentOutboundDeadLetterStore(
        IServiceScopeFactory scopeFactory,
        ILogger<PersistentOutboundDeadLetterStore> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RecordAsync(OutboundDeadLetterRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        // Idempotent insert keyed on DeadLetterId. Caller (the sender)
        // generates a fresh GUID per dead-letter event so the
        // happy-path is always insert; a duplicate DeadLetterId only
        // happens when the same RecordAsync call is retried (e.g.
        // because the prior attempt's SaveChangesAsync threw mid-
        // round-trip), in which case we treat the duplicate as
        // success.
        var existing = await db.OutboundDeadLetters
            .FindAsync(new object[] { record.DeadLetterId }, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        db.OutboundDeadLetters.Add(record);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqlite
            && sqlite.SqliteErrorCode == 19)
        {
            _logger.LogDebug(
                ex,
                "Outbound dead-letter record DeadLetterId={DeadLetterId} was inserted concurrently; treating duplicate-key as success.",
                record.DeadLetterId);
        }
    }

    public async Task<IReadOnlyList<OutboundDeadLetterRecord>> GetByCorrelationIdAsync(
        string correlationId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        return await db.OutboundDeadLetters
            .AsNoTracking()
            .Where(x => x.CorrelationId == correlationId)
            .OrderBy(x => x.FailedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
