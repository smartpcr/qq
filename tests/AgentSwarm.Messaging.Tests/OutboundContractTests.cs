using System.Reflection;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using FluentAssertions;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 1.4 — Outbound Sender and Alert Contracts.
///
/// Locks the interface surface so Stage 2.3 (<c>TelegramMessageSender</c>),
/// Stage 2.6 (<c>TelegramMessengerConnector</c>), and Stage 4.1
/// (<c>OutboundQueueProcessor</c>) can build and mock against a stable
/// API. Tests cover BOTH mockability (call recording, return-value
/// round-trip) AND structural guarantees that the architecture pins:
///   - <see cref="IMessageSender"/> methods return
///     <see cref="Task{TResult}"/> of <see cref="SendResult"/>, not bare
///     <see cref="Task"/> (architecture.md §4.12 — the
///     <c>TelegramMessageId</c> must flow back so
///     <see cref="IOutboundQueue.MarkSentAsync"/> and
///     <c>IPendingQuestionStore.StoreAsync</c> can persist it);
///   - <see cref="SendResult"/> is a positional record carrying a
///     single <see cref="long"/> property named
///     <see cref="SendResult.TelegramMessageId"/> (canonical-type per
///     architecture.md §3.1: Telegram message ids are <c>int64</c>);
///   - <see cref="IOutboundQueue"/>'s mark/dead-letter methods take a
///     <see cref="Guid"/> <c>messageId</c> matching
///     <see cref="OutboundMessage.MessageId"/>, and
///     <see cref="IOutboundQueue.MarkSentAsync"/> takes a
///     <see cref="long"/> Telegram id (not <see cref="int"/>);
///   - <see cref="IOutboundQueue.DequeueAsync"/> returns a NULLABLE
///     <see cref="OutboundMessage"/> so the queue can be empty;
///   - <see cref="IAlertService.SendAlertAsync"/> takes only primitive
///     parameters so it stays Abstractions-safe (no Core type leakage).
/// </summary>
public class OutboundContractTests
{
    // ============================================================
    // IMessageSender — mockability
    // ============================================================

