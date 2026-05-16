using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Worker;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 2.4 — pins <see cref="InboundUpdateRecoveryStartup"/>: a single
/// bulk UPDATE that reverts every crash-stuck Processing row back to
/// Received, run BEFORE the dispatcher and recovery-sweep hosted
/// services. The startup ordering is guarded by registration order in
/// <see cref="Worker.Program"/>; this test pins the StartAsync side
/// effect itself.
/// </summary>
public sealed class InboundUpdateRecoveryStartupTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MessagingDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IInboundUpdateStore, PersistentInboundUpdateStore>();
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task SeedAsync(long id, IdempotencyStatus status)
    {
        await using var scope = _services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IInboundUpdateStore>();
        var row = new InboundUpdate
        {
            UpdateId = id,
            RawPayload = "{\"update_id\":" + id + "}",
            ReceivedAt = DateTimeOffset.UtcNow,
            IdempotencyStatus = IdempotencyStatus.Received,
        };
        await store.PersistAsync(row, CancellationToken.None);
        if (status == IdempotencyStatus.Processing)
        {
            await store.TryMarkProcessingAsync(id, CancellationToken.None);
        }
        else if (status == IdempotencyStatus.Completed)
        {
            await store.TryMarkProcessingAsync(id, CancellationToken.None);
            await store.MarkCompletedAsync(id, null, CancellationToken.None);
        }
        else if (status == IdempotencyStatus.Failed)
        {
            await store.MarkFailedAsync(id, "seed-fail", CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_FlipsProcessingRowsToReceived_LeavesOthersAlone()
    {
        await SeedAsync(1, IdempotencyStatus.Received);
        await SeedAsync(2, IdempotencyStatus.Processing);
        await SeedAsync(3, IdempotencyStatus.Completed);
        await SeedAsync(4, IdempotencyStatus.Failed);
        await SeedAsync(5, IdempotencyStatus.Processing);

        var startup = new InboundUpdateRecoveryStartup(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InboundUpdateRecoveryStartup>.Instance);

        await startup.StartAsync(CancellationToken.None);

        await using var scope = _services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IInboundUpdateStore>();

        (await store.GetByUpdateIdAsync(1, CancellationToken.None))!.IdempotencyStatus
            .Should().Be(IdempotencyStatus.Received);
        (await store.GetByUpdateIdAsync(2, CancellationToken.None))!.IdempotencyStatus
            .Should().Be(IdempotencyStatus.Received, "Processing row 2 must be released for next-sweep reclaim");
        (await store.GetByUpdateIdAsync(3, CancellationToken.None))!.IdempotencyStatus
            .Should().Be(IdempotencyStatus.Completed);
        (await store.GetByUpdateIdAsync(4, CancellationToken.None))!.IdempotencyStatus
            .Should().Be(IdempotencyStatus.Failed);
        (await store.GetByUpdateIdAsync(5, CancellationToken.None))!.IdempotencyStatus
            .Should().Be(IdempotencyStatus.Received, "Processing row 5 must be released for next-sweep reclaim");
    }

    [Fact]
    public async Task StartAsync_OnEmptyTable_DoesNotThrow()
    {
        var startup = new InboundUpdateRecoveryStartup(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InboundUpdateRecoveryStartup>.Instance);

        var act = () => startup.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StopAsync_IsNoOp()
    {
        var startup = new InboundUpdateRecoveryStartup(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InboundUpdateRecoveryStartup>.Instance);

        var act = () => startup.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
