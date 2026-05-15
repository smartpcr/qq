namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Configuration POCO bound from the <c>Telegram:RateLimits</c> section.
/// Drives the proactive dual token-bucket limiter inside
/// <see cref="TelegramMessageSender"/> per architecture.md §10.4.
/// </summary>
/// <remarks>
/// Two layers cooperate: a single global bucket enforces the Telegram
/// Bot API global send ceiling (<see cref="GlobalPerSecond"/>, default 30 msg/s),
/// and a separate per-chat bucket per destination chat enforces the
/// per-chat ceiling (<see cref="PerChatPerMinute"/>, default 20 msg/min).
/// <see cref="GlobalBurstCapacity"/> and <see cref="PerChatBurstCapacity"/>
/// set each bucket's pre-filled token count so steady-state bursts up to
/// those sizes drain without blocking — see architecture.md §10.4 burst
/// math for the derivation of the defaults.
/// </remarks>
public sealed class RateLimitOptions
{
    /// <summary>
    /// Configuration sub-section name under <c>Telegram</c>.
    /// </summary>
    public const string SectionName = "RateLimits";

    /// <summary>
    /// Global send ceiling across all chats, in messages per second.
    /// Default <c>30</c> matches the Telegram Bot API documented limit.
    /// </summary>
    public int GlobalPerSecond { get; set; } = 30;

    /// <summary>
    /// Burst capacity of the global token bucket. Default <c>30</c> so the
    /// first second of a burst drains 30 messages without queueing,
    /// matching the burst math in architecture.md §10.4.
    /// </summary>
    public int GlobalBurstCapacity { get; set; } = 30;

    /// <summary>
    /// Per-chat send ceiling, in messages per minute. Default <c>20</c>
    /// matches Telegram's per-chat soft limit.
    /// </summary>
    public int PerChatPerMinute { get; set; } = 20;

    /// <summary>
    /// Burst capacity of each per-chat token bucket. Default <c>5</c> —
    /// the first 5 messages to a given chat drain from the bucket without
    /// per-chat wait. See architecture.md §10.4 (D-BURST).
    /// </summary>
    public int PerChatBurstCapacity { get; set; } = 5;
}
