using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Core.Tests;

/// <summary>
/// Behavioural checks for the <see cref="NoOpMessageOutbox"/> stub registered as the
/// pre-Stage 6.1 placeholder. Every method must complete without throwing, must honour
/// cancellation, and <see cref="NoOpMessageOutbox.DequeueAsync"/> must return an empty list
/// (rather than the previously incorrect <c>Task.CompletedTask</c> noted in design review).
/// </summary>
public sealed class NoOpMessageOutboxTests
{
    private static NoOpMessageOutbox CreateOutbox()
        => new(NullLogger<NoOpMessageOutbox>.Instance);

    private static OutboxEntry SampleEntry() => new()
    {
        OutboxEntryId = "outbox-1",
        CorrelationId = "corr-1",
        Destination = "teams://tenant/user/u1",
        PayloadType = OutboxPayloadTypes.MessengerMessage,
        PayloadJson = "{}",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task EnqueueAsync_CompletesWithoutThrowing()
    {
        var outbox = CreateOutbox();
        await outbox.EnqueueAsync(SampleEntry(), CancellationToken.None);
    }

    [Fact]
    public async Task DequeueAsync_ReturnsEmptyList()
    {
        var outbox = CreateOutbox();

        var result = await outbox.DequeueAsync(batchSize: 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task AcknowledgeAsync_CompletesWithoutThrowing()
    {
        var outbox = CreateOutbox();
        await outbox.AcknowledgeAsync("outbox-1", CancellationToken.None);
    }

    [Fact]
    public async Task DeadLetterAsync_CompletesWithoutThrowing()
    {
        var outbox = CreateOutbox();
        await outbox.DeadLetterAsync("outbox-1", "test failure", CancellationToken.None);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenLoggerIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new NoOpMessageOutbox(null!));
    }

    [Fact]
    public async Task EnqueueAsync_HonorsCancellation()
    {
        var outbox = CreateOutbox();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => outbox.EnqueueAsync(SampleEntry(), cts.Token));
    }

    [Fact]
    public async Task DequeueAsync_HonorsCancellation()
    {
        var outbox = CreateOutbox();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => outbox.DequeueAsync(1, cts.Token));
    }
}
