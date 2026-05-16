namespace AgentSwarm.Messaging.Telegram.Sending;

/// <summary>
/// Configuration POCO for the dual-layer token-bucket rate limiter
/// enforced by <see cref="TelegramMessageSender"/>. Bound from the
/// <c>Telegram:RateLimits</c> configuration section (see
/// <see cref="TelegramOptions.RateLimits"/>) per implementation-plan.md
/// Stage 2.3 and architecture.md §10.4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two layers, two binding constraints.</b> The global limiter caps
/// outbound throughput across all chats (Telegram's documented 30 msg/s
/// soft limit per bot); the per-chat limiter caps throughput to a single
/// chat (Telegram's documented 20 msg/min per chat).
/// </para>
/// <para>
/// <b>Defaults match architecture.md §10.4</b> — changing them changes
/// the SLO envelope assumed by the §10.4 burst math.
/// </para>
/// </remarks>
public sealed class RateLimitOptions
{
    /// <summary>
    /// Configuration sub-section name (relative to <see cref="TelegramOptions.SectionName"/>).
    /// </summary>
    public const string SectionName = "RateLimits";

    /// <summary>
    /// Global token-bucket refill rate, tokens per second. Default 30
    /// matches Telegram Bot API's documented soft limit per bot.
    /// </summary>
    public int GlobalPerSecond { get; set; } = 30;

    /// <summary>
    /// Maximum number of global tokens the bucket can hold (burst
    /// capacity). Default 30 matches the §10.4 burst-math envelope.
    /// </summary>
    public int GlobalBurstCapacity { get; set; } = 30;

    /// <summary>
    /// Per-chat token-bucket refill rate, tokens per minute. Default 20
    /// matches Telegram's documented per-chat limit.
    /// </summary>
    public int PerChatPerMinute { get; set; } = 20;

    /// <summary>
    /// Per-chat burst capacity. Architecture §10.4 D-BURST treats this
    /// as the load-bearing constraint for the bounded-burst P95 SLO:
    /// when a single chat receives ≤ this many messages in a burst,
    /// all are absorbed by burst tokens and the global limit binds
    /// instead of the per-chat limit. Default 5.
    /// </summary>
    public int PerChatBurstCapacity { get; set; } = 5;
}
