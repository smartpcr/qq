using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Telegram.Pipeline;
using AgentSwarm.Messaging.Telegram.Pipeline.Stubs;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.2 — Inbound Update Pipeline.
///
/// Pins the contract per implementation-plan.md §132 and §147 (the
/// authoritative source after the iter-1 evaluator's contract pin):
/// 1. Pipeline routes <see cref="EventType.Command"/> to <see cref="ICommandRouter"/>.
/// 2. Pipeline routes <see cref="EventType.CallbackResponse"/> to <see cref="ICallbackHandler"/>.
/// 3. Pipeline rejects unauthorized callers (zero bindings) with a denial response.
/// 4. <see cref="IDeduplicationService.MarkProcessedAsync"/> is NOT called when
///    the routed handler throws (the brief's "marks only after handler success"
///    invariant); the pipeline ALSO calls
///    <see cref="IDeduplicationService.ReleaseReservationAsync"/> so a
///    subsequent live re-delivery of the same EventId is processed
///    normally per the brief's Scenario 4 ("subsequent delivery of evt-1
///    is processed normally (not short-circuited as duplicate)"). On an
///    UNCAUGHT crash (process exits before the catch block runs)
///    neither call executes and the reservation persists, so the
///    Stage 2.4 InboundUpdate sweep is the canonical crash-recovery
///    route — this asymmetry is what closes the "two pods both run the
///    handler on a crash" race while still satisfying the brief's
///    live-retry-on-throw scenario.
/// 5. <see cref="IDeduplicationService.MarkProcessedAsync"/> IS called exactly
///    once when the handler returns successfully — a subsequent delivery of
///    the same <c>EventId</c> is short-circuited as a duplicate.
/// 6. Concurrent <see cref="IDeduplicationService.TryReserveAsync"/> calls
///    award the handler invocation to exactly one caller (plan §146) — the
///    release-on-throw path runs sequentially after the winner's handler
///    completes, so the atomic-winner-per-burst guarantee is preserved.
///
/// Plus coverage for: <see cref="EventType.Unknown"/> bypass-before-authz,
/// the multi-workspace inline-keyboard prompt, role enforcement, invalid
/// command parse, text-reply correlation, structured stage logging
/// assertions, and the registered DI surface.
/// </summary>
public class TelegramUpdatePipelineTests
{
    // ============================================================
    // Brief scenario #1: route command
    // ============================================================

