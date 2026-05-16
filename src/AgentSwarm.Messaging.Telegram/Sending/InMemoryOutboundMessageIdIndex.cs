// -----------------------------------------------------------------------
// <copyright file="InMemoryOutboundMessageIdIndex.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Telegram.Sending;

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// In-memory fallback <see cref="IOutboundMessageIdIndex"/> registered
/// by <c>AddTelegram</c> via <c>TryAddSingleton</c>. Lets unit tests
/// and dev-mode bootstraps (no persistence wiring) resolve the
/// sender's dependency without crashing, while production replaces
/// this registration with the EF Core-backed
/// <c>PersistentOutboundMessageIdIndex</c> via
/// <c>AddMessagingPersistence</c>'s <c>Replace</c> call.
/// </summary>
/// <remarks>
/// <para>
/// The in-memory store is NOT durable across process restarts — that
/// is the explicit reason it is a fallback rather than the production
/// default. Tests that exercise the durability contract should
/// register <c>PersistentOutboundMessageIdIndex</c> directly.
/// </para>
/// <para>
/// Backed by <see cref="ConcurrentDictionary{TKey,TValue}"/> so the
/// store survives the high-fan-out burst scenarios architecture.md
/// §10.4 calls out (100+ agents alerting simultaneously) without
/// locking on the singleton sender's hot path.
/// </para>
/// </remarks>
internal sealed class InMemoryOutboundMessageIdIndex : IOutboundMessageIdIndex
{
    private readonly ConcurrentDictionary<(long ChatId, long TelegramMessageId), OutboundMessageIdMapping> _store = new();

    public Task StoreAsync(OutboundMessageIdMapping mapping, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        // Last-write-wins to match the persistent impl's upsert
        // semantics — a second StoreAsync for the same (chat,
        // message id) pair (which only happens on sender retry after
        // a cache hiccup) must not crash the send path. Iter-4
        // evaluator item 3 — keyed on the composite (ChatId,
        // TelegramMessageId) so two chats that receive the same
        // numeric message id keep separate mapping entries.
        _store[(mapping.ChatId, mapping.TelegramMessageId)] = mapping;
        return Task.CompletedTask;
    }

    public Task<string?> TryGetCorrelationIdAsync(long chatId, long telegramMessageId, CancellationToken ct)
    {
        return _store.TryGetValue((chatId, telegramMessageId), out var mapping)
            ? Task.FromResult<string?>(mapping.CorrelationId)
            : Task.FromResult<string?>(null);
    }
}
