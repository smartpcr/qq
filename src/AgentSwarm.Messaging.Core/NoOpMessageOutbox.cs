using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Core;

/// <summary>
/// No-op <see cref="IMessageOutbox"/> stub used as the pre-Stage 6.1 placeholder so that DI
/// composition (registered in Stage 2.1) can complete before the SQL-backed implementation
/// lands. <see cref="EnqueueAsync"/>, <see cref="DequeueAsync"/>, and
/// <see cref="AcknowledgeAsync"/> simply complete without side effects.
/// <see cref="DeadLetterAsync"/> emits a warning-level log entry so operators are notified
/// when messages are dropped — preventing silent loss while the production outbox is being
/// wired in.
/// </summary>
/// <remarks>
/// Stage 6.1 replaces this stub with <c>SqlMessageOutbox</c> via DI override.
/// </remarks>
public sealed class NoOpMessageOutbox : IMessageOutbox
{
    private readonly ILogger<NoOpMessageOutbox> _logger;

    /// <summary>
    /// Initialize a new <see cref="NoOpMessageOutbox"/>.
    /// </summary>
    /// <param name="logger">Logger used to record dead-letter notifications.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="logger"/> is <c>null</c>.</exception>
    public NoOpMessageOutbox(ILogger<NoOpMessageOutbox> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task EnqueueAsync(OutboxEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int batchSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
    }

    /// <inheritdoc />
    public Task AcknowledgeAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecordSendReceiptAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RescheduleAsync(string outboxEntryId, DateTimeOffset nextRetryAt, string error, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeadLetterAsync(string outboxEntryId, string error, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _logger.LogWarning(
            "NoOpMessageOutbox.DeadLetterAsync called for entry {OutboxEntryId} with error: {Error}. " +
            "Replace NoOpMessageOutbox with a durable IMessageOutbox implementation (Stage 6.1 wires SqlMessageOutbox) to persist dead-lettered entries.",
            outboxEntryId,
            error);
        return Task.CompletedTask;
    }
}
