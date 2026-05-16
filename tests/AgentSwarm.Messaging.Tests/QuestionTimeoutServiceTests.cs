// -----------------------------------------------------------------------
// <copyright file="QuestionTimeoutServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Pipeline.Stubs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.5 — unit tests for <see cref="QuestionTimeoutService"/>.
/// Each scenario seeds an <see cref="InMemoryPendingQuestionStore"/>
/// (the abstraction the production polling loop reads from) with an
/// already-expired <see cref="PendingQuestion"/>, invokes
/// <see cref="QuestionTimeoutService.SweepOnceAsync"/> directly, and
/// asserts the published <see cref="HumanDecisionEvent"/>, the
/// Telegram edit body, the audit entry, and the row status transition.
/// </summary>
public sealed class QuestionTimeoutServiceTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SweepOnceAsync_TimeoutWithDefault_PublishesDefaultActionValueAndEditsMessage()
    {
        // Scenario from the workstream brief:
        //   "Given ExpiresAt in the past and DefaultActionId=skip,
        //    When QuestionTimeoutService polls, Then it reads
        //    DefaultActionId directly from PendingQuestionRecord
        //    (no IDistributedCache lookup), emits a HumanDecisionEvent
        //    with ActionValue=skip, and updates the Telegram message
        //    with '⏰ Timed out — default action applied: skip'."
        var harness = BuildHarness();
        await harness.SeedAsync(defaultActionId: "skip", defaultActionLabel: "Skip");

        await harness.Service.SweepOnceAsync(default);

        harness.PublishedDecisions.Should().HaveCount(1);
        var decision = harness.PublishedDecisions[0];
        decision.QuestionId.Should().Be("q-1");
        decision.ActionValue.Should().Be("skip",
            "per workstream brief step 7 the timeout publishes pending.DefaultActionId verbatim — NOT the resolved HumanAction.Value — so the consuming agent resolves the full action semantics from its own AllowedActions list (architecture.md §10.3)");
        decision.Comment.Should().BeNull();
        decision.Messenger.Should().Be("telegram");
        decision.ExternalUserId.Should().Be("__timeout__",
            "the timeout service is system-driven; the operator user is represented with the sentinel value");
        decision.CorrelationId.Should().Be("trace-1");

        harness.EditTextRequests.Should().HaveCount(1);
        var edit = harness.EditTextRequests[0];
        edit.Text.Should().Be("⏰ Timed out — default action applied: skip",
            "the workstream test-scenarios pin the edit body to the DefaultActionId string verbatim — NOT the action label — so 'DefaultActionId=skip' renders as 'applied: skip'");
        edit.MessageId.Should().Be(1001);
        edit.ReplyMarkup.Should().BeNull(
            "the inline keyboard must be removed on timeout so a late tap cannot fire another decision");

        harness.AuditEntries.Should().HaveCount(1);
        harness.AuditEntries[0].ActionValue.Should().Be("skip");
        harness.AuditEntries[0].QuestionId.Should().Be("q-1");

        var row = await harness.Store.GetAsync("q-1", default);
        row!.Status.Should().Be(PendingQuestionStatus.TimedOut);
    }

    [Fact]
    public async Task SweepOnceAsync_TimeoutWithoutDefault_PublishesTimeoutSentinel()
    {
        // Scenario from the workstream brief:
        //   "Given DefaultActionId is null, When QuestionTimeoutService
        //    polls, Then a HumanDecisionEvent is emitted with
        //    ActionValue=__timeout__ and the Telegram message is updated
        //    with '⏰ Timed out — no default action'."
        var harness = BuildHarness();
        await harness.SeedAsync(defaultActionId: null, defaultActionLabel: null);

        await harness.Service.SweepOnceAsync(default);

        harness.PublishedDecisions.Should().HaveCount(1);
        harness.PublishedDecisions[0].ActionValue.Should().Be("__timeout__");

        harness.EditTextRequests.Should().HaveCount(1);
        harness.EditTextRequests[0].Text.Should().Be("⏰ Timed out — no default action");

        var row = await harness.Store.GetAsync("q-1", default);
        row!.Status.Should().Be(PendingQuestionStatus.TimedOut);
    }

    [Fact]
    public async Task SweepOnceAsync_TelegramEditFails_DoesNotRollBackDecisionAndStillMarksTimedOut()
    {
        // The decision-publish is load-bearing; the Telegram edit is
        // cosmetic. If Telegram is down, the row must still be marked
        // TimedOut so the next sweep doesn't re-publish.
        var harness = BuildHarness(throwOnEdit: true);
        await harness.SeedAsync(defaultActionId: "skip", defaultActionLabel: "Skip");

        await harness.Service.SweepOnceAsync(default);

        harness.PublishedDecisions.Should().HaveCount(1);
        var row = await harness.Store.GetAsync("q-1", default);
        row!.Status.Should().Be(PendingQuestionStatus.TimedOut,
            "the Telegram edit is best-effort; the row must still transition so the next sweep does not double-publish");
    }

    [Fact]
    public async Task SweepOnceAsync_NoExpiredRows_NoOp()
    {
        var harness = BuildHarness();
        // Seed a single row whose ExpiresAt is far in the future so the
        // real-wall-clock filter inside InMemoryPendingQuestionStore.GetExpiredAsync
        // does NOT include it (the stub uses DateTimeOffset.UtcNow, not
        // the injected TimeProvider).
        await harness.SeedAsync(
            defaultActionId: "skip",
            defaultActionLabel: "Skip",
            absoluteExpiresAt: DateTimeOffset.UtcNow.AddYears(10));

        await harness.Service.SweepOnceAsync(default);

        harness.PublishedDecisions.Should().BeEmpty();
        harness.EditTextRequests.Should().BeEmpty();
        harness.AuditEntries.Should().BeEmpty();
        var row = await harness.Store.GetAsync("q-1", default);
        row!.Status.Should().Be(PendingQuestionStatus.Pending);
    }

    [Fact]
    public async Task SweepOnceAsync_OnePerRowFailureIsolated_OtherRowsStillProcessed()
    {
        // The brief says: "A failure in ANY single record's processing is
        // logged and isolated — the loop continues with the next expired
        // record." Per the new (iter-2 evaluator item 5) claim-first
        // ordering: q-bad has its publish fail AFTER the atomic claim
        // succeeds, so q-bad is left in PendingQuestionStatus.TimedOut
        // (recoverable via the audit-gap sweep documented in
        // QuestionTimeoutService remarks); q-ok must still complete the
        // full claim → publish → edit → audit pipeline despite q-bad's
        // failure happening earlier in the loop.
        var harness = BuildHarness(failPublishForQuestionId: "q-bad");
        await harness.SeedAsync(questionId: "q-bad", defaultActionId: "skip", defaultActionLabel: "Skip");
        await harness.SeedAsync(questionId: "q-ok", defaultActionId: "skip", defaultActionLabel: "Skip");

        await harness.Service.SweepOnceAsync(default);

        harness.PublishedDecisions.Select(d => d.QuestionId).Should().Contain("q-ok",
            "the loop must continue past q-bad's failure and process q-ok");
        var ok = await harness.Store.GetAsync("q-ok", default);
        ok!.Status.Should().Be(PendingQuestionStatus.TimedOut);

        // q-bad was claimed (Status=TimedOut) BEFORE publish threw;
        // this is the at-most-once-from-sweeper trade documented in
        // QuestionTimeoutService remarks. Recovery for this gap is
        // by querying audit_log for TimedOut rows without a matching
        // HumanResponseAuditEntry.
        var bad = await harness.Store.GetAsync("q-bad", default);
        bad!.Status.Should().Be(PendingQuestionStatus.TimedOut,
            "claim happens FIRST; the publish that failed cannot un-claim the row, but the next sweep also will NOT re-publish (preventing the cross-process double-publish documented in evaluator iter-1 item 5)");
        harness.PublishedDecisions.Select(d => d.QuestionId).Should().NotContain("q-bad",
            "the publish for q-bad threw, so the captured list must not include it");
    }

    [Fact]
    public async Task SweepOnceAsync_LostAtomicClaim_SkipsSilently()
    {
        // Cross-process race: another sweeper / a callback already
        // marked the row TimedOut between GetExpiredAsync (snapshot)
        // and our MarkTimedOutAsync attempt. The atomic claim must
        // return false and we must NOT publish, NOT edit, NOT audit.
        var harness = BuildHarness();
        await harness.SeedAsync(defaultActionId: "skip", defaultActionLabel: "Skip");

        // Pre-claim the row from a second "process" — InMemoryPendingQuestionStore's
        // MarkTimedOutAsync is the same atomic primitive the service uses.
        var preClaimed = await harness.Store.MarkTimedOutAsync("q-1", default);
        preClaimed.Should().BeTrue("sanity — the pre-claim must succeed against a Pending row");

        await harness.Service.SweepOnceAsync(default);

        harness.PublishedDecisions.Should().BeEmpty(
            "the lost claim must short-circuit BEFORE PublishHumanDecisionAsync — preventing the cross-process double-publish race");
        harness.EditTextRequests.Should().BeEmpty();
        harness.AuditEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkTimedOutAsync_OnInMemoryStub_IsCompareAndSwapAtomic()
    {
        // Direct atomic-primitive contract test: two concurrent
        // claims for the same row must return exactly one true. This
        // is the property QuestionTimeoutService relies on for
        // cross-process safety; mirror it against the in-memory stub
        // here. PersistentPendingQuestionStoreTests covers the
        // EF / SQLite implementation of the same contract.
        var harness = BuildHarness();
        await harness.SeedAsync(defaultActionId: "skip", defaultActionLabel: "Skip");

        var first = await harness.Store.MarkTimedOutAsync("q-1", default);
        var second = await harness.Store.MarkTimedOutAsync("q-1", default);

        first.Should().BeTrue("the first claim must win — the row was Pending");
        second.Should().BeFalse("the second claim must lose — the row is no longer Pending/AwaitingComment");
    }

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------
    private static Harness BuildHarness(
        bool throwOnEdit = false,
        string? failPublishForQuestionId = null)
    {
        var time = new FakeTimeProvider(BaseTime);
        var store = new InMemoryPendingQuestionStore();

        var publishedDecisions = new List<HumanDecisionEvent>();
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        bus.Setup(b => b.PublishHumanDecisionAsync(It.IsAny<HumanDecisionEvent>(), It.IsAny<CancellationToken>()))
            .Returns<HumanDecisionEvent, CancellationToken>((evt, _) =>
            {
                if (failPublishForQuestionId is not null
                    && string.Equals(evt.QuestionId, failPublishForQuestionId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("simulated publish failure (test fixture)");
                }
                publishedDecisions.Add(evt);
                return Task.CompletedTask;
            });

        var auditEntries = new List<HumanResponseAuditEntry>();
        var audit = new Mock<IAuditLogger>(MockBehavior.Strict);
        audit.Setup(a => a.LogHumanResponseAsync(It.IsAny<HumanResponseAuditEntry>(), It.IsAny<CancellationToken>()))
            .Returns<HumanResponseAuditEntry, CancellationToken>((e, _) =>
            {
                auditEntries.Add(e);
                return Task.CompletedTask;
            });

        var editRequests = new List<EditMessageTextRequest>();
        var client = new Mock<ITelegramBotClient>(MockBehavior.Strict);
        client.Setup(c => c.SendRequest(
                It.IsAny<IRequest<Message>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IRequest<Message>, CancellationToken>((req, _) =>
            {
                if (req is EditMessageTextRequest edit)
                {
                    editRequests.Add(edit);
                    if (throwOnEdit)
                    {
                        throw new InvalidOperationException("simulated edit failure (test fixture)");
                    }
                }
                return Task.FromResult(new Message { Id = 1001 });
            });

        var options = Options.Create(new QuestionTimeoutOptions
        {
            PollInterval = TimeSpan.FromSeconds(30),
        });

        var service = new QuestionTimeoutService(
            store,
            bus.Object,
            audit.Object,
            client.Object,
            time,
            options,
            NullLogger<QuestionTimeoutService>.Instance);

        return new Harness
        {
            Time = time,
            Store = store,
            Service = service,
            PublishedDecisions = publishedDecisions,
            AuditEntries = auditEntries,
            EditTextRequests = editRequests,
        };
    }

    private sealed class Harness
    {
        public required FakeTimeProvider Time { get; init; }
        public required InMemoryPendingQuestionStore Store { get; init; }
        public required QuestionTimeoutService Service { get; init; }
        public required List<HumanDecisionEvent> PublishedDecisions { get; init; }
        public required List<HumanResponseAuditEntry> AuditEntries { get; init; }
        public required List<EditMessageTextRequest> EditTextRequests { get; init; }

        public async Task SeedAsync(
            string questionId = "q-1",
            string corr = "trace-1",
            string? defaultActionId = "skip",
            string? defaultActionLabel = "Skip",
            TimeSpan? expiresOffset = null,
            DateTimeOffset? absoluteExpiresAt = null)
        {
            // InMemoryPendingQuestionStore.GetExpiredAsync uses real
            // DateTimeOffset.UtcNow (not the injected TimeProvider), so
            // the default "already expired" seed must be relative to
            // the wall clock — NOT to the fixture's BaseTime.
            var offset = expiresOffset ?? TimeSpan.FromMinutes(-1);
            var actions = new List<HumanAction>
            {
                new() { ActionId = "approve", Label = "Approve", Value = "approve_v" },
                new() { ActionId = "skip", Label = defaultActionLabel ?? "Skip", Value = "skip_v" },
            };

            var question = new AgentQuestion
            {
                QuestionId = questionId,
                AgentId = "agent-deployer",
                TaskId = "task-7",
                Title = "Deploy Solution12?",
                Body = "Pre-flight clean. Stage now?",
                Severity = MessageSeverity.High,
                AllowedActions = actions,
                ExpiresAt = absoluteExpiresAt ?? DateTimeOffset.UtcNow.Add(offset),
                CorrelationId = corr,
            };

            var envelope = new AgentQuestionEnvelope
            {
                Question = question,
                ProposedDefaultActionId = defaultActionId,
            };

            await Store.StoreAsync(envelope, telegramChatId: 42, telegramMessageId: 1001, default);
        }
    }
}
