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
    private readonly TimeSpan _idleEvictionThreshold;
    private readonly int _evictionThreshold;

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

        _idleEvictionThreshold = TimeSpan.FromMinutes(
            Math.Max(1, _options.PerChatIdleEvictionMinutes));
        _evictionThreshold = Math.Max(1, _options.PerChatEvictionThreshold);
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

                // Opportunistic LRU-style eviction: when the per-chat
                // dictionary grows beyond the configured soft cap, sweep
                // entries whose last access exceeds the idle threshold.
                // Bucket reconstruction is cheap (full capacity is the
                // conservative, correct default) so evicting and
                // re-creating a long-idle chat costs at most one extra
                // dictionary insert. This bounds memory for long-running
                // workers that fan out across many distinct chats over
                // time. See PR #20 review comment on TokenBucketRateLimiter
                // unbounded growth.
                if (_perChat.Count > _evictionThreshold)
                {
                    EvictIdleChats(now);
                }

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
                chatBucket.MarkAccessed(now);

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

    /// <summary>
    /// Test-visible snapshot of the per-chat dictionary size. Callers
    /// should treat this as best-effort: the lock is taken to obtain a
    /// consistent count, but the value can change immediately after
    /// return.
    /// </summary>
    internal int TrackedChatCount
    {
        get
        {
            lock (_gate)
            {
                return _perChat.Count;
            }
        }
    }

    /// <summary>
    /// Removes per-chat buckets whose last access exceeds the configured
    /// idle threshold. Caller must hold <see cref="_gate"/>. Iterates the
    /// dictionary once, materialising the doomed keys, then removes them
    /// in a second pass — necessary because <see cref="Dictionary{TKey,TValue}"/>
    /// does not permit mutation during enumeration.
    /// </summary>
    private void EvictIdleChats(long now)
    {
        List<long>? toEvict = null;
        foreach (var (chatId, bucket) in _perChat)
        {
            var idleFor = _timeProvider.GetElapsedTime(bucket.LastAccessTimestamp, now);
            if (idleFor > _idleEvictionThreshold)
            {
                toEvict ??= new List<long>();
                toEvict.Add(chatId);
            }
        }
        if (toEvict is null) return;
        foreach (var chatId in toEvict)
        {
            _perChat.Remove(chatId);
        }
    }

    private sealed class Bucket
    {
        private readonly int _capacity;
        private readonly double _refillPerSecond;
        private double _tokens;
        private long _lastRefillTimestamp;
        private long _lastAccessTimestamp;

        public Bucket(int capacity, double refillPerSecond, long startTimestamp)
        {
            _capacity = capacity;
            _refillPerSecond = refillPerSecond;
            _tokens = capacity;
            _lastRefillTimestamp = startTimestamp;
            _lastAccessTimestamp = startTimestamp;
        }

        public double Tokens => _tokens;

        public long LastAccessTimestamp => _lastAccessTimestamp;

        public void MarkAccessed(long now) => _lastAccessTimestamp = now;

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
