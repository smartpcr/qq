using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram.Swarm;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.7 — locks the <see cref="StubSwarmCommandBus"/> contract:
/// publishes are silently consumed, queries return empty/minimal
/// projections, and <see cref="StubSwarmCommandBus.SubscribeAsync"/>
/// returns a well-formed empty <see cref="IAsyncEnumerable{T}"/> that
/// completes cleanly instead of blocking the
/// <c>SwarmEventSubscriptionService</c> loop.
/// </summary>
public class StubSwarmCommandBusTests
{
    [Fact]
    public async Task PublishCommandAsync_SilentlyAccepts()
    {
        var bus = new StubSwarmCommandBus(NullLogger<StubSwarmCommandBus>.Instance);
        var command = new SwarmCommand
        {
            CommandType = SwarmCommandType.CreateTask,
            TaskId = "T",
            OperatorId = Guid.NewGuid(),
            CorrelationId = "trace",
        };

        await bus.PublishCommandAsync(command, default);
    }

    [Fact]
    public async Task PublishHumanDecisionAsync_SilentlyAccepts()
    {
        var bus = new StubSwarmCommandBus(NullLogger<StubSwarmCommandBus>.Instance);
        var decision = new HumanDecisionEvent
        {
            QuestionId = "Q-1",
            ActionValue = "yes",
            CorrelationId = "trace",
            Messenger = "telegram",
            ExternalUserId = "1",
            ExternalMessageId = "msg-1",
            ReceivedAt = DateTimeOffset.UtcNow,
        };

        await bus.PublishHumanDecisionAsync(decision, default);
    }

    [Fact]
    public async Task QueryStatusAsync_EchoesWorkspaceAndReturnsStubMarker()
    {
        var bus = new StubSwarmCommandBus(NullLogger<StubSwarmCommandBus>.Instance);
        var query = new SwarmStatusQuery { WorkspaceId = "w-1" };

        var result = await bus.QueryStatusAsync(query, default);

        result.WorkspaceId.Should().Be("w-1");
        result.State.Should().Be("stub");
    }

    [Fact]
    public async Task QueryAgentsAsync_ReturnsEmpty()
    {
        var bus = new StubSwarmCommandBus(NullLogger<StubSwarmCommandBus>.Instance);
        var result = await bus.QueryAgentsAsync(new SwarmAgentsQuery { WorkspaceId = "w-1" }, default);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SubscribeAsync_ReturnsEmptyStreamThatCompletes()
    {
        var bus = new StubSwarmCommandBus(NullLogger<StubSwarmCommandBus>.Instance);

        var consumed = 0;
        await foreach (var _ in bus.SubscribeAsync("t-1", default))
        {
            consumed++;
        }

        consumed.Should().Be(0);
    }

    [Fact]
    public async Task SubscribeAsync_BlankTenant_Throws()
    {
        var bus = new StubSwarmCommandBus(NullLogger<StubSwarmCommandBus>.Instance);
        var act = async () =>
        {
            await foreach (var _ in bus.SubscribeAsync("", default))
            {
            }
        };
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