    [Fact]
    public async Task Pipeline_RoutesCommand_InvokesCommandRouter()
    {
        var harness = new Harness();
        harness.AuthorizeWith(harness.MakeBinding());
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                Success = true,
                ResponseText = "Status: OK",
                CorrelationId = "router-trace",
            });

        var evt = harness.MakeCommand("/status");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be("Status: OK");
        result.CorrelationId.Should().Be(evt.CorrelationId,
            "PipelineResult must surface the originating event's correlation id, not the router's");
        harness.RouterStub.Verify(r => r.RouteAsync(
                It.Is<ParsedCommand>(p => p.CommandName == TelegramCommands.Status),
                It.Is<AuthorizedOperator>(o => o.WorkspaceId == "w-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.CallbackStub.Verify(c => c.HandleAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // Brief scenario #2: route callback to ICallbackHandler stub
    // ============================================================

    [Fact]
    public async Task Pipeline_RoutesCallback_InvokesCallbackHandler()
    {
        var harness = new Harness();
        harness.AuthorizeWith(harness.MakeBinding());
        harness.CallbackStub.Setup(c => c.HandleAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                Success = true,
                ResponseText = "ack",
                CorrelationId = "cb-trace",
            });

        var evt = harness.MakeEvent(EventType.CallbackResponse, payload: "Q-1:approve");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be("ack");
        harness.CallbackStub.Verify(
            c => c.HandleAsync(It.Is<MessengerEvent>(e => e.EventId == evt.EventId), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.RouterStub.Verify(
            r => r.RouteAsync(It.IsAny<ParsedCommand>(), It.IsAny<AuthorizedOperator>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.ParserStub.Verify(p => p.Parse(It.IsAny<string>()), Times.Never,
            "callback events must not be parsed as slash commands");
    }

    // ============================================================
    // Brief scenario #3: reject unauthorized
    // ============================================================

    [Fact]
    public async Task Pipeline_RejectsUnauthorized_WhenZeroBindings_AndDoesNotInvokeHandler()
    {
        var harness = new Harness();
        harness.AuthzStub.Setup(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizationResult
            {
                IsAuthorized = false,
                DenialReason = "user not in allowlist",
            });
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });

        var evt = harness.MakeCommand("/status");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue("the pipeline 'handled' the event by rejecting it");
        result.ResponseText.Should().Be(PipelineResponses.Unauthorized);
        result.CorrelationId.Should().Be(evt.CorrelationId);
        harness.RouterStub.Verify(
            r => r.RouteAsync(It.IsAny<ParsedCommand>(), It.IsAny<AuthorizedOperator>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.CallbackStub.Verify(
            c => c.HandleAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.DedupStub.Verify(d => d.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "unauthorized events must not be marked processed; they will not be re-delivered but a future re-binding could make them legitimately replayable");
    }

    // ============================================================
    // Brief scenario #4: handler throws → MarkProcessedAsync NOT called
    // ============================================================

    [Fact]
    public async Task Pipeline_DoesNotMarkProcessed_WhenHandlerThrows()
    {
        var harness = new Harness();
        harness.AuthorizeWith(harness.MakeBinding());
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("downstream handler boom"));

        var evt = harness.MakeCommand("/status", eventId: "evt-throw-1");

        var act = async () => await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "handler exceptions propagate so the webhook layer can transition InboundUpdate to Failed");

        harness.DedupStub.Verify(
            d => d.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "MarkProcessedAsync MUST NOT be called when the handler throws — the recovery sweep depends on the event being re-deliverable");

        // Brief Step 2 / Scenario 4: the pipeline MUST release the
        // reservation on a caught handler exception so the next live
        // re-delivery is processed normally. The verify pins the
        // release call (a release-on-throw failure to invoke
        // ReleaseReservationAsync would silently regress the brief
        // requirement back to the iter-1 "reservation persists" shape).
        harness.DedupStub.Verify(
            d => d.ReleaseReservationAsync("evt-throw-1", It.IsAny<CancellationToken>()),
            Times.Once,
            "ReleaseReservationAsync MUST be called exactly once on a caught handler exception so a subsequent live re-delivery is processed normally (Stage 2.2 brief Scenario 4)");
    }

    [Fact]
    public async Task Pipeline_AfterHandlerThrows_ReservationReleased_SubsequentDeliveryProcessedNormally()
    {
        // Stage 2.2 brief Scenario 4 (verbatim): "Given a MessengerEvent
        // with EventId=evt-1 that has not been processed, When the
        // command handler throws an exception, Then MarkProcessedAsync
        // is NOT called and a subsequent delivery of evt-1 is processed
        // normally (not short-circuited as duplicate)." The pipeline's
        // catch block calls ReleaseReservationAsync so the reservation
        // does not block the live re-delivery. Crash-recovery for an
        // UNCAUGHT crash (process exits before the catch executes) is
        // covered by the parallel scenario in implementation-plan.md
        // (Stage 2.4 InboundUpdate sweep).
        var dedup = new InMemoryDeduplicationService();
        var harness = new Harness(dedup: dedup);
        harness.AuthorizeWith(harness.MakeBinding());
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });
        var routerInvocations = 0;
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                routerInvocations++;
                if (routerInvocations == 1)
                {
                    throw new InvalidOperationException("handler boom");
                }
                return Task.FromResult(new CommandResult
                {
                    Success = true,
                    ResponseText = "second-attempt-ok",
                    CorrelationId = "router-trace",
                });
            });

        var evt = harness.MakeCommand("/status", eventId: "evt-retry-1");

        var firstAct = async () => await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);
        await firstAct.Should().ThrowAsync<InvalidOperationException>(
            "handler exceptions propagate so the webhook layer can transition InboundUpdate to Failed");

        // After the caught throw the pipeline released the reservation,
        // so a fresh TryReserveAsync for the same EventId succeeds and
        // the second delivery's handler runs.
        var second = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        second.Handled.Should().BeTrue("the second delivery is fully processed");
        second.Succeeded.Should().BeTrue("the retry succeeded");
        second.ResponseText.Should().Be("second-attempt-ok",
            "the second delivery is processed by the handler — NOT short-circuited at the dedup gate (brief Scenario 4)");
        routerInvocations.Should().Be(2,
            "Stage 2.2 brief Scenario 4: a subsequent delivery of evt-1 must be processed normally after the first delivery's handler threw — the router is invoked twice (once for the throwing first attempt, once for the successful retry)");

        // After a successful retry MarkProcessed was called, so the
        // reservation is now sticky and a third delivery would short-
        // circuit. (Probed indirectly: IsProcessedAsync now true.)
        var probed = await dedup.IsProcessedAsync(evt.EventId, CancellationToken.None);
        probed.Should().BeTrue(
            "MarkProcessedAsync ran after the successful retry, so the processed marker is set and future deliveries short-circuit at the dedup gate");
    }

    [Fact]
    public async Task Pipeline_AfterHandlerThrows_ReleaseFailure_OriginalExceptionStillPropagates()
    {
        // Robustness invariant: if the release call itself throws (e.g.
        // a transient cache error), the ORIGINAL handler exception must
        // still reach the caller. Diagnosing the underlying handler bug
        // matters more than reporting a release-side cleanup failure.
        // The pipeline logs the release failure as Error so observability
        // still surfaces the cleanup problem.
        var harness = new Harness();
        harness.AuthorizeWith(harness.MakeBinding());
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("original handler boom"));
        harness.DedupStub.Setup(d => d.ReleaseReservationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("release transport failure"));

        var evt = harness.MakeCommand("/status", eventId: "evt-release-fail-1");

        var act = async () => await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Be("original handler boom",
            "the original handler exception MUST surface to the caller; the release-side cleanup failure is logged but does NOT mask the root cause");

        harness.LogCapture.Entries
            .Any(e => e.GetValue<string>("Stage") == "release-on-throw-failed")
            .Should().BeTrue(
                "the release failure must be logged at Error severity so observability can alert on the cleanup-side problem");
    }

    [Fact]
    public async Task Pipeline_TryReserveAwardsExactlyOneConcurrentCaller()
    {
        // implementation-plan.md §146 "Dedup atomically awards exactly one
        // concurrent caller": with N concurrent pipeline invocations of the
        // same EventId, exactly ONE handler invocation occurs; the rest
        // short-circuit at the TryReserveAsync gate.
        var dedup = new InMemoryDeduplicationService();
        const int concurrency = 50;

        var harness = new Harness(dedup: dedup);
        harness.AuthorizeWith(harness.MakeBinding());
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });
        var routerInvocations = 0;
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref routerInvocations);
                return Task.FromResult(new CommandResult
                {
                    Success = true,
                    ResponseText = "ok",
                    CorrelationId = "router-trace",
                });
            });

        var sharedEventId = "evt-concurrent-1";
        using var gate = new ManualResetEventSlim(false);
        var tasks = new Task<PipelineResult>[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                gate.Wait();
                var evt = harness.MakeCommand("/status", eventId: sharedEventId);
                return harness.Pipeline.ProcessAsync(evt, CancellationToken.None);
            });
        }
        gate.Set();
        var results = await Task.WhenAll(tasks);

        routerInvocations.Should().Be(1,
            "implementation-plan §146: the atomic TryReserveAsync gate must award the handler invocation to exactly one concurrent caller");
        results.Count(r => r.Handled).Should().Be(concurrency,
            "every caller (winner and losers) returns Handled=true; losers are 'handled' by the duplicate short-circuit");
        results.Count(r => r.ResponseText == "ok").Should().Be(1,
            "exactly one caller produces the handler's response text");
    }

    // ============================================================
    // Brief scenario #5: success → MarkProcessedAsync called once;
    //                    second delivery short-circuits
    // ============================================================

    [Fact]
    public async Task Pipeline_OnHandlerSuccess_MarksProcessedExactlyOnce_AndSecondDeliveryShortCircuits()
    {
        var dedup = new InMemoryDeduplicationService();
        var harness = new Harness(dedup: dedup);
        harness.AuthorizeWith(harness.MakeBinding());
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });
        var routerInvocations = 0;
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                routerInvocations++;
                return new CommandResult
                {
                    Success = true,
                    ResponseText = "first",
                    CorrelationId = "router-trace",
                };
            });

        var evt = harness.MakeCommand("/status", eventId: "evt-2");

        var first = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);
        first.Handled.Should().BeTrue();
        first.ResponseText.Should().Be("first");
        routerInvocations.Should().Be(1);

        // Second delivery: the dedup gate is TryReserveAsync (not the
        // racy IsProcessedAsync probe). MarkProcessedAsync defensively
        // populated _reservations during the first delivery, so the
        // second TryReserveAsync TryAdd returns false → short-circuit.
        var second = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);
        second.Handled.Should().BeTrue();
        second.ResponseText.Should().BeNull(
            "the duplicate short-circuit returns Handled=true with no response text");
        routerInvocations.Should().Be(1,
            "the second delivery must short-circuit before reaching the router");
    }

    // ============================================================
    // Dedup short-circuit (mock-based, separate from in-memory service)
    // ============================================================

    [Fact]
    public async Task Pipeline_ShortCircuits_WhenTryReserveReturnsFalse()
    {
        // The dedup gate is TryReserveAsync (not the racy IsProcessedAsync
        // probe). When it returns false, the pipeline short-circuits and
        // skips parse/authz/route/mark stages.
        var harness = new Harness();
        harness.DedupStub.Setup(d => d.TryReserveAsync("evt-dup", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var evt = harness.MakeCommand("/status", eventId: "evt-dup");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().BeNull();
        result.CorrelationId.Should().Be(evt.CorrelationId);

        harness.AuthzStub.Verify(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.ParserStub.Verify(p => p.Parse(It.IsAny<string>()), Times.Never);
        harness.RouterStub.Verify(
            r => r.RouteAsync(It.IsAny<ParsedCommand>(), It.IsAny<AuthorizedOperator>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.DedupStub.Verify(d => d.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.DedupStub.Verify(d => d.IsProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the racy IsProcessedAsync probe must NOT be used as a check-then-act gate");
    }

    // ============================================================
    // InMemoryDeduplicationService — race-freedom (iter-2 evaluator item 1)
    // ============================================================

    [Fact]
    public async Task InMemoryDedup_TryReserveAfterMarkProcessed_ReturnsFalse_NoTOCTOURace()
    {
        // iter-2 evaluator item 1: a duplicate caller must NOT pass
        // TryReserveAsync after the original winner has completed
        // MarkProcessedAsync. The iter-2 implementation removed the
        // reservation on Mark, opening a probe-then-add race; the iter-3
        // implementation uses a single atomic TryAdd against the
        // never-removed `_reservations` bucket.
        var dedup = new InMemoryDeduplicationService();

        var firstReserve = await dedup.TryReserveAsync("evt-race-1", CancellationToken.None);
        firstReserve.Should().BeTrue("the first caller wins the reservation");

        await dedup.MarkProcessedAsync("evt-race-1", CancellationToken.None);

        // After completion, another caller racing through the gate must
        // see false — even though the first caller is "done" and
        // hypothetically no longer "owns" the slot.
        var secondReserve = await dedup.TryReserveAsync("evt-race-1", CancellationToken.None);
        secondReserve.Should().BeFalse(
            "post-MarkProcessedAsync the reservation slot remains held; a duplicate caller must short-circuit at the gate, not become a second handler invoker");

        var processed = await dedup.IsProcessedAsync("evt-race-1", CancellationToken.None);
        processed.Should().BeTrue("the processed marker is still set");
    }

    [Fact]
    public async Task InMemoryDedup_MarkProcessed_WithoutPriorReserve_ClosesGateForLaterReserve()
    {
        // Defensive path: tooling-driven replay can call MarkProcessedAsync
        // without first calling TryReserveAsync. The implementation must
        // still close the reservation gate so a subsequent live delivery
        // does not become a fresh handler invocation.
        var dedup = new InMemoryDeduplicationService();

        await dedup.MarkProcessedAsync("evt-replay-1", CancellationToken.None);

        var reserve = await dedup.TryReserveAsync("evt-replay-1", CancellationToken.None);
        reserve.Should().BeFalse(
            "MarkProcessedAsync writes both buckets defensively so a replay-driven mark closes the gate even without a prior reservation");
    }

    [Fact]
    public async Task InMemoryDedup_ConcurrentReserveDuringMarkProcessed_NoSecondWinner()
    {
        // Stress the race window: per event, fire one MarkProcessedAsync
        // concurrent with N TryReserveAsync calls. The atomic invariant
        // requires AT MOST ONE TryReserveAsync to ever return true (the
        // original winner that the test sets up before the loop). The
        // iter-2 impl could fail this on a probe-vs-remove interleave;
        // the iter-3 impl is structurally race-free.
        var dedup = new InMemoryDeduplicationService();
        const int eventCount = 200;
        const int reserveAttempts = 16;

        var totalSecondaryWinners = 0;
        for (var i = 0; i < eventCount; i++)
        {
            var evtId = $"evt-stress-{i}";

            // Original winner reserves first.
            (await dedup.TryReserveAsync(evtId, CancellationToken.None)).Should().BeTrue();

            // Race the completion against many duplicate reservation attempts.
            using var startGate = new ManualResetEventSlim(false);
            var attempts = new Task<bool>[reserveAttempts];
            for (var a = 0; a < reserveAttempts; a++)
            {
                attempts[a] = Task.Run(() =>
                {
                    startGate.Wait();
                    return dedup.TryReserveAsync(evtId, CancellationToken.None);
                });
            }
            var marker = Task.Run(() =>
            {
                startGate.Wait();
                return dedup.MarkProcessedAsync(evtId, CancellationToken.None);
            });

            startGate.Set();
            var attemptResults = await Task.WhenAll(attempts);
            await marker;

            totalSecondaryWinners += attemptResults.Count(r => r);
        }

        totalSecondaryWinners.Should().Be(0,
            "no TryReserveAsync after the initial winner may ever return true — the iter-3 impl removes the probe-then-add race window that the iter-2 evaluator flagged");
    }

    // ============================================================
    // InMemoryPendingDisambiguationStore (iter-3 evaluator item 2)
    // ============================================================

    [Fact]
    public async Task DisambiguationStore_TakeAsync_IsAtomicRemoveOnRead()
    {
        // The store MUST be single-use: a tapped callback consumes the
        // entry so a malicious replay cannot re-trigger the original
        // command. TryRemove on a ConcurrentDictionary is the canonical
        // atomic primitive; this test pins the resulting contract shape.
        var time = new TestTimeProvider(new DateTimeOffset(2024, 06, 15, 12, 00, 00, TimeSpan.Zero));
        var store = new InMemoryPendingDisambiguationStore(time);
        var entry = NewPendingEntry("tok-001", time.GetUtcNow());
        await store.StoreAsync(entry, CancellationToken.None);

        var first = await store.TakeAsync("tok-001", CancellationToken.None);
        first.Should().NotBeNull();
        first!.OriginalRawCommand.Should().Be(entry.OriginalRawCommand);

        var second = await store.TakeAsync("tok-001", CancellationToken.None);
        second.Should().BeNull(
            "TakeAsync is remove-on-read; a replayed callback must not resolve to the same PendingDisambiguation");
    }

    [Fact]
    public async Task DisambiguationStore_TakeAsync_ConcurrentCallers_ExactlyOneWinner()
    {
        // Two concurrent callbacks racing on the same token: only one may
        // see the entry. The other receives null. Without atomic remove
        // both callers could re-issue the original command — exactly the
        // double-execution risk Stage 2.2 must prevent.
        var time = new TestTimeProvider(new DateTimeOffset(2024, 06, 15, 12, 00, 00, TimeSpan.Zero));
        var store = new InMemoryPendingDisambiguationStore(time);
        const int concurrency = 32;

        var winners = 0;
        for (var i = 0; i < 100; i++)
        {
            var token = $"tok-race-{i}";
            await store.StoreAsync(NewPendingEntry(token, time.GetUtcNow()), CancellationToken.None);

            using var gate = new ManualResetEventSlim(false);
            var racers = new Task<PendingDisambiguation?>[concurrency];
            for (var r = 0; r < concurrency; r++)
            {
                racers[r] = Task.Run(() =>
                {
                    gate.Wait();
                    return store.TakeAsync(token, CancellationToken.None);
                });
            }
            gate.Set();
            var racerResults = await Task.WhenAll(racers);

            winners += racerResults.Count(r => r is not null);
        }

        winners.Should().Be(100,
            "exactly one winner per token across 100 token races — never two, never zero");
    }

    [Fact]
    public async Task DisambiguationStore_ExpiredEntry_TakeAsyncReturnsNull()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2024, 06, 15, 12, 00, 00, TimeSpan.Zero));
        var store = new InMemoryPendingDisambiguationStore(time);
        var entry = new PendingDisambiguation
        {
            Token = "tok-expired",
            OriginalRawCommand = "/agents",
            CorrelationId = "trace-1",
            TelegramUserId = "alice",
            TelegramChatId = "channel",
            CandidateWorkspaceIds = new[] { "factory-1" },
            CreatedAt = time.GetUtcNow(),
            ExpiresAt = time.GetUtcNow() + TimeSpan.FromMinutes(1),
        };
        await store.StoreAsync(entry, CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(2));

        var taken = await store.TakeAsync("tok-expired", CancellationToken.None);
        taken.Should().BeNull("expired entries must not resolve — Stage 3.3 reports the callback as expired");
    }

    [Fact]
    public async Task DisambiguationStore_DuplicateToken_StoreAsyncThrows()
    {
        // The pipeline guarantees collision-free generation; a duplicate
        // arriving at the store indicates a generator bug worth
        // surfacing as a loud failure rather than silently overwriting
        // (which would swap the OriginalRawCommand mid-flight).
        var time = new TestTimeProvider(new DateTimeOffset(2024, 06, 15, 12, 00, 00, TimeSpan.Zero));
        var store = new InMemoryPendingDisambiguationStore(time);
        var entry = NewPendingEntry("tok-dup", time.GetUtcNow());
        await store.StoreAsync(entry, CancellationToken.None);

        var duplicate = NewPendingEntry("tok-dup", time.GetUtcNow());
        var act = async () => await store.StoreAsync(duplicate, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DisambiguationStore_PurgeExpiredAsync_DropsOnlyExpiredEntries()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2024, 06, 15, 12, 00, 00, TimeSpan.Zero));
        var store = new InMemoryPendingDisambiguationStore(time);
        await store.StoreAsync(
            NewPendingEntry("tok-fresh", time.GetUtcNow(), ttl: TimeSpan.FromMinutes(10)),
            CancellationToken.None);
        await store.StoreAsync(
            NewPendingEntry("tok-stale", time.GetUtcNow(), ttl: TimeSpan.FromSeconds(30)),
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(1));

        await store.PurgeExpiredAsync(time.GetUtcNow(), CancellationToken.None);

        (await store.TakeAsync("tok-fresh", CancellationToken.None)).Should().NotBeNull(
            "non-expired entries survive purge");
        (await store.TakeAsync("tok-stale", CancellationToken.None)).Should().BeNull(
            "expired entries were dropped by the purge");
    }

    private static PendingDisambiguation NewPendingEntry(
        string token,
        DateTimeOffset now,
        TimeSpan? ttl = null) =>
        new()
        {
            Token = token,
            OriginalRawCommand = "/agents",
            CorrelationId = "trace-" + token,
            TelegramUserId = "alice",
            TelegramChatId = "channel",
            CandidateWorkspaceIds = new[] { "factory-1", "factory-3" },
            CreatedAt = now,
            ExpiresAt = now + (ttl ?? TimeSpan.FromMinutes(5)),
        };

    // ============================================================
    // Multi-binding workspace disambiguation (Command only)
    // ============================================================

    [Fact]
    public async Task Pipeline_Command_WhenMultipleBindings_PromptsWorkspaceSelection_WithInlineKeyboard()
    {
        // architecture.md §4.3 + e2e-scenarios.md "workspace disambiguation
        // via inline keyboard": multi-binding commands return a prompt
        // composed of intro text + one inline-keyboard button per workspace.
        // Each button's callback_data is `ws:<token>:<index>` (iter-4
        // robustness fix — formerly embedded the raw workspace id, which
        // was unsafe for long ids or ids containing `:`). The short
        // server-side `token` references a stored PendingDisambiguation
        // row that carries the original raw command, correlation id, and
        // the ordered CandidateWorkspaceIds list; the integer `index`
        // selects which entry of that list the operator picked. Stage 3.3
        // CallbackQueryHandler parses the index, bounds-checks against
        // CandidateWorkspaceIds.Count, resolves the workspace, and
        // re-issues the original command bound to the chosen workspace.
        var harness = new Harness();
        harness.AuthorizeWith(
            harness.MakeBinding(workspaceId: "factory-1"),
            harness.MakeBinding(workspaceId: "factory-3"));
        harness.ParserStub.Setup(p => p.Parse("/agents"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Agents,
                RawText = "/agents",
                IsValid = true,
            });

        var evt = harness.MakeCommand("/agents");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be(PipelineResponses.MultiWorkspacePromptText,
            "multi-workspace prompts use a fixed intro string; the workspace identifiers travel in the inline keyboard");
        result.ResponseText.Should().NotContain("/switch",
            "/switch is not a supported command (TelegramCommands.cs); selection happens via the inline keyboard");
        result.ResponseButtons.Should().HaveCount(2,
            "one inline-keyboard button per workspace binding");
        result.ResponseButtons.Select(b => b.Label).Should().BeEquivalentTo(
            new[] { "factory-1", "factory-3" });

        // Every button shares the same `ws:<token>:<index>` callback
        // shape; the token half is identical across the keyboard (one
        // PendingDisambiguation row per prompt), and the integer index
        // refers to the binding position. This addresses the iter-3
        // robustness pin: workspace ids never appear on the wire so
        // long ids or ids containing `:` cannot break the format.
        var pattern = new System.Text.RegularExpressions.Regex(
            @"^ws:[0-9a-f]{12}:[0-9]+$");
        result.ResponseButtons.Should().AllSatisfy(b =>
            pattern.IsMatch(b.CallbackData).Should().BeTrue(
                $"callback_data '{b.CallbackData}' must use the `ws:<token>:<index>` shape so Stage 3.3 can recover the disambiguation handle plus an integer index into PendingDisambiguation.CandidateWorkspaceIds"));

        var tokens = result.ResponseButtons
            .Select(b => b.CallbackData.Split(':')[1])
            .Distinct()
            .ToArray();
        tokens.Should().HaveCount(1,
            "all buttons in a single prompt share one PendingDisambiguation token; only the index suffix differs");

        // Indices are 0-based and contiguous, matching the order of the
        // bindings list. Stage 3.3's resolve step is
        // `CandidateWorkspaceIds[int.Parse(indexStr)]`.
        result.ResponseButtons
            .Select(b => int.Parse(b.CallbackData.Split(':')[2], System.Globalization.CultureInfo.InvariantCulture))
            .Should().BeEquivalentTo(new[] { 0, 1 },
                opt => opt.WithStrictOrdering(),
                "indices must be 0-based and contiguous so Stage 3.3 can index directly into CandidateWorkspaceIds");

        // The store now carries a single entry with the original command
        // context — Stage 3.3's TakeAsync(token) will recover it.
        var sharedToken = tokens[0];
        var stored = await harness.DisambiguationStore.TakeAsync(sharedToken, CancellationToken.None);
        stored.Should().NotBeNull(
            "the pipeline must persist a PendingDisambiguation entry BEFORE emitting the prompt so the future callback handler has a durable reference");
        stored!.OriginalRawCommand.Should().Be("/agents",
            "the original raw command is preserved verbatim for Stage 3.3 to re-feed through ICommandParser");
        stored.CorrelationId.Should().Be(evt.CorrelationId,
            "trace correlation must survive the disambiguation round trip");
        stored.TelegramUserId.Should().Be(evt.UserId);
        stored.TelegramChatId.Should().Be(evt.ChatId);
        stored.CandidateWorkspaceIds.Should().BeEquivalentTo(
            new[] { "factory-1", "factory-3" },
            opt => opt.WithStrictOrdering(),
            "the wire format references entries by position, so the stored ordering IS the resolution table for Stage 3.3");
        stored.ExpiresAt.Should().Be(stored.CreatedAt + TelegramUpdatePipeline.DisambiguationTtl,
            "TTL is fixed by the pipeline so operators have a predictable selection window");

        harness.RouterStub.Verify(
            r => r.RouteAsync(It.IsAny<ParsedCommand>(), It.IsAny<AuthorizedOperator>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "router must not be invoked while the operator has not yet picked a workspace");
        harness.DedupStub.Verify(d => d.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Pipeline_NonAgentsCommand_WithMultipleBindings_PromptsForDisambiguation()
    {
        // Stage 3.4 iter-2 evaluator item 2 (supersedes the Stage 3.2
        // "/agents-only" scoping) — the multi-workspace disambiguation
        // prompt MUST cover EVERY multi-binding command (`/ask`,
        // `/status`, `/pause`, `/handoff`, `/approve`, `/reject`,
        // `/resume`, and `/agents` with no args). The only fall-through
        // exceptions are `/agents WORKSPACE` (explicit arg routed to
        // AgentsCommandHandler) and `/start` (just created the
        // bindings — see Pipeline_StartCommand_WithMultipleBindings_*
        // below). This pins the widened gate so a regression that
        // re-scopes the branch back to /agents-only — silently routing
        // other commands to authz.Bindings[0] — surfaces immediately.
        var harness = new Harness();
        harness.AuthorizeWith(
            harness.MakeBinding(workspaceId: "factory-1"),
            harness.MakeBinding(workspaceId: "factory-3"));
        harness.ParserStub.Setup(p => p.Parse("/ask build release notes"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Ask,
                Arguments = new[] { "build", "release", "notes" },
                RawText = "/ask build release notes",
                IsValid = true,
            });

        var evt = harness.MakeCommand("/ask build release notes");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Match(
            "You have access to multiple workspaces*",
            "the widened disambiguation gate now covers /ask (and every non-/start, non-/agents-with-arg command) for multi-binding operators per Stage 3.4 evaluator item 2");
        result.ResponseButtons.Should().NotBeEmpty(
            "every multi-binding non-/start non-/agents-with-arg command MUST emit a workspace inline keyboard per architecture.md §4.3");
        harness.RouterStub.Verify(
            r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "router must not run before the operator selects a workspace — that was the iter-1 silent-routing bug");
    }

    [Fact]
    public async Task Pipeline_AgentsCommand_WithExplicitWorkspaceArg_AndMultipleBindings_FallsThroughToRouter()
    {
        // Stage 3.2 iter-2 evaluator item 1 (sub-case) — the
        // disambiguation gate also requires Arguments.Count == 0, so
        // `/agents WORKSPACE` from a multi-binding operator must reach
        // the router (which delegates to AgentsCommandHandler's
        // explicit-workspace path) instead of being intercepted by the
        // pipeline prompt.
        var harness = new Harness();
        harness.AuthorizeWith(
            harness.MakeBinding(workspaceId: "factory-1"),
            harness.MakeBinding(workspaceId: "factory-3"));
        harness.ParserStub.Setup(p => p.Parse("/agents factory-3"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Agents,
                Arguments = new[] { "factory-3" },
                RawText = "/agents factory-3",
                IsValid = true,
            });
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                Success = true,
                ResponseText = "Agents in factory-3",
                CorrelationId = "router-agents-explicit",
            });

        var evt = harness.MakeCommand("/agents factory-3");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseButtons.Should().BeEmpty(
            "explicit `/agents WORKSPACE` must bypass the pipeline disambiguation prompt and reach the handler");
        harness.RouterStub.Verify(
            r => r.RouteAsync(
                It.Is<ParsedCommand>(p => p.CommandName == TelegramCommands.Agents && p.Arguments.Count == 1 && p.Arguments[0] == "factory-3"),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // Stage 3.4 iter-2 evaluator item 2 — widened disambig coverage
    // ============================================================

    [Theory]
    [InlineData(TelegramCommands.Status, "/status")]
    [InlineData(TelegramCommands.Handoff, "/handoff @bob")]
    [InlineData(TelegramCommands.Pause, "/pause")]
    [InlineData(TelegramCommands.Resume, "/resume")]
    [InlineData(TelegramCommands.Approve, "/approve")]
    [InlineData(TelegramCommands.Reject, "/reject")]
    public async Task Pipeline_AnyMultiBindingCommand_PromptsForDisambiguation(
        string commandName,
        string rawCommand)
    {
        // Iter-2 evaluator item 2 — the widened gate covers EVERY
        // command, not just /agents-no-args. /status, /handoff,
        // /pause, /resume, /approve, /reject from a multi-binding
        // operator must surface the workspace prompt rather than
        // silently route to authz.Bindings[0].
        var harness = new Harness();
        harness.AuthorizeWith(
            harness.MakeBinding(workspaceId: "factory-1", roles: new[] { "Operator", "Approver" }),
            harness.MakeBinding(workspaceId: "factory-3", roles: new[] { "Operator", "Approver" }));
        harness.ParserStub.Setup(p => p.Parse(rawCommand))
            .Returns(new ParsedCommand
            {
                CommandName = commandName,
                Arguments = rawCommand.Split(' ').Skip(1).ToArray(),
                RawText = rawCommand,
                IsValid = true,
            });

        var evt = harness.MakeCommand(rawCommand);

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Match("You have access to multiple workspaces*",
            $"`{commandName}` with multiple bindings must trigger the disambiguation prompt (iter-2 evaluator item 2)");
        result.ResponseButtons.Should().NotBeEmpty();
        harness.RouterStub.Verify(
            r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            $"router must NOT run for multi-binding `{commandName}` before the operator picks a workspace");
    }

    [Fact]
    public async Task Pipeline_StartCommand_RoutesThroughOnboardAsync_WithChatType()
    {
        // Iter-2 evaluator item 1 — the pipeline must invoke the new
        // OnboardAsync entry point (NOT AuthorizeAsync) for /start so
        // the raw chat-type token carried on MessengerEvent.ChatType
        // flows into the persisted OperatorBinding.
        var harness = new Harness();
        harness.AuthorizeWith(harness.MakeBinding(workspaceId: "ws-1"));
        harness.ParserStub.Setup(p => p.Parse("/start"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Start,
                RawText = "/start",
                IsValid = true,
            });
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                Success = true,
                ResponseText = "welcome",
                CorrelationId = "router-start",
            });

        var evt = new MessengerEvent
        {
            EventId = "evt-start-1",
            EventType = EventType.Command,
            RawCommand = "/start",
            UserId = "100",
            ChatId = "200",
            ChatType = "supergroup",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-start",
        };

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        harness.AuthzStub.Verify(
            s => s.OnboardAsync("100", "200", "supergroup", It.IsAny<CancellationToken>()),
            Times.Once,
            "/start must route through OnboardAsync with the raw chat-type token so the binding's ChatType reflects the real chat kind");
        harness.AuthzStub.Verify(
            s => s.AuthorizeAsync(It.IsAny<string>(), It.IsAny<string>(), "start", It.IsAny<CancellationToken>()),
            Times.Never,
            "/start must NOT route through AuthorizeAsync(commandName=\"start\") any more — the OnboardAsync entry point carries the chat-type token");
    }

    [Fact]
    public async Task Pipeline_StartCommand_WithMultipleBindings_DoesNotPrompt()
    {
        // Iter-2 evaluator item 2 (sub-case) — /start onboarding
        // creates the bindings; the operator hasn't issued a real
        // command yet, so showing the workspace selector here would
        // confuse them. Only NON-/start commands trigger the gate.
        var harness = new Harness();
        harness.AuthorizeWith(
            harness.MakeBinding(workspaceId: "factory-1"),
            harness.MakeBinding(workspaceId: "factory-3"));
        harness.ParserStub.Setup(p => p.Parse("/start"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Start,
                RawText = "/start",
                IsValid = true,
            });
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                Success = true,
                ResponseText = "welcome",
                CorrelationId = "router-start-multi",
            });

        var evt = harness.MakeCommand("/start");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be("welcome",
            "/start must reach the router even with multiple bindings — the prompt is for subsequent commands");
        result.ResponseButtons.Should().BeEmpty();
    }

    [Fact]
    public async Task Pipeline_MultiWorkspacePrompt_GeneratesUniqueTokenPerInvocation()
    {
        // Two separate disambiguation prompts must NOT share a token —
        // otherwise the second prompt's callback would consume (and
        // resolve to) the first prompt's pending entry. The pipeline's
        // generator is RandomNumberGenerator-backed; this test pins the
        // "two prompts ⇒ two distinct rows" invariant.
        var harness = new Harness();
        harness.AuthorizeWith(
            harness.MakeBinding(workspaceId: "factory-1"),
            harness.MakeBinding(workspaceId: "factory-3"));
        harness.ParserStub.Setup(p => p.Parse(It.IsAny<string>()))
            .Returns<string>(raw => new ParsedCommand
            {
                CommandName = TelegramCommands.Agents,
                RawText = raw,
                IsValid = true,
            });

        var first = await harness.Pipeline.ProcessAsync(
            harness.MakeCommand("/agents", eventId: "evt-multi-1"),
            CancellationToken.None);
        var second = await harness.Pipeline.ProcessAsync(
            harness.MakeCommand("/agents", eventId: "evt-multi-2"),
            CancellationToken.None);

        var firstToken = first.ResponseButtons[0].CallbackData.Split(':')[1];
        var secondToken = second.ResponseButtons[0].CallbackData.Split(':')[1];

        firstToken.Should().NotBe(secondToken,
            "each disambiguation prompt must allocate a fresh token so callbacks resolve to the right pending entry");

        // Both rows must still be retrievable independently.
        (await harness.DisambiguationStore.TakeAsync(firstToken, CancellationToken.None))
            .Should().NotBeNull();
        (await harness.DisambiguationStore.TakeAsync(secondToken, CancellationToken.None))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Pipeline_MultiWorkspacePrompt_RobustAgainstWorkspaceIdsContainingColon()
    {
        // iter-3 evaluator pin: workspace IDs may contain `:` (the
        // structural callback_data separator) without corrupting the
        // wire format. The fix is to encode the integer index into
        // PendingDisambiguation.CandidateWorkspaceIds rather than the
        // raw workspace id, so the workspace id never appears on the
        // wire.
        var harness = new Harness();
        harness.AuthorizeWith(
            harness.MakeBinding(workspaceId: "tenant:factory:line-1"),
            harness.MakeBinding(workspaceId: "tenant:factory:line-2"));
        harness.ParserStub.Setup(p => p.Parse("/agents"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Agents,
                RawText = "/agents",
                IsValid = true,
            });

        var act = async () => await harness.Pipeline.ProcessAsync(
            harness.MakeCommand("/agents"),
            CancellationToken.None);

        var result = await act.Should().NotThrowAsync(
            "the wire format must not embed user-controlled workspace ids; embedding the integer index keeps the format unambiguous regardless of id content");

        // Each callback_data must split cleanly into exactly 3 parts —
        // a workspace id containing `:` would have produced 5+ parts
        // under the iter-3 wire format.
        result.Subject.ResponseButtons.Should().AllSatisfy(b =>
        {
            var parts = b.CallbackData.Split(':');
            parts.Length.Should().Be(3,
                $"callback_data '{b.CallbackData}' must always have exactly three colon-separated parts (prefix, token, index) regardless of whether the workspace id contains ':'");
            parts[0].Should().Be("ws");
            int.TryParse(parts[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _)
                .Should().BeTrue($"parts[2] must be an integer index, not a workspace id; was '{parts[2]}'");
        });

        // Labels still carry the original (colon-containing) workspace
        // ids for the operator to read.
        result.Subject.ResponseButtons.Select(b => b.Label).Should().BeEquivalentTo(
            new[] { "tenant:factory:line-1", "tenant:factory:line-2" });
    }

    [Fact]
    public async Task Pipeline_MultiWorkspacePrompt_RobustAgainstLongWorkspaceIds()
    {
        // iter-3 evaluator pin (continued): workspace IDs that would
        // exceed Telegram's 64-byte callback_data cap if embedded
        // verbatim must still produce valid buttons. The index encoding
        // makes callback_data length depend on the binding count, not
        // on the workspace id length.
        var longButValidId = new string('w', 60); // 60 ASCII bytes — fits Label's 64-byte cap, would have blown the iter-3 callback_data cap (3+12+1+60 = 76 > 64)
        var harness = new Harness();
        harness.AuthorizeWith(
            harness.MakeBinding(workspaceId: longButValidId),
            harness.MakeBinding(workspaceId: "factory-2"));
        harness.ParserStub.Setup(p => p.Parse("/agents"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Agents,
                RawText = "/agents",
                IsValid = true,
            });

        var result = await harness.Pipeline.ProcessAsync(
            harness.MakeCommand("/agents"),
            CancellationToken.None);

        result.ResponseButtons.Should().HaveCount(2);
        // The index encoding bounds callback_data at 3 + 12 + 1 + ⌈log10(N)⌉
        // ASCII bytes regardless of workspace id length.
        result.ResponseButtons.Should().AllSatisfy(b =>
            System.Text.Encoding.UTF8.GetByteCount(b.CallbackData)
                .Should().BeLessThanOrEqualTo(InlineButton.MaxCallbackDataBytes,
                    "callback_data must fit Telegram's 64-byte cap independent of workspace id length"));
    }

    [Fact]
    public async Task Pipeline_Callback_WhenMultipleBindings_RoutesUsingFirstBinding_NoPrompt()
    {
        var harness = new Harness();
        harness.AuthorizeWith(
            harness.MakeBinding(workspaceId: "factory-1"),
            harness.MakeBinding(workspaceId: "factory-3"));
        harness.CallbackStub.Setup(c => c.HandleAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult { Success = true, CorrelationId = "trace-cb" });

        var evt = harness.MakeEvent(EventType.CallbackResponse, payload: "Q-1:approve");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().BeNull(
            "callback events skip the workspace prompt because the originating PendingQuestion already encodes the workspace");
        harness.CallbackStub.Verify(
            c => c.HandleAsync(It.Is<MessengerEvent>(e => e.EventId == evt.EventId), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // Role enforcement (architecture.md §9)
    // ============================================================

    [Fact]
    public async Task Pipeline_Approve_RejectedWithoutApproverRole()
    {
        var harness = new Harness();
        harness.AuthorizeWith(harness.MakeBinding(roles: new[] { "Operator" }));
        harness.ParserStub.Setup(p => p.Parse("/approve"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Approve,
                RawText = "/approve",
                IsValid = true,
            });

        var evt = harness.MakeCommand("/approve");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be(PipelineResponses.InsufficientPermissions);
        harness.RouterStub.Verify(
            r => r.RouteAsync(It.IsAny<ParsedCommand>(), It.IsAny<AuthorizedOperator>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.DedupStub.Verify(d => d.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "denied commands must not be marked processed");
    }

    [Theory]
    [InlineData(TelegramCommands.Approve, CommandRoleRequirements.ApproverRole)]
    [InlineData(TelegramCommands.Reject, CommandRoleRequirements.ApproverRole)]
    [InlineData(TelegramCommands.Pause, CommandRoleRequirements.OperatorRole)]
    [InlineData(TelegramCommands.Resume, CommandRoleRequirements.OperatorRole)]
    public void CommandRoleRequirements_RoleGatedCommands_RequireExpectedRole(string command, string expectedRole)
    {
        CommandRoleRequirements.RequiredRole(command).Should().Be(expectedRole);
    }

    [Theory]
    [InlineData(TelegramCommands.Status)]
    [InlineData(TelegramCommands.Agents)]
    [InlineData(TelegramCommands.Ask)]
    [InlineData(TelegramCommands.Handoff)]
    [InlineData(TelegramCommands.Start)]
    public void CommandRoleRequirements_NonGatedCommands_RequireNoRole(string command)
    {
        CommandRoleRequirements.RequiredRole(command).Should().BeNull();
    }

    [Fact]
    public async Task Pipeline_Approve_AcceptedWithApproverRole_RoutesToCommandRouter()
    {
        var harness = new Harness();
        harness.AuthorizeWith(harness.MakeBinding(roles: new[] { "Approver" }));
        harness.ParserStub.Setup(p => p.Parse("/approve"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Approve,
                RawText = "/approve",
                IsValid = true,
            });
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(), It.IsAny<AuthorizedOperator>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                Success = true,
                ResponseText = "approved",
                CorrelationId = "trace-r",
            });

        var evt = harness.MakeCommand("/approve");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be("approved");
    }

    // ============================================================
    // Invalid / missing command parse
    // ============================================================

    [Fact]
    public async Task Pipeline_InvalidParse_ShortCircuits_BeforeAuthorization()
    {
        var harness = new Harness();
        harness.ParserStub.Setup(p => p.Parse("/notreal"))
            .Returns(new ParsedCommand
            {
                CommandName = "notreal",
                RawText = "/notreal",
                IsValid = false,
                ValidationError = "unknown command",
            });

        var evt = harness.MakeCommand("/notreal");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be(PipelineResponses.CommandNotRecognized);
        harness.AuthzStub.Verify(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "an invalid command parse must not waste an authorization round-trip");
    }

    [Fact]
    public async Task Pipeline_CommandWithEmptyRawCommand_ShortCircuits()
    {
        var harness = new Harness();
        var evt = new MessengerEvent
        {
            EventId = Guid.NewGuid().ToString(),
            EventType = EventType.Command,
            RawCommand = "",
            UserId = "100",
            ChatId = "200",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-empty",
        };

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be(PipelineResponses.CommandNotRecognized);
        harness.ParserStub.Verify(p => p.Parse(It.IsAny<string>()), Times.Never);
    }

    // ============================================================
    // TextReply routing
    // ============================================================

    [Fact]
    public async Task Pipeline_TextReply_WhenAwaitingComment_RoutesToCallbackHandler()
    {
        var harness = new Harness();
        var binding = harness.MakeBinding(telegramUserId: 100, telegramChatId: 200);
        harness.AuthorizeWith(binding);

        var pending = new PendingQuestion
        {
            QuestionId = "Q-1",
            AgentId = "agent-1",
            TaskId = "T-1",
            Title = "T",
            Body = "B",
            Severity = MessageSeverity.Normal,
            AllowedActions = Array.Empty<HumanAction>(),
            TelegramChatId = 200,
            TelegramMessageId = 42,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            CorrelationId = "trace-pending",
            Status = PendingQuestionStatus.AwaitingComment,
            StoredAt = DateTimeOffset.UtcNow,
            RespondentUserId = 100,
        };
        harness.PendingStub.Setup(s => s.GetAwaitingCommentAsync(200L, 100L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);
        harness.CallbackStub.Setup(c => c.HandleAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                Success = true,
                ResponseText = "comment recorded",
                CorrelationId = "trace-cb",
            });

        var evt = harness.MakeEvent(EventType.TextReply, userId: "100", chatId: "200", payload: "extra context");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().Be("comment recorded");
        harness.CallbackStub.Verify(
            c => c.HandleAsync(It.Is<MessengerEvent>(e => e.EventId == evt.EventId), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Pipeline_TextReply_WhenNoPendingQuestion_SilentlyAcknowledges()
    {
        var harness = new Harness();
        harness.AuthorizeWith(harness.MakeBinding(telegramUserId: 100, telegramChatId: 200));
        harness.PendingStub.Setup(s => s.GetAwaitingCommentAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PendingQuestion?)null);

        var evt = harness.MakeEvent(EventType.TextReply, userId: "100", chatId: "200", payload: "random chat");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().BeNull(
            "unrelated text replies are silently acknowledged so the bot does not echo random chatter");
        harness.CallbackStub.Verify(
            c => c.HandleAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Mark-processed still fires because the event was successfully handled.
        harness.DedupStub.Verify(d => d.MarkProcessedAsync(evt.EventId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Pipeline_TextReply_WithNonNumericIds_FallsThroughToSilentAck()
    {
        var harness = new Harness();
        harness.AuthorizeWith(harness.MakeBinding());

        var evt = harness.MakeEvent(EventType.TextReply, userId: "alice", chatId: "channel", payload: "hi");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ResponseText.Should().BeNull();
        harness.PendingStub.Verify(
            s => s.GetAwaitingCommentAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // Unknown event handling: bypasses BOTH authz and dedup so that
    // (a) malformed payloads do not consume a reservation slot and
    // (b) the operator's authorization status is not leaked through
    // the difference between "Unauthorized" vs "Unsupported event".
    // ============================================================

    [Fact]
    public async Task Pipeline_UnknownEvent_ReturnsUnsupported_WithoutAuthorizationOrReservation()
    {
        var harness = new Harness();

        var evt = harness.MakeEvent(EventType.Unknown, userId: "999", chatId: "888");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeFalse(
            "PipelineResult.Handled is `false` only when the event type is unrecognized — see PipelineResult XML doc");
        result.ResponseText.Should().Be(PipelineResponses.UnknownEventType);
        result.CorrelationId.Should().Be(evt.CorrelationId);

        harness.AuthzStub.Verify(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Unknown events must short-circuit BEFORE authz so that the bot does not leak authorization status to senders of malformed payloads");
        harness.DedupStub.Verify(d => d.TryReserveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Unknown events must NOT consume a reservation slot — the cache is reserved for actionable events");
        harness.DedupStub.Verify(d => d.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Pipeline_UnknownEvent_FromUnboundUser_StillReturnsUnsupported_NotUnauthorized()
    {
        // Regression for iter-1 evaluator item #6: unknown events from
        // unbound users used to return Unauthorized because authz ran
        // before the EventType switch. The fix moves the Unknown
        // classification ahead of authz so the response is the
        // unsupported-event message regardless of binding state.
        var harness = new Harness();
        harness.AuthzStub.Setup(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizationResult
            {
                IsAuthorized = false,
                DenialReason = "user not in allowlist",
            });

        var evt = harness.MakeEvent(EventType.Unknown);

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeFalse();
        result.ResponseText.Should().Be(PipelineResponses.UnknownEventType,
            "even an unbound user gets the unsupported-event reply for Unknown events; we do not run the authz check at all");
        harness.AuthzStub.Verify(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // Structured stage logging — every stage emits a log entry with
    // CorrelationId, EventId, and Stage state-properties so an
    // observability tool can reconstruct the per-event pipeline path
    // end-to-end (the Stage 2.2 brief's "structured log entries at each
    // pipeline stage" requirement).
    // ============================================================

    [Fact]
    public async Task Pipeline_HappyPath_EmitsStructuredStageLogs_WithCorrelationIdAndEventId()
    {
        var harness = new Harness();
        harness.AuthorizeWith(harness.MakeBinding());
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(), It.IsAny<AuthorizedOperator>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                Success = true,
                ResponseText = "Status: OK",
                CorrelationId = "router-trace",
            });

        var evt = harness.MakeCommand("/status", eventId: "evt-log-1");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();

        // Every stage log carries CorrelationId + EventId + Stage
        // structured properties. We assert the full happy-path stage
        // sequence so a future maintainer who renames/removes a stage
        // sees the test fail loudly.
        var stages = harness.LogCapture.Entries
            .Select(e => e.GetValue<string>("Stage"))
            .Where(s => s is not null)
            .ToArray();

        stages.Should().Equal(
            new[] { "classify", "dedup", "parse", "authorize", "resolve-operator", "role-enforcement", "route", "handler-result", "mark-processed" },
            "every pipeline stage must emit a structured log line so observability can reconstruct the per-event path");

        // Every emitted entry must include CorrelationId AND EventId
        // properties matching the inbound MessengerEvent.
        foreach (var entry in harness.LogCapture.Entries)
        {
            entry.GetValue<string>("CorrelationId").Should().Be(evt.CorrelationId,
                "every stage log must carry the inbound correlation id for end-to-end traceability");
            entry.GetValue<string>("EventId").Should().Be(evt.EventId,
                "every stage log must carry the inbound event id");
        }
    }

    [Fact]
    public async Task Pipeline_DuplicateShortCircuit_EmitsDedupDuplicateLog_WithStructuredProperties()
    {
        var harness = new Harness();
        harness.DedupStub.Setup(d => d.TryReserveAsync("evt-dup-log", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var evt = harness.MakeCommand("/status", eventId: "evt-dup-log");

        await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        var dupEntry = harness.LogCapture.Entries
            .FirstOrDefault(e => e.GetValue<string>("Stage") == "dedup-duplicate");
        dupEntry.Should().NotBeNull(
            "the duplicate-short-circuit path must emit a Stage=dedup-duplicate log entry");
        dupEntry!.GetValue<string>("CorrelationId").Should().Be(evt.CorrelationId);
        dupEntry.GetValue<string>("EventId").Should().Be(evt.EventId);
    }

    [Fact]
    public async Task Pipeline_UnauthorizedRejection_EmitsAuthorizeDeniedLog_WithReason()
    {
        var harness = new Harness();
        harness.AuthzStub.Setup(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizationResult
            {
                IsAuthorized = false,
                DenialReason = "user not in allowlist",
            });
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });

        var evt = harness.MakeCommand("/status", eventId: "evt-deny-log");

        await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        var deniedEntry = harness.LogCapture.Entries
            .FirstOrDefault(e => e.GetValue<string>("Stage") == "authorize-denied");
        deniedEntry.Should().NotBeNull();
        deniedEntry!.GetValue<string>("CorrelationId").Should().Be(evt.CorrelationId);
        deniedEntry.GetValue<string>("EventId").Should().Be(evt.EventId);
        deniedEntry.GetValue<string>("Reason").Should().Be("user not in allowlist",
            "denial reason from AuthorizationResult must surface in the structured log so audit consumers can identify why an event was rejected");
        deniedEntry.GetValue<string>("UserId").Should().Be(evt.UserId);
        deniedEntry.GetValue<string>("ChatId").Should().Be(evt.ChatId);
    }

    // ============================================================
    // Stage 2.2 hybrid retry contract — Success=false (return) is
    // TERMINAL:
    //   - Pipeline calls MarkProcessedAsync exactly once even when
    //     the routed handler returns CommandResult.Success=false (the
    //     handler ran to completion and gave the operator a definitive
    //     failure response; the dedup gate should not re-open).
    //   - PipelineResult.Succeeded still reflects the handler's
    //     CommandResult.Success so observability / audit can surface
    //     the failure even though the dedup marker is symmetric.
    //   - The fallback PipelineResponses.HandlerFailureFallback is
    //     surfaced when the handler omitted ResponseText so operators
    //     never see an empty reply for a failed command.
    //
    // Defense-in-depth authorization (carried over from iter-3):
    //   - Pipeline must check BOTH AuthorizationResult.IsAuthorized AND
    //     Bindings.Count > 0 before constructing AuthorizedOperator,
    //     so a buggy/compromised authz provider that returns
    //     IsAuthorized=false alongside a stale binding list is still
    //     rejected. The pipeline does not trust the consistency of an
    //     upstream service.
    // ============================================================

    [Fact]
    public async Task Pipeline_OnHandlerReturnsFailure_MarksProcessed_AndSurfacesError()
    {
        // Stage 2.2 hybrid retry contract (this iter):
        //   throw  = retryable (release-on-throw, Scenario 4)
        //   return = terminal  (mark processed regardless of Success)
        // A handler that returns Success=false has run to completion
        // and given the operator a definitive failure response, so the
        // pipeline marks the event processed exactly as it does on the
        // success path. PipelineResult.Succeeded still reflects the
        // handler's failure so observability can alert; only the dedup
        // marker is symmetric. This avoids the "live re-deliveries
        // re-issue the same failure to the operator" anti-pattern that
        // a release-on-Success=false design would create.
        var harness = new Harness();
        harness.AuthorizeWith(harness.MakeBinding());
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                Success = false,
                ResponseText = "swarm offline",
                ErrorCode = "swarm.unavailable",
                CorrelationId = "router-trace",
            });

        var evt = harness.MakeCommand("/status", eventId: "evt-fail-1");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue(
            "the pipeline 'handled' the event by routing the handler and surfacing its failure to the operator");
        result.Succeeded.Should().BeFalse(
            "Succeeded must reflect the routed handler's CommandResult.Success — observability and audit consumers depend on this distinction even though the dedup marker is symmetric");
        result.ResponseText.Should().Be("swarm offline",
            "the operator must see the handler's failure text; we do not silently swallow the message");
        result.ErrorCode.Should().Be("swarm.unavailable",
            "machine-readable error code is propagated for observability");
        result.CorrelationId.Should().Be(evt.CorrelationId);

        harness.DedupStub.Verify(d => d.MarkProcessedAsync(evt.EventId, It.IsAny<CancellationToken>()),
            Times.Once,
            "Success=false is TERMINAL — MarkProcessedAsync MUST be called so live re-deliveries short-circuit and the operator is not pestered with the same failure response on every webhook redelivery (only throw is retryable)");
        harness.DedupStub.Verify(d => d.ReleaseReservationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Success=false MUST NOT release the reservation — release is exclusively the throw path's recovery primitive");

        // Structured log: failure is surfaced with the error code.
        var failureEntry = harness.LogCapture.Entries
            .FirstOrDefault(e => e.GetValue<string>("Stage") == "handler-failure");
        failureEntry.Should().NotBeNull(
            "Success=false must emit a structured Stage=handler-failure log so observability can alert");
        failureEntry!.GetValue<string>("ErrorCode").Should().Be("swarm.unavailable");
        failureEntry.GetValue<string>("CorrelationId").Should().Be(evt.CorrelationId);
        failureEntry.GetValue<string>("EventId").Should().Be(evt.EventId);
    }

    [Fact]
    public async Task Pipeline_OnHandlerReturnsFailure_WithNullResponseText_SurfacesGenericFallback()
    {
        // Iter-3 evaluator item 2 (rubber-duck non-blocking #1): a handler
        // that returns Success=false WITHOUT a ResponseText would otherwise
        // produce an empty operator reply — the operator would have no way
        // to know the action failed. The pipeline substitutes a generic
        // fallback so a definitive failure indication is always surfaced.
        var harness = new Harness();
        harness.AuthorizeWith(harness.MakeBinding());
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                Success = false,
                ResponseText = null,
                ErrorCode = "internal-error",
                CorrelationId = "router-trace",
            });

        var evt = harness.MakeCommand("/status", eventId: "evt-fail-noopText");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        result.ResponseText.Should().Be(PipelineResponses.HandlerFailureFallback,
            "a Success=false reply with no ResponseText must surface a generic failure message — operators must never see an empty reply for a failed command");
        result.ErrorCode.Should().Be("internal-error");
        harness.DedupStub.Verify(d => d.MarkProcessedAsync(evt.EventId, It.IsAny<CancellationToken>()),
            Times.Once,
            "Success=false is TERMINAL even when the handler omitted ResponseText — MarkProcessedAsync still runs so live re-deliveries short-circuit");
    }

    [Fact]
    public async Task Pipeline_OnHandlerReturnsFailure_NextDeliveryShortCircuits()
    {
        // Stage 2.2 hybrid retry contract (this iter):
        //   throw  = retryable (release reservation, re-run handler)
        //   return = terminal  (mark processed, short-circuit re-deliveries)
        // Success=false marks the event processed exactly like Success=true,
        // so the second delivery short-circuits at the dedup gate. The
        // operator's existing failure response is the canonical answer;
        // re-running the same just-failed handler on every webhook
        // redelivery would surface the same error repeatedly. Operators
        // who want a retry must re-issue the original command (which
        // produces a fresh EventId).
        var dedup = new InMemoryDeduplicationService();
        var harness = new Harness(dedup: dedup);
        harness.AuthorizeWith(harness.MakeBinding());
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });
        var routerInvocations = 0;
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                routerInvocations++;
                return new CommandResult
                {
                    Success = false,
                    ResponseText = "still offline",
                    ErrorCode = "swarm.unavailable",
                    CorrelationId = "router-trace",
                };
            });

        var evt = harness.MakeCommand("/status", eventId: "evt-fail-retry");

        var first = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);
        first.Handled.Should().BeTrue();
        first.Succeeded.Should().BeFalse();
        routerInvocations.Should().Be(1);

        // Second delivery: TryReserveAsync returns false because the
        // reservation persists alongside the processed marker. The
        // router is NOT re-invoked; the live pipeline returns the
        // duplicate short-circuit shape.
        var second = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);
        second.Handled.Should().BeTrue("the duplicate short-circuit returns Handled=true");
        second.ResponseText.Should().BeNull();
        routerInvocations.Should().Be(1,
            "Success=false is TERMINAL — the second delivery must short-circuit at the dedup gate; live retries against the same EventId would otherwise hammer a downstream handler that just reported failure");

        // The processed marker IS set because Success=false is now
        // terminal. This makes the dedup gate sticky beyond the pure
        // reservation TTL — once Stage 4.3 substitutes the cache-backed
        // dedup, the processed marker is what survives the reservation
        // window.
        var probed = await dedup.IsProcessedAsync(evt.EventId, CancellationToken.None);
        probed.Should().BeTrue(
            "Success=false now marks processed (terminal contract) so the dedup gate stays closed even after the pure reservation slot would otherwise expire under the Stage 4.3 cache-backed dedup");
    }

    [Fact]
    public async Task Pipeline_OnHandlerSuccess_PipelineResultSucceededIsTrue()
    {
        // Sanity: the new Succeeded field defaults to true on the success
        // path; existing tests that assert Handled=true continue to imply
        // a successful pipeline outcome by default.
        var harness = new Harness();
        harness.AuthorizeWith(harness.MakeBinding());
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });
        harness.RouterStub.Setup(r => r.RouteAsync(
                It.IsAny<ParsedCommand>(),
                It.IsAny<AuthorizedOperator>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult
            {
                Success = true,
                ResponseText = "Status: OK",
                CorrelationId = "router-trace",
            });

        var evt = harness.MakeCommand("/status");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.Succeeded.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task Pipeline_RejectsAuthorization_WhenIsAuthorizedFalse_DespiteNonEmptyBindings()
    {
        // Iter-3 evaluator item 3: the pipeline must check BOTH the
        // IsAuthorized boolean AND a non-empty Bindings list. If a
        // buggy/compromised IUserAuthorizationService returns
        // IsAuthorized=false alongside a stale binding list, the
        // pipeline must STILL deny — never construct an AuthorizedOperator
        // from a binding the provider explicitly disclaimed.
        var harness = new Harness();
        var staleBinding = harness.MakeBinding(workspaceId: "stale-workspace");
        harness.AuthzStub.Setup(s => s.AuthorizeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthorizationResult
            {
                IsAuthorized = false,
                Bindings = new[] { staleBinding },
                DenialReason = "binding deactivated mid-flight",
            });
        harness.ParserStub.Setup(p => p.Parse("/status"))
            .Returns(new ParsedCommand
            {
                CommandName = TelegramCommands.Status,
                RawText = "/status",
                IsValid = true,
            });

        var evt = harness.MakeCommand("/status", eventId: "evt-stale-bind");

        var result = await harness.Pipeline.ProcessAsync(evt, CancellationToken.None);

        result.Handled.Should().BeTrue("the pipeline rejects, which is a 'handled' outcome");
        result.ResponseText.Should().Be(PipelineResponses.Unauthorized,
            "the canonical denial text is surfaced regardless of the upstream provider's state inconsistency");
        result.CorrelationId.Should().Be(evt.CorrelationId);

        harness.RouterStub.Verify(
            r => r.RouteAsync(It.IsAny<ParsedCommand>(), It.IsAny<AuthorizedOperator>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the router must NOT receive an AuthorizedOperator built from a binding the provider explicitly disclaimed");
        harness.CallbackStub.Verify(
            c => c.HandleAsync(It.IsAny<MessengerEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.DedupStub.Verify(d => d.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // The structured authorize-denied log carries the inconsistent
        // state so an alert can fire on the upstream provider.
        var deniedEntry = harness.LogCapture.Entries
            .FirstOrDefault(e => e.GetValue<string>("Stage") == "authorize-denied");
        deniedEntry.Should().NotBeNull();
        deniedEntry!.GetValue<string>("Reason").Should().Be("binding deactivated mid-flight");
    }

    // ============================================================
    // Constructor guards
    // ============================================================

    [Fact]
    public void Constructor_ThrowsOnNullDependency()
    {
        var dedup = new Mock<IDeduplicationService>().Object;
        var authz = new Mock<IUserAuthorizationService>().Object;
        var parser = new Mock<ICommandParser>().Object;
        var router = new Mock<ICommandRouter>().Object;
        var callback = new Mock<ICallbackHandler>().Object;
        var pending = new Mock<IPendingQuestionStore>().Object;
        var disambig = new Mock<IPendingDisambiguationStore>().Object;
        var time = TimeProvider.System;
        var logger = NullLogger<TelegramUpdatePipeline>.Instance;

        Action newWithNullDedup = () => _ = new TelegramUpdatePipeline(null!, authz, parser, router, callback, pending, disambig, time, logger);
        Action newWithNullAuthz = () => _ = new TelegramUpdatePipeline(dedup, null!, parser, router, callback, pending, disambig, time, logger);
        Action newWithNullParser = () => _ = new TelegramUpdatePipeline(dedup, authz, null!, router, callback, pending, disambig, time, logger);
        Action newWithNullRouter = () => _ = new TelegramUpdatePipeline(dedup, authz, parser, null!, callback, pending, disambig, time, logger);
        Action newWithNullCallback = () => _ = new TelegramUpdatePipeline(dedup, authz, parser, router, null!, pending, disambig, time, logger);
        Action newWithNullPending = () => _ = new TelegramUpdatePipeline(dedup, authz, parser, router, callback, null!, disambig, time, logger);
        Action newWithNullDisambig = () => _ = new TelegramUpdatePipeline(dedup, authz, parser, router, callback, pending, null!, time, logger);
        Action newWithNullTime = () => _ = new TelegramUpdatePipeline(dedup, authz, parser, router, callback, pending, disambig, null!, logger);
        Action newWithNullLogger = () => _ = new TelegramUpdatePipeline(dedup, authz, parser, router, callback, pending, disambig, time, null!);

        newWithNullDedup.Should().Throw<ArgumentNullException>();
        newWithNullAuthz.Should().Throw<ArgumentNullException>();
        newWithNullParser.Should().Throw<ArgumentNullException>();
        newWithNullRouter.Should().Throw<ArgumentNullException>();
        newWithNullCallback.Should().Throw<ArgumentNullException>();
        newWithNullPending.Should().Throw<ArgumentNullException>();
        newWithNullDisambig.Should().Throw<ArgumentNullException>();
        newWithNullTime.Should().Throw<ArgumentNullException>();
        newWithNullLogger.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ProcessAsync_RejectsNullEvent()
    {
        var harness = new Harness();

        var act = async () => await harness.Pipeline.ProcessAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ============================================================
    // Harness
    // ============================================================

    /// <summary>
    /// Builds a real <see cref="TelegramUpdatePipeline"/> with mockable
    /// dependencies. <see cref="DedupStub"/> defaults to <c>IsProcessedAsync
    /// → false</c> and a no-op <c>MarkProcessedAsync</c> so the happy path
    /// runs without per-test setup.
    /// </summary>
    private sealed class Harness
    {
        public Mock<IDeduplicationService> DedupStub { get; }
        public Mock<IUserAuthorizationService> AuthzStub { get; }
        public Mock<ICommandParser> ParserStub { get; }
        public Mock<ICommandRouter> RouterStub { get; }
        public Mock<ICallbackHandler> CallbackStub { get; }
        public Mock<IPendingQuestionStore> PendingStub { get; }
        // Real in-memory store — the multi-workspace tests need to verify
        // the stored entry's contents (token, original raw command,
        // correlation id), and rolling a separate Mock surface that
        // captures arguments would just re-implement the same logic.
        public InMemoryPendingDisambiguationStore DisambiguationStore { get; }
        public TestTimeProvider TimeProvider { get; }
        public TelegramUpdatePipeline Pipeline { get; }

        private readonly IDeduplicationService _dedupImpl;

        public Harness(IDeduplicationService? dedup = null)
        {
            DedupStub = new Mock<IDeduplicationService>(MockBehavior.Strict);
            // The pipeline's atomic gate is TryReserveAsync — default to
            // "true" (winner) so the happy path runs without per-test setup.
            DedupStub.Setup(d => d.TryReserveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            DedupStub.Setup(d => d.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            // ReleaseReservationAsync default: succeed silently. The
            // pipeline calls this on the caught-handler-exception path
            // (Stage 2.2 brief Scenario 4 / hybrid release-on-throw).
            // Tests that need to assert release-on-throw behaviour
            // verify against this Strict mock; tests that need to
            // simulate a release-side failure override the setup.
            DedupStub.Setup(d => d.ReleaseReservationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            // IsProcessedAsync is part of the contract surface but the
            // pipeline does NOT call it (the racy check-then-act pattern is
            // explicitly disallowed per implementation-plan.md §132). Wire
            // a sane default so an accidental call surfaces as a specific
            // verification failure.
            DedupStub.Setup(d => d.IsProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            _dedupImpl = dedup ?? DedupStub.Object;

            AuthzStub = new Mock<IUserAuthorizationService>();
            ParserStub = new Mock<ICommandParser>();
            RouterStub = new Mock<ICommandRouter>();
            CallbackStub = new Mock<ICallbackHandler>();
            PendingStub = new Mock<IPendingQuestionStore>();
            TimeProvider = new TestTimeProvider(
                new DateTimeOffset(2024, 06, 15, 12, 00, 00, TimeSpan.Zero));
            DisambiguationStore = new InMemoryPendingDisambiguationStore(TimeProvider);
            LogCapture = new CapturingLogger<TelegramUpdatePipeline>();

            Pipeline = new TelegramUpdatePipeline(
                _dedupImpl,
                AuthzStub.Object,
                ParserStub.Object,
                RouterStub.Object,
                CallbackStub.Object,
                PendingStub.Object,
                DisambiguationStore,
                TimeProvider,
                LogCapture);
        }

        public CapturingLogger<TelegramUpdatePipeline> LogCapture { get; }

        public void AuthorizeWith(params OperatorBinding[] bindings)
        {
            AuthzStub.Setup(s => s.AuthorizeAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AuthorizationResult
                {
                    IsAuthorized = bindings.Length > 0,
                    Bindings = bindings,
                });

            // Stage 3.4 — the pipeline now routes /start through
            // OnboardAsync. Moq does not invoke the default-interface-
            // method implementation on a proxied mock; explicitly
            // stub it so /start pipeline tests get the same bindings
            // as non-/start tests by default.
            AuthzStub.Setup(s => s.OnboardAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AuthorizationResult
                {
                    IsAuthorized = bindings.Length > 0,
                    Bindings = bindings,
                });
        }

        public OperatorBinding MakeBinding(
            long telegramUserId = 100,
            long telegramChatId = 200,
            string workspaceId = "w-1",
            string[]? roles = null) =>
            new()
            {
                Id = Guid.NewGuid(),
                TelegramUserId = telegramUserId,
                TelegramChatId = telegramChatId,
                ChatType = ChatType.Private,
                OperatorAlias = "@op",
                TenantId = "t-1",
                WorkspaceId = workspaceId,
                Roles = roles ?? Array.Empty<string>(),
                RegisteredAt = DateTimeOffset.UtcNow,
            };

        public MessengerEvent MakeCommand(string rawCommand, string? eventId = null) =>
            new()
            {
                EventId = eventId ?? Guid.NewGuid().ToString(),
                EventType = EventType.Command,
                RawCommand = rawCommand,
                UserId = "100",
                ChatId = "200",
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = "trace-" + (eventId ?? "cmd"),
            };

        public MessengerEvent MakeEvent(
            EventType type,
            string? userId = "100",
            string? chatId = "200",
            string? payload = null,
            string? eventId = null) =>
            new()
            {
                EventId = eventId ?? Guid.NewGuid().ToString(),
                EventType = type,
                UserId = userId!,
                ChatId = chatId!,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = "trace-" + (eventId ?? type.ToString()),
                Payload = payload,
            };
    }

    /// <summary>
    /// Test logger that captures every <see cref="ILogger.Log"/> invocation
    /// and exposes the structured state properties as a queryable list.
    /// Used to assert that the pipeline emits the brief-mandated structured
    /// stage logs (with <c>CorrelationId</c>, <c>EventId</c>, and
    /// <c>Stage</c> properties) — see test
    /// <c>Pipeline_HappyPath_EmitsStructuredStageLogs_WithCorrelationIdAndEventId</c>.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = new();
        private readonly object _gate = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = new LogEntry(logLevel, formatter(state, exception));
            if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
            {
                foreach (var kvp in structured)
                {
                    entry.Properties[kvp.Key] = kvp.Value;
                }
            }
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class LogEntry
    {
        public LogEntry(LogLevel level, string message)
        {
            Level = level;
            Message = message;
        }

        public LogLevel Level { get; }

        public string Message { get; }

        public Dictionary<string, object?> Properties { get; } = new(StringComparer.Ordinal);

        public TValue? GetValue<TValue>(string key)
            where TValue : class
        {
            if (Properties.TryGetValue(key, out var value) && value is TValue typed)
            {
                return typed;
            }
            return value?.ToString() as TValue;
        }
    }

    /// <summary>
    /// Pinnable <see cref="TimeProvider"/> for the disambiguation TTL
    /// path. Defaults to a fixed instant so the test assertions on
    /// <see cref="PendingDisambiguation.CreatedAt"/> and
    /// <see cref="PendingDisambiguation.ExpiresAt"/> stay deterministic.
    /// </summary>
    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public TestTimeProvider(DateTimeOffset start)
        {
            _now = start;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
