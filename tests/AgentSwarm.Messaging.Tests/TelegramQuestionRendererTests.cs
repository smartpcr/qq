using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram.Sending;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace AgentSwarm.Messaging.Tests;

public sealed class TelegramQuestionRendererTests
{
    private static AgentQuestion BuildQuestion(
        string title = "Deploy Solution12?",
        string body = "Pre-flight clean. Stage now?",
        MessageSeverity severity = MessageSeverity.High,
        int actionCount = 2,
        bool actionRequiresComment = false,
        TimeSpan? expiresIn = null,
        string correlationId = "trace-7f3a") =>
        new()
        {
            QuestionId = "q-001",
            AgentId = "agent-deployer",
            TaskId = "task-12",
            Title = title,
            Body = body,
            Severity = severity,
            AllowedActions = Enumerable.Range(0, actionCount)
                .Select(i => new HumanAction
                {
                    ActionId = "a" + i,
                    Label = "Action " + i,
                    Value = "value-" + i,
                    RequiresComment = actionRequiresComment && i == 0,
                })
                .ToList(),
            ExpiresAt = DateTimeOffset.Parse("2025-01-01T00:00:00Z") + (expiresIn ?? TimeSpan.FromMinutes(15)),
            CorrelationId = correlationId,
        };

    private static FakeTimeProvider FixedTime() =>
        new(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));

    [Fact]
    public void BuildInlineKeyboard_ProducesOneRowPerAction()
    {
        var q = BuildQuestion(actionCount: 3);
        var keyboard = TelegramQuestionRenderer.BuildInlineKeyboard(q);

        keyboard.InlineKeyboard.Should().HaveCount(3, "each AllowedAction renders on its own row");
        var buttons = keyboard.InlineKeyboard.SelectMany(row => row).ToList();
        buttons.Should().HaveCount(3);
        buttons[0].CallbackData.Should().Be("q-001:a0");
        buttons[1].CallbackData.Should().Be("q-001:a1");
        buttons[2].CallbackData.Should().Be("q-001:a2");
    }

    [Fact]
    public void BuildInlineKeyboard_AppendsRequiresCommentSuffix()
    {
        var q = BuildQuestion(actionCount: 2, actionRequiresComment: true);
        var keyboard = TelegramQuestionRenderer.BuildInlineKeyboard(q);

        var buttons = keyboard.InlineKeyboard.SelectMany(row => row).ToList();
        buttons[0].Text.Should().EndWith(TelegramQuestionRenderer.RequiresCommentSuffix,
            "RequiresComment=true must surface in the button label so the operator knows to type a reply");
        buttons[1].Text.Should().NotEndWith(TelegramQuestionRenderer.RequiresCommentSuffix);
    }

    [Fact]
    public void BuildBody_IncludesSeverityTimeoutBodyDefaultActionAndTraceFooter()
    {
        var time = FixedTime();
        var q = BuildQuestion(severity: MessageSeverity.Critical, expiresIn: TimeSpan.FromMinutes(15));
        var envelope = new AgentQuestionEnvelope
        {
            Question = q,
            ProposedDefaultActionId = "a1",
        };

        var body = TelegramQuestionRenderer.BuildBody(envelope, time);

        body.Should().Contain("Deploy Solution12", "title must appear");
        body.Should().Contain("Pre\\-flight clean", "body must appear (MarkdownV2 escapes hyphens)");
        body.Should().Contain("Severity: Critical", "severity badge + label must appear");
        body.Should().Contain("Times out in 15 min", "timeout countdown must appear");
        body.Should().Contain("Default action if no response: Action 1",
            "proposed default action label must be displayed");
        body.Should().Contain("trace: trace\\-7f3a", "correlation id footer per architecture §10.1");
    }

    [Fact]
    public void BuildBody_OmitsDefaultActionLine_WhenProposedDefaultActionIdIsNull()
    {
        var time = FixedTime();
        var envelope = new AgentQuestionEnvelope { Question = BuildQuestion() };

        var body = TelegramQuestionRenderer.BuildBody(envelope, time);

        body.Should().NotContain("Default action if no response",
            "no default-action line when ProposedDefaultActionId is null");
    }

    [Fact]
    public async Task CacheActionsAsync_WritesOnePayloadPerActionWithGracePeriodExpiry()
    {
        var time = FixedTime();
        var cache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        var q = BuildQuestion(actionCount: 2, expiresIn: TimeSpan.FromMinutes(10));

        await TelegramQuestionRenderer.CacheActionsAsync(q, cache, time, CancellationToken.None);

        var bytes0 = await cache.GetAsync("q-001:a0", CancellationToken.None);
        var bytes1 = await cache.GetAsync("q-001:a1", CancellationToken.None);
        bytes0.Should().NotBeNull("a0 must have been cached");
        bytes1.Should().NotBeNull("a1 must have been cached");

        var roundTripped = JsonSerializer.Deserialize<HumanAction>(bytes0!);
        roundTripped!.ActionId.Should().Be("a0");
        roundTripped.Value.Should().Be("value-0");
    }

    /// <summary>
    /// Iter-9 Stage 2.3 contract pin — the brief is literal: "expiry set
    /// to <c>AgentQuestion.ExpiresAt + 5 minutes</c>". The prior test
    /// only verified that the payload bytes were cached, NOT that the
    /// TTL matched the contract. Capturing the
    /// <see cref="DistributedCacheEntryOptions"/> via a stub
    /// <see cref="IDistributedCache"/> proves the renderer honours the
    /// brief's 5-minute grace window aligned to the Stage 3.3
    /// <c>CallbackQueryHandler</c> late-tap resolution path
    /// (architecture.md §5.2 invariant 2). A drift here would make
    /// inline-button taps near the <c>ExpiresAt</c> boundary resolve
    /// as "unknown action" and the operator's decision would be
    /// silently dropped.
    /// </summary>
    [Fact]
    public async Task CacheActionsAsync_AbsoluteExpiryEqualsExpiresAtPlusFiveMinutesGrace()
    {
        var time = FixedTime();
        var cache = new CapturingDistributedCache();
        var expiresIn = TimeSpan.FromMinutes(10);
        var q = BuildQuestion(actionCount: 2, expiresIn: expiresIn);

        await TelegramQuestionRenderer.CacheActionsAsync(q, cache, time, CancellationToken.None);

        cache.Captured.Should().HaveCount(2,
            "one cache entry per HumanAction per implementation-plan.md Stage 2.3 step 3");

        // Brief contract: expiry = ExpiresAt + 5 min. With ExpiresAt 10
        // min from "now" the TTL relative-to-now is (10 + 5) = 15 min.
        var expectedTtl = expiresIn + TelegramQuestionRenderer.CacheGracePeriod;
        foreach (var (key, _, options) in cache.Captured)
        {
            options.AbsoluteExpirationRelativeToNow.Should().Be(expectedTtl,
                "the renderer must request expiry of ExpiresAt + 5-minute grace per the Stage 2.3 brief AND architecture.md §5.2 invariant 2; a shorter TTL drops late button taps, a longer TTL leaks resolved actions into the cache indefinitely. Key={0}", key);
            options.AbsoluteExpiration.Should().BeNull(
                "the renderer intentionally uses AbsoluteExpirationRelativeToNow (cache anchors to its own UtcNow, NOT our injected TimeProvider) so a FakeTimeProvider in tests does not produce already-expired entries");
            options.SlidingExpiration.Should().BeNull(
                "callback resolution is one-shot — sliding expiration would let a late tap keep the entry alive past the contract window");
        }

        cache.Captured.Select(c => c.Key).Should().BeEquivalentTo(
            new[] { "q-001:a0", "q-001:a1" },
            "keys must be QuestionId:ActionId per architecture.md §5.2 invariant 2 + implementation-plan.md Stage 3.3 (CallbackQueryHandler resolves the cached HumanAction by exactly this key shape)");
    }

    /// <summary>
    /// Iter-9 Stage 2.3 contract pin — when an envelope arrives whose
    /// <c>ExpiresAt</c> has already drifted into the past (e.g. a
    /// queue-replay after a worker restart), the renderer MUST still
    /// cache the actions for at least the grace window so any
    /// in-flight callback can resolve. A naïve
    /// <c>AbsoluteExpirationRelativeToNow = ttl</c> with a negative
    /// <c>ttl</c> would be rejected by
    /// <see cref="MemoryDistributedCache"/> and the actions would never
    /// be cached, breaking the <c>CallbackQueryHandler</c> hot path
    /// for any late but legitimate tap. The implementation's
    /// <c>ttl &lt;= TimeSpan.Zero</c> floor matches the
    /// <see cref="TelegramQuestionRenderer.CacheGracePeriod"/>
    /// constant — pin that here so a future refactor does not
    /// reintroduce the regression.
    /// </summary>
    [Fact]
    public async Task CacheActionsAsync_FloorsTtlAtGracePeriod_WhenExpiresAtAlreadyPast()
    {
        var time = FixedTime();
        var cache = new CapturingDistributedCache();
        var q = BuildQuestion(actionCount: 1, expiresIn: TimeSpan.FromMinutes(-30));

        await TelegramQuestionRenderer.CacheActionsAsync(q, cache, time, CancellationToken.None);

        cache.Captured.Should().HaveCount(1);
        cache.Captured[0].Options.AbsoluteExpirationRelativeToNow.Should().Be(
            TelegramQuestionRenderer.CacheGracePeriod,
            "negative-or-zero TTLs MUST floor at the 5-minute grace window per CacheActionsAsync's documented guard; otherwise the cache rejects the entry and the late-callback path silently breaks");
    }

    /// <summary>
    /// Test-only <see cref="IDistributedCache"/> that records every
    /// SetAsync call's key, payload bytes, and
    /// <see cref="DistributedCacheEntryOptions"/> so the test can
    /// assert the brief's expiry contract without depending on the
    /// production <see cref="MemoryDistributedCache"/> (which only
    /// exposes the bytes; the TTL is internal).
    /// </summary>
    private sealed class CapturingDistributedCache : IDistributedCache
    {
        public List<(string Key, byte[] Value, DistributedCacheEntryOptions Options)> Captured { get; } = new();

        public byte[]? Get(string key) => null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            Task.FromResult<byte[]?>(null);

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key)
        {
        }

        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            Captured.Add((key, value, options));

        public Task SetAsync(
            string key,
            byte[] value,
            DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            Captured.Add((key, value, options));
            return Task.CompletedTask;
        }
    }
}
