using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.2 — pins pause/resume behaviour: both handlers emit a
/// <see cref="SwarmCommand"/> with the matching
/// <see cref="SwarmCommand.CommandType"/> and the operator-supplied
/// <see cref="SwarmCommand.TaskId"/>.
/// </summary>
public class PauseResumeCommandHandlerTests
{
    [Fact]
    public async Task Pause_EmitsSwarmCommand_WithPauseTypeAndTaskId()
    {
        SwarmCommand? captured = null;
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        bus.Setup(b => b.PublishCommandAsync(It.IsAny<SwarmCommand>(), It.IsAny<CancellationToken>()))
            .Callback<SwarmCommand, CancellationToken>((c, _) => captured = c)
            .Returns(Task.CompletedTask);

        var handler = new PauseCommandHandler(bus.Object, NullLogger<PauseCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Build("pause", "TASK-7"),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.CommandType.Should().Be(SwarmCommandType.Pause);
        captured.TaskId.Should().Be("TASK-7");
        captured.OperatorId.Should().Be(TestOperator.Default.OperatorId);
        captured.CorrelationId.Should().Be(result.CorrelationId);
    }

    [Fact]
    public async Task Resume_EmitsSwarmCommand_WithResumeType()
    {
        SwarmCommand? captured = null;
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        bus.Setup(b => b.PublishCommandAsync(It.IsAny<SwarmCommand>(), It.IsAny<CancellationToken>()))
            .Callback<SwarmCommand, CancellationToken>((c, _) => captured = c)
            .Returns(Task.CompletedTask);

        var handler = new ResumeCommandHandler(bus.Object, NullLogger<ResumeCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Build("resume", "TASK-7"),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        captured!.CommandType.Should().Be(SwarmCommandType.Resume);
        captured.TaskId.Should().Be("TASK-7");
    }

    [Fact]
    public async Task Pause_MissingTaskId_ReturnsUsageHelp()
    {
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        var handler = new PauseCommandHandler(bus.Object, NullLogger<PauseCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Parse("/pause"),
            TestOperator.Default,
            default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("pause_missing_task_id");
        bus.VerifyNoOtherCalls();
    }
}
