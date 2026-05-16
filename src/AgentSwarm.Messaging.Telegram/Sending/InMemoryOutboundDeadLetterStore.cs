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
/// <remarks>
/// <para>
/// Backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed
/// on <see cref="OutboundDeadLetterRecord.DeadLetterId"/>. The
/// dictionary key + <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>
/// enforces the idempotency contract documented on
/// <see cref="IOutboundDeadLetterStore"/> (a second
/// <see cref="RecordAsync"/> call with the same
/// <c>DeadLetterId</c> is a no-op) so unit tests that exercise the
/// sender's retry-of-<c>RecordAsync</c> path produce the same
/// observable outcome here as they do against the EF Core-backed
/// <c>PersistentOutboundDeadLetterStore</c>, which enforces the same
/// contract via <c>FindAsync</c>-then-skip plus a SQLite unique-key
/// catch. A <see cref="ConcurrentBag{T}"/> was rejected for this
/// reason: it would silently accept duplicate records and let
/// in-memory tests pass against a regression that drops the
/// duplicate-check in the persistent store (or vice versa).
/// </para>
/// </remarks>
internal sealed class InMemoryOutboundDeadLetterStore : IOutboundDeadLetterStore
{
    private readonly ConcurrentDictionary<Guid, OutboundDeadLetterRecord> _store = new();

    public Task RecordAsync(OutboundDeadLetterRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        _store.TryAdd(record.DeadLetterId, record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboundDeadLetterRecord>> GetByCorrelationIdAsync(
        string correlationId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        IReadOnlyList<OutboundDeadLetterRecord> matches = _store.Values
            .Where(r => r.CorrelationId == correlationId)
            .OrderBy(r => r.FailedAt)
            .ToList();
        return Task.FromResult(matches);
    }
}
