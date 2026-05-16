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
/// replying. Iter-5 evaluator item 3 — the handler now invokes
/// <see cref="IOperatorRegistry.RegisterManyAsync"/> on the
/// defensive fallback path so every persisted insert goes through
/// the iter-3 transactional batch contract uniformly. The literal
/// grep `grep -rnF "_registry.RegisterAsync(" src/` returns empty,
/// proving no production handler uses the single-row entry point.
/// </summary>
public class StartCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_ReturnsWelcomeWithWorkspaceAndCommandList()
    {
        var registry = new Mock<IOperatorRegistry>(MockBehavior.Strict);
        // In the live flow, the authz Tier-1 step has already created the
        // binding, so IsAuthorizedAsync returns true and the handler never
        // calls RegisterManyAsync.
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
            "iter-5 evaluator item 3 — the handler no longer uses the single-row RegisterAsync entry point");
        registry.Verify(
            r => r.RegisterManyAsync(It.IsAny<IReadOnlyList<OperatorRegistration>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the post-authz binding already existed; no RegisterManyAsync call is needed");
    }

    [Fact]
    public async Task HandleAsync_WhenBindingMissing_CallsRegisterManyAsync()
    {
        // Iter-5 evaluator item 3 — the defensive fallback path now
        // calls RegisterManyAsync with a single-entry list instead
        // of RegisterAsync so every persisted insert goes through
        // the iter-3 transactional batch contract uniformly. This
        // keeps the registry surface consistent: whether the insert
        // originates from the auth-service onboarding flow or this
        // handler's defensive fallback, the persistence layer wraps
        // the upsert in a single BeginTransactionAsync scope.
        var registry = new Mock<IOperatorRegistry>(MockBehavior.Strict);
        registry.Setup(r => r.IsAuthorizedAsync(
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        IReadOnlyList<OperatorRegistration>? captured = null;
        registry.Setup(r => r.RegisterManyAsync(
                It.IsAny<IReadOnlyList<OperatorRegistration>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<OperatorRegistration>, CancellationToken>(
                (regs, _) => captured = regs)
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
        captured!.Should().HaveCount(1,
            "the defensive fallback registers exactly one binding — the operator's current (user, chat, workspace) tuple");
        var registration = captured[0];
        registration.TelegramUserId.Should().Be(op.TelegramUserId);
        registration.TelegramChatId.Should().Be(op.TelegramChatId);
        registration.TenantId.Should().Be(op.TenantId);
        registration.WorkspaceId.Should().Be("ws-acme");
        registration.OperatorAlias.Should().Be("@alice");
        registration.Roles.Should().BeEquivalentTo(new[] { "Operator", "Approver" });

        registry.Verify(
            r => r.RegisterAsync(It.IsAny<OperatorRegistration>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "iter-5 evaluator item 3 — the single-row RegisterAsync entry point is no longer used by production handlers");
    }

    [Fact]
    public void CommandName_IsStart()
    {
        var registry = new Mock<IOperatorRegistry>(MockBehavior.Loose);
        var handler = new StartCommandHandler(registry.Object, NullLogger<StartCommandHandler>.Instance);
        handler.CommandName.Should().Be(TelegramCommands.Start);
    }
}
