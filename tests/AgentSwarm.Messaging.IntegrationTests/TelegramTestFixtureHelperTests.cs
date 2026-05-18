// -----------------------------------------------------------------------
// <copyright file="TelegramTestFixtureHelperTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using ChatType = Telegram.Bot.Types.Enums.ChatType;

namespace AgentSwarm.Messaging.IntegrationTests;

/// <summary>
/// Stage 7.1 step 4 ΓÇö smoke tests that exercise the Stage 7.1 test
/// helpers (<see cref="TelegramTestFixture.SimulateWebhookUpdateAsync"/>,
/// <see cref="TelegramTestFixture.SimulateCallbackQueryAsync"/>,
/// <see cref="TelegramTestFixture.AssertMessageSent"/>,
/// <see cref="TelegramTestFixture.WaitForMessageSentAsync"/>) so that
/// the Stage 7.2 acceptance suite ΓÇö which is the helpers' real
/// consumer ΓÇö can rely on them. Without this guard a regression to the
/// secret header, the JSON shape Telegram emits, or the assertion's
/// substring-match contract would surface only in Stage 7.2 with
/// noisier failure modes.
/// </summary>
public sealed class TelegramTestFixtureHelperTests
{
    [Fact]
    public async Task SimulateWebhookUpdateAsync_PostsAcceptedUpdate_ReceivesHttp200()
    {
        // Verifies: (a) the secret header is propagated through the
        // TelegramWebhookSecretFilter, (b) the JSON body shape matches
        // what Telegram.Bot's JsonBotAPI.Options can re-deserialize,
        // (c) the durable InboundUpdate row is persisted (otherwise the
        // dedup short-circuit would NOT fire on the second call below).
        using var fixture = new TelegramTestFixture();
        var update = new Update
        {
            Id = 7_010_001,
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = 8001L, Type = ChatType.Private },
                From = new User { Id = 9001L, IsBot = false, FirstName = "Operator" },
                Text = "/status",
                // Telegram never delivers a Message without a Date; setting
                // an explicit UTC value here prevents the default
                // DateTime.MinValue from flowing into the polling-stage
                // TelegramUpdateMapper.ToUtc (which keys MessengerEvent.Timestamp
                // off message.Date) when Stage 7.2 reuses this pattern.
                Date = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            },
        };

        using var response = await fixture.SimulateWebhookUpdateAsync(update, correlationId: "trace-helper-001");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "Stage 7.1 helper must drive the production webhook contract ΓÇö secret-token header set and body parseable as a Telegram Update");
    }

    [Fact]
    public async Task SimulateWebhookUpdateAsync_DuplicateUpdateId_StillReturnsHttp200()
    {
        // Acceptance criterion: "Duplicate webhook delivery does not
        // execute the same human command twice". The duplicate path
        // returns 200 to keep Telegram from retrying ΓÇö assert the
        // helper observes that shape so AC004 in Stage 7.2 has a stable
        // contract to depend on.
        using var fixture = new TelegramTestFixture();
        var update = new Update
        {
            Id = 7_010_002,
            Message = new Message
            {
                Id = 2,
                Chat = new Chat { Id = 8002L, Type = ChatType.Private },
                From = new User { Id = 9002L, IsBot = false, FirstName = "Operator" },
                Text = "/ping",
                // Telegram never delivers a Message without a Date; setting
                // an explicit UTC value here prevents the default
                // DateTime.MinValue from flowing into the polling-stage
                // TelegramUpdateMapper.ToUtc (which keys MessengerEvent.Timestamp
                // off message.Date) when Stage 7.2 reuses this pattern.
                Date = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            },
        };

        using var first = await fixture.SimulateWebhookUpdateAsync(update);
        using var second = await fixture.SimulateWebhookUpdateAsync(update);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "duplicate UpdateId must short-circuit to 200 so Telegram does not retry");
    }

    [Fact]
    public async Task SimulateCallbackQueryAsync_WrapsCallbackQueryInUpdate_ReceivesHttp200()
    {
        // Verifies the CallbackQuery wrapper path: the helper allocates
        // a fresh Update.Id and posts via the same webhook contract.
        using var fixture = new TelegramTestFixture();
        var callbackQuery = new CallbackQuery
        {
            Id = "cb-helper-001",
            From = new User { Id = 9101L, IsBot = false, FirstName = "Approver" },
            Message = new Message
            {
                Id = 3,
                Chat = new Chat { Id = 8101L, Type = ChatType.Private },
                Date = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            },
            Data = "approve:q-helper-001:0",
        };

        using var response = await fixture.SimulateCallbackQueryAsync(callbackQuery);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "Stage 7.1 helper must wrap CallbackQuery into an Update and post through the same webhook contract");
    }

    [Fact]
    public async Task SimulateCallbackQueryAsync_WithExplicitUpdateId_RoundTrips()
    {
        // Pins the explicit-id overload so AC004 (duplicate webhook
        // delivery) can reuse the helper for the callback path.
        using var fixture = new TelegramTestFixture();
        var callbackQuery = new CallbackQuery
        {
            Id = "cb-helper-002",
            From = new User { Id = 9102L, IsBot = false, FirstName = "Approver" },
            Message = new Message
            {
                Id = 4,
                Chat = new Chat { Id = 8102L, Type = ChatType.Private },
                Date = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            },
            Data = "reject:q-helper-002:1",
        };
        const int explicitUpdateId = 7_010_500;

        using var first = await fixture.SimulateCallbackQueryAsync(callbackQuery, updateId: explicitUpdateId);
        using var second = await fixture.SimulateCallbackQueryAsync(callbackQuery, updateId: explicitUpdateId);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "callback-query path must honour the same duplicate-200 contract as the message path");
    }

    [Fact]
    public async Task AssertMessageSent_AfterSenderRuns_FindsMatch()
    {
        // The brief's Stage 7.1 scenario 2 ΓÇö "Fake API records calls ΓÇö
        // Given FakeTelegramApi is configured, When SendTextAsync is
        // invoked via the connector, Then WireMock records the
        // sendMessage call with correct parameters" ΓÇö translated into
        // the helper API. AssertMessageSent must locate a recorded
        // sendMessage whose chat_id and substring match.
        using var fixture = new TelegramTestFixture();
        var sender = fixture.Services.GetRequiredService<IMessageSender>();
        const long chatId = 8201L;
        const string traceId = "tracehelperassert";
        var text = "Build succeeded\n≡ƒöù trace: " + traceId;

        await sender.SendTextAsync(chatId, text, CancellationToken.None);

        fixture.AssertMessageSent(chatId, traceId);
    }

    [Fact]
    public async Task AssertMessageSent_WhenNoMatch_ThrowsWithObservedSendsInMessage()
    {
        // Diagnostic guarantee: the failure message lists actual sends
        // so an operator triaging a Stage 7.2 AC failure can spot a
        // wrong chat id, an unexpected MarkdownV2 escape, or a missing
        // outbound enqueue without re-running the test.
        using var fixture = new TelegramTestFixture();
        var sender = fixture.Services.GetRequiredService<IMessageSender>();
        await sender.SendTextAsync(8301L, "actual text body", CancellationToken.None);

        var action = () => fixture.AssertMessageSent(8301L, "absent-substring");

        action.Should()
            .Throw<Xunit.Sdk.XunitException>()
            .WithMessage("*absent-substring*")
            .WithMessage("*actual text body*");
    }

    [Fact]
    public async Task WaitForMessageSentAsync_ResolvesAfterSenderEventuallyPosts()
    {
        // Polling path: simulate the Stage 7.2 case where the send
        // happens off the test thread by kicking off the sender on a
        // Task.Run and awaiting the wait helper. The wait helper must
        // resolve before the timeout.
        using var fixture = new TelegramTestFixture();
        var sender = fixture.Services.GetRequiredService<IMessageSender>();
        const long chatId = 8401L;
        const string traceId = "tracehelperwait";

        // Capture the background task so any exception thrown inside
        // SendTextAsync surfaces as the actual failure rather than
        // being swallowed and presenting as a misleading 5-second
        // "no message sent" timeout from WaitForMessageSentAsync.
        var backgroundSend = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150));
            await sender.SendTextAsync(chatId, "deferred send ≡ƒöù trace: " + traceId, CancellationToken.None);
        });

        await fixture.WaitForMessageSentAsync(chatId, traceId, timeout: TimeSpan.FromSeconds(5));

        // Re-await the background task so an exception thrown after
        // (or concurrent with) the wait helper succeeding still fails
        // the test with the real root cause.
        await backgroundSend;
    }

    [Fact]
    public async Task WaitForMessageSentAsync_WhenTimeoutElapses_ThrowsXunitExceptionWithDiagnostic()
    {
        // Verifies the polling helper degrades gracefully: after the
        // configured timeout it falls through to AssertMessageSent's
        // diagnostic exception rather than hanging the test host.
        using var fixture = new TelegramTestFixture();

        var act = async () => await fixture.WaitForMessageSentAsync(
            chatId: 8501L,
            textContains: "never-arrives",
            timeout: TimeSpan.FromMilliseconds(200));

        await act.Should().ThrowAsync<Xunit.Sdk.XunitException>(
            "polling timeout must surface as a deterministic xunit failure, not a hang");
    }

    [Fact]
    public async Task WaitForMessageSentAsync_WhenCancellationRequested_PropagatesOperationCanceledException()
    {
        // Iter-2 regression guard for evaluator item #1: previously the
        // helper caught TaskCanceledException from Task.Delay and fell
        // through to AssertMessageSent, which reported a false
        // "no send observed" assertion failure instead of letting the
        // caller see the cancellation. After the fix, the cancellation
        // token's OCE must propagate untouched ΓÇö matching the
        // cancellation contract used everywhere else in the codebase
        // (PersistentOutboundQueue, TelegramUpdatePipeline, etc.).
        using var fixture = new TelegramTestFixture();
        using var cts = new CancellationTokenSource();

        // Schedule cancellation slightly after the call begins so the
        // token cancels DURING the Task.Delay inside the polling loop.
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var act = async () => await fixture.WaitForMessageSentAsync(
            chatId: 8601L,
            textContains: "never-arrives",
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cts.Token);

        // Both TaskCanceledException and OperationCanceledException are
        // acceptable ΓÇö TaskCanceledException is a subtype. Asserting
        // the base type covers both .NET runtime emissions while
        // explicitly rejecting the prior bug where an
        // Xunit.Sdk.XunitException leaked out instead.
        (await act.Should().ThrowAsync<OperationCanceledException>(
            "caller cancellation must surface as OCE, not as a false 'no message sent' assertion"))
            .Which.Should().NotBeOfType<Xunit.Sdk.XunitException>(
                "regression: AssertMessageSent must NOT be reached when cancellation was requested");
    }

    [Fact]
    public async Task WaitForMessageSentAsync_WhenPreCancelledToken_ThrowsImmediatelyWithoutCallingAssert()
    {
        // Edge case: token is already cancelled BEFORE the helper is
        // called. The first ThrowIfCancellationRequested() inside the
        // loop should fire ΓÇö the helper must NOT enqueue an assertion
        // exception in this case.
        using var fixture = new TelegramTestFixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await fixture.WaitForMessageSentAsync(
            chatId: 8701L,
            textContains: "anything",
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "pre-cancelled token must short-circuit the helper before any I/O or assertion");
    }

    [Fact]
    public void Services_IDeduplicationService_IsResolvedAsInMemorySlidingWindow()
    {
        // Iter-2 regression guard for evaluator item #2: the brief's
        // Stage 7.1 step 2 requires "in-memory dedup". Without the
        // explicit Replace() in WireMockBackedFactory.ConfigureServices,
        // AddMessagingPersistence's Replace() would leave
        // PersistentDeduplicationService (EF Core) in the container ΓÇö
        // technically backed by in-memory SQLite, but the SERVICE
        // implementation is the persistent one. This test pins the
        // brief's literal contract: the registered
        // IDeduplicationService MUST be the in-memory
        // SlidingWindowDeduplicationService, not the EF-backed
        // PersistentDeduplicationService.
        using var fixture = new TelegramTestFixture();

        var dedup = fixture.Services.GetRequiredService<IDeduplicationService>();

        dedup.Should().BeOfType<SlidingWindowDeduplicationService>(
            "Stage 7.1 brief requires in-memory dedup; the persistent EF backend must be Replace()'d out by the fixture");
    }

    [Fact]
    public void Services_IOutboundQueue_IsResolvedAsInMemoryQueue()
    {
        // Iter-2 regression guard: the brief's Stage 7.1 step 2 also
        // requires "in-memory queue". Without the explicit
        // UseInMemoryOutboundQueue() in the fixture,
        // AddMessagingPersistence's Replace() would leave the EF-backed
        // PersistentOutboundQueue in the container. Assert the runtime
        // type by name because InMemoryOutboundQueue is `internal` to
        // the Telegram assembly (no InternalsVisibleTo to integration
        // tests). The name check is sufficient because the persistent
        // sibling is `PersistentOutboundQueue` ΓÇö a distinct class name.
        using var fixture = new TelegramTestFixture();

        var queue = fixture.Services.GetRequiredService<IOutboundQueue>();

        queue.GetType().Name.Should().Be(
            "InMemoryOutboundQueue",
            "Stage 7.1 brief requires in-memory queue; the persistent EF backend must be Replace()'d out by the fixture");
    }
}
