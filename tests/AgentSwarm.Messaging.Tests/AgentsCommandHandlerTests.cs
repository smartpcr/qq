using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Core.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.2 — pins <see cref="AgentsCommandHandler"/> behaviour:
/// single-workspace operators (and the post-disambiguation re-issue
/// case) query the swarm directly with the
/// <see cref="AuthorizedOperator.WorkspaceId"/> resolved by the
/// pipeline; explicit <c>/agents WORKSPACE</c> arguments are
/// validated against the operator's bindings. Multi-binding
/// disambiguation is the pipeline's responsibility (see
/// <c>TelegramUpdatePipelineTests.Pipeline_Command_WhenMultipleBindings_PromptsWorkspaceSelection_WithInlineKeyboard</c>);
/// this handler never emits its own workspace prompt.
/// </summary>
public class AgentsCommandHandlerTests
{
    [Fact]
    public async Task NoArgument_QueriesSwarmForOperatorWorkspace()
    {
        var registry = new Mock<IOperatorRegistry>(MockBehavior.Strict);
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        bus.Setup(b => b.QueryAgentsAsync(
                It.Is<SwarmAgentsQuery>(q => q.WorkspaceId == "w-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new AgentInfo { AgentId = "agent-1", Role = "planner", State = "idle" },
                new AgentInfo { AgentId = "agent-2", Role = "executor", State = "busy", CurrentTaskId = "TASK-7" },
            });

        var handler = new AgentsCommandHandler(bus.Object, registry.Object, NullLogger<AgentsCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Parse("/agents"),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        result.ResponseButtons.Should().BeEmpty(
            "the handler never emits its own workspace-selection keyboard; the pipeline owns that prompt");
        result.ResponseText.Should().Contain("agent-1");
        result.ResponseText.Should().Contain("agent-2");
        result.ResponseText.Should().Contain("task TASK-7");

        // Registry was NOT consulted — the operator's resolved
        // WorkspaceId from the pipeline is authoritative for the
        // no-argument path.
        registry.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PostDisambiguationReissue_QueriesChosenWorkspace()
    {
        // Stage 3.3 re-issues the original `/agents` command after the
        // operator picks a workspace from the pipeline's inline-keyboard
        // prompt. By the time we get here, AuthorizedOperator.WorkspaceId
        // already names the chosen workspace, so the handler just queries
        // it — no second disambiguation round-trip is needed.
        var registry = new Mock<IOperatorRegistry>(MockBehavior.Strict);
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        bus.Setup(b => b.QueryAgentsAsync(
                It.Is<SwarmAgentsQuery>(q => q.WorkspaceId == "ws-alpha"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new AgentInfo { AgentId = "alpha-agent", Role = "planner", State = "idle" },
            });

        var handler = new AgentsCommandHandler(bus.Object, registry.Object, NullLogger<AgentsCommandHandler>.Instance);
        var op = TestOperator.Default with { WorkspaceId = "ws-alpha" };

        var result = await handler.HandleAsync(
            TestCommands.Parse("/agents"),
            op,
            default);

        result.Success.Should().BeTrue();
        result.ResponseText.Should().Contain("ws-alpha");
        result.ResponseText.Should().Contain("alpha-agent");
        result.ResponseButtons.Should().BeEmpty();
    }

    [Fact]
    public async Task ExplicitWorkspaceArgument_QueriesFilteredRoster_AfterAuthorizationCheck()
    {
        var registry = new Mock<IOperatorRegistry>(MockBehavior.Strict);
        registry.Setup(r => r.GetAllBindingsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Binding("ws-alpha"), Binding("ws-beta") });

        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        bus.Setup(b => b.QueryAgentsAsync(
                It.Is<SwarmAgentsQuery>(q => q.WorkspaceId == "ws-beta"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new AgentInfo { AgentId = "beta-agent", Role = "planner", State = "idle" },
            });

        var handler = new AgentsCommandHandler(bus.Object, registry.Object, NullLogger<AgentsCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Build("agents", "ws-beta"),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        result.ResponseText.Should().Contain("ws-beta");
        result.ResponseText.Should().Contain("beta-agent");
        result.ResponseButtons.Should().BeEmpty();
    }

    [Fact]
    public async Task ExplicitWorkspaceArgument_NotBoundToOperator_IsRejected()
    {
        var registry = new Mock<IOperatorRegistry>(MockBehavior.Strict);
        registry.Setup(r => r.GetAllBindingsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Binding("ws-alpha") });

        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        var handler = new AgentsCommandHandler(bus.Object, registry.Object, NullLogger<AgentsCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Build("agents", "ws-other"),
            TestOperator.Default,
            default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("agents_unauthorized_workspace");
        result.ResponseText.Should().Contain("ws-other");
    }

    private static OperatorBinding Binding(string workspaceId) => new()
    {
        Id = Guid.NewGuid(),
        TelegramUserId = 42,
        TelegramChatId = 100,
        ChatType = ChatType.Private,
        OperatorAlias = "@operator-1",
        TenantId = "t-acme",
        WorkspaceId = workspaceId,
        RegisteredAt = DateTimeOffset.UtcNow,
        IsActive = true,
    };
}
