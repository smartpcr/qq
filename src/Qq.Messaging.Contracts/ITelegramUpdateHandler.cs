using Qq.Messaging.Abstractions;

namespace Qq.Messaging.Contracts;

/// <summary>
/// Processes a raw Telegram update into a normalized <see cref="InboundInteraction"/>
/// and dispatches it through the interaction router.
/// </summary>
public interface ITelegramUpdateHandler
{
    /// <summary>
    /// Handle a single Telegram update.
    /// Implementations must perform deduplication, authorization, and routing.
    /// </summary>
    Task HandleUpdateAsync(
        string updateJson,
        CancellationToken cancellationToken = default);
}
