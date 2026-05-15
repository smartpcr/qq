using System.Collections.Concurrent;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// In-memory thread-safe <see cref="IMessageIdTracker"/>. Suitable for
/// dev/local and unit tests where mapping durability across restarts is
/// not required. Production deployments register
/// <c>AgentSwarm.Messaging.Persistence.PersistentMessageIdTracker</c>
/// via <c>AddMessagingPersistence</c>; the Telegram DI extension uses
/// <c>TryAddSingleton</c> so the persistent registration always wins.
/// </summary>
/// <remarks>
/// <para>
/// The dictionary key is the (<c>chatId</c>, <c>telegramMessageId</c>)
/// tuple — Telegram assigns <c>message_id</c> values that are only unique
/// within a single chat, so a singleton key would let a send to chat A
/// silently overwrite a send to chat B that happened to receive the same
/// numeric id.
/// </para>
/// </remarks>
internal sealed class InMemoryMessageIdTracker : IMessageIdTracker
{
    private readonly ConcurrentDictionary<(long ChatId, long MessageId), string> _map = new();

    public Task TrackAsync(
        long chatId,
        long telegramMessageId,
        string correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException(
                "CorrelationId must be non-null, non-empty, and non-whitespace.",
                nameof(correlationId));
        }
        _map[(chatId, telegramMessageId)] = correlationId;
        return Task.CompletedTask;
    }

    public Task<string?> TryGetCorrelationIdAsync(
        long chatId,
        long telegramMessageId,
        CancellationToken ct)
    {
        var found = _map.TryGetValue((chatId, telegramMessageId), out var correlationId)
            ? correlationId
            : null;
        return Task.FromResult(found);
    }
}
