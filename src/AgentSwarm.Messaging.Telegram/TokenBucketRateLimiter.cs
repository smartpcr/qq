using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Proactive dual token-bucket rate limiter for the Telegram outbound path
/// (architecture.md §10.4). Maintains:
/// <list type="bullet">
///   <item>A single <b>global</b> bucket capped by
///         <see cref="RateLimitOptions.GlobalBurstCapacity"/> and refilling
///         at <see cref="RateLimitOptions.GlobalPerSecond"/> tokens/second.</item>
///   <item>A lazily-created <b>per-chat</b> bucket per destination chat
///         capped by <see cref="RateLimitOptions.PerChatBurstCapacity"/>
///         and refilling at
///         <see cref="RateLimitOptions.PerChatPerMinute"/> / 60 tokens/second.</item>
/// </list>
/// Each <see cref="AcquireAsync"/> call consumes one token from each
/// bucket; when either bucket is empty the caller waits, via the injected
/// <see cref="IDelayProvider"/>, for the shorter of the two refill
/// horizons. Tests inject a fake <see cref="IDelayProvider"/> and a fake
/// <see cref="TimeProvider"/> to drive the limiter without sleeping.
/// </summary>
internal sealed class TokenBucketRateLimiter : ITelegramRateLimiter
{
    private readonly IDelayProvider _delayProvider;
    private readonly TimeProvider _timeProvider;
    private readonly RateLimitOptions _options;

    private readonly object _gate = new();
    private readonly Bucket _global;
    private readonly Dictionary<long, Bucket> _perChat = new();

    public TokenBucketRateLimiter(
        IOptions<RateLimitOptions> options,
        IDelayProvider delayProvider,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _delayProvider = delayProvider ?? throw new ArgumentNullException(nameof(delayProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));

        if (_options.GlobalPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.GlobalPerSecond,
                "RateLimitOptions.GlobalPerSecond must be positive.");
        }
        if (_options.PerChatPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.PerChatPerMinute,
                "RateLimitOptions.PerChatPerMinute must be positive.");
        }

        _global = new Bucket(
            capacity: Math.Max(1, _options.GlobalBurstCapacity),
            refillPerSecond: _options.GlobalPerSecond,
            startTimestamp: _timeProvider.GetTimestamp());
    }

    public async Task AcquireAsync(long chatId, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            TimeSpan wait;
            lock (_gate)
            {
                var now = _timeProvider.GetTimestamp();
                _global.Refill(now, _timeProvider);

                if (!_perChat.TryGetValue(chatId, out var chatBucket))
                {
                    chatBucket = new Bucket(
                        capacity: Math.Max(1, _options.PerChatBurstCapacity),
                        refillPerSecond: _options.PerChatPerMinute / 60d,
                        startTimestamp: now);
                    _perChat[chatId] = chatBucket;
                }
                else
                {
                    chatBucket.Refill(now, _timeProvider);
                }

                if (_global.Tokens >= 1 && chatBucket.Tokens >= 1)
                {
                    _global.Consume();
                    chatBucket.Consume();
                    return;
                }

                var globalWait = _global.Tokens >= 1
                    ? TimeSpan.Zero
                    : _global.TimeUntilNextToken();
                var chatWait = chatBucket.Tokens >= 1
                    ? TimeSpan.Zero
                    : chatBucket.TimeUntilNextToken();
                wait = globalWait > chatWait ? globalWait : chatWait;
                if (wait < TimeSpan.FromMilliseconds(1))
                {
                    // Never busy-spin: enforce a minimum sleep so the
                    // injected delay provider is always exercised.
                    wait = TimeSpan.FromMilliseconds(1);
                }
            }

            await _delayProvider.DelayAsync(wait, ct).ConfigureAwait(false);
        }
    }

    private sealed class Bucket
    {
        private readonly int _capacity;
        private readonly double _refillPerSecond;
        private double _tokens;
        private long _lastRefillTimestamp;

        public Bucket(int capacity, double refillPerSecond, long startTimestamp)
        {
            _capacity = capacity;
            _refillPerSecond = refillPerSecond;
            _tokens = capacity;
            _lastRefillTimestamp = startTimestamp;
        }

        public double Tokens => _tokens;

        public void Refill(long now, TimeProvider clock)
        {
            var elapsed = clock.GetElapsedTime(_lastRefillTimestamp, now);
            if (elapsed <= TimeSpan.Zero)
            {
                return;
            }
            _tokens = Math.Min(_capacity, _tokens + elapsed.TotalSeconds * _refillPerSecond);
            _lastRefillTimestamp = now;
        }

        public void Consume() => _tokens -= 1;

        public TimeSpan TimeUntilNextToken()
        {
            if (_tokens >= 1) return TimeSpan.Zero;
            var deficit = 1 - _tokens;
            var seconds = deficit / _refillPerSecond;
            return TimeSpan.FromSeconds(seconds);
        }
    }
}
