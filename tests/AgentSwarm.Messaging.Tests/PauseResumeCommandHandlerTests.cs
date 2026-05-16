using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.2 — pins pause/resume behaviour: both handlers emit a
/// <see cref="SwarmCommand"/> with the matching
/// <see cref="SwarmCommand.CommandType"/> and either an explicit
/// <see cref="SwarmCommand.AgentId"/> (single-agent scope) or a
/// workspace-wide fan-out (<see cref="SwarmCommandScope.All"/>) per
/// architecture.md §5 (<c>/pause AGENT-ID</c> | <c>/pause all</c>,
/// <c>/resume AGENT-ID</c> | <c>/resume all</c>).
/// </summary>
public class PauseResumeCommandHandlerTests
{
    [Fact]
    public async Task Pause_EmitsSwarmCommand_WithPauseTypeAndAgentIdAndSingleScope()
    {
        SwarmCommand? captured = null;
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        bus.Setup(b => b.PublishCommandAsync(It.IsAny<SwarmCommand>(), It.IsAny<CancellationToken>()))
            .Callback<SwarmCommand, CancellationToken>((c, _) => captured = c)
            .Returns(Task.CompletedTask);

        var handler = new PauseCommandHandler(bus.Object, NullLogger<PauseCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Build("pause", "agent-7"),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        result.ResponseText.Should().Contain("agent-7");
        captured.Should().NotBeNull();
        captured!.CommandType.Should().Be(SwarmCommandType.Pause);
        captured.AgentId.Should().Be("agent-7");
        captured.WorkspaceId.Should().Be(TestOperator.Default.WorkspaceId);
        captured.Scope.Should().Be(SwarmCommandScope.Single);
        captured.TaskId.Should().BeNull();
        captured.OperatorId.Should().Be(TestOperator.Default.OperatorId);
        captured.CorrelationId.Should().Be(result.CorrelationId);
    }

    [Fact]
    public async Task Resume_EmitsSwarmCommand_WithResumeTypeAndAgentId()
    {
        SwarmCommand? captured = null;
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        bus.Setup(b => b.PublishCommandAsync(It.IsAny<SwarmCommand>(), It.IsAny<CancellationToken>()))
            .Callback<SwarmCommand, CancellationToken>((c, _) => captured = c)
            .Returns(Task.CompletedTask);

        var handler = new ResumeCommandHandler(bus.Object, NullLogger<ResumeCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Build("resume", "agent-7"),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        captured!.CommandType.Should().Be(SwarmCommandType.Resume);
        captured.AgentId.Should().Be("agent-7");
        captured.Scope.Should().Be(SwarmCommandScope.Single);
        captured.WorkspaceId.Should().Be(TestOperator.Default.WorkspaceId);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    [InlineData("All")]
    public async Task Pause_AllToken_EmitsAllScope_WithNoAgentId(string token)
    {
        SwarmCommand? captured = null;
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        bus.Setup(b => b.PublishCommandAsync(It.IsAny<SwarmCommand>(), It.IsAny<CancellationToken>()))
            .Callback<SwarmCommand, CancellationToken>((c, _) => captured = c)
            .Returns(Task.CompletedTask);

        var handler = new PauseCommandHandler(bus.Object, NullLogger<PauseCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Build("pause", token),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        result.ResponseText.Should().Contain("all agents");
        result.ResponseText.Should().Contain(TestOperator.Default.WorkspaceId);
        captured.Should().NotBeNull();
        captured!.CommandType.Should().Be(SwarmCommandType.Pause);
        captured.AgentId.Should().BeNull("the 'all' token fans out to every agent in the workspace");
        captured.Scope.Should().Be(SwarmCommandScope.All);
        captured.WorkspaceId.Should().Be(TestOperator.Default.WorkspaceId);
    }

    [Fact]
    public async Task Resume_AllToken_EmitsAllScope()
    {
        SwarmCommand? captured = null;
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        bus.Setup(b => b.PublishCommandAsync(It.IsAny<SwarmCommand>(), It.IsAny<CancellationToken>()))
            .Callback<SwarmCommand, CancellationToken>((c, _) => captured = c)
            .Returns(Task.CompletedTask);

        var handler = new ResumeCommandHandler(bus.Object, NullLogger<ResumeCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Build("resume", "all"),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        captured!.CommandType.Should().Be(SwarmCommandType.Resume);
        captured.AgentId.Should().BeNull();
        captured.Scope.Should().Be(SwarmCommandScope.All);
        captured.WorkspaceId.Should().Be(TestOperator.Default.WorkspaceId);
    }

    [Fact]
    public async Task Pause_MissingTarget_ReturnsUsageHelp()
    {
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        var handler = new PauseCommandHandler(bus.Object, NullLogger<PauseCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Parse("/pause"),
            TestOperator.Default,
            default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("pause_missing_target");
        result.ResponseText.Should().Contain("agentId");
        result.ResponseText.Should().Contain("all");
        bus.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Resume_MissingTarget_ReturnsUsageHelp()
    {
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        var handler = new ResumeCommandHandler(bus.Object, NullLogger<ResumeCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Parse("/resume"),
            TestOperator.Default,
            default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("resume_missing_target");
        bus.VerifyNoOtherCalls();
    }
}
