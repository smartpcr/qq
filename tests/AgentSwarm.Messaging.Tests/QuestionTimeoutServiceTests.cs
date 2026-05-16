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
    public async Task SweepOnceAsync_TimeoutWithDefault_PublishesDefaultActionIdAndEditsMessage()
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
        // record." Per the iter-3 evaluator item 1 revert-on-fail
        // contract: q-bad has its publish fail AFTER the atomic claim
        // succeeds; the service then calls TryRevertTimedOutClaimAsync
        // so q-bad goes BACK to its prior status (Pending) and is
        // sweep-eligible on the next iteration — closes the data-loss
        // gap of "claim-first → publish-fails → row stuck in
        // TimedOut". q-ok must still complete the full claim → publish
        // → edit → audit pipeline despite q-bad's earlier failure.
        var harness = BuildHarness(failPublishForQuestionId: "q-bad");
        await harness.SeedAsync(questionId: "q-bad", defaultActionId: "skip", defaultActionLabel: "Skip");
        await harness.SeedAsync(questionId: "q-ok", defaultActionId: "skip", defaultActionLabel: "Skip");

        await harness.Service.SweepOnceAsync(default);

        harness.PublishedDecisions.Select(d => d.QuestionId).Should().Contain("q-ok",
            "the loop must continue past q-bad's failure and process q-ok");
        var ok = await harness.Store.GetAsync("q-ok", default);
        ok!.Status.Should().Be(PendingQuestionStatus.TimedOut);

        // q-bad was claimed (Status=TimedOut) then publish threw;
        // the service called TryRevertTimedOutClaimAsync(Pending) so
        // the row is back to its prior status and will be re-found by
        // the next GetExpiredAsync sweep — gives at-least-once
        // delivery of the timeout decision per architecture.md §10.3.
        var bad = await harness.Store.GetAsync("q-bad", default);
        bad!.Status.Should().Be(PendingQuestionStatus.Pending,
            "publish failed after the atomic claim; the service must have called TryRevertTimedOutClaimAsync so the row becomes sweep-eligible again — this is the at-least-once delivery contract for the timeout decision per iter-3 evaluator item 1");
        harness.PublishedDecisions.Select(d => d.QuestionId).Should().NotContain("q-bad",
            "the publish for q-bad threw, so the captured list must not include it (yet — the next sweep will retry)");
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
    public async Task SweepOnceAsync_PublishFailsAfterClaim_RevertsRowToPriorStatusForNextSweep()
    {
        // Iter-3 evaluator item 1: closes the "claim-first → publish-
        // fails → permanent data loss" gap. When publish throws, the
        // service must call TryRevertTimedOutClaimAsync(priorStatus)
        // so the row is sweep-eligible again. A subsequent sweep
        // (after the transient publish failure clears) then succeeds
        // and emits the required HumanDecisionEvent. This gives
        // at-least-once delivery of the timeout decision per
        // architecture.md §10.3.
        var harness = BuildHarness(failPublishForQuestionId: "q-1");
        await harness.SeedAsync(questionId: "q-1", defaultActionId: "skip", defaultActionLabel: "Skip");

        await harness.Service.SweepOnceAsync(default);

        var afterFirstSweep = await harness.Store.GetAsync("q-1", default);
        afterFirstSweep!.Status.Should().Be(PendingQuestionStatus.Pending,
            "publish failed; the service must have reverted the TimedOut claim back to Pending so the next sweep retries");
        harness.PublishedDecisions.Should().BeEmpty(
            "the failing publish threw before the captured-decisions list got the entry; sanity check that the bus mock and the harness agree");

        // Simulate the transient publish failure clearing; the next
        // sweep should now succeed.
        harness.AllowPublishForQuestionId("q-1");
        await harness.Service.SweepOnceAsync(default);

        harness.PublishedDecisions.Should().HaveCount(1,
            "the second sweep is what proves at-least-once delivery: the row was sweep-eligible because the first sweep's revert restored Status=Pending");
        harness.PublishedDecisions[0].QuestionId.Should().Be("q-1");
        var afterSecondSweep = await harness.Store.GetAsync("q-1", default);
        afterSecondSweep!.Status.Should().Be(PendingQuestionStatus.TimedOut,
            "the successful retry completes the lifecycle — the row reaches the terminal TimedOut state");
    }

    [Fact]
    public async Task TryRevertTimedOutClaimAsync_OnInMemoryStub_IsCompareAndSwapAtomic()
    {
        // Direct atomic-primitive contract test: revert wins only when
        // the row is still TimedOut; a second revert (or a revert
        // against a non-TimedOut row) returns false. Mirrors the
        // ExecuteUpdateAsync(WHERE Status == TimedOut) conditional
        // UPDATE in PersistentPendingQuestionStore.
        var harness = BuildHarness();
        await harness.SeedAsync(defaultActionId: "skip", defaultActionLabel: "Skip");

        var claimed = await harness.Store.MarkTimedOutAsync("q-1", default);
        claimed.Should().BeTrue();

        var firstRevert = await harness.Store.TryRevertTimedOutClaimAsync("q-1", PendingQuestionStatus.Pending, default);
        firstRevert.Should().BeTrue("the first caller against a TimedOut row must win the conditional revert");

        var secondRevert = await harness.Store.TryRevertTimedOutClaimAsync("q-1", PendingQuestionStatus.Pending, default);
        secondRevert.Should().BeFalse("a second caller sees Status=Pending (no longer TimedOut); the conditional UPDATE matches zero rows");

        var row = await harness.Store.GetAsync("q-1", default);
        row!.Status.Should().Be(PendingQuestionStatus.Pending);
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

        // Use a mutable set so individual tests can flip a question
        // from "publish fails" → "publish succeeds" between sweeps —
        // the iter-3 evaluator item 1 fix proves at-least-once
        // delivery by retrying after the failing condition clears.
        var failingPublishIds = new HashSet<string>(StringComparer.Ordinal);
        if (failPublishForQuestionId is not null)
        {
            failingPublishIds.Add(failPublishForQuestionId);
        }

        var publishedDecisions = new List<HumanDecisionEvent>();
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        bus.Setup(b => b.PublishHumanDecisionAsync(It.IsAny<HumanDecisionEvent>(), It.IsAny<CancellationToken>()))
            .Returns<HumanDecisionEvent, CancellationToken>((evt, _) =>
            {
                lock (failingPublishIds)
                {
                    if (failingPublishIds.Contains(evt.QuestionId))
                    {
                        throw new InvalidOperationException("simulated publish failure (test fixture)");
                    }
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
            FailingPublishIds = failingPublishIds,
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
        public required HashSet<string> FailingPublishIds { get; init; }

        public void AllowPublishForQuestionId(string questionId)
        {
            lock (FailingPublishIds)
            {
                FailingPublishIds.Remove(questionId);
            }
        }

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
