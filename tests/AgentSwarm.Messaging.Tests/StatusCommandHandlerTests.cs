using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.2 — pins <see cref="StatusCommandHandler"/> behaviour: hits
/// <see cref="ISwarmCommandBus.QueryStatusAsync"/> with the operator's
/// workspace (and optional task id), then renders the summary.
/// </summary>
public class StatusCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_QueriesSwarmWithOperatorWorkspace_AndRendersSummary()
    {
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        bus.Setup(b => b.QueryStatusAsync(
                It.Is<SwarmStatusQuery>(q => q.WorkspaceId == "w-1" && q.TaskId == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwarmStatusSummary
            {
                WorkspaceId = "w-1",
                State = "running",
                ActiveAgentCount = 3,
                PendingTaskCount = 2,
                DisplayText = "All systems green.",
            });

        var handler = new StatusCommandHandler(bus.Object, NullLogger<StatusCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Parse("/status"),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        result.ResponseText.Should().Contain("running");
        result.ResponseText.Should().Contain("Active agents: 3");
        result.ResponseText.Should().Contain("Pending tasks: 2");
        result.ResponseText.Should().Contain("All systems green.");
    }

    [Fact]
    public async Task HandleAsync_ForwardsTaskIdArgumentToQuery()
    {
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        SwarmStatusQuery? captured = null;
        bus.Setup(b => b.QueryStatusAsync(It.IsAny<SwarmStatusQuery>(), It.IsAny<CancellationToken>()))
            .Callback<SwarmStatusQuery, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(new SwarmStatusSummary
            {
                WorkspaceId = "w-1",
                State = "running",
                TaskId = "TASK-001",
            });

        var handler = new StatusCommandHandler(bus.Object, NullLogger<StatusCommandHandler>.Instance);
        var result = await handler.HandleAsync(
            TestCommands.Build("status", "TASK-001"),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.TaskId.Should().Be("TASK-001");
        result.ResponseText.Should().Contain("TASK-001");
    }
}
