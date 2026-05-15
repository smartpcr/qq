using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task AcknowledgeAsync_HonorsCancellation()
    {
        var outbox = CreateOutbox();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => outbox.AcknowledgeAsync("outbox-1", cts.Token));
    }

    [Fact]
    public async Task DeadLetterAsync_HonorsCancellation()
    {
        var outbox = CreateOutbox();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => outbox.DeadLetterAsync("outbox-1", "test failure", cts.Token));
    }

    [Fact]
    public async Task DeadLetterAsync_EmitsWarningLogContainingEntryIdAndError()
    {
        var logger = new CapturingLogger<NoOpMessageOutbox>();
        var outbox = new NoOpMessageOutbox(logger);

        await outbox.DeadLetterAsync("outbox-abc", "boom — 429 Too Many Requests", CancellationToken.None);

        var warning = Assert.Single(logger.Entries.Where(e => e.Level == LogLevel.Warning));
        Assert.Contains("outbox-abc", warning.Message, StringComparison.Ordinal);
        Assert.Contains("boom", warning.Message, StringComparison.Ordinal);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
