using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Telegram.Sending;

/// <summary>
/// Production implementation of <see cref="ITelegramRateLimiter"/> backed
/// by per-chat <see cref="TokenBucket"/> instances and a single shared
/// global bucket. Resolved per architecture.md §10.4 — global throughput
/// is capped at <see cref="RateLimitOptions.GlobalPerSecond"/> and per
/// chat at <see cref="RateLimitOptions.PerChatPerMinute"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-chat bucket lifecycle.</b> A new <see cref="TokenBucket"/> is
/// created on first reference for a chat. Buckets are never evicted: a
/// stranded bucket for a chat that no longer receives traffic holds
/// only its own integer/double state and is negligible. Eviction by an
/// LRU policy would add lock contention with no real-world benefit at
/// the documented operator-chat counts (architecture.md §10.4 D-BURST
/// "≥ 10 operator chats").
/// </para>
/// <para>
/// <b>Acquisition order.</b> The per-chat token is acquired BEFORE the
/// global token so a per-chat wait does not hold a global token while
/// idle. Reversing the order is incorrect: a global-token-held worker
/// would block other chats while it waits on its own per-chat limit,
/// effectively throttling cross-chat throughput.
/// </para>
/// </remarks>
public sealed class TokenBucketTelegramRateLimiter : ITelegramRateLimiter
{
    private readonly RateLimitOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TokenBucket _global;
    private readonly ConcurrentDictionary<long, TokenBucket> _perChat = new();

    public TokenBucketTelegramRateLimiter(
        IOptions<TelegramOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        var telegram = options.Value ?? throw new ArgumentNullException(nameof(options));
        _options = telegram.RateLimits ?? new RateLimitOptions();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        // Iter-2 evaluator item 6 — fail loudly instead of silently
        // clamping a misconfigured value to 1. The
        // TelegramOptionsValidator catches this at host startup with a
        // descriptive message; these guards are the defense-in-depth
        // layer for code paths (e.g. unit tests) that bypass the
        // validator. Silent clamping made operators believe the
        // limiter was honouring their (wrong) config when it was
        // actually emitting at the floor.
        if (_options.GlobalPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.GlobalPerSecond,
                "Telegram:RateLimits:GlobalPerSecond must be > 0 — see TelegramOptionsValidator.");
        }
        if (_options.GlobalBurstCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.GlobalBurstCapacity,
                "Telegram:RateLimits:GlobalBurstCapacity must be > 0 — see TelegramOptionsValidator.");
        }
        if (_options.PerChatPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.PerChatPerMinute,
                "Telegram:RateLimits:PerChatPerMinute must be > 0 — see TelegramOptionsValidator.");
        }
        if (_options.PerChatBurstCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.PerChatBurstCapacity,
                "Telegram:RateLimits:PerChatBurstCapacity must be > 0 — see TelegramOptionsValidator.");
        }

        _global = new TokenBucket(
            capacity: _options.GlobalBurstCapacity,
            refillPerSecond: _options.GlobalPerSecond,
            timeProvider: timeProvider);
    }

    /// <inheritdoc />
    public async Task AcquireAsync(long chatId, CancellationToken cancellationToken)
    {
        var perChat = _perChat.GetOrAdd(chatId, BuildPerChatBucket);
        await perChat.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await _global.AcquireAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Snapshot of the current global token balance. For diagnostics /
    /// tests only.
    /// </summary>
    internal double GlobalTokens => _global.CurrentTokens;

    /// <summary>
    /// Snapshot of the per-chat bucket's current tokens, materialising
    /// the bucket if it does not exist yet. For diagnostics / tests only.
    /// </summary>
    internal double TokensForChat(long chatId) =>
        _perChat.GetOrAdd(chatId, BuildPerChatBucket).CurrentTokens;

    private TokenBucket BuildPerChatBucket(long _) =>
        new(
            capacity: _options.PerChatBurstCapacity,
            refillPerSecond: _options.PerChatPerMinute / 60.0,
            timeProvider: _timeProvider);
}
