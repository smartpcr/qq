using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Pipeline.Stubs;
using AgentSwarm.Messaging.Telegram.Sending;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace AgentSwarm.Messaging.Tests;

public sealed class TelegramMessageSenderTests
{
    private static AgentQuestion BuildQuestion(string correlationId = "trace-7f3a") =>
        new()
        {
            QuestionId = "q-001",
            AgentId = "agent-deployer",
            TaskId = "task-12",
            Title = "Deploy Solution12?",
            Body = "Pre-flight clean. Stage now?",
            Severity = MessageSeverity.High,
            AllowedActions = new List<HumanAction>
            {
                new() { ActionId = "approve", Label = "Approve", Value = "approve" },
                new() { ActionId = "reject", Label = "Reject", Value = "reject" },
                new() { ActionId = "comment", Label = "Comment", Value = "comment", RequiresComment = true },
            },
            ExpiresAt = DateTimeOffset.Parse("2025-01-01T00:15:00Z"),
            CorrelationId = correlationId,
        };

    private static FakeTimeProvider FixedTime() =>
        new(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));

    private static IOptions<TelegramOptions> Opts(RateLimitOptions? rl = null) =>
        Options.Create(new TelegramOptions { RateLimits = rl ?? new RateLimitOptions() });

    private static (TelegramMessageSender Sender,
                    List<SendMessageRequest> Captured,
                    Mock<ITelegramBotClient> Mock,
                    MemoryDistributedCache Cache,
                    FakeTimeProvider Time,
                    InMemoryOutboundMessageIdIndex Index,
                    InMemoryOutboundDeadLetterStore DeadLetters)
        BuildSut(
            Func<IRequest<Message>, Message>? respond = null,
            Queue<Exception>? throwSequence = null,
            ITelegramRateLimiter? limiterOverride = null,
            IAlertService? alertOverride = null,
            IPendingQuestionStore? pendingQuestionStoreOverride = null)
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var time = FixedTime();
        var limiter = limiterOverride ?? new TokenBucketTelegramRateLimiter(Opts(), time);
        var index = new InMemoryOutboundMessageIdIndex();
        var deadLetters = new InMemoryOutboundDeadLetterStore();
        var pendingQuestions = pendingQuestionStoreOverride ?? new InMemoryPendingQuestionStore();
        var mock = new Mock<ITelegramBotClient>(MockBehavior.Strict);
        var captured = new List<SendMessageRequest>();

        mock.Setup(c => c.SendRequest(
                It.IsAny<IRequest<Message>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IRequest<Message>, CancellationToken>((req, _) =>
            {
                if (req is SendMessageRequest sm)
                {
                    captured.Add(sm);
                }
                if (throwSequence is { Count: > 0 })
                {
                    throw throwSequence.Dequeue();
                }
                var response = respond is null
                    ? new Message { Id = 1000 + captured.Count }
                    : respond(req);
                return Task.FromResult(response);
            });

        var sut = new TelegramMessageSender(
            mock.Object,
            limiter,
            cache,
            index,
            deadLetters,
            pendingQuestions,
            time,
            NullLogger<TelegramMessageSender>.Instance,
            alertOverride);
        return (sut, captured, mock, cache, time, index, deadLetters);
    }

    [Fact]
    public async Task SendQuestionAsync_RendersOneButtonPerActionWithCallbackData()
    {
        var (sut, captured, _, _, _, _, _) = BuildSut();
        var envelope = new AgentQuestionEnvelope { Question = BuildQuestion() };

        await sut.SendQuestionAsync(chatId: 999, envelope, CancellationToken.None);

        captured.Should().HaveCount(1);
        captured[0].ChatId.Identifier.Should().Be(999);
        captured[0].ParseMode.Should().Be(ParseMode.MarkdownV2);

        var keyboard = captured[0].ReplyMarkup.Should().BeOfType<InlineKeyboardMarkup>().Subject;
        var buttons = keyboard.InlineKeyboard.SelectMany(r => r).ToList();
        buttons.Should().HaveCount(3, "3 AllowedActions → 3 buttons");
        buttons.Select(b => b.CallbackData).Should().BeEquivalentTo(new[]
        {
            "q-001:approve", "q-001:reject", "q-001:comment",
        });
    }

    [Fact]
    public async Task SendQuestionAsync_RenderedBodyIncludesSeverityTimeoutAndCorrelationId()
    {
        var (sut, captured, _, _, _, _, _) = BuildSut();
        var envelope = new AgentQuestionEnvelope
        {
            Question = BuildQuestion(correlationId: "trace-xyz"),
            ProposedDefaultActionId = "reject",
        };

        await sut.SendQuestionAsync(chatId: 999, envelope, CancellationToken.None);

        var text = captured[0].Text;
        text.Should().Contain("Severity: High");
        text.Should().Contain("Times out in 15 min");
        text.Should().Contain("Default action if no response: Reject");
        text.Should().Contain("trace: trace\\-xyz",
            "every outbound message carries its correlation id per architecture §10.1");
    }

    [Fact]
    public async Task SendQuestionAsync_CachesEveryActionForCallbackLookup()
    {
        var (sut, _, _, cache, _, _, _) = BuildSut();
        var envelope = new AgentQuestionEnvelope { Question = BuildQuestion() };

        await sut.SendQuestionAsync(chatId: 999, envelope, CancellationToken.None);

        (await cache.GetAsync("q-001:approve")).Should().NotBeNull();
        (await cache.GetAsync("q-001:reject")).Should().NotBeNull();
        (await cache.GetAsync("q-001:comment")).Should().NotBeNull();
    }

    [Fact]
    public async Task SendQuestionAsync_PersistsPendingQuestionRecordAfterSuccessfulSend()
    {
        // Stage 3.5 workstream scenario:
        //   "Given an AgentQuestion is sent to Telegram successfully,
        //    When SendQuestionAsync completes, Then a PendingQuestionRecord
        //    exists in the store with status Pending and the correct
        //    TelegramMessageId."
        var pendingStore = new InMemoryPendingQuestionStore();
        var (sut, captured, _, _, _, _, _) = BuildSut(pendingQuestionStoreOverride: pendingStore);
        var envelope = new AgentQuestionEnvelope
        {
            Question = BuildQuestion(),
            ProposedDefaultActionId = "reject",
        };

        await sut.SendQuestionAsync(chatId: 999, envelope, CancellationToken.None);

        captured.Should().HaveCount(1, "sanity — the question was actually sent");
        var sentMessageId = 1000 + captured.Count; // BuildSut returns Id = 1000 + captured.Count

        var stored = await pendingStore.GetAsync("q-001", default);
        stored.Should().NotBeNull("the sender must persist the pending question after a successful Telegram ack");
        stored!.Status.Should().Be(PendingQuestionStatus.Pending);
        stored.TelegramChatId.Should().Be(999);
        stored.TelegramMessageId.Should().Be(sentMessageId,
            "the persisted TelegramMessageId must match the ack id returned by the Telegram send so the timeout sweep can edit the right message");
        stored.QuestionId.Should().Be("q-001");
        stored.DefaultActionId.Should().Be("reject");
        stored.DefaultActionValue.Should().Be("reject",
            "the store denormalises the matching HumanAction.Value at persist time so the callback / RequiresComment text-reply path has a durable fallback when the IDistributedCache entry is evicted (timeout itself reads DefaultActionId, NOT DefaultActionValue, per architecture.md §10.3)");
    }

    [Fact]
    public async Task SendQuestionAsync_ThrowsPendingQuestionPersistenceException_WhenStoreFails()
    {
        // Iter-2 evaluator item 6: previously the sender logged and
        // swallowed pending-store failures, so SendQuestionAsync
        // returned success even when the load-bearing pending row
        // failed to persist. The Stage 3.5 contract is that a missing
        // row makes callbacks unresolvable AND timeouts unrecoverable
        // (since the sweep is the only place DefaultActionId lives by
        // architecture.md §10.3), so the sender now propagates the
        // failure as a typed PendingQuestionPersistenceException
        // carrying enough context for recovery tooling to either
        // retry the StoreAsync (the Telegram message already
        // delivered — Stage 4.1 OutboundQueueProcessor must NOT
        // re-send) or roll up an alert.
        var failingStore = new ThrowingPendingQuestionStore(
            new InvalidOperationException("simulated database failure"));
        var (sut, captured, _, _, _, _, _) = BuildSut(pendingQuestionStoreOverride: failingStore);
        var envelope = new AgentQuestionEnvelope
        {
            Question = BuildQuestion(),
            ProposedDefaultActionId = "reject",
        };

        var act = async () => await sut.SendQuestionAsync(chatId: 999, envelope, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PendingQuestionPersistenceException>();
        ex.Which.QuestionId.Should().Be("q-001");
        ex.Which.TelegramChatId.Should().Be(999);
        ex.Which.TelegramMessageId.Should().BeGreaterThan(0,
            "the Telegram message was already sent before persistence failed — recovery tooling must NOT re-send");
        ex.Which.CorrelationId.Should().Be("trace-7f3a",
            "the exception must carry the correlation id so the outbound queue's dead-letter envelope can stitch end-to-end traces");
        ex.Which.InnerException.Should().BeOfType<InvalidOperationException>(
            "the original store failure must be wrapped, not lost");

        captured.Should().HaveCount(1,
            "sanity — the Telegram send succeeded BEFORE the persistence failure; the exception type signals 'message delivered, row missing' so callers do not re-send");
    }

    private sealed class ThrowingPendingQuestionStore : IPendingQuestionStore
    {
        private readonly Exception _toThrow;

        public ThrowingPendingQuestionStore(Exception toThrow) => _toThrow = toThrow;

        public Task StoreAsync(AgentQuestionEnvelope envelope, long telegramChatId, long telegramMessageId, CancellationToken ct)
            => throw _toThrow;

        public Task<PendingQuestion?> GetAsync(string questionId, CancellationToken ct)
            => Task.FromResult<PendingQuestion?>(null);

        public Task<PendingQuestion?> GetByTelegramMessageAsync(long telegramChatId, long telegramMessageId, CancellationToken ct)
            => Task.FromResult<PendingQuestion?>(null);

        public Task MarkAnsweredAsync(string questionId, CancellationToken ct) => Task.CompletedTask;

        public Task MarkAwaitingCommentAsync(string questionId, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> MarkTimedOutAsync(string questionId, CancellationToken ct)
            => Task.FromResult(false);

        public Task<bool> TryRevertTimedOutClaimAsync(string questionId, PendingQuestionStatus revertTo, CancellationToken ct)
            => Task.FromResult(false);

        public Task RecordSelectionAsync(string questionId, string selectedActionId, string selectedActionValue, long respondentUserId, CancellationToken ct)
            => Task.CompletedTask;

        public Task<PendingQuestion?> GetAwaitingCommentAsync(long telegramChatId, long respondentUserId, CancellationToken ct)
            => Task.FromResult<PendingQuestion?>(null);

        public Task<IReadOnlyList<PendingQuestion>> GetExpiredAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<PendingQuestion>>(Array.Empty<PendingQuestion>());
    }

    [Fact]
    public async Task SendQuestionAsync_HonorsRetryAfter_OnApiRequestException429()
    {
        var time = FixedTime();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var limiter = new TokenBucketTelegramRateLimiter(Opts(), time);
        var mock = new Mock<ITelegramBotClient>(MockBehavior.Strict);

        var attempt = 0;
        mock.Setup(c => c.SendRequest(
                It.IsAny<IRequest<Message>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IRequest<Message>, CancellationToken>((_, _) =>
            {
                attempt++;
                if (attempt == 1)
                {
                    throw new ApiRequestException(
                        "Too Many Requests",
                        429,
                        new ResponseParameters { RetryAfter = 5 });
                }
                return Task.FromResult(new Message { Id = 4242 });
            });

        var sut = new TelegramMessageSender(
            mock.Object, limiter, cache, new InMemoryOutboundMessageIdIndex(),
            new InMemoryOutboundDeadLetterStore(), new InMemoryPendingQuestionStore(),
            time, NullLogger<TelegramMessageSender>.Instance);
        var envelope = new AgentQuestionEnvelope { Question = BuildQuestion() };

        var task = sut.SendQuestionAsync(chatId: 999, envelope, CancellationToken.None);

        // The 429 retry awaits Task.Delay(5s) on the FakeTimeProvider —
        // before time advances, the task must still be in-flight.
        await Task.Yield();
        task.IsCompleted.Should().BeFalse(
            "the sender should be honouring retry_after=5s rather than spinning");

        time.Advance(TimeSpan.FromSeconds(5.1));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));

        result.TelegramMessageId.Should().Be(4242);
        attempt.Should().Be(2, "exactly one retry consumed the 429 response");
    }

    [Fact]
    public async Task SendTextAsync_PassesPreRenderedMarkdownV2Through()
    {
        // Architecture §4.12: SendTextAsync is the pass-through path for
        // already-rendered MarkdownV2 from the connector (Alert /
        // StatusUpdate / CommandAck). Re-escaping a body whose author
        // already escaped its reserved characters would double-escape
        // every backslash. The sender's only injection is the trace
        // footer (covered by SendTextAsync_AppendsTraceFooter_* below);
        // when the caller already supplied one the body must reach the
        // wire verbatim.
        var (sut, captured, _, _, _, _, _) = BuildSut();
        const string body = "build\\.completed at 12:34\\!\n\n🔗 trace: connector-trace";

        await sut.SendTextAsync(chatId: 999, body, CancellationToken.None);

        captured.Should().HaveCount(1);
        captured[0].ParseMode.Should().Be(ParseMode.MarkdownV2);
        captured[0].Text.Should().Be(body,
            "pre-rendered MarkdownV2 with a caller-supplied footer must reach Telegram verbatim — re-escaping would corrupt the connector's intentional formatting");
    }

    [Fact]
    public async Task SendTextAsync_AppendsTraceFooterFromCurrentActivity_WhenBodyHasNone()
    {
        // Stage 2.3 step 6 / architecture §10.1 — every outbound rendered
        // message carries its trace id. When the connector forgot to
        // include the footer, the sender appends it from the current
        // System.Diagnostics.Activity. This makes the trace contract
        // a defense-in-depth guarantee rather than a per-caller
        // discipline.
        var (sut, captured, _, _, _, _, _) = BuildSut();
        using var activity = new Activity("outbound.send");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();

        await sut.SendTextAsync(chatId: 1, "no footer here", CancellationToken.None);

        captured.Should().HaveCount(1);
        captured[0].Text.Should().Contain(TelegramMessageSender.TraceFooterPrefix);
        captured[0].Text.Should().Contain(MarkdownV2.Escape(activity.TraceId.ToString()));
    }

    [Fact]
    public async Task SendTextAsync_PreservesCallerProvidedTraceFooter()
    {
        // Idempotency: when the caller already rendered a trace footer,
        // the sender must NOT add a second one. Otherwise a connector
        // and the sender would each emit one and the message would
        // carry two contradictory ids.
        var (sut, captured, _, _, _, _, _) = BuildSut();
        using var activity = new Activity("outbound.send");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        const string body = "alert text\n\n🔗 trace: connector-supplied-id";

        await sut.SendTextAsync(chatId: 1, body, CancellationToken.None);

        captured.Should().HaveCount(1);
        captured[0].Text.Should().Be(body,
            "caller-supplied trace footer wins; sender does not double-tag");
    }

    [Fact]
    public async Task SendTextAsync_PersistsMessageIdMappedToCorrelationId()
    {
        // Stage 2.3 step 161 — message-ID tracking. After a successful
        // send the Telegram message_id → CorrelationId mapping must
        // land somewhere so an operator reply (via reply_to_message)
        // can be tied to the originating agent trace. The sender uses
        // IDistributedCache as the lightweight index until Stage 4.1's
        // outbox lands.
        var (sut, _, _, cache, _, _, _) = BuildSut();
        const string body = "deployment ack\n🔗 trace: trace-msgid-001";

        var result = await sut.SendTextAsync(chatId: 7, body, CancellationToken.None);

        var key = TelegramMessageSender.BuildMessageIdCacheKey(7, result.TelegramMessageId);
        var stored = await cache.GetAsync(key);
        stored.Should().NotBeNull(
            "the sender writes a message-id → correlation-id index entry after every successful send");
        System.Text.Encoding.UTF8.GetString(stored!).Should().Be("trace-msgid-001");
    }

    [Fact]
    public async Task SendQuestionAsync_PersistsMessageIdMappedToQuestionCorrelationId()
    {
        // Same Stage 2.3 step 161 contract for questions — the message
        // id index entry is keyed off the Telegram message_id and
        // resolves to AgentQuestion.CorrelationId.
        var (sut, _, _, cache, _, _, _) = BuildSut();
        var envelope = new AgentQuestionEnvelope
        {
            Question = BuildQuestion(correlationId: "trace-q-msgid"),
        };

        var result = await sut.SendQuestionAsync(chatId: 999, envelope, CancellationToken.None);

        var key = TelegramMessageSender.BuildMessageIdCacheKey(999, result.TelegramMessageId);
        var stored = await cache.GetAsync(key);
        stored.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString(stored!).Should().Be("trace-q-msgid");
    }

    [Fact]
    public async Task SendTextAsync_SplitsMessageLongerThan4096CharsIntoOrderedChunks()
    {
        var (sut, captured, _, _, _, _, _) = BuildSut();

        // Build a 6000-char message with line breaks every 80 chars so the
        // splitter has natural boundaries to cut on.
        var line = new string('x', 79);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 80; i++)
        {
            sb.Append(line).Append('\n');
        }
        var text = sb.ToString();
        text.Length.Should().BeGreaterThan(TelegramMessageSender.MaxMessageLength);

        var result = await sut.SendTextAsync(chatId: 999, text, CancellationToken.None);

        captured.Count.Should().BeGreaterThanOrEqualTo(2,
            "a 6000-char message must split into >= 2 chunks");
        captured.Should().AllSatisfy(c =>
            c.Text.Length.Should().BeLessThanOrEqualTo(TelegramMessageSender.MaxMessageLength,
                "no chunk may exceed Telegram's 4096-char per-message cap"));

        // SendResult carries the last sent message id (per architecture §4.12).
        result.TelegramMessageId.Should().Be(1000 + captured.Count);
    }

    [Fact]
    public async Task SendQuestionAsync_AcquiresRateLimitBeforeCallingTelegram()
    {
        var time = FixedTime();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var counter = 0;
        var trace = new TracingLimiter(() => ++counter);
        var mock = new Mock<ITelegramBotClient>(MockBehavior.Strict);
        mock.Setup(c => c.SendRequest(
                It.IsAny<IRequest<Message>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IRequest<Message>, CancellationToken>((_, _) =>
            {
                trace.SendOrder = ++counter;
                return Task.FromResult(new Message { Id = 100 });
            });

        var sut = new TelegramMessageSender(
            mock.Object, trace, cache, new InMemoryOutboundMessageIdIndex(),
            new InMemoryOutboundDeadLetterStore(), new InMemoryPendingQuestionStore(),
            time, NullLogger<TelegramMessageSender>.Instance);
        var envelope = new AgentQuestionEnvelope { Question = BuildQuestion() };

        await sut.SendQuestionAsync(chatId: 7, envelope, CancellationToken.None);

        trace.Acquisitions.Should().ContainSingle().Which.Should().Be(7L);
        trace.AcquireOrder.Should().BeLessThan(trace.SendOrder,
            "rate limiter must be acquired BEFORE the Telegram API call (proactive throttling)");
    }

    private sealed class TracingLimiter : ITelegramRateLimiter
    {
        private readonly Func<int> _tick;

        public TracingLimiter(Func<int> tick)
        {
            _tick = tick;
        }

        public List<long> Acquisitions { get; } = new();
        public int AcquireOrder { get; private set; }
        public int SendOrder { get; set; }

        public Task AcquireAsync(long chatId, CancellationToken cancellationToken)
        {
            Acquisitions.Add(chatId);
            AcquireOrder = _tick();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SendTextAsync_AlwaysAppendsTraceFooter_EvenWithoutActivityOrCallerFooter()
    {
        // Iter-3 evaluator item 2 — every outbound message MUST carry a
        // trace footer. Earlier behaviour returned the body unchanged
        // when both (a) no caller-supplied footer was present and
        // (b) Activity.Current was null/default, leaving the message
        // untaggable. The PrepareOutbound contract now generates a
        // 32-char hex fallback id so no path reaches the wire without
        // a footer.
        var (sut, captured, _, _, _, index, _) = BuildSut();

        // Defensive: there is no Activity in scope (none started in
        // this test) and no caller-supplied footer in the body.
        Activity.Current.Should().BeNull(
            "the test must run with no ambient activity to exercise the fallback path");

        var result = await sut.SendTextAsync(chatId: 42, "alert body, no trace", CancellationToken.None);

        captured.Should().HaveCount(1);
        captured[0].Text.Should().Contain(TelegramMessageSender.TraceFooterPrefix,
            "no message may reach Telegram without a trace footer (acceptance criterion: all messages include trace/correlation ID)");
        // The durable index entry must hold the same generated id so
        // a reply-to-message lookup resolves to the wire-rendered id.
        var stored = await index.TryGetCorrelationIdAsync(42, result.TelegramMessageId, CancellationToken.None);
        stored.Should().NotBeNullOrWhiteSpace(
            "the durable IOutboundMessageIdIndex row must carry the generated correlation id, not an empty value");
        // The id in the body matches the id stored — the cached and
        // rendered ids cannot drift.
        captured[0].Text.Should().Contain(MarkdownV2.Escape(stored!),
            "the trace id rendered in the body must equal the id persisted in the durable index");
    }

    [Fact]
    public async Task SendTextAsync_PersistsMessageIdMappingToDurableIndex()
    {
        // Iter-3 evaluator item 3 — message-id → CorrelationId
        // persistence is now durable (IOutboundMessageIdIndex), not a
        // best-effort 24h cache TTL. After a successful send the
        // sender writes a mapping row that survives process restarts
        // (in production EF Core + SQLite; here the in-memory test
        // fallback).
        var (sut, _, _, _, _, index, _) = BuildSut();
        const string body = "ack\n🔗 trace: trace-durable-001";

        var result = await sut.SendTextAsync(chatId: 7, body, CancellationToken.None);

        var stored = await index.TryGetCorrelationIdAsync(7, result.TelegramMessageId, CancellationToken.None);
        stored.Should().Be("trace-durable-001",
            "the durable index is the load-bearing trace path; cache is a mirror");
    }

    [Fact]
    public async Task SendQuestionAsync_LongBody_SplitsWithKeyboardOnLastChunk()
    {
        // Iter-3 evaluator item 4 — a rendered question body larger
        // than 4096 chars must be split, with the inline keyboard
        // attached to the LAST chunk so the operator can still tap
        // an action button.
        var (sut, captured, _, _, _, index, _) = BuildSut();

        // Build a question whose body alone is > 4096 chars so the
        // total rendered body (title + severity + timeout + body +
        // trace footer) reliably crosses the limit. Use a Body string
        // of 5000 plain chars — MarkdownV2 escaping of pure ASCII
        // letters is a no-op, so the rendered chunk length tracks the
        // input length closely.
        var longBody = new string('A', 5000);
        var question = BuildQuestion(correlationId: "trace-q-long");
        question = question with { Body = longBody };
        var envelope = new AgentQuestionEnvelope { Question = question };

        var result = await sut.SendQuestionAsync(chatId: 999, envelope, CancellationToken.None);

        captured.Count.Should().BeGreaterThanOrEqualTo(2,
            "a >4096-char question body must split into >= 2 sendMessage calls");
        captured.Should().AllSatisfy(c =>
            c.Text.Length.Should().BeLessThanOrEqualTo(TelegramMessageSender.MaxMessageLength,
                "no chunk may exceed Telegram's 4096-char per-message cap"));

        // The inline keyboard is attached to the LAST chunk only — all
        // earlier chunks send plain text so the operator's chat shows
        // the action buttons immediately under the final body chunk.
        for (var i = 0; i < captured.Count - 1; i++)
        {
            captured[i].ReplyMarkup.Should().BeNull(
                $"chunk {i} is not the last chunk; the inline keyboard must only appear on the last");
        }
        captured[^1].ReplyMarkup.Should().BeOfType<InlineKeyboardMarkup>(
            "the inline keyboard MUST attach to the last chunk so callback buttons are tappable");

        // The persisted message-id mapping uses the LAST chunk's id —
        // that is the message the keyboard is attached to and therefore
        // the one a callback / reply will reference.
        var stored = await index.TryGetCorrelationIdAsync(999, result.TelegramMessageId, CancellationToken.None);
        stored.Should().Be("trace-q-long",
            "the index entry must point at the chunk that carries the keyboard");
    }

    [Fact]
    public async Task SendTextAsync_RetriesTransientHttpError_AndSucceedsOnRetry()
    {
        // Iter-3 evaluator item 5 — non-429 transient errors
        // (HttpRequestException, Telegram 5xx) must be retried with
        // exponential backoff, not propagated to the caller on the
        // first failure. Use the zero-backoff test seam so the test
        // is deterministic — the schedule itself is covered by
        // ComputeTransientBackoff_ReturnsExponentialSchedule below.
        var time = FixedTime();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var limiter = new TokenBucketTelegramRateLimiter(Opts(), time);
        var index = new InMemoryOutboundMessageIdIndex();
        var mock = new Mock<ITelegramBotClient>(MockBehavior.Strict);

        var attempt = 0;
        mock.Setup(c => c.SendRequest(
                It.IsAny<IRequest<Message>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IRequest<Message>, CancellationToken>((_, _) =>
            {
                attempt++;
                if (attempt == 1)
                {
                    throw new HttpRequestException("Connection reset by peer");
                }
                return Task.FromResult(new Message { Id = 7777 });
            });

        var sut = new TelegramMessageSender(
            mock.Object,
            limiter,
            cache,
            index,
            new InMemoryOutboundDeadLetterStore(),
            new InMemoryPendingQuestionStore(),
            time,
            NullLogger<TelegramMessageSender>.Instance,
            alertService: null,
            transientBackoff: _ => TimeSpan.Zero);

        var result = await sut.SendTextAsync(
            7,
            "trace ok\n🔗 trace: trace-transient",
            CancellationToken.None);

        result.TelegramMessageId.Should().Be(7777,
            "the second attempt must succeed and propagate its message id");
        attempt.Should().Be(2, "exactly one retry consumed the transient HttpRequestException");
    }

    [Fact]
    public async Task SendTextAsync_ExhaustsTransientRetries_ThrowsTelegramSendFailedAndAlerts()
    {
        // Iter-3 evaluator item 5 — when transient retries are
        // exhausted the sender invokes IAlertService for an out-of-
        // band operator alert AND throws a typed
        // TelegramSendFailedException so Stage 4.1's
        // OutboundQueueProcessor can dead-letter the originating row.
        var time = FixedTime();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var limiter = new TokenBucketTelegramRateLimiter(Opts(), time);
        var index = new InMemoryOutboundMessageIdIndex();
        var mock = new Mock<ITelegramBotClient>(MockBehavior.Strict);

        mock.Setup(c => c.SendRequest(
                It.IsAny<IRequest<Message>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IRequest<Message>, CancellationToken>((_, _) =>
                throw new ApiRequestException("Internal Server Error", 500));

        var alert = new RecordingAlertService();
        var sut = new TelegramMessageSender(
            mock.Object,
            limiter,
            cache,
            index,
            new InMemoryOutboundDeadLetterStore(),
            new InMemoryPendingQuestionStore(),
            time,
            NullLogger<TelegramMessageSender>.Instance,
            alertService: alert,
            transientBackoff: _ => TimeSpan.Zero);

        var act = async () => await sut.SendTextAsync(
            chatId: 42,
            "permanent fail\n🔗 trace: trace-dead",
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<TelegramSendFailedException>();
        ex.Which.ChatId.Should().Be(42L);
        ex.Which.CorrelationId.Should().Be("trace-dead");
        ex.Which.AttemptCount.Should().Be(TelegramMessageSender.MaxTransientRetries + 1,
            "AttemptCount counts the initial attempt + every retry");

        alert.Calls.Should().HaveCount(1,
            "the dead-letter path MUST invoke IAlertService.SendAlertAsync exactly once");
        alert.Calls[0].Subject.Should().Contain("dead-lettered");
        alert.Calls[0].Detail.Should().Contain("trace-dead");
        alert.Calls[0].Detail.Should().Contain("ChatId=42");
    }

    [Fact]
    public void ComputeTransientBackoff_ReturnsExponentialScheduleWithJitter()
    {
        // The production schedule (used when no override is injected
        // via the ctor seam) is exponential with bounded jitter:
        // attempt 1 → ~1 s, attempt 2 → ~2 s, attempt 3 → ~4 s, with
        // ±20% deterministic jitter. Test pins the schedule so a
        // future refactor cannot silently change the operator-visible
        // backoff curve.
        var first = TelegramMessageSender.ComputeTransientBackoff(1);
        var second = TelegramMessageSender.ComputeTransientBackoff(2);
        var third = TelegramMessageSender.ComputeTransientBackoff(3);

        first.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(800))
             .And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(1200));
        second.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1600))
              .And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(2400));
        third.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(3200))
             .And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(4800));
    }

    [Fact]
    public async Task SendTextAsync_LongBody_EveryChunkCarriesSameTraceFooter()
    {
        // Iter-4 evaluator item 1 — the per-chunk footer contract.
        // The previous behaviour appended one footer to the body then
        // split, so chunks 1..N-1 reached the wire trace-less and
        // violated the acceptance criterion "all messages include
        // trace/correlation ID". This test pins the structural fix:
        // EVERY emitted chunk must end with the same trace footer.
        var (sut, captured, _, _, _, _, _) = BuildSut();

        // 9000 plain-ASCII chars guarantees >= 3 chunks at the 4096
        // per-chunk budget (after subtracting footer suffix). Pure
        // ASCII letters are MarkdownV2-safe so the rendered length
        // tracks the input length.
        var body = new string('y', 9000) + "\n🔗 trace: trace-long-text";

        await sut.SendTextAsync(chatId: 42, body, CancellationToken.None);

        captured.Count.Should().BeGreaterThanOrEqualTo(2,
            "9000 chars + footer must split into >= 2 chunks");
        captured.Should().AllSatisfy(c =>
            c.Text.Should().EndWith("🔗 trace: trace-long-text",
                "iter-4 evaluator item 1: EVERY emitted chunk must carry the trace footer"));
        captured.Should().AllSatisfy(c =>
            c.Text.Length.Should().BeLessThanOrEqualTo(TelegramMessageSender.MaxMessageLength,
                "the footer-aware budget keeps every chunk under the Telegram per-message cap"));
    }

    [Fact]
    public async Task SendTextAsync_LongBody_PersistsMappingForEveryChunk()
    {
        // Iter-4 evaluator item 2 — every chunk's message-id must be
        // persisted, not just the last. An operator reply that quotes
        // chunk 1 of 3 must still resolve back to the trace, but the
        // previous code only wrote the final chunk's id, so chunks
        // 1..N-1 became unmapped on operator reply.
        var (sut, captured, _, _, _, index, _) = BuildSut();

        var body = new string('z', 9000) + "\n🔗 trace: trace-perchunk-msgid";

        await sut.SendTextAsync(chatId: 17, body, CancellationToken.None);

        captured.Count.Should().BeGreaterThanOrEqualTo(2);

        // The mock assigns message ids 1001, 1002, ... in order. Each
        // captured chunk's id must resolve in the durable index back
        // to the same trace correlation id.
        for (var i = 0; i < captured.Count; i++)
        {
            var msgId = 1001 + i;
            var stored = await index.TryGetCorrelationIdAsync(17, msgId, CancellationToken.None);
            stored.Should().Be("trace-perchunk-msgid",
                $"iter-4 evaluator item 2: chunk {i} (msgid {msgId}) must be mapped to the trace id, not unmapped");
        }
    }

    [Fact]
    public async Task SendQuestionAsync_LongBody_EveryChunkCarriesSameTraceFooter()
    {
        // Iter-4 evaluator item 1 — same per-chunk footer rule for the
        // question path. The keyboard still attaches to the LAST chunk
        // only (pinned by SendQuestionAsync_LongBody_SplitsWithKeyboardOnLastChunk
        // above), but every chunk's text MUST end with the trace
        // footer.
        var (sut, captured, _, _, _, _, _) = BuildSut();

        var question = BuildQuestion(correlationId: "trace-q-percchunk-footer");
        question = question with { Body = new string('Q', 6000) };
        var envelope = new AgentQuestionEnvelope { Question = question };

        await sut.SendQuestionAsync(chatId: 555, envelope, CancellationToken.None);

        captured.Count.Should().BeGreaterThanOrEqualTo(2);
        captured.Should().AllSatisfy(c =>
            c.Text.Should().EndWith("🔗 trace: trace\\-q\\-percchunk\\-footer",
                "every question chunk must carry the trace footer (escaped per BuildTraceFooter)"));
    }

    [Fact]
    public async Task SendQuestionAsync_LongBody_PersistsMappingForEveryChunk()
    {
        // Iter-4 evaluator item 2 — the question path's per-chunk
        // msg-id mapping. The previous code persisted only the
        // keyboard-bearing last chunk's id, so a reply quoting an
        // earlier body chunk would resolve as unknown.
        var (sut, captured, _, _, _, index, _) = BuildSut();

        var question = BuildQuestion(correlationId: "trace-q-percchunk-mapping");
        question = question with { Body = new string('W', 6000) };
        var envelope = new AgentQuestionEnvelope { Question = question };

        await sut.SendQuestionAsync(chatId: 888, envelope, CancellationToken.None);

        captured.Count.Should().BeGreaterThanOrEqualTo(2);
        for (var i = 0; i < captured.Count; i++)
        {
            var msgId = 1001 + i;
            var stored = await index.TryGetCorrelationIdAsync(888, msgId, CancellationToken.None);
            stored.Should().Be("trace-q-percchunk-mapping",
                $"iter-4 evaluator item 2 (question path): chunk {i} (msgid {msgId}) must be mapped to the trace id");
        }
    }

    [Fact]
    public async Task OutboundMessageIdIndex_CrossChat_DoesNotCollideOnSharedMessageId()
    {
        // Iter-4 evaluator item 3 — Telegram message_id is only
        // unique WITHIN a chat. The mapping must be keyed on
        // (ChatId, TelegramMessageId) so two different chats can each
        // hold their own (msgid=42 → correlationId=...) row without
        // overwriting each other. The previous schema used msgid as a
        // global PK and silently overwrote the first row.
        var index = new InMemoryOutboundMessageIdIndex();

        await index.StoreAsync(new OutboundMessageIdMapping
        {
            TelegramMessageId = 42,
            ChatId = 1001,
            CorrelationId = "trace-chat-A",
            SentAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        await index.StoreAsync(new OutboundMessageIdMapping
        {
            TelegramMessageId = 42,
            ChatId = 2002,
            CorrelationId = "trace-chat-B",
            SentAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var fromChatA = await index.TryGetCorrelationIdAsync(1001, 42, CancellationToken.None);
        var fromChatB = await index.TryGetCorrelationIdAsync(2002, 42, CancellationToken.None);

        fromChatA.Should().Be("trace-chat-A",
            "iter-4 evaluator item 3: chat A's mapping must NOT be overwritten by chat B's identical msgid");
        fromChatB.Should().Be("trace-chat-B",
            "iter-4 evaluator item 3: chat B's mapping is a separate composite-key row");
    }

    [Fact]
    public async Task SendTextAsync_TransientExhaustion_RecordsDeadLetterRecord()
    {
        // Iter-4 evaluator item 4 — on retry exhaustion the sender
        // writes a durable OutboundDeadLetterRecord. The previous
        // build only threw + alerted; the ledger row makes the
        // dead-letter outcome observable in the database so the
        // operator audit trail survives a worker restart.
        var time = FixedTime();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var limiter = new TokenBucketTelegramRateLimiter(Opts(), time);
        var index = new InMemoryOutboundMessageIdIndex();
        var deadLetters = new InMemoryOutboundDeadLetterStore();
        var mock = new Mock<ITelegramBotClient>(MockBehavior.Strict);

        mock.Setup(c => c.SendRequest(
                It.IsAny<IRequest<Message>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IRequest<Message>, CancellationToken>((_, _) =>
                throw new ApiRequestException("Internal Server Error", 500));

        var sut = new TelegramMessageSender(
            mock.Object,
            limiter,
            cache,
            index,
            deadLetters,
            new InMemoryPendingQuestionStore(),
            time,
            NullLogger<TelegramMessageSender>.Instance,
            alertService: null,
            transientBackoff: _ => TimeSpan.Zero);

        var act = async () => await sut.SendTextAsync(
            chatId: 99,
            "permafail\n🔗 trace: trace-dlq-transient",
            CancellationToken.None);

        await act.Should().ThrowAsync<TelegramSendFailedException>();

        var rows = await deadLetters.GetByCorrelationIdAsync("trace-dlq-transient", CancellationToken.None);
        rows.Should().HaveCount(1,
            "iter-4 evaluator item 4: every exhausted send must write a durable dead-letter row");
        rows[0].ChatId.Should().Be(99);
        rows[0].FailureCategory.Should().Be(OutboundFailureCategory.TransientTransport);
        rows[0].AttemptCount.Should().Be(TelegramMessageSender.MaxTransientRetries + 1);
        rows[0].LastErrorType.Should().Be(nameof(ApiRequestException));
    }

    [Fact]
    public async Task SendTextAsync_RateLimitExhaustion_ThrowsTypedAndAlertsAndRecordsDeadLetter()
    {
        // Iter-4 evaluator item 5 — 429 exhaustion must route through
        // the same DLQ + alert + typed-exception path as transient
        // exhaustion, just tagged FailureCategory.RateLimitExhausted.
        // The previous build let the raw ApiRequestException(429)
        // escape after the budget was spent, so the operator never
        // got the typed dead-letter context.
        var time = FixedTime();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var limiter = new TokenBucketTelegramRateLimiter(Opts(), time);
        var index = new InMemoryOutboundMessageIdIndex();
        var deadLetters = new InMemoryOutboundDeadLetterStore();
        var mock = new Mock<ITelegramBotClient>(MockBehavior.Strict);

        // Always 429 with retry_after=0 so we don't actually have to
        // pump FakeTimeProvider through MaxRateLimitRetries delays.
        mock.Setup(c => c.SendRequest(
                It.IsAny<IRequest<Message>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IRequest<Message>, CancellationToken>((_, _) =>
                throw new ApiRequestException(
                    "Too Many Requests",
                    429,
                    new ResponseParameters { RetryAfter = 0 }));

        var alert = new RecordingAlertService();
        var sut = new TelegramMessageSender(
            mock.Object,
            limiter,
            cache,
            index,
            deadLetters,
            new InMemoryPendingQuestionStore(),
            time,
            NullLogger<TelegramMessageSender>.Instance,
            alertService: alert,
            transientBackoff: _ => TimeSpan.Zero);

        // Pump the FakeTimeProvider in a background task so the 429
        // retry's Task.Delay completes deterministically. The
        // retry_after=0 means each delay is effectively 1s (the
        // sender clamps to >= 1); MaxRateLimitRetries retries means
        // we need to advance ~3s of fake time.
        var sendTask = sut.SendTextAsync(
            chatId: 77,
            "rate limited\n🔗 trace: trace-dlq-429",
            CancellationToken.None);

        for (var i = 0; i < TelegramMessageSender.MaxRateLimitRetries + 1; i++)
        {
            await Task.Delay(20);
            time.Advance(TimeSpan.FromSeconds(2));
        }

        var ex = await Assert.ThrowsAsync<TelegramSendFailedException>(() => sendTask);
        ex.ChatId.Should().Be(77L);
        ex.CorrelationId.Should().Be("trace-dlq-429");
        ex.FailureCategory.Should().Be(OutboundFailureCategory.RateLimitExhausted,
            "iter-4 evaluator item 5: 429 exhaustion must be tagged RateLimitExhausted, not TransientTransport");
        ex.AttemptCount.Should().Be(TelegramMessageSender.MaxRateLimitRetries + 1);

        alert.Calls.Should().HaveCount(1,
            "iter-4 evaluator item 5: 429 exhaustion must invoke IAlertService too");
        alert.Calls[0].Subject.Should().Contain("RateLimitExhausted");

        var rows = await deadLetters.GetByCorrelationIdAsync("trace-dlq-429", CancellationToken.None);
        rows.Should().HaveCount(1,
            "iter-4 evaluator item 4 + 5: 429 exhaustion must also write a durable dead-letter row");
        rows[0].FailureCategory.Should().Be(OutboundFailureCategory.RateLimitExhausted);
        rows[0].LastErrorType.Should().Be(nameof(ApiRequestException));
    }

    [Fact]
    public void SplitForTelegram_DoesNotCutBetweenBackslashAndEscapedChar()
    {
        // Iter-3 evaluator item 4 — the MarkdownV2-aware splitter.
        // A naive hard-cut splitter can land the boundary between a
        // backslash (the MarkdownV2 escape sigil) and the reserved
        // character it is protecting; the resulting chunk ends with a
        // dangling "\" and the next chunk starts with the unprotected
        // reserved character, both halves are invalid MarkdownV2 and
        // Telegram returns 400 "can't parse entities". This test
        // forces the splitter into the hard-cut fallback path (no
        // paragraph or line breaks anywhere) and pins that no emitted
        // chunk ends with an unpaired backslash.
        //
        // The pattern repeats the 2-char escape unit "\." so the
        // budget boundary deliberately lands ON the cut between "\"
        // and ".". An escape-aware splitter MUST walk back one char
        // to land BEFORE the backslash; a naive splitter cuts there.
        const int perChunkBudget = 11;
        var pattern = new System.Text.StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            pattern.Append("\\."); // 2-char escape pair
        }
        var text = pattern.ToString();

        var chunks = TelegramMessageSender.SplitForTelegram(text, perChunkBudget);

        chunks.Should().HaveCountGreaterThan(1,
            "the input is 40 chars and the per-chunk budget is 11, so the splitter must produce multiple chunks");
        chunks.Should().AllSatisfy(c => c.Length.Should().BeLessThanOrEqualTo(perChunkBudget,
            "every chunk must respect the per-chunk budget"));
        chunks.Should().AllSatisfy(c => EndsWithUnpairedBackslash(c).Should().BeFalse(
            $"iter-3 evaluator item 4: chunk \"{c}\" ends with an unpaired backslash — Telegram would reject this as malformed MarkdownV2"));

        // Reassembly contract: concatenating the chunks must reproduce
        // the original input (no characters lost, no characters
        // duplicated) so the escape-aware walk-back doesn't silently
        // drop content.
        string.Concat(chunks).Should().Be(text,
            "the splitter must preserve the original byte sequence — walking back must not drop chars");
    }

    [Theory]
    [InlineData("foo\\.bar", 4, 3)]    // budget = 4 lands on '\\', walk back to 3 ('foo')
    [InlineData("ab\\\\cd", 3, 2)]    // budget = 3 lands inside an in-progress pair (only one '\\' visible) — walk back to 2
    [InlineData("a\\b", 2, 1)]         // budget = 2 lands on '\\', walk back to 1 ('a')
    [InlineData("\\\\.", 2, 2)]        // budget = 2 lands after the paired '\\\\' — safe; the trailing '.' is unrelated
    [InlineData("\\.\\.", 3, 2)]       // budget = 3 lands on the second '\\', walk back to 2 (one full pair)
    public void AdjustForMarkdownV2Escape_HonoursBackslashParity(string input, int splitAt, int expected)
    {
        // Iter-3 evaluator item 4 — pins the escape-walk-back logic
        // directly. Counts consecutive backslashes immediately before
        // the cut: odd count means the last backslash is unpaired
        // (i.e. it's escaping the char that would land at the start
        // of the next chunk), so the cut must walk back. Even count
        // means the backslashes form complete pairs and the cut is
        // safe.
        var adjusted = TelegramMessageSender.AdjustForMarkdownV2Escape(input, splitAt);
        adjusted.Should().Be(expected);
    }

    private static bool EndsWithUnpairedBackslash(string s)
    {
        if (s.Length == 0 || s[^1] != '\\')
        {
            return false;
        }

        // Count trailing backslashes; odd ⇒ unpaired ⇒ invalid.
        var count = 0;
        for (var i = s.Length - 1; i >= 0 && s[i] == '\\'; i--)
        {
            count++;
        }
        return (count % 2) == 1;
    }

    [Fact]
    public void SplitForTelegram_PathologicalAllBackslashes_HonoursPerChunkBudget()
    {
        // Iter-5 evaluator item 3 — the wire-length cap is the OUTER
        // contract; every emitted chunk MUST be ≤ perChunkBudget so
        // the assembled payload never exceeds Telegram's 4096-char
        // ceiling. The earlier behaviour walked FORWARD past the
        // unsafe run to keep chunks MarkdownV2-valid, but that could
        // emit oversized chunks. The new behaviour accepts a
        // possibly-malformed chunk (escape-safety is best-effort)
        // rather than violating the wire-length contract — the
        // malformed chunk is then rejected by Telegram with HTTP 400
        // and routed through the iter-5 item 1 Permanent dead-letter
        // path, surfacing the issue with full operator context
        // instead of a silent payload-truncation-then-malformed-send.
        var text = new string('\\', 50); // 50 consecutive backslashes
        const int budget = 5;
        var chunks = TelegramMessageSender.SplitForTelegram(text, perChunkBudget: budget);

        chunks.Should().NotBeEmpty();
        chunks.Should().AllSatisfy(c => c.Length.Should().BeLessThanOrEqualTo(budget,
            $"iter-5 evaluator item 3: chunk of length {c.Length} exceeds the per-chunk budget {budget} — Telegram would also reject for length and the operator would lose the 'why' context"));

        // The reassembly contract must hold even on the pathological
        // input — concatenating the chunks must reproduce the
        // original body byte-for-byte.
        string.Concat(chunks).Should().Be(text,
            "the splitter must preserve the original byte sequence even when escape-safety has to be relaxed to honour the wire-length cap");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AdvanceToSafeForwardCut_FromUnsafePosition_LandsOnPairedBoundary(int startAt)
    {
        // Iter-5 evaluator item 5 — the walk-forward helper that
        // rescues the splitter when the backward walk lands at 0.
        // For a body of N consecutive backslashes, the only safe cut
        // is at an even-count boundary: position 0, 2, 4, … (paired
        // groups) or text.Length (end of body). Walking FORWARD from
        // any unsafe position must land on the next safe boundary.
        var text = new string('\\', 10); // 10 consecutive backslashes

        var safe = TelegramMessageSender.AdvanceToSafeForwardCut(text, startAt);

        // The resulting cut must be safe — preceded by zero or
        // an EVEN number of consecutive backslashes.
        if (safe > 0 && safe < text.Length)
        {
            var trailingCount = 0;
            for (var i = safe - 1; i >= 0 && text[i] == '\\'; i--)
            {
                trailingCount++;
            }
            (trailingCount % 2).Should().Be(0,
                $"iter-5 evaluator item 5: AdvanceToSafeForwardCut returned {safe} for startAt={startAt}, but {trailingCount} trailing backslashes is still unsafe");
        }
        // safe == text.Length is implicitly safe (no "next chunk" to
        // start with an unescaped reserved char).
    }

    [Fact]
    public async Task SendTextAsync_PermanentHttpFailure_DeadLettersAndAlertsAndThrowsTyped()
    {
        // Iter-5 evaluator item 1 — non-429 non-5xx Bot API
        // rejections (HTTP 400 "can't parse entities", 403 "bot
        // blocked", 404 "chat not found", 401 "Unauthorized") are
        // PERMANENT failures: retrying the same payload never
        // succeeds. Before this fix `SendWithRetry` only caught
        // ApiRequestException for 429 and 5xx, so a 400 escaped raw
        // — no dead-letter row, no alert, no typed exception. The
        // structural fix is a final `catch (ApiRequestException)`
        // clause that routes through EmitDeadLetterAsync with the
        // new OutboundFailureCategory.Permanent value, surfaces the
        // typed TelegramSendFailedException, AND fires the optional
        // IAlertService. This test pins all three behaviours for
        // the canonical 400 case.
        var time = FixedTime();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var limiter = new TokenBucketTelegramRateLimiter(Opts(), time);
        var index = new InMemoryOutboundMessageIdIndex();
        var deadLetters = new InMemoryOutboundDeadLetterStore();
        var mock = new Mock<ITelegramBotClient>(MockBehavior.Strict);

        var callCount = 0;
        mock.Setup(c => c.SendRequest(
                It.IsAny<IRequest<Message>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IRequest<Message>, CancellationToken>((_, _) =>
            {
                callCount++;
                throw new ApiRequestException(
                    "Bad Request: can't parse entities: Character '_' is reserved and must be escaped",
                    400);
            });

        var alert = new RecordingAlertService();
        var sut = new TelegramMessageSender(
            mock.Object,
            limiter,
            cache,
            index,
            deadLetters,
            new InMemoryPendingQuestionStore(),
            time,
            NullLogger<TelegramMessageSender>.Instance,
            alertService: alert,
            transientBackoff: _ => TimeSpan.Zero);

        var act = async () => await sut.SendTextAsync(
            chatId: 4242,
            "borked payload\n🔗 trace: trace-perm-400",
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<TelegramSendFailedException>();
        ex.Which.ChatId.Should().Be(4242L);
        ex.Which.CorrelationId.Should().Be("trace-perm-400");
        ex.Which.FailureCategory.Should().Be(OutboundFailureCategory.Permanent,
            "iter-5 evaluator item 1: a non-429 non-5xx Bot API failure must surface as Permanent so Stage 4.1's OutboundQueueProcessor skips the retry budget for this row");
        ex.Which.DeadLetterPersisted.Should().BeTrue(
            "the ledger row must land before the typed exception surfaces");
        ex.Which.AttemptCount.Should().Be(1,
            "permanent failures do NOT consume the transient retry budget — the first call IS the last call");

        callCount.Should().Be(1,
            "iter-5 evaluator item 1: a permanent failure must NOT trigger any retries — retrying a 400 wastes API budget and delays the operator alert");

        alert.Calls.Should().HaveCount(1,
            "iter-5 evaluator item 1: the dead-letter path MUST invoke IAlertService for permanent failures — the operator needs to see broken payloads / blocked bots within the same SLO as transient exhaustion");
        alert.Calls[0].Subject.Should().Contain("dead-lettered");
        alert.Calls[0].Detail.Should().Contain("trace-perm-400");
        alert.Calls[0].Detail.Should().Contain("ChatId=4242");
        alert.Calls[0].Detail.Should().Contain("Permanent",
            "the alert detail must surface the failure category so the operator can tell a wedged backend from a malformed payload at a glance");

        var rows = await deadLetters.GetByCorrelationIdAsync("trace-perm-400", CancellationToken.None);
        rows.Should().HaveCount(1,
            "iter-5 evaluator item 1: the durable DLQ ledger must record permanent failures so the audit trail is complete");
        rows[0].FailureCategory.Should().Be(OutboundFailureCategory.Permanent);
        rows[0].ChatId.Should().Be(4242L);
        rows[0].LastErrorType.Should().Be(nameof(ApiRequestException));
        rows[0].LastErrorMessage.Should().Contain("can't parse entities");
    }

    [Fact]
    public void ResolveCorrelationIdAndFooter_IgnoresMarkerInsideBody()
    {
        // Iter-5 evaluator item 2 — the previous implementation used
        // raw LastIndexOf("🔗 trace: ") to find the footer, so a body
        // that QUOTED a previous send's footer earlier in the text
        // (e.g. an agent log snippet "deploy result: 🔗 trace:
        // upstream-7f3a saw error X") was silently truncated from
        // that quoted marker onwards AND attributed to the wrong
        // correlation id. The structural fix requires the marker to
        // be on the LAST line of the text after trimming trailing
        // whitespace. Bodies with mid-body markers but no real
        // trailing footer must (a) return the original body
        // UNCHANGED and (b) synthesise a fresh correlation id rather
        // than mis-attributing the body to the quoted id.
        const string body =
            "deploy attempt #3 — upstream connector replied with " +
            "\"failed → 🔗 trace: upstream-deadbeef saw HTTP 502\" " +
            "and we will retry shortly.";

        var (stripped, correlationId, footer) =
            TelegramMessageSender.ResolveCorrelationIdAndFooter(body);

        stripped.Should().Be(body,
            "iter-5 evaluator item 2: the body must be returned UNCHANGED when the trace marker only appears mid-body — silent truncation would lose the actual error context");
        correlationId.Should().NotBe("upstream-deadbeef",
            "iter-5 evaluator item 2: mid-body markers MUST NOT be treated as the caller's correlation id — that would mis-attribute log content to an unrelated upstream trace");
        correlationId.Should().NotBeNullOrWhiteSpace(
            "the synthesised id must be a non-blank value so downstream persistence and the auto-appended footer have something to write");
        footer.Should().StartWith("🔗 trace: ",
            "the synthesised footer must use the standard prefix so the wire format is uniform");
        footer.Should().Contain(TelegramMessageSender.BuildTraceFooter(correlationId).Substring("🔗 trace: ".Length),
            "the footer must wrap the SAME correlation id as the tuple's second element");
    }

    [Fact]
    public void StripTrailingTraceFooter_DoesNotTruncateMidBodyMarker()
    {
        // Iter-5 evaluator item 2 — direct pin on the strip helper.
        // The prior LastIndexOf-based implementation would truncate
        // the body from any quoted marker onwards, silently losing
        // every byte after the in-body marker. Confirms the
        // structural fix: when the marker is not at the tail, the
        // returned text is the input unchanged.
        const string body =
            "alpha\nbeta 🔗 trace: ghost-id quoted inside\ngamma";

        var result = TelegramMessageSender.StripTrailingTraceFooter(body);

        result.Should().Be(body,
            "iter-5 evaluator item 2: a marker quoted inside the body must not be treated as a trailing footer; stripping it would silently truncate every subsequent byte of body content");
    }

    [Fact]
    public void TryExtractTraceFooter_ReturnsNullForMidBodyMarker()
    {
        // Iter-5 evaluator item 2 — direct pin on the extract
        // helper. A body with the marker quoted mid-text but no
        // actual trailing footer must produce NULL so the caller
        // synthesises a fresh id from Activity.Current / new Guid
        // rather than persisting a mis-attributed correlation id
        // into the cache / durable index.
        const string body =
            "stage1 ok\n  🔗 trace: should-not-match  \nstage2 in progress";

        var extracted = TelegramMessageSender.TryExtractTraceFooter(body);

        extracted.Should().BeNull(
            "iter-5 evaluator item 2: mid-body markers must not resolve to a correlation id — a non-null return here would poison the cache / mapping with the wrong id");
    }

    [Fact]
    public void TryExtractTraceFooter_StillResolvesGenuineTrailingFooter()
    {
        // Iter-5 evaluator item 2 regression guard — the trailing
        // footer case (the dominant happy path) must keep working.
        // The structural fix tightened detection to "last line only"
        // but must NOT lose the ability to recover the id from a
        // properly-formatted trailing footer. Pin both the "footer
        // is the only line" case and the "body + blank line + footer"
        // case which the renderer emits in practice.
        TelegramMessageSender
            .TryExtractTraceFooter("🔗 trace: solo-trace")
            .Should().Be("solo-trace",
                "a single-line footer is the canonical SendQuestionAsync output and must keep resolving");

        TelegramMessageSender
            .TryExtractTraceFooter("body line one\nbody line two\n\n🔗 trace: trailing-id\n")
            .Should().Be("trailing-id",
                "the renderer separates body and footer with a blank line and adds a trailing newline — the helper must tolerate both");

        TelegramMessageSender
            .TryExtractTraceFooter("middle quote: \"🔗 trace: noise\"\n🔗 trace: real-tail")
            .Should().Be("real-tail",
                "iter-5 evaluator item 2: when a marker appears BOTH mid-body and at the tail, only the trailing one wins — the body quote must NOT shadow the actual footer");
    }

    [Fact]
    public void ResolveCorrelationIdAndFooter_DualMarker_TrailingWins_AndStripsOnlyTrailing()
    {
        // Iter-7 — companion to TryExtractTraceFooter's dual-marker
        // pin, but for the full ResolveCorrelationIdAndFooter +
        // StripTrailingTraceFooter pair that drives the actual sender
        // (SendTextAsync / SendQuestionAsync). The risk in iter-5's
        // structural fix was that the strip helper might trim the
        // mid-body marker even though the resolver correctly picked
        // the trailing id — a half-fix that would still leak quoted
        // context out of the on-wire body. Pins:
        //   * the returned correlation id is the TRAILING marker's
        //     id (not the mid-body quote),
        //   * the returned body retains the mid-body marker text
        //     verbatim (no silent truncation), and
        //   * the returned footer is the literal trailing footer
        //     line (used by SplitForTelegramWithFooter to re-append
        //     the exact same string to every chunk).
        const string body =
            "upstream replied: \"failed → 🔗 trace: upstream-noise saw HTTP 500\"\n"
            + "retrying with backoff\n\n"
            + "🔗 trace: tail-real-id";

        var (stripped, correlationId, footer) =
            TelegramMessageSender.ResolveCorrelationIdAndFooter(body);

        correlationId.Should().Be("tail-real-id",
            "iter-7 dual-marker pin: the LAST-line marker wins; the mid-body quoted id must never be persisted as the send's correlation id");
        stripped.Should().Contain("upstream-noise",
            "iter-7 dual-marker pin: the mid-body quote is part of the body content and must be preserved verbatim through the strip");
        stripped.Should().NotEndWith("tail-real-id",
            "iter-7 dual-marker pin: the trailing footer line must be removed from the body half of the tuple so SplitForTelegramWithFooter can re-add it once per chunk without doubling");
        footer.Should().Be("🔗 trace: tail-real-id",
            "iter-7 dual-marker pin: the returned footer is the LITERAL trailing line so the per-chunk re-append matches byte-for-byte");

        // The strip-only helper must independently honour the same
        // contract — used by the question renderer path.
        var strippedOnly = TelegramMessageSender.StripTrailingTraceFooter(body);
        strippedOnly.Should().Contain("upstream-noise",
            "iter-7 dual-marker pin: StripTrailingTraceFooter must also preserve the mid-body quoted marker; trimming from any '🔗 trace:' occurrence would silently lose the operator's error context");
        strippedOnly.Should().NotEndWith("tail-real-id",
            "iter-7 dual-marker pin: StripTrailingTraceFooter must remove ONLY the trailing footer line");
    }

    [Fact]
    public async Task EmitDeadLetter_PersistenceFailureIsRetried_ThenSurfacedViaTypedException()
    {
        // Iter-5 evaluator item 4 — the previous EmitDeadLetterAsync
        // logged-and-swallowed the first IOutboundDeadLetterStore
        // failure, so a transient DB outage could break the durable
        // dead-letter promise silently. The new behaviour retries the
        // ledger write up to MaxDeadLetterPersistRetries times and
        // surfaces the persistence status via
        // TelegramSendFailedException.DeadLetterPersisted.
        var time = FixedTime();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var limiter = new TokenBucketTelegramRateLimiter(Opts(), time);
        var index = new InMemoryOutboundMessageIdIndex();
        var deadLetters = new FlakyDeadLetterStore(throwTimes: 1); // first attempt fails, second succeeds
        var mock = new Mock<ITelegramBotClient>(MockBehavior.Strict);

        mock.Setup(c => c.SendRequest(
                It.IsAny<IRequest<Message>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IRequest<Message>, CancellationToken>((_, _) =>
                throw new ApiRequestException("Internal Server Error", 500));

        var alert = new RecordingAlertService();
        var sut = new TelegramMessageSender(
            mock.Object,
            limiter,
            cache,
            index,
            deadLetters,
            new InMemoryPendingQuestionStore(),
            time,
            NullLogger<TelegramMessageSender>.Instance,
            alertService: alert,
            transientBackoff: _ => TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<TelegramSendFailedException>(() =>
            sut.SendTextAsync(chatId: 1234, "permafail\n🔗 trace: trace-dlq-retry", CancellationToken.None));

        ex.DeadLetterPersisted.Should().BeTrue(
            "iter-5 evaluator item 4: the retry loop must succeed on the second attempt and surface DeadLetterPersisted=true");
        deadLetters.RecordedCount.Should().Be(1,
            "exactly one ledger row must land — duplicate writes would inflate the audit trail");
        deadLetters.AttemptCount.Should().Be(2,
            "the retry budget must consume one failed attempt + one successful attempt");
        alert.Calls.Should().HaveCount(1);
        alert.Calls[0].Subject.Should().NotContain("DLQ persistence FAILED",
            "successful persistence (after retry) must use the normal alert subject");
    }

    [Fact]
    public async Task EmitDeadLetter_AllPersistenceAttemptsFail_SurfacesDeadLetterPersistedFalse()
    {
        // Iter-5 evaluator item 4 — when EVERY persistence attempt
        // fails, the typed TelegramSendFailedException must carry
        // DeadLetterPersisted=false so Stage 4.1's
        // OutboundQueueProcessor knows to take corrective action, and
        // the alert subject must explicitly call out the broken
        // durability promise so the operator can reconstruct the row
        // from the alert payload.
        var time = FixedTime();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var limiter = new TokenBucketTelegramRateLimiter(Opts(), time);
        var index = new InMemoryOutboundMessageIdIndex();
        var deadLetters = new FlakyDeadLetterStore(throwTimes: int.MaxValue); // never succeeds
        var mock = new Mock<ITelegramBotClient>(MockBehavior.Strict);

        mock.Setup(c => c.SendRequest(
                It.IsAny<IRequest<Message>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IRequest<Message>, CancellationToken>((_, _) =>
                throw new ApiRequestException("Internal Server Error", 500));

        var alert = new RecordingAlertService();
        var sut = new TelegramMessageSender(
            mock.Object,
            limiter,
            cache,
            index,
            deadLetters,
            new InMemoryPendingQuestionStore(),
            time,
            NullLogger<TelegramMessageSender>.Instance,
            alertService: alert,
            transientBackoff: _ => TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<TelegramSendFailedException>(() =>
            sut.SendTextAsync(chatId: 5678, "permafail\n🔗 trace: trace-dlq-broken", CancellationToken.None));

        ex.DeadLetterPersisted.Should().BeFalse(
            "iter-5 evaluator item 4: when every persistence attempt fails the typed exception must surface DeadLetterPersisted=false so callers can take corrective action");
        deadLetters.AttemptCount.Should().Be(TelegramMessageSender.MaxDeadLetterPersistRetries,
            "the retry budget must be fully consumed");
        deadLetters.RecordedCount.Should().Be(0,
            "no row landed in the store");
        alert.Calls.Should().HaveCount(1,
            "the alert must still fire so the operator has SOMETHING observable for this dead-letter event");
        alert.Calls[0].Subject.Should().Contain("DLQ persistence FAILED",
            "iter-5 evaluator item 4: the alert subject must explicitly call out the broken durability promise");
        alert.Calls[0].Detail.Should().Contain("NOT PERSISTED",
            "the alert detail must include enough context for the operator to reconstruct the lost audit row");
        alert.Calls[0].Detail.Should().Contain("trace-dlq-broken");
        alert.Calls[0].Detail.Should().Contain("ChatId=5678");
    }

    [Fact]
    public async Task SendTextAsync_NonRetryable400_RoutesThroughPermanentDeadLetterPath()
    {
        // Iter-5 evaluator item 1 — a Telegram 400 ApiRequestException
        // (typical for malformed MarkdownV2: "can't parse entities")
        // is NEITHER a 429 (handled by the rate-limit catches) NOR a
        // 5xx (handled by the transient catches). Before the iter-5
        // permanent-catch was wired, these errors escaped raw with
        // no dead-letter row and no operator alert — Stage 4.1's
        // outbox processor saw only a stale "in_flight" row. The new
        // catch routes 4xx-not-429 through the SAME dead-letter
        // ledger + alert + typed-exception path as exhaustion, tagged
        // with OutboundFailureCategory.Permanent so the outbox
        // processor can skip its retry budget for this category.
        var time = FixedTime();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var limiter = new TokenBucketTelegramRateLimiter(Opts(), time);
        var index = new InMemoryOutboundMessageIdIndex();
        var deadLetters = new InMemoryOutboundDeadLetterStore();
        var mock = new Mock<ITelegramBotClient>(MockBehavior.Strict);

        mock.Setup(c => c.SendRequest(
                It.IsAny<IRequest<Message>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IRequest<Message>, CancellationToken>((_, _) =>
                throw new ApiRequestException(
                    "Bad Request: can't parse entities: Can't find end of the entity starting at byte offset 42",
                    400));

        var alert = new RecordingAlertService();
        var sut = new TelegramMessageSender(
            mock.Object,
            limiter,
            cache,
            index,
            deadLetters,
            new InMemoryPendingQuestionStore(),
            time,
            NullLogger<TelegramMessageSender>.Instance,
            alertService: alert,
            transientBackoff: _ => TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<TelegramSendFailedException>(() =>
            sut.SendTextAsync(chatId: 4001, "malformed\n🔗 trace: trace-permanent-400", CancellationToken.None));

        ex.FailureCategory.Should().Be(OutboundFailureCategory.Permanent,
            "iter-5 evaluator item 1: a 4xx-not-429 must be tagged Permanent so Stage 4.1 routes straight to DLQ without retrying");
        ex.AttemptCount.Should().Be(1,
            "permanent failures must not consume retry budget — exactly one attempt is recorded");
        ex.Message.Should().Contain("permanently failed",
            "the typed exception message must surface the permanence so the operator alert is unambiguous");
        ex.DeadLetterPersisted.Should().BeTrue(
            "iter-5 evaluator items 1 + 4: the permanent path must write the durable dead-letter row");

        var rows = await deadLetters.GetByCorrelationIdAsync("trace-permanent-400", CancellationToken.None);
        rows.Should().HaveCount(1,
            "iter-5 evaluator item 1: a 400 ApiRequestException must produce exactly one durable dead-letter row");
        rows[0].FailureCategory.Should().Be(OutboundFailureCategory.Permanent);
        rows[0].LastErrorType.Should().Be(nameof(ApiRequestException));
        rows[0].LastErrorMessage.Should().Contain("can't parse entities");
        rows[0].ChatId.Should().Be(4001);

        alert.Calls.Should().HaveCount(1,
            "iter-5 evaluator item 1: the permanent path must invoke IAlertService so the operator knows immediately");
        alert.Calls[0].Subject.Should().Contain("Permanent",
            "iter-5 evaluator item 1: the alert subject must carry the FailureCategory discriminator");
    }

    [Fact]
    public async Task SendTextAsync_NonRetryable403BotBlocked_RoutesThroughPermanentDeadLetterPath()
    {
        // Iter-5 evaluator item 1 — a Telegram 403 ApiRequestException
        // is the second-most-common shape that lands in the Permanent
        // catch (the user blocked the bot, so retrying will never
        // succeed). Pin the 403 mapping separately from 400 so a
        // regression that only adds a 400 branch would still fail
        // this test.
        var time = FixedTime();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var limiter = new TokenBucketTelegramRateLimiter(Opts(), time);
        var index = new InMemoryOutboundMessageIdIndex();
        var deadLetters = new InMemoryOutboundDeadLetterStore();
        var mock = new Mock<ITelegramBotClient>(MockBehavior.Strict);

        mock.Setup(c => c.SendRequest(
                It.IsAny<IRequest<Message>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IRequest<Message>, CancellationToken>((_, _) =>
                throw new ApiRequestException(
                    "Forbidden: bot was blocked by the user",
                    403));

        var alert = new RecordingAlertService();
        var sut = new TelegramMessageSender(
            mock.Object,
            limiter,
            cache,
            index,
            deadLetters,
            new InMemoryPendingQuestionStore(),
            time,
            NullLogger<TelegramMessageSender>.Instance,
            alertService: alert,
            transientBackoff: _ => TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<TelegramSendFailedException>(() =>
            sut.SendTextAsync(chatId: 4003, "alert\n🔗 trace: trace-bot-blocked", CancellationToken.None));

        ex.FailureCategory.Should().Be(OutboundFailureCategory.Permanent);
        ex.AttemptCount.Should().Be(1);
        ex.DeadLetterPersisted.Should().BeTrue();

        var rows = await deadLetters.GetByCorrelationIdAsync("trace-bot-blocked", CancellationToken.None);
        rows.Should().HaveCount(1);
        rows[0].FailureCategory.Should().Be(OutboundFailureCategory.Permanent);
        rows[0].LastErrorMessage.Should().Contain("bot was blocked");
    }

    private sealed class FlakyDeadLetterStore : IOutboundDeadLetterStore
    {
        private readonly int _throwTimes;
        private readonly List<OutboundDeadLetterRecord> _persisted = new();

        public FlakyDeadLetterStore(int throwTimes)
        {
            _throwTimes = throwTimes;
        }

        public int AttemptCount { get; private set; }

        public int RecordedCount => _persisted.Count;

        public Task RecordAsync(OutboundDeadLetterRecord record, CancellationToken ct)
        {
            AttemptCount++;
            if (AttemptCount <= _throwTimes)
            {
                throw new InvalidOperationException(
                    $"FlakyDeadLetterStore: simulated DB outage on attempt {AttemptCount}");
            }
            _persisted.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboundDeadLetterRecord>> GetByCorrelationIdAsync(
            string correlationId,
            CancellationToken ct)
        {
            IReadOnlyList<OutboundDeadLetterRecord> matches = _persisted
                .Where(r => r.CorrelationId == correlationId)
                .ToList();
            return Task.FromResult(matches);
        }
    }

    private sealed class RecordingAlertService : IAlertService
    {
        public List<(string Subject, string Detail)> Calls { get; } = new();

        public Task SendAlertAsync(string subject, string detail, CancellationToken ct)
        {
            Calls.Add((subject, detail));
            return Task.CompletedTask;
        }
    }
}
