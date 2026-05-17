using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.2 — pins <see cref="AskCommandHandler"/> behaviour: emits a
/// <see cref="SwarmCommandType.CreateTask"/>
/// <see cref="SwarmCommand"/> carrying the operator's body text and
/// responds with the freshly-minted task id + correlation id so the
/// operator can quote them in follow-up commands. Covers the story
/// acceptance criterion <i>"Ask creates work item"</i>.
/// </summary>
public class AskCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_CreatesTaskAndReplyIncludesTaskId()
    {
        SwarmCommand? published = null;
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        bus.Setup(b => b.PublishCommandAsync(It.IsAny<SwarmCommand>(), It.IsAny<CancellationToken>()))
            .Callback<SwarmCommand, CancellationToken>((c, _) => published = c)
            .Returns(Task.CompletedTask);

        var handler = new AskCommandHandler(
            bus.Object,
            new FakeTimeProvider(),
            NullLogger<AskCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Build("ask", "build", "release", "notes", "for", "Solution12"),
            TestOperator.Default,
            default);

        result.Success.Should().BeTrue();
        result.ResponseText.Should().StartWith("✅ Task TASK-");

        published.Should().NotBeNull();
        published!.CommandType.Should().Be(SwarmCommandType.CreateTask);
        published.TaskId.Should().StartWith("TASK-");
        published.Payload.Should().Be("build release notes for Solution12");
        published.OperatorId.Should().Be(TestOperator.Default.OperatorId);
        published.CorrelationId.Should().Be(result.CorrelationId);
        result.ResponseText.Should().Contain(published.TaskId!);
        result.ResponseText.Should().Contain(published.CorrelationId);
    }

    [Fact]
    public async Task HandleAsync_MissingBody_ReturnsUsageHelp()
    {
        var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
        var handler = new AskCommandHandler(
            bus.Object,
            new FakeTimeProvider(),
            NullLogger<AskCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            TestCommands.Parse("/ask"),
            TestOperator.Default,
            default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("ask_missing_body");
        result.ResponseText.Should().Contain("/ask");
        bus.VerifyNoOtherCalls();
    }
}
