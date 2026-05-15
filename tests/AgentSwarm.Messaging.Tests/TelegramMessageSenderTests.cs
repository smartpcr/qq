using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Telegram;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.3 — Outbound Message Sender. These tests pin the eight
/// scenarios listed in implementation-plan.md Stage 2.3 onto the
/// observable behaviour of <see cref="TelegramMessageSender"/>,
/// <see cref="TokenBucketRateLimiter"/>, and the cache/rate-limit/429
/// integration paths. The tests use a recording fake
/// <see cref="ITelegramApiClient"/> (so neither the Bot API library's
/// extension-method send surface nor a live HTTP call is exercised), a
/// fake <see cref="IDelayProvider"/> that captures requested delays
/// without sleeping, a recording fake <see cref="IDistributedCache"/>
/// for the <c>QuestionId:ActionId</c> writes, and a deterministic
/// <see cref="TimeProvider"/> so the body's "Expires in 30 minutes"
/// substring is stable.
/// </summary>
public class TelegramMessageSenderTests
{
    // ============================================================
    // Test fixtures
    // ============================================================

    private static AgentQuestion BuildQuestion(
        string questionId = "Q-001",
        string correlationId = "corr-abc-123",
        MessageSeverity severity = MessageSeverity.Critical,
        DateTimeOffset? expiresAt = null,
        params HumanAction[] actions)
    {
        return new AgentQuestion
        {
            QuestionId = questionId,
            AgentId = "agent-1",
            TaskId = "task-1",
            Title = "Deploy approval",
            Body = "Confirm release 1.2.3 to production",
            Severity = severity,
            AllowedActions = actions.Length == 0
                ? new HumanAction[]
                  {
                      new() { ActionId = "approve", Label = "Approve", Value = "approve" },
                      new() { ActionId = "reject", Label = "Reject", Value = "reject" },
                  }
                : actions,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30),
            CorrelationId = correlationId,
        };
    }

    private static AgentQuestionEnvelope BuildEnvelope(
        AgentQuestion? question = null,
        string? proposedDefaultActionId = null,
        IReadOnlyDictionary<string, string>? routingMetadata = null)
    {
        return new AgentQuestionEnvelope
        {
            Question = question ?? BuildQuestion(),
            ProposedDefaultActionId = proposedDefaultActionId,
            RoutingMetadata = routingMetadata ?? new Dictionary<string, string>(),
        };
    }

    private static TelegramMessageSender BuildSender(
        out RecordingApiClient api,
        out RecordingDelayProvider delays,
        out RecordingRateLimiter rateLimiter,
        out RecordingDistributedCache cache,
        out RecordingMessageIdTracker tracker,
        TimeProvider? timeProvider = null)
    {
        api = new RecordingApiClient();
        delays = new RecordingDelayProvider();
        rateLimiter = new RecordingRateLimiter();
        cache = new RecordingDistributedCache();
        tracker = new RecordingMessageIdTracker();
        return new TelegramMessageSender(
            api,
            rateLimiter,
            cache,
            tracker,
            delays,
            NullLogger<TelegramMessageSender>.Instance,
            timeProvider ?? TimeProvider.System);
    }

    // ============================================================
    // Question renders buttons (Scenario 1)
    // ============================================================

    [Fact]
    public async Task SendQuestionAsync_RendersThreeButtons_WhenThreeAllowedActions()
    {
        var sender = BuildSender(out var api, out _, out _, out _, out _);
        var question = BuildQuestion(actions: new[]
        {
            new HumanAction { ActionId = "approve", Label = "Approve", Value = "approve" },
            new HumanAction { ActionId = "reject", Label = "Reject", Value = "reject" },
            new HumanAction { ActionId = "defer", Label = "Defer", Value = "defer" },
        });
        var envelope = BuildEnvelope(question);

        await sender.SendQuestionAsync(chatId: 1001, envelope, CancellationToken.None);

        api.Sends.Should().HaveCount(1);
        var keyboard = api.Sends[0].ReplyMarkup.Should().BeOfType<InlineKeyboardMarkup>().Subject;
        var buttons = keyboard.InlineKeyboard.SelectMany(row => row).ToList();
        buttons.Should().HaveCount(3);
        buttons.Select(b => b.Text).Should().BeEquivalentTo(new[] { "Approve", "Reject", "Defer" });
        buttons.Select(b => b.CallbackData).Should().BeEquivalentTo(new[]
        {
            "Q-001:approve", "Q-001:reject", "Q-001:defer",
        });
    }

    // ============================================================
    // Question body includes full context (Scenario 2)
    // ============================================================

    [Fact]
    public async Task SendQuestionAsync_BodyIncludesSeverityTimeoutDefaultAndQuestionBody()
    {
        var fixedNow = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new StubTimeProvider(fixedNow);
        var sender = BuildSender(out var api, out _, out _, out _, out _, clock);
        var question = BuildQuestion(
            severity: MessageSeverity.Critical,
            expiresAt: fixedNow.AddMinutes(30),
            actions: new[]
            {
                new HumanAction { ActionId = "skip", Label = "skip", Value = "skip" },
                new HumanAction { ActionId = "go", Label = "Go", Value = "go" },
            });
        var envelope = BuildEnvelope(question, proposedDefaultActionId: "skip");

        await sender.SendQuestionAsync(7777, envelope, CancellationToken.None);

        api.Sends.Should().HaveCount(1);
        var body = api.Sends[0].Text;
        body.Should().Contain("Critical", because: "severity badge must appear");
        body.Should().Contain("Default action if no response: skip");
        body.Should().Contain("30 minutes", because: "ExpiresAt countdown is the operator-facing timeout");
        body.Should().Contain("Confirm release 1\\.2\\.3 to production",
            because: "question Body is MarkdownV2-escaped (the '.' must be escaped) and rendered verbatim");
    }

    // ============================================================
    // HumanAction cached on keyboard build (Scenario 3)
    // ============================================================

    [Fact]
    public async Task SendQuestionAsync_CachesEachHumanAction_WithExpiresAtPlusFiveMinutes()
    {
        var fixedNow = new DateTimeOffset(2099, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new StubTimeProvider(fixedNow);
        var expiresAt = fixedNow.AddMinutes(30);
        var sender = BuildSender(out _, out _, out _, out var cache, out _, clock);
        var question = BuildQuestion(
            expiresAt: expiresAt,
            actions: new[]
            {
                new HumanAction { ActionId = "approve", Label = "Approve", Value = "approve" },
                new HumanAction { ActionId = "reject", Label = "Reject", Value = "reject" },
            });
        var envelope = BuildEnvelope(question);

        await sender.SendQuestionAsync(123, envelope, CancellationToken.None);

        cache.Sets.Should().HaveCount(2,
            "two AllowedActions → two cache writes per architecture.md §5.2 invariant 3");

        var approve = cache.Sets.Single(s => s.Key == $"{question.QuestionId}:approve");
        var reject = cache.Sets.Single(s => s.Key == $"{question.QuestionId}:reject");

        // Round-trip JSON to confirm the full HumanAction was serialized.
        var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        JsonSerializer.Deserialize<HumanAction>(approve.Value, jsonOpts)!.ActionId.Should().Be("approve");
        JsonSerializer.Deserialize<HumanAction>(reject.Value, jsonOpts)!.ActionId.Should().Be("reject");

        // Expiry: Absolute at ExpiresAt + 5 min — the 5-minute grace
        // window for CallbackQueryHandler per implementation-plan Stage
        // 2.3 step 3 and architecture.md §5.2.
        var expectedExpiry = expiresAt + TimeSpan.FromMinutes(5);
        approve.Options.AbsoluteExpiration.Should().Be(expectedExpiry);
        reject.Options.AbsoluteExpiration.Should().Be(expectedExpiry);
    }

    // ============================================================
    // Rate limit handled gracefully (Scenario 4) — 429 retry
    // ============================================================

    [Fact]
    public async Task SendQuestionAsync_HandlesHttp429_WaitsAtLeastRetryAfter_AndDoesNotThrow()
    {
        var sender = BuildSender(out var api, out var delays, out _, out _, out _);
        var rateLimitException = new ApiRequestException(
            message: "Too Many Requests",
            errorCode: 429,
            parameters: new ResponseParameters { RetryAfter = 5 });
        api.QueueException(rateLimitException);

        var envelope = BuildEnvelope(BuildQuestion());

        var act = async () => await sender.SendQuestionAsync(42, envelope, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "the sender retries 429 responses after the RetryAfter hint and only surfaces after MaxRetryAttempts");
        delays.Requested.Should().Contain(TimeSpan.FromSeconds(5),
            "the sender must back off by at least the RetryAfter window before retrying");
        api.Sends.Should().HaveCount(1, "the retry attempt succeeds on the second call");
    }

    // ============================================================
    // CorrelationId in message (Scenario 5)
    // ============================================================

    [Fact]
    public async Task SendQuestionAsync_IncludesCorrelationIdInBody()
    {
        var sender = BuildSender(out var api, out _, out _, out _, out _);
        var question = BuildQuestion(correlationId: "trace-9876-zeta");
        var envelope = BuildEnvelope(question);

        await sender.SendQuestionAsync(99, envelope, CancellationToken.None);

        api.Sends.Should().HaveCount(1);
        api.Sends[0].Text.Should().Contain("trace\\-9876\\-zeta",
            "the CorrelationId is rendered as a MarkdownV2-escaped trace footer (the '-' character must be escaped)");
    }

    // ============================================================
    // RequiresComment action labeled (Scenario 6)
    // ============================================================

    [Fact]
    public async Task SendQuestionAsync_AppendsReplyRequiredSuffix_WhenActionRequiresComment()
    {
        var sender = BuildSender(out var api, out _, out _, out _, out _);
        var question = BuildQuestion(actions: new[]
        {
            new HumanAction { ActionId = "approve", Label = "Approve", Value = "approve" },
            new HumanAction
            {
                ActionId = "request_info",
                Label = "Request info",
                Value = "request_info",
                RequiresComment = true,
            },
        });
        var envelope = BuildEnvelope(question);

        await sender.SendQuestionAsync(50, envelope, CancellationToken.None);

        var keyboard = api.Sends[0].ReplyMarkup.Should().BeOfType<InlineKeyboardMarkup>().Subject;
        var labels = keyboard.InlineKeyboard.SelectMany(row => row).Select(b => b.Text).ToList();
        labels.Should().Contain("Approve");
        labels.Should().Contain("Request info (reply required)");
    }

    // ============================================================
    // Proactive rate limiter throttles (Scenario 7)
    // ============================================================

    [Fact]
    public async Task TokenBucketRateLimiter_BlocksOnceGlobalBucketExhausted()
    {
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var delays = new SyntheticDelayProvider(clock);
        var options = Options.Create(new RateLimitOptions
        {
            GlobalPerSecond = 30,
            GlobalBurstCapacity = 30,
            PerChatPerMinute = 1200, // disable per-chat as the binding constraint
            PerChatBurstCapacity = 1000,
        });
        var limiter = new TokenBucketRateLimiter(options, delays, clock);

        // 30 acquires drain the global burst capacity without any delay.
        for (var i = 0; i < 30; i++)
        {
            await limiter.AcquireAsync(chatId: 1, CancellationToken.None);
        }
        delays.Requested.Should().BeEmpty(
            "the first GlobalBurstCapacity acquires draw from pre-filled tokens and never await");

        // The 31st acquire must request a wait through the delay provider
        // because the global bucket is exhausted. The synthetic provider
        // advances the stub clock by the requested span so the limiter's
        // refill check on the next iteration succeeds.
        await limiter.AcquireAsync(chatId: 1, CancellationToken.None);

        delays.Requested.Should().NotBeEmpty(
            "the 31st acquire must invoke DelayAsync because the global bucket is exhausted");
        delays.Requested[0].Should().BeGreaterThan(TimeSpan.Zero,
            "the wait time must be positive — the limiter computes the duration until the next token refills");
    }

    // ============================================================
    // Iter-2 item 4 / iter-3 / iter-5 structural fix: plain-text path
    // applies MarkdownV2 escaping ONCE up front, then chunks the
    // already-escaped body via SplitEscapedOnBoundaries. Because the
    // chunk size budget is enforced against the RENDERED length
    // Telegram receives, a body dense in MarkdownV2 metacharacters
    // can no longer overflow 4096 post-escape. The earlier iter-3
    // "chunk-then-escape" pipeline was structurally vulnerable to
    // up-to-2× post-escape inflation; the iter-5 escape-then-chunk
    // pipeline closes that hole. Pinned by:
    //   * SendMessageAsync_RawTextWithMarkdownV2Metacharacters_…
    //     (this test) — short body, every reserved char escaped
    //   * SendMessageAsync_RawTextDenseInMarkdownV2Metacharacters_…
    //     (iter-5 test below) — long body, asserts chunks ≤ 4096
    // ============================================================

    [Fact]
    public async Task SendMessageAsync_RawTextWithMarkdownV2Metacharacters_EscapesEachReservedCharacter()
    {
        var sender = BuildSender(out var api, out _, out _, out _, out _);
        IMessageSender abstraction = sender;
        // 18 of the MarkdownV2 reserved characters in a realistic
        // ops alert body. Per Telegram docs, these MUST each be
        // prefixed with a backslash when sent as MarkdownV2 text.
        const string raw = "Build v1.2.3 failed: error_code = 42 (timeout). Check #ci-bot.";
        var message = new MessengerMessage
        {
            MessageId = "m-esc",
            CorrelationId = "corr-escape-test",
            ConversationId = "conv-1",
            Timestamp = DateTimeOffset.UtcNow,
            Text = raw,
            Severity = MessageSeverity.Normal,
        };

        await abstraction.SendMessageAsync(chatId: 7L, message, CancellationToken.None);

        api.Sends.Should().HaveCount(1);
        var sent = api.Sends[0].Text;

        // The reserved metacharacters in the raw text must appear with
        // a leading backslash. Spot-check the four most-rejected ones
        // ('.', '-', '_', '=') plus '(' and ')'.
        sent.Should().Contain("v1\\.2\\.3", "the '.' separator characters in the version triplet must be escaped");
        sent.Should().Contain("error\\_code", "underscore in the variable name must be escaped");
        sent.Should().Contain("\\= 42", "equals sign must be escaped");
        sent.Should().Contain("\\(timeout\\)", "parentheses must be escaped");
        sent.Should().Contain("\\#ci\\-bot", "hash and hyphen in the channel name must be escaped");

        // The raw, unescaped substrings must NOT appear (this would
        // indicate the escape pass was skipped or only partial).
        sent.Should().NotContain("v1.2.3", "the unescaped version literal must be absent — Telegram would reject it");
        sent.Should().NotContain("error_code", "the unescaped underscore form must be absent");
    }

    // ============================================================
    // Long message split into chunks (Scenario 8) — exercised through
    // the IMessageSender.SendMessageAsync abstraction so this also
    // covers the iter-1 evaluator's item 4 (correlation must propagate
    // through the registered abstraction, not only the concrete
    // overload).
    // ============================================================

    [Fact]
    public async Task SendMessageAsync_LongBodySplitIntoChunks_EachUnder4096_AndInOrder_ThroughAbstraction()
    {
        var sender = BuildSender(out var api, out _, out _, out _, out var tracker);
        IMessageSender abstraction = sender;
        var body = new string('a', 6000);
        var message = new MessengerMessage
        {
            MessageId = "m-long",
            CorrelationId = "corr-long",
            ConversationId = "conv-1",
            Timestamp = DateTimeOffset.UtcNow,
            Text = body,
            Severity = MessageSeverity.Normal,
        };

        var result = await abstraction.SendMessageAsync(chatId: 11L, message, CancellationToken.None);

        api.Sends.Should().HaveCount(2,
            "a 6000-character body exceeds 4096 and must be split into exactly two chunks");
        api.Sends.Select(s => s.Text.Length)
            .Should().AllSatisfy(len => len.Should().BeLessThanOrEqualTo(TelegramMessageSender.MaxTelegramMessageLength));
        api.Sends.Select(s => s.ChatId).Should().AllBeEquivalentTo(11L);

        // Both chunks must carry the same correlation footer — even
        // though only the IMessageSender.SendMessageAsync overload was
        // called, NOT the concrete (long, string, string?) helper.
        api.Sends.Should().AllSatisfy(s =>
            s.Text.Should().Contain("corr\\-long",
                "each chunk carries the same CorrelationId per e2e-scenarios.md"));

        // First chunk's message_id is returned in SendResult.
        result.TelegramMessageId.Should().Be(api.Sends[0].ReturnedMessageId);

        // Both chunks are tracked back to the correlation id, keyed by
        // (chatId, telegramMessageId) — fixes iter-1 item 1.
        var c0 = await tracker.TryGetCorrelationIdAsync(11L, api.Sends[0].ReturnedMessageId, CancellationToken.None);
        var c1 = await tracker.TryGetCorrelationIdAsync(11L, api.Sends[1].ReturnedMessageId, CancellationToken.None);
        c0.Should().Be("corr-long");
        c1.Should().Be("corr-long");
    }

    // ============================================================
    // Iter-2 item 1: chatId is part of the tracker composite key —
    // the same Telegram message_id in two different chats must NOT
    // collide.
    // ============================================================

    [Fact]
    public async Task SendMessageAsync_SameMessageIdInDifferentChats_DoesNotCollideInTracker()
    {
        // The recording API client returns monotonically-increasing
        // message ids, so to force the same numeric id across two chats
        // we use a deterministic API client that returns a fixed id.
        var fixedId = 555_000L;
        var api = new FixedIdApiClient(fixedId);
        var rate = new RecordingRateLimiter();
        var cache = new RecordingDistributedCache();
        var tracker = new RecordingMessageIdTracker();
        var delays = new RecordingDelayProvider();
        var sender = new TelegramMessageSender(
            api, rate, cache, tracker, delays, NullLogger<TelegramMessageSender>.Instance);
        IMessageSender abstraction = sender;

        var msgA = new MessengerMessage
        {
            MessageId = "m-A", CorrelationId = "corr-chat-A", ConversationId = "convA",
            Timestamp = DateTimeOffset.UtcNow, Text = "hello A", Severity = MessageSeverity.Normal,
        };
        var msgB = new MessengerMessage
        {
            MessageId = "m-B", CorrelationId = "corr-chat-B", ConversationId = "convB",
            Timestamp = DateTimeOffset.UtcNow, Text = "hello B", Severity = MessageSeverity.Normal,
        };

        await abstraction.SendMessageAsync(chatId: 1111L, msgA, CancellationToken.None);
        await abstraction.SendMessageAsync(chatId: 2222L, msgB, CancellationToken.None);

        var fromChat1 = await tracker.TryGetCorrelationIdAsync(1111L, fixedId, CancellationToken.None);
        var fromChat2 = await tracker.TryGetCorrelationIdAsync(2222L, fixedId, CancellationToken.None);
        fromChat1.Should().Be("corr-chat-A",
            "with chatId in the composite key, the chat-1 mapping survives the chat-2 send");
        fromChat2.Should().Be("corr-chat-B",
            "the chat-2 mapping is recorded under (2222, 555000) and must not overwrite chat-1's (1111, 555000) entry");
    }

    // ============================================================
    // Iter-2 item 1 / iter-3 structural fix: tracker-failure semantics
    // are now an EXPLICIT INTERFACE CONTRACT, not a sender-side wrap.
    //
    // The IMessageIdTracker.TrackAsync contract (see
    // src/AgentSwarm.Messaging.Abstractions/IMessageIdTracker.cs)
    // declares that implementations MUST NOT propagate persistence
    // failures — they are required to retry/log/suppress internally.
    // The sender no longer wraps the call with a defensive SafeTrackAsync
    // shim; instead it trusts the contract. This test pins both halves:
    //
    //   (a) A contract-compliant tracker (BestEffortLoggingTracker) that
    //       internally simulates a DB failure and swallows it does not
    //       cause the sender to throw, and the send completes normally.
    //   (b) The simulated internal failure is observable — the tracker
    //       records that it suppressed an error — proving the test
    //       genuinely exercised the failure path rather than the happy
    //       path. (Without this, a no-op tracker would produce a
    //       false-pass.)
    //
    // The PersistentMessageIdTracker's OWN observance of the contract
    // (bounded inline retries with log+suppress on persistent failure)
    // is exercised by PersistentMessageIdTrackerTests in the
    // Persistence test suite.
    // ============================================================

    [Fact]
    public async Task SendMessageAsync_TrackerObservesBestEffortContract_SendCompletesNormally()
    {
        var api = new RecordingApiClient();
        var rate = new RecordingRateLimiter();
        var cache = new RecordingDistributedCache();
        var tracker = new BestEffortLoggingTracker(simulatedFailure: new InvalidOperationException("simulated DB outage"));
        var delays = new RecordingDelayProvider();
        var sender = new TelegramMessageSender(
            api, rate, cache, tracker, delays, NullLogger<TelegramMessageSender>.Instance);
        IMessageSender abstraction = sender;
        var message = new MessengerMessage
        {
            MessageId = "m-1", CorrelationId = "corr-tracker-fail", ConversationId = "conv-1",
            Timestamp = DateTimeOffset.UtcNow, Text = "hello", Severity = MessageSeverity.Normal,
        };

        var act = async () => await abstraction.SendMessageAsync(chatId: 9999L, message, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "the IMessageIdTracker contract requires implementations to absorb persistence failures internally; the sender trusts that contract and does not wrap with a defensive shim");
        api.Sends.Should().HaveCount(1, "the Telegram send proceeded as normal");
        tracker.Calls.Should().HaveCount(1, "the sender invoked the tracker exactly once with (chatId, telegramMessageId, correlationId)");
        tracker.SuppressedFailures.Should().Be(1,
            "the tracker actually exercised its suppression path on this call — proving the test would catch a regression where the tracker silently became a no-op");
    }

    // ============================================================
    // Iter-2 item 3: Rendering vs persistence boundary. The Stage 2.3
    // sender renders the proposed default action label into the body
    // but does NOT denormalise PendingQuestion.DefaultActionId — that
    // belongs to the Stage 4.1 OutboundQueueProcessor's post-send hook
    // into the Stage 3.5 IPendingQuestionStore.StoreAsync. This test
    // pins the render side and asserts (by reflection on the
    // constructor) that no IPendingQuestionStore is involved.
    // ============================================================

    [Fact]
    public async Task SendQuestionAsync_RendersDefaultActionLabel_DoesNotPersistToPendingQuestionStore()
    {
        var sender = BuildSender(out var api, out _, out _, out _, out _);
        var question = BuildQuestion(actions: new[]
        {
            new HumanAction { ActionId = "approve", Label = "Approve", Value = "approve" },
            new HumanAction { ActionId = "skip", Label = "Skip", Value = "skip" },
        });
        var envelope = BuildEnvelope(question, proposedDefaultActionId: "approve");

        await sender.SendQuestionAsync(404, envelope, CancellationToken.None);

        // Render side: the body carries the label.
        api.Sends[0].Text.Should().Contain("Default action if no response: Approve",
            "Stage 2.3 sender's responsibility per implementation-plan.md is to render the default action label");

        // Persistence side: the sender constructor does not require an
        // IPendingQuestionStore, proving by construction that it cannot
        // touch one. The Stage 4.1 OutboundQueueProcessor + Stage 3.5
        // IPendingQuestionStore handle that responsibility.
        var ctorParams = typeof(TelegramMessageSender).GetConstructors()
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToList();
        ctorParams.Should().NotContain(t => t.Name.Contains("PendingQuestion", StringComparison.Ordinal),
            "the Stage 2.3 sender does not denormalise PendingQuestion.DefaultActionId — that belongs to OutboundQueueProcessor (Stage 4.1) → IPendingQuestionStore.StoreAsync (Stage 3.5)");
    }

    // ============================================================
    // Iter-4 evaluator items 1 + 2 (iter-5 fix): post-escape chunk
    // length budget + escape-pair integrity.
    //
    // The pre-iter-5 chunker had two bugs:
    //
    //   item 1 — Plain-text chunker used a PRE-escape budget. Raw
    //   bodies dense in MarkdownV2 metacharacters could escape to up
    //   to 2× the raw size, producing chunks that exceed Telegram's
    //   4096-character limit. Fix: escape ONCE up front, then chunk
    //   the already-escaped body so the budget reflects what Telegram
    //   actually receives. SendTextInternalAsync now does this.
    //
    //   item 2 — Question body chunker's hard-cut path could split a
    //   `\X` escape token, leaving one chunk ending in a stray `\`
    //   and the next chunk starting with the escaped char — invalid
    //   MarkdownV2. Fix: AdjustForEscapePair walks back consecutive
    //   trailing backslashes; on odd count the cut is inside a token,
    //   so back off by 1 to keep the token whole.
    //
    // The four tests below pin both halves of the structural fix:
    // a direct unit test on SplitEscapedOnBoundaries, a tiny-budget
    // guard test, an integration test through the IMessageSender
    // abstraction, and an integration test through SendQuestionAsync.
    // ============================================================

    [Fact]
    public void SplitEscapedOnBoundaries_HardCutInsideEscapePair_BacksOffToPreservePair_AndReassembleEqualsOriginal()
    {
        // Construct an escaped body that has NO whitespace separators,
        // forcing the chunker into the hard-cut path. Pattern is a
        // 3-char prefix "abc" followed by N copies of "\." (the
        // MarkdownV2 escape for '.'). With a footer length chosen so
        // that the budget cuts the prefix-plus-tokens stream at a
        // position whose preceding char is '\\', AdjustForEscapePair
        // must back off by 1.
        //
        // We assert: every chunk body ≤ limit, no chunk's body portion
        // ends with an unpaired (odd-count) '\\' run, and the
        // concatenation of the body portions reconstructs the original
        // escaped input — i.e. the chunker is a partition function,
        // not a lossy/expansive transform.
        const string footer = ""; // empty footer makes the hard-cut math easy to reason about
        var sb = new StringBuilder("abc");
        for (var i = 0; i < 4000; i++)
        {
            sb.Append('\\').Append('.');
        }
        var escaped = sb.ToString();

        var chunks = TelegramMessageSender.SplitEscapedOnBoundaries(escaped, footer);

        chunks.Should().HaveCountGreaterThan(1, "the body length far exceeds 4096 so multiple chunks are required");
        chunks.Should().AllSatisfy(c =>
        {
            c.Length.Should().BeLessThanOrEqualTo(TelegramMessageSender.MaxTelegramMessageLength,
                "every chunk's RENDERED length (post-escape) must respect the 4096 limit");
            // Count consecutive trailing backslashes; odd means an
            // unpaired '\\' that Telegram would reject.
            var trailing = 0;
            for (var i = c.Length - 1; i >= 0 && c[i] == '\\'; i--) trailing++;
            (trailing % 2).Should().Be(0,
                $"a chunk must not end with an odd-count run of '\\\\' (would be an unpaired escape token); chunk trailing-backslash count: {trailing}");
        });
        // Reassembly invariant: chunker is a partition, not a
        // lossy transform.
        string.Concat(chunks).Should().Be(escaped,
            "concatenating the chunks (no footer in this test) must reconstruct the original escaped body byte-for-byte — the chunker is purely a splitter");
    }

    [Fact]
    public void SplitEscapedOnBoundaries_FooterLeavesLessThanTwoCharsPerChunk_ThrowsClearly()
    {
        // A 4 095-char footer leaves limit = 4096 - 4095 = 1, which is
        // smaller than the 2-char minimum needed to fit a single
        // MarkdownV2 escape token (\X). The chunker must fail fast
        // with a clear error rather than spin in a zero-progress
        // loop. This is the iter-5 rubber-duck-flagged tiny-budget
        // edge case.
        var footer = new string('x', 4095);
        // Body is large enough to force multi-chunk path.
        var escaped = new string('a', 5000);

        var act = () => TelegramMessageSender.SplitEscapedOnBoundaries(escaped, footer);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*per chunk*", "the error must mention the per-chunk budget so operators can diagnose oversize footers / correlation ids");
    }

    [Fact]
    public async Task SendMessageAsync_RawTextDenseInMarkdownV2Metacharacters_AllChunksUnderLimit()
    {
        // Item 1: a 5 000-character raw body of pure '.' (every char
        // is a MarkdownV2 reserved char that escapes to "\\." = 2
        // chars). Post-escape body length = 10 000. With the
        // pre-iter-5 pre-escape budget chunker, this would have been
        // split at the raw 4096 budget into one ~4096-raw chunk
        // (escaping to ~8192 chars) plus a ~904-raw remainder
        // (escaping to ~1808 chars) — Telegram would reject the first
        // chunk for exceeding 4096. With the iter-5 escape-then-chunk
        // pipeline, the budget is enforced against the rendered
        // length, so no chunk can exceed 4096.
        var sender = BuildSender(out var api, out _, out _, out _, out _);
        IMessageSender abstraction = sender;
        var raw = new string('.', 5000);
        var message = new MessengerMessage
        {
            MessageId = "m-dense", CorrelationId = "corr-dense", ConversationId = "conv-dense",
            Timestamp = DateTimeOffset.UtcNow, Text = raw, Severity = MessageSeverity.Normal,
        };

        await abstraction.SendMessageAsync(99L, message, CancellationToken.None);

        api.Sends.Should().HaveCountGreaterThan(1,
            "a 5 000-char body of metacharacters renders to 10 000+ chars and MUST be split into multiple chunks");
        api.Sends.Should().AllSatisfy(s =>
            s.Text.Length.Should().BeLessThanOrEqualTo(TelegramMessageSender.MaxTelegramMessageLength,
                "the chunker must enforce the 4096 limit on the RENDERED (post-escape) length, not the raw input length"));
        // Every input '.' must be present (escaped) across all chunks.
        // The escaped form of one '.' is "\\." — count the '.'s in
        // the rendered chunks (the footer has no '.'s).
        api.Sends.SelectMany(s => s.Text).Count(c => c == '.').Should().Be(5000,
            "every input '.' must round-trip through the chunker — no characters dropped at chunk boundaries");
    }

    [Fact]
    public async Task SendQuestionAsync_LongPunctuationHeavyBody_NoChunkEndsWithUnpairedBackslash()
    {
        // Item 2: a long question body with a punctuation-heavy
        // pattern. The chunker's hard-cut path must back off when the
        // cut would land between '\\' and the escaped char. Assert
        // that no chunk's body portion (everything before the trace
        // footer) ends with an odd-count run of '\\'.
        var sender = BuildSender(out var api, out _, out _, out _, out _);
        var sb = new StringBuilder(7000);
        // Repeating dense-metacharacter pattern with intermittent
        // letters; total ~7 700 raw chars → ~9 500+ escaped chars,
        // forcing 2-3 chunks.
        for (var i = 0; i < 350; i++)
        {
            sb.Append("v1.2.3=err_code(t).#bot-");
        }
        var question = BuildQuestion(actions: new[]
        {
            new HumanAction { ActionId = "ack", Label = "Ack", Value = "ack" },
        });
        var customQuestion = new AgentQuestion
        {
            QuestionId = question.QuestionId,
            AgentId = question.AgentId,
            TaskId = question.TaskId,
            Title = question.Title,
            Body = sb.ToString(),
            Severity = question.Severity,
            AllowedActions = question.AllowedActions,
            ExpiresAt = question.ExpiresAt,
            CorrelationId = question.CorrelationId,
        };
        var envelope = BuildEnvelope(customQuestion);

        await sender.SendQuestionAsync(33L, envelope, CancellationToken.None);

        api.Sends.Should().HaveCountGreaterThan(1, "the body is large enough to require multiple chunks");
        api.Sends.Should().AllSatisfy(s =>
        {
            s.Text.Length.Should().BeLessThanOrEqualTo(TelegramMessageSender.MaxTelegramMessageLength,
                "every chunk must respect the 4096 RENDERED-length limit");
            // Strip the trace footer so we measure trailing '\\'s on
            // the body portion only. Footer is "\n\nTrace: <escaped>".
            var footerStart = s.Text.LastIndexOf("\n\nTrace: ", StringComparison.Ordinal);
            var bodyPortion = footerStart >= 0 ? s.Text.Substring(0, footerStart) : s.Text;
            var trailing = 0;
            for (var i = bodyPortion.Length - 1; i >= 0 && bodyPortion[i] == '\\'; i--) trailing++;
            (trailing % 2).Should().Be(0,
                $"a chunk's body portion must not end with an odd-count run of '\\\\' — that would leave an unpaired '\\X' escape token that Telegram rejects as 'can't parse entities'. Trailing-\\ count: {trailing}");
        });
    }


    // ============================================================
    // Construction null-guards (defensive)
    // ============================================================

    [Fact]
    public void Constructor_ThrowsOnNullDependencies()
    {
        var api = new RecordingApiClient();
        var rate = new RecordingRateLimiter();
        var cache = new RecordingDistributedCache();
        var tracker = new RecordingMessageIdTracker();
        var delays = new RecordingDelayProvider();
        var log = NullLogger<TelegramMessageSender>.Instance;

        ((Action)(() => new TelegramMessageSender(null!, rate, cache, tracker, delays, log))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new TelegramMessageSender(api, null!, cache, tracker, delays, log))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new TelegramMessageSender(api, rate, null!, tracker, delays, log))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new TelegramMessageSender(api, rate, cache, null!, delays, log))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new TelegramMessageSender(api, rate, cache, tracker, null!, log))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new TelegramMessageSender(api, rate, cache, tracker, delays, null!))).Should().Throw<ArgumentNullException>();
    }

    // ============================================================
    // Recording fakes
    // ============================================================

    private sealed class RecordingApiClient : ITelegramApiClient
    {
        public List<SentMessage> Sends { get; } = new();
        private readonly Queue<Exception> _queuedExceptions = new();
        private long _nextMessageId = 1_000_000;

        public void QueueException(Exception ex) => _queuedExceptions.Enqueue(ex);

        public Task<long> SendMessageAsync(
            long chatId,
            string text,
            ParseMode parseMode,
            ReplyMarkup? replyMarkup,
            CancellationToken ct)
        {
            if (_queuedExceptions.Count > 0)
            {
                throw _queuedExceptions.Dequeue();
            }
            var id = Interlocked.Increment(ref _nextMessageId);
            Sends.Add(new SentMessage(chatId, text, parseMode, replyMarkup, id));
            return Task.FromResult(id);
        }
    }

    private sealed class FixedIdApiClient : ITelegramApiClient
    {
        private readonly long _fixedId;
        public List<SentMessage> Sends { get; } = new();
        public FixedIdApiClient(long fixedId) { _fixedId = fixedId; }
        public Task<long> SendMessageAsync(
            long chatId, string text, ParseMode parseMode,
            ReplyMarkup? replyMarkup, CancellationToken ct)
        {
            Sends.Add(new SentMessage(chatId, text, parseMode, replyMarkup, _fixedId));
            return Task.FromResult(_fixedId);
        }
    }

    private sealed record SentMessage(
        long ChatId,
        string Text,
        ParseMode ParseMode,
        ReplyMarkup? ReplyMarkup,
        long ReturnedMessageId);

    private sealed class RecordingDelayProvider : IDelayProvider
    {
        private readonly List<TimeSpan> _requested = new();
        private readonly object _gate = new();

        public IReadOnlyList<TimeSpan> Requested
        {
            get { lock (_gate) { return _requested.ToArray(); } }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            lock (_gate) { _requested.Add(delay); }
            return Task.CompletedTask;
        }
    }

    private sealed class SyntheticDelayProvider : IDelayProvider
    {
        private readonly StubTimeProvider _clock;
        private readonly List<TimeSpan> _requested = new();
        private readonly object _gate = new();

        public SyntheticDelayProvider(StubTimeProvider clock)
        {
            _clock = clock;
        }

        public IReadOnlyList<TimeSpan> Requested
        {
            get { lock (_gate) { return _requested.ToArray(); } }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            lock (_gate) { _requested.Add(delay); }
            _clock.Advance(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRateLimiter : ITelegramRateLimiter
    {
        public int AcquireCount;
        public Task AcquireAsync(long chatId, CancellationToken ct)
        {
            Interlocked.Increment(ref AcquireCount);
            return Task.CompletedTask;
        }
    }

    private sealed class StubTimeProvider : TimeProvider
    {
        private long _ticks;
        public StubTimeProvider(DateTimeOffset start) { _ticks = start.UtcTicks; }
        public override DateTimeOffset GetUtcNow() => new(_ticks, TimeSpan.Zero);
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan span) => Interlocked.Add(ref _ticks, span.Ticks);
    }

    private sealed class RecordingDistributedCache : IDistributedCache
    {
        public List<(string Key, byte[] Value, DistributedCacheEntryOptions Options)> Sets { get; } = new();
        private readonly Dictionary<string, byte[]> _store = new();

        public byte[]? Get(string key) => _store.TryGetValue(key, out var v) ? v : null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => _store.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) { _store.Remove(key); return Task.CompletedTask; }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            _store[key] = value;
            Sets.Add((key, value, options));
        }
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }

    // In-memory tracker that records every Track call AND supports
    // chatId-keyed lookup. Replaces the production
    // InMemoryMessageIdTracker for tests that want to assert on the
    // recorded calls.
    internal sealed class RecordingMessageIdTracker : IMessageIdTracker
    {
        public List<(long ChatId, long MessageId, string CorrelationId)> Calls { get; } = new();
        private readonly ConcurrentDictionary<(long ChatId, long MessageId), string> _map = new();
        private readonly object _gate = new();

        public Task TrackAsync(long chatId, long telegramMessageId, string correlationId, CancellationToken ct)
        {
            lock (_gate) { Calls.Add((chatId, telegramMessageId, correlationId)); }
            _map[(chatId, telegramMessageId)] = correlationId;
            return Task.CompletedTask;
        }

        public Task<string?> TryGetCorrelationIdAsync(long chatId, long telegramMessageId, CancellationToken ct)
        {
            return Task.FromResult(_map.TryGetValue((chatId, telegramMessageId), out var v) ? v : null);
        }
    }

    // Always-throwing tracker — used to assert that the sender no
    // longer defends against contract violations. The
    // IMessageIdTracker contract requires implementations to absorb
    // persistence failures; a tracker that violates the contract by
    // throwing is a bug in the implementation, and the sender
    // legitimately propagates that exception (it is no longer the
    // sender's job to mask buggy trackers).
    internal sealed class ThrowingMessageIdTracker : IMessageIdTracker
    {
        private readonly Exception _toThrow;
        public List<(long ChatId, long MessageId, string CorrelationId)> Calls { get; } = new();
        public ThrowingMessageIdTracker(Exception ex) { _toThrow = ex; }
        public Task TrackAsync(long chatId, long telegramMessageId, string correlationId, CancellationToken ct)
        {
            Calls.Add((chatId, telegramMessageId, correlationId));
            throw _toThrow;
        }
        public Task<string?> TryGetCorrelationIdAsync(long chatId, long telegramMessageId, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }

    // Contract-compliant tracker that internally simulates a
    // persistence failure and observes the IMessageIdTracker contract
    // by suppressing the failure and recording it for test inspection.
    // This is the shape every production IMessageIdTracker
    // implementation MUST take.
    internal sealed class BestEffortLoggingTracker : IMessageIdTracker
    {
        private readonly Exception? _simulatedFailure;
        public List<(long ChatId, long MessageId, string CorrelationId)> Calls { get; } = new();
        public int SuppressedFailures { get; private set; }

        public BestEffortLoggingTracker(Exception? simulatedFailure = null)
        {
            _simulatedFailure = simulatedFailure;
        }

        public Task TrackAsync(long chatId, long telegramMessageId, string correlationId, CancellationToken ct)
        {
            Calls.Add((chatId, telegramMessageId, correlationId));
            if (_simulatedFailure is not null)
            {
                // Honour the contract: log/record + swallow.
                SuppressedFailures++;
            }
            return Task.CompletedTask;
        }

        public Task<string?> TryGetCorrelationIdAsync(long chatId, long telegramMessageId, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }
}
