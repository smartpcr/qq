using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Telegram.Swarm;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.7 — locks the <see cref="StubTaskOversightRepository"/>
/// contract: reads return null/empty so the
/// <c>SwarmEventSubscriptionService</c> falls back to broadcast /
/// workspace-default routing; writes succeed but are discarded.
/// </summary>
public class StubTaskOversightRepositoryTests
{
    [Fact]
    public async Task GetByTaskId_AlwaysReturnsNull()
    {
        var repo = new StubTaskOversightRepository();
        var result = await repo.GetByTaskIdAsync("TASK-1", default);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByTaskId_BlankTaskId_Throws()
    {
        var repo = new StubTaskOversightRepository();
        await Assert.ThrowsAsync<ArgumentException>(() => repo.GetByTaskIdAsync(" ", default));
    }

    [Fact]
    public async Task UpsertAsync_DiscardsButReadStillReturnsNull()
    {
        var repo = new StubTaskOversightRepository();
        await repo.UpsertAsync(new TaskOversight
        {
            TaskId = "TASK-1",
            OperatorBindingId = Guid.NewGuid(),
            AssignedAt = DateTimeOffset.UtcNow,
            AssignedBy = "@op",
            CorrelationId = "trace-1",
        }, default);

        // Discarded — the stub's read returns null even after upsert.
        (await repo.GetByTaskIdAsync("TASK-1", default)).Should().BeNull();
    }

    [Fact]
    public async Task GetByOperator_AlwaysReturnsEmpty()
    {
        var repo = new StubTaskOversightRepository();
        var result = await repo.GetByOperatorAsync(Guid.NewGuid(), default);
        result.Should().BeEmpty();
    }
}
