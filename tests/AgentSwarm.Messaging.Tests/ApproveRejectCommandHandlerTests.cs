using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.2 — pins approve/reject behaviour: the handler loads the
/// pending question, emits a <see cref="HumanDecisionEvent"/> with the
/// canonical action value plus the originating
/// <see cref="PendingQuestion.CorrelationId"/>, and persists a
/// <see cref="HumanResponseAuditEntry"/> with the five mandatory
/// fields from the story brief.
/// </summary>
public class ApproveRejectCommandHandlerTests
{
    [Fact]
    public async Task Approve_EmitsHumanDecisionEvent_WithApproveActionAndOriginalCorrelationId()
    {
        var (handler, bus, audit, store, _) = BuildApprove();
        store.Setup(s => s.GetAsync("Q1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewPendingQuestion("Q1", "corr-1"));

        HumanDecisionEvent? captured = null;
        bus.Setup(b => b.PublishHumanDecisionAsync(It.IsAny<HumanDecisionEvent>(), It.IsAny<CancellationToken>()))
            .Callback<HumanDecisionEvent, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);

        HumanResponseAuditEntry? auditEntry = null;
        audit.Setup(a => a.LogHumanResponseAsync(It.IsAny<HumanResponseAuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<HumanResponseAuditEntry, CancellationToken>((e, _) => auditEntry = e)
            .Returns(Task.CompletedTask);

        var result = await handler.HandleAsync(
            TestCommands.Build("approve", "Q1"),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        result.CorrelationId.Should().Be("corr-1");
        result.ResponseText.Should().Contain("Q1");
        result.ResponseText.Should().Contain("approved");

        captured.Should().NotBeNull();
        captured!.QuestionId.Should().Be("Q1");
        captured.ActionValue.Should().Be("approve");
        captured.CorrelationId.Should().Be("corr-1");
        captured.Messenger.Should().Be("telegram");
        captured.ExternalUserId.Should().Be("42");
        captured.ExternalMessageId.Should().Be("cmd:approve:Q1");

        auditEntry.Should().NotBeNull();
        auditEntry!.QuestionId.Should().Be("Q1");
        auditEntry.ActionValue.Should().Be("approve");
        auditEntry.AgentId.Should().Be("agent-7");
        auditEntry.MessageId.Should().Be("cmd:approve:Q1");
        auditEntry.UserId.Should().Be("42");
        auditEntry.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public async Task Reject_EmitsHumanDecisionEvent_WithRejectAction()
    {
        var (handler, bus, _, store, _) = BuildReject();
        store.Setup(s => s.GetAsync("Q2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewPendingQuestion("Q2", "corr-2"));

        HumanDecisionEvent? captured = null;
        bus.Setup(b => b.PublishHumanDecisionAsync(It.IsAny<HumanDecisionEvent>(), It.IsAny<CancellationToken>()))
            .Callback<HumanDecisionEvent, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);

        var result = await handler.HandleAsync(
            TestCommands.Build("reject", "Q2"),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        captured!.ActionValue.Should().Be("reject");
        captured.CorrelationId.Should().Be("corr-2");
    }

    [Fact]
    public async Task Approve_UnknownQuestionId_ReturnsNotFound()
    {
        var (handler, bus, _, store, _) = BuildApprove();
        store.Setup(s => s.GetAsync("Q-missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PendingQuestion?)null);

        var result = await handler.HandleAsync(
            TestCommands.Build("approve", "Q-missing"),
            TestOperator.Default,
            default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("approve_question_not_found");
        result.ResponseText.Should().Contain("Q-missing");
        bus.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Approve_NoArgument_ReturnsUsageHelp()
    {
        var (handler, _, _, _, _) = BuildApprove();

        var result = await handler.HandleAsync(
            TestCommands.Parse("/approve"),
            TestOperator.Default,
            default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("approve_missing_question_id");
    }

    private static (
        ApproveCommandHandler handler,
        Mock<ISwarmCommandBus> bus,
        Mock<IAuditLogger> audit,
        Mock<IPendingQuestionStore> store,
        FakeTimeProvider time)
        BuildApprove()
    {
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        var audit = new Mock<IAuditLogger>(MockBehavior.Strict);
        var store = new Mock<IPendingQuestionStore>(MockBehavior.Strict);
        var time = new FakeTimeProvider();
        var handler = new ApproveCommandHandler(
            store.Object,
            bus.Object,
            audit.Object,
            time,
            NullLogger<ApproveCommandHandler>.Instance);
        return (handler, bus, audit, store, time);
    }

    private static (
        RejectCommandHandler handler,
        Mock<ISwarmCommandBus> bus,
        Mock<IAuditLogger> audit,
        Mock<IPendingQuestionStore> store,
        FakeTimeProvider time)
        BuildReject()
    {
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        var audit = new Mock<IAuditLogger>(MockBehavior.Strict);
        audit.Setup(a => a.LogHumanResponseAsync(It.IsAny<HumanResponseAuditEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var store = new Mock<IPendingQuestionStore>(MockBehavior.Strict);
        var time = new FakeTimeProvider();
        var handler = new RejectCommandHandler(
            store.Object,
            bus.Object,
            audit.Object,
            time,
            NullLogger<RejectCommandHandler>.Instance);
        return (handler, bus, audit, store, time);
    }

    private static PendingQuestion NewPendingQuestion(string questionId, string correlationId) => new()
    {
        QuestionId = questionId,
        AgentId = "agent-7",
        TaskId = "TASK-7",
        Title = "Deploy?",
        Body = "Ready to deploy build #42",
        Severity = MessageSeverity.Normal,
        AllowedActions = new[]
        {
            new HumanAction { ActionId = "ap", Label = "Approve", Value = "approve" },
            new HumanAction { ActionId = "rj", Label = "Reject", Value = "reject" },
        },
        TelegramChatId = 100,
        TelegramMessageId = 99,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        CorrelationId = correlationId,
        Status = PendingQuestionStatus.Pending,
        StoredAt = DateTimeOffset.UtcNow,
    };
}
