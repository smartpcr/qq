using AgentSwarm.Messaging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Persistence;

/// <summary>
/// EF Core / SQLite-backed <see cref="IMessageIdTracker"/>. Persists the
/// (<c>ChatId</c>, <c>TelegramMessageId</c>) → <c>CorrelationId</c>
/// mapping into the <c>OutboundMessageIdMapping</c> table so that a
/// reply received after a process restart can still be threaded back to
/// the originating trace.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton by <c>AddMessagingPersistence</c>. Because
/// <see cref="MessagingDbContext"/> is scoped, the tracker bridges the
/// lifetime mismatch via <see cref="IServiceScopeFactory"/> — every
/// call opens a short-lived scope, resolves the context, performs the
/// upsert/lookup, and disposes the scope.
/// </para>
/// <para>
/// <b>Best-effort semantics with bounded inline retries.</b> Per the
/// <see cref="IMessageIdTracker"/> contract, <see cref="TrackAsync"/>
/// MUST NOT throw on persistence failure. The implementation performs
/// up to <see cref="MaxAttempts"/> attempts with exponential backoff
/// (100 ms / 500 ms / 2 s) and, on persistent failure, logs an
/// <see cref="LogLevel.Error"/> structured event and suppresses the
/// exception. Operational alerting on the structured log message is
/// the operator's recourse for prolonged DB outages. The canonical
/// durable record of every send is the Stage 4.1 <c>OutboundMessage</c>
/// row (<c>CorrelationId</c> + <c>TelegramMessageId</c>); this tracker
/// table is a supplementary fast-lookup index, and a future Stage 5.x
/// reply-correlator may reconcile gaps via the <c>OutboundMessage</c>
/// backfill.
/// </para>
/// <para>
/// <b>Upsert semantics.</b> <see cref="TrackAsync"/> uses
/// <c>FindAsync</c>+<c>SaveChangesAsync</c> so a re-write for the same
/// composite key updates the existing row in place rather than throwing
/// a unique-key violation. Telegram never re-issues a <c>message_id</c>
/// within a chat, so the only way to hit the upsert path is a retry of
/// the same (chat, message id) pair — which is the desired idempotent
/// behaviour.
/// </para>
/// </remarks>
public sealed class PersistentMessageIdTracker : IMessageIdTracker
{
    /// <summary>
    /// Maximum inline attempts before <see cref="TrackAsync"/> gives up
    /// and logs+suppresses. Includes the initial attempt — i.e. one
    /// initial try plus <c>MaxAttempts - 1</c> retries.
    /// </summary>
    public const int MaxAttempts = 3;

    private static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(2),
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PersistentMessageIdTracker> _logger;

    public PersistentMessageIdTracker(
        IServiceScopeFactory scopeFactory,
        ILogger<PersistentMessageIdTracker> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task TrackAsync(
        long chatId,
        long telegramMessageId,
        string correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException(
                "CorrelationId must be non-null, non-empty, and non-whitespace.",
                nameof(correlationId));
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await WriteOnceAsync(chatId, telegramMessageId, correlationId, ct)
                    .ConfigureAwait(false);
                if (attempt > 1)
                {
                    _logger.LogInformation(
                        "PersistentMessageIdTracker recovered on attempt {Attempt} for chat {ChatId}, message {MessageId}.",
                        attempt,
                        chatId,
                        telegramMessageId);
                }
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation is the only exception we let escape, per
                // the IMessageIdTracker contract.
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < MaxAttempts)
                {
                    var backoff = BackoffSchedule[attempt - 1];
                    _logger.LogWarning(
                        ex,
                        "PersistentMessageIdTracker write failed on attempt {Attempt}/{MaxAttempts} for chat {ChatId}, message {MessageId}; retrying in {BackoffMs} ms.",
                        attempt,
                        MaxAttempts,
                        chatId,
                        telegramMessageId,
                        backoff.TotalMilliseconds);
                    try
                    {
                        await Task.Delay(backoff, _timeProvider, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                }
            }
        }

        // Persistent failure across all attempts. Per the
        // IMessageIdTracker contract we log + suppress; the upstream
        // Telegram send has already succeeded, so propagating would
        // cause a duplicate operator-visible message on retry.
        _logger.LogError(
            lastError,
            "PersistentMessageIdTracker exhausted {MaxAttempts} attempts for chat {ChatId}, message {MessageId}, correlation {CorrelationId}; suppressing per best-effort tracker contract. The Stage 4.1 OutboundMessage row remains the canonical durable record of this send.",
            MaxAttempts,
            chatId,
            telegramMessageId,
            correlationId);
    }

    public async Task<string?> TryGetCorrelationIdAsync(
        long chatId,
        long telegramMessageId,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var row = await db.OutboundMessageIdMappings
            .FindAsync(new object[] { chatId, telegramMessageId }, ct)
            .ConfigureAwait(false);
        return row?.CorrelationId;
    }

    private async Task WriteOnceAsync(
        long chatId,
        long telegramMessageId,
        string correlationId,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();

        var existing = await db.OutboundMessageIdMappings
            .FindAsync(new object[] { chatId, telegramMessageId }, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.OutboundMessageIdMappings.Add(new OutboundMessageIdMapping
            {
                ChatId = chatId,
                TelegramMessageId = telegramMessageId,
                CorrelationId = correlationId,
                RecordedAt = _timeProvider.GetUtcNow(),
            });
        }
        else
        {
            existing.CorrelationId = correlationId;
            existing.RecordedAt = _timeProvider.GetUtcNow();
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
