using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.2 — round-trip tests for
/// <see cref="PersistentTaskOversightRepository"/> against an
/// in-memory SQLite connection using the real
/// <see cref="MessagingDbContext"/> schema (so the
/// <see cref="TaskOversightConfiguration"/>-defined indexes and
/// value converters all run).
/// </summary>
public sealed class PersistentTaskOversightRepositoryTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private PersistentTaskOversightRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<MessagingDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        await using (var scope = _provider.CreateAsyncScope())
        await using (var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _repo = new PersistentTaskOversightRepository(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PersistentTaskOversightRepository>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task UpsertAsync_PersistsNewRow_AndGetByTaskIdReturnsIt()
    {
        var oversight = new TaskOversight
        {
            TaskId = "TASK-1",
            OperatorBindingId = Guid.NewGuid(),
            AssignedAt = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero),
            AssignedBy = "@operator-1",
            CorrelationId = "trace-1",
        };

        await _repo.UpsertAsync(oversight, default);

        var loaded = await _repo.GetByTaskIdAsync("TASK-1", default);
        loaded.Should().NotBeNull();
        loaded!.TaskId.Should().Be("TASK-1");
        loaded.OperatorBindingId.Should().Be(oversight.OperatorBindingId);
        loaded.AssignedBy.Should().Be("@operator-1");
        loaded.AssignedAt.Should().Be(oversight.AssignedAt);
        loaded.CorrelationId.Should().Be("trace-1");
    }

    [Fact]
    public async Task UpsertAsync_ExistingTask_UpdatesRowInPlace()
    {
        var initial = new TaskOversight
        {
            TaskId = "TASK-2",
            OperatorBindingId = Guid.NewGuid(),
            AssignedAt = DateTimeOffset.UtcNow.AddDays(-1),
            AssignedBy = "@operator-1",
            CorrelationId = "trace-initial",
        };
        await _repo.UpsertAsync(initial, default);

        var newOwner = Guid.NewGuid();
        var transferred = initial with
        {
            OperatorBindingId = newOwner,
            AssignedAt = DateTimeOffset.UtcNow,
            AssignedBy = "@operator-2",
            CorrelationId = "trace-transferred",
        };
        await _repo.UpsertAsync(transferred, default);

        var loaded = await _repo.GetByTaskIdAsync("TASK-2", default);
        loaded!.OperatorBindingId.Should().Be(newOwner);
        loaded.AssignedBy.Should().Be("@operator-2");
        loaded.CorrelationId.Should().Be("trace-transferred");

        // Still one row — handoff updates, not inserts.
        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        (await ctx.TaskOversights.CountAsync(x => x.TaskId == "TASK-2")).Should().Be(1);
    }

    [Fact]
    public async Task GetByTaskIdAsync_UnknownTask_ReturnsNull()
    {
        (await _repo.GetByTaskIdAsync("UNKNOWN", default)).Should().BeNull();
    }

    [Fact]
    public async Task GetByOperatorAsync_ReturnsOnlyOperatorOwnedTasks()
    {
        var operatorA = Guid.NewGuid();
        var operatorB = Guid.NewGuid();
        await _repo.UpsertAsync(NewOversight("TA-A1", operatorA), default);
        await _repo.UpsertAsync(NewOversight("TA-A2", operatorA), default);
        await _repo.UpsertAsync(NewOversight("TA-B1", operatorB), default);

        var ownedByA = await _repo.GetByOperatorAsync(operatorA, default);
        ownedByA.Select(x => x.TaskId).Should().BeEquivalentTo(new[] { "TA-A1", "TA-A2" });

        var ownedByB = await _repo.GetByOperatorAsync(operatorB, default);
        ownedByB.Select(x => x.TaskId).Should().BeEquivalentTo(new[] { "TA-B1" });
    }

    private static TaskOversight NewOversight(string taskId, Guid operatorId) => new()
    {
        TaskId = taskId,
        OperatorBindingId = operatorId,
        AssignedAt = DateTimeOffset.UtcNow,
        AssignedBy = "@assigner",
        CorrelationId = "trace-" + taskId,
    };
}
