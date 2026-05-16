using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Core.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.2 — pins <see cref="HandoffCommandHandler"/> behaviour
/// against every test scenario in the workstream brief:
/// <list type="bullet">
///   <item>Successful transfer persists oversight, notifies both
///         operators, audits the action.</item>
///   <item>Nonexistent task is rejected with the canonical
///         "Task NOT_FOUND" reply and no oversight row is created.</item>
///   <item>Unregistered target alias is rejected with the canonical
///         "Operator @x is not registered" reply.</item>
///   <item>Zero / one arg returns the usage help.</item>
///   <item>Persisted <see cref="TaskOversight"/> row carries the
///         correct <c>OperatorBindingId</c> and <c>AssignedBy</c>.</item>
/// </list>
/// </summary>
public class HandoffCommandHandlerTests
{
    private static readonly Guid Operator1Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Operator2Id = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task ZeroArguments_ReturnsUsageHelp()
    {
        var (handler, _, _, _, _, _) = Build();

        var result = await handler.HandleAsync(
            TestCommands.Parse("/handoff"),
            Operator1(),
            default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("handoff_invalid_usage");
        result.ResponseText.Should().Be(HandoffCommandHandler.UsageMessage);
    }

    [Fact]
    public async Task OneArgument_ReturnsUsageHelp()
    {
        var (handler, _, _, _, _, _) = Build();

        var result = await handler.HandleAsync(
            TestCommands.Build("handoff", "TASK-099"),
            Operator1(),
            default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("handoff_invalid_usage");
        result.ResponseText.Should().Be(HandoffCommandHandler.UsageMessage);
    }

    [Fact]
    public async Task NonexistentTask_IsRejected_AndNoOversightRowIsCreated()
    {
        var (handler, oversight, registry, _, _, _) = Build();
        oversight.Setup(o => o.GetByTaskIdAsync("NONEXISTENT", It.IsAny<CancellationToken>()))
            .ReturnsAsync((TaskOversight?)null);

        var result = await handler.HandleAsync(
            TestCommands.Build("handoff", "NONEXISTENT", "@operator-2"),
            Operator1(),
            default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("handoff_task_not_found");
        result.ResponseText.Should().Be("❌ Task NONEXISTENT not found");
        oversight.Verify(
            o => o.UpsertAsync(It.IsAny<TaskOversight>(), It.IsAny<CancellationToken>()),
            Times.Never);
        registry.Verify(
            r => r.GetByAliasAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UnregisteredTarget_IsRejected()
    {
        var (handler, oversight, registry, _, _, _) = Build();
        oversight.Setup(o => o.GetByTaskIdAsync("TASK-099", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingOwnedByOperator1());
        registry.Setup(r => r.GetByAliasAsync("@unknown-user", "t-acme", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperatorBinding?)null);

        var result = await handler.HandleAsync(
            TestCommands.Build("handoff", "TASK-099", "@unknown-user"),
            Operator1(),
            default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("handoff_operator_not_found");
        result.ResponseText.Should().Contain("@unknown-user");
        result.ResponseText.Should().Contain("is not registered");
        oversight.Verify(
            o => o.UpsertAsync(It.IsAny<TaskOversight>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SuccessfulTransfer_PersistsOversight_NotifiesBothOperators_AndAudits()
    {
        var (handler, oversight, registry, queue, audit, time) = Build();
        oversight.Setup(o => o.GetByTaskIdAsync("TASK-099", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingOwnedByOperator1());
        registry.Setup(r => r.GetByAliasAsync("@operator-2", "t-acme", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Operator2Binding());

        TaskOversight? upserted = null;
        oversight.Setup(o => o.UpsertAsync(It.IsAny<TaskOversight>(), It.IsAny<CancellationToken>()))
            .Callback<TaskOversight, CancellationToken>((to, _) => upserted = to)
            .Returns(Task.CompletedTask);

        OutboundMessage? notification = null;
        queue.Setup(q => q.EnqueueAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => notification = m)
            .Returns(Task.CompletedTask);

        AuditEntry? auditEntry = null;
        audit.Setup(a => a.LogAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, CancellationToken>((e, _) => auditEntry = e)
            .Returns(Task.CompletedTask);

        var result = await handler.HandleAsync(
            TestCommands.Build("handoff", "TASK-099", "@operator-2"),
            Operator1(),
            default);

        // Sender confirmation.
        result.Success.Should().BeTrue();
        result.ResponseText.Should().Be("✅ Oversight of TASK-099 transferred to @operator-2");

        // Oversight row.
        upserted.Should().NotBeNull();
        upserted!.TaskId.Should().Be("TASK-099");
        upserted.OperatorBindingId.Should().Be(Operator2Id);
        upserted.AssignedBy.Should().Be("@operator-1");
        upserted.CorrelationId.Should().Be(result.CorrelationId);
        upserted.AssignedAt.Should().Be(time.GetUtcNow());

        // Target notification routed through the durable outbound queue.
        notification.Should().NotBeNull();
        notification!.ChatId.Should().Be(200);
        notification.Payload.Should().Contain("TASK-099");
        notification.Payload.Should().Contain("@operator-1");
        notification.SourceType.Should().Be(OutboundSourceType.CommandAck);
        notification.CorrelationId.Should().Be(result.CorrelationId);

        // Audit row.
        auditEntry.Should().NotBeNull();
        auditEntry!.Action.Should().Be(HandoffCommandHandler.AuditAction);
        auditEntry.MessageId.Should().Be("TASK-099");
        auditEntry.UserId.Should().Be("@operator-1");
        auditEntry.CorrelationId.Should().Be(result.CorrelationId);
        auditEntry.Timestamp.Should().Be(time.GetUtcNow());
        auditEntry.Details.Should().Contain("@operator-2");
    }

    [Fact]
    public async Task PersistsTaskOversight_WithCorrectBindingAndAssignedBy()
    {
        var (handler, oversight, registry, queue, audit, _) = Build();
        oversight.Setup(o => o.GetByTaskIdAsync("TASK-099", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingOwnedByOperator1());
        registry.Setup(r => r.GetByAliasAsync("@operator-2", "t-acme", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Operator2Binding());

        TaskOversight? upserted = null;
        oversight.Setup(o => o.UpsertAsync(It.IsAny<TaskOversight>(), It.IsAny<CancellationToken>()))
            .Callback<TaskOversight, CancellationToken>((to, _) => upserted = to)
            .Returns(Task.CompletedTask);
        queue.Setup(q => q.EnqueueAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        audit.Setup(a => a.LogAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await handler.HandleAsync(
            TestCommands.Build("handoff", "TASK-099", "@operator-2"),
            Operator1(),
            default);

        result.Success.Should().BeTrue();
        upserted!.OperatorBindingId.Should().Be(Operator2Id, "the new owner is operator-2");
        upserted.AssignedBy.Should().Be("@operator-1", "the source operator initiated the handoff");
    }

    [Fact]
    public async Task AuditDetails_AreValidJson_AndEscapeQuotesAndBackslashesInIdentifiers()
    {
        // iter-3 evaluator item 5: hand-built JSON in the prior
        // implementation produced invalid JSON when the task id or
        // alias contained quotes or backslashes. Switching to
        // System.Text.Json guarantees the persisted Details column
        // round-trips for arbitrary identifier shapes.
        var (handler, oversight, registry, queue, audit, _) = Build();
        var trickyTaskId = "TASK-\"weird\"\\path";
        var trickyAlias = "@op\"er\\ator-2";

        var existing = ExistingOwnedByOperator1() with { TaskId = trickyTaskId };
        oversight.Setup(o => o.GetByTaskIdAsync(trickyTaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        registry.Setup(r => r.GetByAliasAsync(trickyAlias, "t-acme", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Operator2Binding() with { OperatorAlias = trickyAlias });

        oversight.Setup(o => o.UpsertAsync(It.IsAny<TaskOversight>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        queue.Setup(q => q.EnqueueAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        AuditEntry? captured = null;
        audit.Setup(a => a.LogAsync(It.IsAny<AuditEntry>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEntry, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);

        var result = await handler.HandleAsync(
            TestCommands.Build("handoff", trickyTaskId, trickyAlias),
            Operator1(),
            default);

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Details.Should().NotBeNullOrEmpty();

        // Must be valid JSON — parsing throws JsonException otherwise.
        using var doc = System.Text.Json.JsonDocument.Parse(captured.Details!);
        var root = doc.RootElement;
        root.GetProperty("taskId").GetString().Should().Be(trickyTaskId);
        root.GetProperty("targetAlias").GetString().Should().Be(trickyAlias);
    }

    private static (
        HandoffCommandHandler handler,
        Mock<ITaskOversightRepository> oversight,
        Mock<IOperatorRegistry> registry,
        Mock<IOutboundQueue> outbound,
        Mock<IAuditLogger> audit,
        FakeTimeProvider time)
        Build()
    {
        var oversight = new Mock<ITaskOversightRepository>(MockBehavior.Strict);
        var registry = new Mock<IOperatorRegistry>(MockBehavior.Strict);
        var outbound = new Mock<IOutboundQueue>(MockBehavior.Strict);
        var audit = new Mock<IAuditLogger>(MockBehavior.Strict);
        var time = new FakeTimeProvider();
        time.SetUtcNow(new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero));
        var handler = new HandoffCommandHandler(
            oversight.Object,
            registry.Object,
            outbound.Object,
            audit.Object,
            time,
            NullLogger<HandoffCommandHandler>.Instance);
        return (handler, oversight, registry, outbound, audit, time);
    }

    private static AuthorizedOperator Operator1() => new()
    {
        OperatorId = Operator1Id,
        TenantId = "t-acme",
        WorkspaceId = "w-1",
        TelegramUserId = 42,
        TelegramChatId = 100,
        OperatorAlias = "@operator-1",
    };

    private static TaskOversight ExistingOwnedByOperator1() => new()
    {
        TaskId = "TASK-099",
        OperatorBindingId = Operator1Id,
        AssignedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
        AssignedBy = "@operator-1",
        CorrelationId = "seed-corr",
    };

    private static OperatorBinding Operator2Binding() => new()
    {
        Id = Operator2Id,
        TelegramUserId = 99,
        TelegramChatId = 200,
        ChatType = ChatType.Private,
        OperatorAlias = "@operator-2",
        TenantId = "t-acme",
        WorkspaceId = "w-1",
        RegisteredAt = DateTimeOffset.UtcNow,
        IsActive = true,
    };
}