    [Fact]
    public async Task MessageSender_Mock_SendTextAsync_RecordsCallAndReturnsSendResult()
    {
        var mock = new Mock<IMessageSender>();
        mock.Setup(s => s.SendTextAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendResult(42L));

        var result = await mock.Object.SendTextAsync(123L, "hello", CancellationToken.None);

        result.Should().NotBeNull();
        result.TelegramMessageId.Should().Be(42L);
        mock.Verify(
            s => s.SendTextAsync(123L, "hello", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MessageSender_Mock_SendQuestionAsync_AcceptsEnvelopeAndReturnsSendResult()
    {
        var mock = new Mock<IMessageSender>();
        mock.Setup(s => s.SendQuestionAsync(
                It.IsAny<long>(),
                It.IsAny<AgentQuestionEnvelope>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendResult(99L));

        var envelope = CreateEnvelope(proposedDefault: "approve");

        var result = await mock.Object.SendQuestionAsync(555L, envelope, CancellationToken.None);

        result.TelegramMessageId.Should().Be(99L);
        mock.Verify(
            s => s.SendQuestionAsync(
                555L,
                It.Is<AgentQuestionEnvelope>(e =>
                    e.ProposedDefaultActionId == "approve"
                    && e.Question.QuestionId == "Q-1"
                    && e.Question.Severity == MessageSeverity.High),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MessageSender_Mock_SendQuestionAsync_EnvelopePropertiesAccessibleInsideHandler()
    {
        // Story-acceptance scenario: "Given a Moq mock of IMessageSender,
        // When SendQuestionAsync is invoked with an AgentQuestionEnvelope
        // containing ProposedDefaultActionId, Then the mock records the
        // call and envelope properties are accessible." Assert with a
        // callback that observes the envelope inside the mock setup.
        var mock = new Mock<IMessageSender>();
        AgentQuestionEnvelope? captured = null;
        mock.Setup(s => s.SendQuestionAsync(
                It.IsAny<long>(),
                It.IsAny<AgentQuestionEnvelope>(),
                It.IsAny<CancellationToken>()))
            .Callback<long, AgentQuestionEnvelope, CancellationToken>((_, e, _) => captured = e)
            .ReturnsAsync(new SendResult(7L));

        var envelope = CreateEnvelope(proposedDefault: "reject");
        await mock.Object.SendQuestionAsync(123L, envelope, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.ProposedDefaultActionId.Should().Be("reject");
        captured.Question.AllowedActions.Should().HaveCount(2);
        captured.Question.AllowedActions
            .Should().Contain(a => a.ActionId == "reject" && a.RequiresComment);
    }

    // ============================================================
    // IMessageSender — structural pins
    // ============================================================

    [Fact]
    public void MessageSender_BothMethods_ReturnTaskOfSendResult()
    {
        var sendText = typeof(IMessageSender).GetMethod(nameof(IMessageSender.SendTextAsync));
        var sendQuestion = typeof(IMessageSender).GetMethod(nameof(IMessageSender.SendQuestionAsync));

        sendText.Should().NotBeNull();
        sendQuestion.Should().NotBeNull();

        sendText!.ReturnType.Should().Be(typeof(Task<SendResult>),
            "architecture.md §4.12 requires SendTextAsync to return Task<SendResult> so "
            + "OutboundQueueProcessor can pass the Telegram message id to MarkSentAsync");
        sendQuestion!.ReturnType.Should().Be(typeof(Task<SendResult>),
            "architecture.md §4.12 requires SendQuestionAsync to return Task<SendResult> so "
            + "OutboundQueueProcessor can pass the Telegram message id to both "
            + "IOutboundQueue.MarkSentAsync and IPendingQuestionStore.StoreAsync");
    }

    [Fact]
    public void MessageSender_SendQuestionAsync_TakesAgentQuestionEnvelope_NotBareAgentQuestion()
    {
        var sendQuestion = typeof(IMessageSender).GetMethod(nameof(IMessageSender.SendQuestionAsync));
        var envelopeParam = sendQuestion!.GetParameters()[1];

        envelopeParam.ParameterType.Should().Be(typeof(AgentQuestionEnvelope),
            "the envelope carries ProposedDefaultActionId and RoutingMetadata that the "
            + "sender needs at render time; passing a bare AgentQuestion would force the "
            + "sender to re-query sidecar state per architecture.md §4.12");
    }

    [Fact]
    public void MessageSender_LivesInCoreAssembly_NotAbstractions()
    {
        // Pinned in test form so a future "tidy-up refactor" cannot quietly
        // move IMessageSender into Abstractions and break the layering rule
        // (Abstractions does not reference Core, and SendResult / the
        // implementation reference Core types).
        typeof(IMessageSender).Assembly.GetName().Name
            .Should().Be("AgentSwarm.Messaging.Core");
        typeof(SendResult).Assembly.GetName().Name
            .Should().Be("AgentSwarm.Messaging.Core");
    }

    // ============================================================
    // SendResult — value-type contract
    // ============================================================

    [Fact]
    public void SendResult_IsSealedRecordWithSinglePositionalLongProperty()
    {
        var t = typeof(SendResult);

        t.IsSealed.Should().BeTrue("architecture.md §4.12 declares the type sealed");

        var prop = t.GetProperty(nameof(SendResult.TelegramMessageId));
        prop.Should().NotBeNull();
        prop!.PropertyType.Should().Be(typeof(long),
            "Telegram message ids are int64 on the wire per architecture.md §3.1; "
            + "narrowing to int would silently truncate ids above 2^31");

        // Positional record => generated copy ctor + Deconstruct method.
        t.GetMethod("Deconstruct").Should().NotBeNull(
            "architecture.md §4.12 declares SendResult as a positional record "
            + "(public sealed record SendResult(long TelegramMessageId))");
    }

    [Fact]
    public void SendResult_RoundTripsTelegramMessageId()
    {
        var r = new SendResult(9_876_543_210L);

        r.TelegramMessageId.Should().Be(9_876_543_210L);
        r.Should().Be(new SendResult(9_876_543_210L));
        r.Should().NotBe(new SendResult(0L));
    }

    // ============================================================
    // IAlertService — mockability + contract
    // ============================================================

    [Fact]
    public async Task AlertService_Mock_SendAlertAsync_RecordsCall()
    {
        var mock = new Mock<IAlertService>();
        mock.Setup(s => s.SendAlertAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await mock.Object.SendAlertAsync(
            "Outbound message dead-lettered",
            "MessageId=abcd-... CorrelationId=trace-1 Reason=Telegram 5xx after 5 attempts",
            CancellationToken.None);

        mock.Verify(
            s => s.SendAlertAsync(
                "Outbound message dead-lettered",
                It.Is<string>(d => d.Contains("CorrelationId=trace-1")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void AlertService_LivesInAbstractionsAssembly_AndUsesOnlyPrimitiveParameters()
    {
        typeof(IAlertService).Assembly.GetName().Name
            .Should().Be("AgentSwarm.Messaging.Abstractions",
                "the alert channel uses only primitive parameters and must stay "
                + "Abstractions-safe so any project that has only the Abstractions "
                + "reference can raise an alert");

        var send = typeof(IAlertService).GetMethod(nameof(IAlertService.SendAlertAsync));
        send.Should().NotBeNull();
        var parameters = send!.GetParameters();
        parameters.Should().HaveCount(3);
        parameters[0].ParameterType.Should().Be(typeof(string));
        parameters[1].ParameterType.Should().Be(typeof(string));
        parameters[2].ParameterType.Should().Be(typeof(CancellationToken));
        send.ReturnType.Should().Be(typeof(Task));
    }

    // ============================================================
    // IOutboundQueue — mockability
    // ============================================================

    [Fact]
    public async Task OutboundQueue_Mock_EnqueueAsync_RecordsCall()
    {
        var mock = new Mock<IOutboundQueue>();
        mock.Setup(q => q.EnqueueAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = CreateOutboundMessage(severity: MessageSeverity.High);

        await mock.Object.EnqueueAsync(message, CancellationToken.None);

        mock.Verify(
            q => q.EnqueueAsync(
                It.Is<OutboundMessage>(m =>
                    m.MessageId == message.MessageId
                    && m.CorrelationId == "trace-1"
                    && m.Severity == MessageSeverity.High),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OutboundQueue_Mock_DequeueAsync_ReturnsConfiguredMessage()
    {
        var mock = new Mock<IOutboundQueue>();
        var critical = CreateOutboundMessage(severity: MessageSeverity.Critical);

        mock.Setup(q => q.DequeueAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(critical);

        var fetched = await mock.Object.DequeueAsync(CancellationToken.None);

        fetched.Should().BeSameAs(critical);
    }

    [Fact]
    public async Task OutboundQueue_Mock_DequeueAsync_CanReturnNullForEmptyQueue()
    {
        var mock = new Mock<IOutboundQueue>();
        mock.Setup(q => q.DequeueAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((OutboundMessage?)null);

        var fetched = await mock.Object.DequeueAsync(CancellationToken.None);

        fetched.Should().BeNull(
            "DequeueAsync must return null when the queue has no pending work so the "
            + "caller can back off; otherwise OutboundQueueProcessor would have no way "
            + "to distinguish empty from delivered.");
    }

    [Fact]
    public async Task OutboundQueue_Mock_MarkSentAsync_RecordsGuidAndLong()
    {
        var mock = new Mock<IOutboundQueue>();
        mock.Setup(q => q.MarkSentAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var messageId = Guid.NewGuid();
        await mock.Object.MarkSentAsync(messageId, 12345L, CancellationToken.None);

        mock.Verify(
            q => q.MarkSentAsync(messageId, 12345L, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OutboundQueue_Mock_MarkFailedAsync_RecordsErrorString()
    {
        var mock = new Mock<IOutboundQueue>();
        mock.Setup(q => q.MarkFailedAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var messageId = Guid.NewGuid();
        await mock.Object.MarkFailedAsync(messageId, "Telegram 502 Bad Gateway", CancellationToken.None);

        mock.Verify(
            q => q.MarkFailedAsync(
                messageId,
                "Telegram 502 Bad Gateway",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OutboundQueue_Mock_DeadLetterAsync_RecordsCall()
    {
        var mock = new Mock<IOutboundQueue>();
        mock.Setup(q => q.DeadLetterAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var messageId = Guid.NewGuid();
        await mock.Object.DeadLetterAsync(messageId, "test:reason", CancellationToken.None);

        mock.Verify(
            q => q.DeadLetterAsync(messageId, "test:reason", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // IOutboundQueue — structural pins
    // ============================================================

    [Fact]
    public void OutboundQueue_LivesInAbstractionsAssembly_NotCore()
    {
        // Pinned because Stage 1.4's brief explicitly places IOutboundQueue
        // in Abstractions; the structural enabler is that OutboundMessage
        // (which IOutboundQueue references on every method) was relocated
        // from Core to Abstractions during this iter. A future "tidy-up
        // refactor" that moves either type back to Core would re-introduce
        // the Abstractions→Core circular reference this layout is designed
        // to prevent.
        typeof(IOutboundQueue).Assembly.GetName().Name
            .Should().Be("AgentSwarm.Messaging.Abstractions");
        typeof(OutboundMessage).Assembly.GetName().Name
            .Should().Be("AgentSwarm.Messaging.Abstractions");
    }

    [Fact]
    public void OutboundQueue_MarkSentAsync_TakesGuidAndLong_NotInt()
    {
        var m = typeof(IOutboundQueue).GetMethod(nameof(IOutboundQueue.MarkSentAsync));
        m.Should().NotBeNull();
        var p = m!.GetParameters();
        p.Should().HaveCount(3);
        p[0].ParameterType.Should().Be(typeof(Guid),
            "messageId corresponds to OutboundMessage.MessageId (Guid)");
        p[1].ParameterType.Should().Be(typeof(long),
            "telegramMessageId is int64 per architecture.md §3.1 canonical-type rule");
        p[2].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [Fact]
    public void OutboundQueue_MarkFailedAsync_TakesGuidAndString()
    {
        var m = typeof(IOutboundQueue).GetMethod(nameof(IOutboundQueue.MarkFailedAsync));
        m.Should().NotBeNull();
        var p = m!.GetParameters();
        p.Should().HaveCount(3);
        p[0].ParameterType.Should().Be(typeof(Guid));
        p[1].ParameterType.Should().Be(typeof(string));
        p[2].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [Fact]
    public void OutboundQueue_DeadLetterAsync_TakesGuidStringAndCancellationToken()
    {
        // Stage 4.1 iter-2 evaluator item 5 — the terminal DLQ
        // transition MUST take a reason string so the audit row's
        // ErrorDetail column carries the failure category + message
        // verbatim. The prior single-Guid signature lost that detail
        // (the dead-letter row landed indistinguishable from any
        // other DLQ cause).
        var m = typeof(IOutboundQueue).GetMethod(nameof(IOutboundQueue.DeadLetterAsync));
        m.Should().NotBeNull();
        var p = m!.GetParameters();
        p.Should().HaveCount(3);
        p[0].ParameterType.Should().Be(typeof(Guid));
        p[1].ParameterType.Should().Be(typeof(string),
            "the reason string is persisted to OutboundMessage.ErrorDetail on the terminal transition; without it the audit trail loses the failure cause");
        p[2].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [Fact]
    public void OutboundQueue_DequeueAsync_ReturnsNullableOutboundMessage()
    {
        var m = typeof(IOutboundQueue).GetMethod(nameof(IOutboundQueue.DequeueAsync));
        m.Should().NotBeNull();
        m!.ReturnType.Should().Be(typeof(Task<OutboundMessage?>),
            "DequeueAsync must be nullable so the caller can distinguish 'queue empty' "
            + "from 'message claimed' — otherwise back-off logic in OutboundQueueProcessor "
            + "(Stage 4.1) would have no way to detect drained state without throwing.");

        // Verify the nullable annotation is preserved through reflection so a
        // future refactor that drops the '?' would trip this test.
        var nullabilityCtx = new NullabilityInfoContext();
        var info = nullabilityCtx.Create(m.ReturnParameter);
        info.GenericTypeArguments.Should().NotBeEmpty();
        info.GenericTypeArguments[0].ReadState.Should().Be(NullabilityState.Nullable,
            "Task<OutboundMessage?> — the nullable annotation on the inner type matters");
    }

    [Fact]
    public void OutboundQueue_AllMutationMethods_ReturnBareTask()
    {
        var enqueue = typeof(IOutboundQueue).GetMethod(nameof(IOutboundQueue.EnqueueAsync));
        var markSent = typeof(IOutboundQueue).GetMethod(nameof(IOutboundQueue.MarkSentAsync));
        var markFailed = typeof(IOutboundQueue).GetMethod(nameof(IOutboundQueue.MarkFailedAsync));
        var deadLetter = typeof(IOutboundQueue).GetMethod(nameof(IOutboundQueue.DeadLetterAsync));

        enqueue!.ReturnType.Should().Be(typeof(Task));
        markSent!.ReturnType.Should().Be(typeof(Task));
        markFailed!.ReturnType.Should().Be(typeof(Task));
        deadLetter!.ReturnType.Should().Be(typeof(Task));
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static AgentQuestionEnvelope CreateEnvelope(string? proposedDefault)
    {
        var question = new AgentQuestion
        {
            QuestionId = "Q-1",
            AgentId = "agent-1",
            TaskId = "T-1",
            Title = "Approve?",
            Body = "Body text",
            Severity = MessageSeverity.High,
            AllowedActions = new[]
            {
                new HumanAction { ActionId = "approve", Label = "Approve", Value = "approve" },
                new HumanAction { ActionId = "reject",  Label = "Reject",  Value = "reject", RequiresComment = true }
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            CorrelationId = "trace-1"
        };
        return new AgentQuestionEnvelope
        {
            Question = question,
            ProposedDefaultActionId = proposedDefault
        };
    }

    private static OutboundMessage CreateOutboundMessage(MessageSeverity severity)
    {
        return new OutboundMessage
        {
            MessageId = Guid.NewGuid(),
            IdempotencyKey = "idem-" + Guid.NewGuid().ToString("N"),
            ChatId = 999L,
            Payload = "hello",
            Severity = severity,
            SourceType = OutboundSourceType.StatusUpdate,
            CreatedAt = DateTimeOffset.UtcNow,
            CorrelationId = "trace-1"
        };
    }
}
