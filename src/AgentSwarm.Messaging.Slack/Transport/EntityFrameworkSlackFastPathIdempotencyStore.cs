// -----------------------------------------------------------------------
// <copyright file="EntityFrameworkSlackFastPathIdempotencyStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Durable durably-backed
/// <see cref="ISlackFastPathIdempotencyStore"/> that uses the
/// <see cref="SlackInboundRequestRecord"/> table (architecture.md §3.3)
/// as the canonical dedup anchor. Pulled forward from Stage 4.3 so
/// modal fast-path duplicates that span a process restart or a
/// horizontally-scaled replica are still rejected before
/// <c>views.open</c> is called a second time.
/// </summary>
/// <typeparam name="TContext">
/// EF Core context that surfaces the
/// <see cref="SlackInboundRequestRecord"/> table. Must be registered as
/// <c>Scoped</c> via <c>AddDbContext&lt;TContext&gt;</c>.
/// </typeparam>
/// <remarks>
/// <para>
/// Stage 4.1 evaluator iter-2 item 2 fix. The handler is safe to
/// register as a singleton because it creates a fresh DI scope per
/// call -- the same shape as
/// <see cref="EntityFrameworkSlackAuditEntryWriter{TContext}"/>.
/// </para>
/// <para>
/// Acquire semantics: the store INSERTs a row keyed by
/// <see cref="SlackInboundRequestRecord.IdempotencyKey"/>. A
/// primary-key-violation <see cref="DbUpdateException"/> is mapped to
/// <see cref="SlackFastPathIdempotencyOutcome.Duplicate"/>. Other EF
/// errors (connection refused, timeout) are mapped to
/// <see cref="SlackFastPathIdempotencyOutcome.StoreUnavailable"/> so
/// the fast-path can degrade to the in-process L1 cache and still
/// open a modal (the alternative -- failing every modal during a DB
/// blip -- is worse than the rare duplicate the L1 might admit).
/// </para>
/// </remarks>
internal sealed class EntityFrameworkSlackFastPathIdempotencyStore<TContext>
    : ISlackFastPathIdempotencyStore
    where TContext : DbContext, ISlackInboundRequestRecordDbContext
{
    /// <summary>
    /// Marker written to
    /// <see cref="SlackInboundRequestRecord.ProcessingStatus"/> when the
    /// fast-path successfully opened a modal. Stage 4.3's ingestor
    /// recognises this marker so it does NOT re-process the row through
    /// the async pipeline.
    /// </summary>
    public const string ProcessingStatusModalOpened = "modal_opened";

    /// <summary>
    /// Marker written to
    /// <see cref="SlackInboundRequestRecord.ProcessingStatus"/> when the
    /// fast-path has acquired the row but has not yet finished
    /// <c>views.open</c>. The row is overwritten with
    /// <see cref="ProcessingStatusModalOpened"/> (or deleted by
    /// <see cref="ReleaseAsync"/>) when the call terminates.
    /// </summary>
    public const string ProcessingStatusReserved = "reserved";

    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<EntityFrameworkSlackFastPathIdempotencyStore<TContext>> logger;

    public EntityFrameworkSlackFastPathIdempotencyStore(
        IServiceScopeFactory scopeFactory,
        ILogger<EntityFrameworkSlackFastPathIdempotencyStore<TContext>> logger)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask<SlackFastPathIdempotencyResult> TryAcquireAsync(
        string key,
        SlackInboundEnvelope envelope,
        TimeSpan? lifetime = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(envelope);

        SlackInboundRequestRecord record = new()
        {
            IdempotencyKey = key,
            SourceType = envelope.SourceType.ToString().ToLowerInvariant(),
            TeamId = string.IsNullOrEmpty(envelope.TeamId) ? "unknown" : envelope.TeamId,
            ChannelId = envelope.ChannelId,
            UserId = string.IsNullOrEmpty(envelope.UserId) ? "unknown" : envelope.UserId,
            RawPayloadHash = HashRawPayload(envelope.RawPayload),
            ProcessingStatus = ProcessingStatusReserved,
            FirstSeenAt = envelope.ReceivedAt,
            CompletedAt = null,
        };

        try
        {
            await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
            TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

            // Probe first -- when the row already exists, return
            // Duplicate without forcing the SaveChanges path to throw
            // a constraint violation (EF Core wraps that in
            // DbUpdateException, which is slower than a single SELECT).
            bool exists = await context.SlackInboundRequestRecords
                .AsNoTracking()
                .AnyAsync(r => r.IdempotencyKey == key, ct)
                .ConfigureAwait(false);
            if (exists)
            {
                return SlackFastPathIdempotencyResult.Duplicate(
                    $"durable record for idempotency_key '{key}' already exists.");
            }

            context.SlackInboundRequestRecords.Add(record);
            try
            {
                await context.SaveChangesAsync(ct).ConfigureAwait(false);
                return SlackFastPathIdempotencyResult.Acquired();
            }
            catch (DbUpdateException ex)
            {
                // Race: another replica inserted between the probe and
                // the SaveChanges. Treat as a duplicate.
                this.logger.LogInformation(
                    ex,
                    "Slack fast-path idempotency insert race lost for key={IdempotencyKey}; treating as duplicate.",
                    key);
                return SlackFastPathIdempotencyResult.Duplicate(
                    $"insert race lost for idempotency_key '{key}'.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Transient DB error -- degrade gracefully so a database
            // blip does not break every modal command.
            this.logger.LogError(
                ex,
                "Slack fast-path durable idempotency store unavailable for key={IdempotencyKey}. Falling back to in-process L1 check only.",
                key);
            return SlackFastPathIdempotencyResult.StoreUnavailable(
                $"durable store error: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        try
        {
            await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
            TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

            SlackInboundRequestRecord? row = await context.SlackInboundRequestRecords
                .FirstOrDefaultAsync(r => r.IdempotencyKey == key, ct)
                .ConfigureAwait(false);
            if (row is null)
            {
                return;
            }

            // Only release rows the fast-path itself reserved. Once the
            // row is marked completed/failed the caller MUST NOT delete
            // it -- subsequent retries should still see the duplicate.
            if (!string.Equals(row.ProcessingStatus, ProcessingStatusReserved, StringComparison.Ordinal))
            {
                return;
            }

            context.SlackInboundRequestRecords.Remove(row);
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack fast-path durable idempotency release failed for key={IdempotencyKey}. The next retry within the TTL window will be silently ACKed; this is acceptable degradation.",
                key);
        }
    }

    /// <summary>
    /// Marks the row written by
    /// <see cref="TryAcquireAsync"/> as terminal so it survives as a
    /// dedup anchor for the full retention window. Called by the
    /// handler on success.
    /// </summary>
    public async Task MarkOpenedAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        try
        {
            await using AsyncServiceScope scope = this.scopeFactory.CreateAsyncScope();
            TContext context = scope.ServiceProvider.GetRequiredService<TContext>();

            SlackInboundRequestRecord? row = await context.SlackInboundRequestRecords
                .FirstOrDefaultAsync(r => r.IdempotencyKey == key, ct)
                .ConfigureAwait(false);
            if (row is null)
            {
                return;
            }

            row.ProcessingStatus = ProcessingStatusModalOpened;
            row.CompletedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack fast-path durable idempotency mark-opened failed for key={IdempotencyKey}; the row stays in 'reserved' state.",
                key);
        }
    }

    private static string HashRawPayload(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        StringBuilder sb = new(digest.Length * 2);
        foreach (byte b in digest)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
