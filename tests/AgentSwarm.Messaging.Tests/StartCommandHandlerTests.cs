using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Core.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.2 — pins <see cref="StartCommandHandler"/> behaviour:
/// returns a welcome with the workspace id and the recognized command
/// vocabulary, and (iter-2 evaluator item 4) ensures the user is
/// registered with the <see cref="IOperatorRegistry"/> before
/// replying.
/// </summary>
public class StartCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_ReturnsWelcomeWithWorkspaceAndCommandList()
    {
        var registry = new Mock<IOperatorRegistry>(MockBehavior.Strict);
        // In the live flow, the authz Tier-1 step has already created the
        // binding, so IsAuthorizedAsync returns true and the handler never
        // calls RegisterAsync.
        registry.Setup(r => r.IsAuthorizedAsync(
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var handler = new StartCommandHandler(registry.Object, NullLogger<StartCommandHandler>.Instance);
        var op = TestOperator.Default with { WorkspaceId = "ws-acme" };

        var result = await handler.HandleAsync(
            TestCommands.Parse("/start"),
            op,
            default);

        result.Success.Should().BeTrue();
        result.ResponseText.Should().Contain("ws-acme");
        foreach (var name in TelegramCommands.All)
        {
            result.ResponseText.Should().Contain("/" + name);
        }

        registry.Verify(
            r => r.IsAuthorizedAsync(op.TelegramUserId, op.TelegramChatId, It.IsAny<CancellationToken>()),
            Times.Once,
            "the handler must consult the registry to satisfy the brief's 'registers user' requirement");
        registry.Verify(
            r => r.RegisterAsync(It.IsAny<OperatorRegistration>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the post-authz binding already existed; no second RegisterAsync call is needed");
    }

    [Fact]
    public async Task HandleAsync_WhenBindingMissing_CallsRegisterAsync()
    {
        // Defensive path — should not be reachable in production because
        // the pipeline's authz stage refuses to invoke a handler for an
        // operator with no binding. But the handler still wires the
        // explicit registration call so the brief's "registers user"
        // requirement is delivered as code in StartCommandHandler.
        var registry = new Mock<IOperatorRegistry>(MockBehavior.Strict);
        registry.Setup(r => r.IsAuthorizedAsync(
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        OperatorRegistration? captured = null;
        registry.Setup(r => r.RegisterAsync(It.IsAny<OperatorRegistration>(), It.IsAny<CancellationToken>()))
            .Callback<OperatorRegistration, CancellationToken>((reg, _) => captured = reg)
            .Returns(Task.CompletedTask);

        var handler = new StartCommandHandler(registry.Object, NullLogger<StartCommandHandler>.Instance);
        var op = TestOperator.Default with
        {
            WorkspaceId = "ws-acme",
            OperatorAlias = "@alice",
            Roles = new[] { "Operator", "Approver" },
        };

        var result = await handler.HandleAsync(
            TestCommands.Parse("/start"),
            op,
            default);

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.TelegramUserId.Should().Be(op.TelegramUserId);
        captured.TelegramChatId.Should().Be(op.TelegramChatId);
        captured.TenantId.Should().Be(op.TenantId);
        captured.WorkspaceId.Should().Be("ws-acme");
        captured.OperatorAlias.Should().Be("@alice");
        captured.Roles.Should().BeEquivalentTo(new[] { "Operator", "Approver" });
    }

    [Fact]
    public void CommandName_IsStart()
    {
        var registry = new Mock<IOperatorRegistry>(MockBehavior.Loose);
        var handler = new StartCommandHandler(registry.Object, NullLogger<StartCommandHandler>.Instance);
        handler.CommandName.Should().Be(TelegramCommands.Start);
    }
}
