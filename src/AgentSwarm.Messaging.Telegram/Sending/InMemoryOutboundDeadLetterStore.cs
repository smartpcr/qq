// -----------------------------------------------------------------------
// <copyright file="InMemoryOutboundDeadLetterStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Telegram.Sending;

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// In-memory fallback <see cref="IOutboundDeadLetterStore"/>
/// registered by <c>AddTelegram</c> via <c>TryAddSingleton</c>. Lets
/// unit tests and dev-mode bootstraps (no persistence wiring) resolve
/// the sender's dependency on a dead-letter ledger without crashing;
/// production replaces this registration with the EF Core-backed
/// <c>PersistentOutboundDeadLetterStore</c> via
/// <c>AddMessagingPersistence</c>'s <c>Replace</c> call.
/// </summary>
internal sealed class InMemoryOutboundDeadLetterStore : IOutboundDeadLetterStore
{
    private readonly ConcurrentBag<OutboundDeadLetterRecord> _store = new();

    public Task RecordAsync(OutboundDeadLetterRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        _store.Add(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboundDeadLetterRecord>> GetByCorrelationIdAsync(
        string correlationId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        IReadOnlyList<OutboundDeadLetterRecord> matches = _store
            .Where(r => r.CorrelationId == correlationId)
            .OrderBy(r => r.FailedAt)
            .ToList();
        return Task.FromResult(matches);
    }
}
