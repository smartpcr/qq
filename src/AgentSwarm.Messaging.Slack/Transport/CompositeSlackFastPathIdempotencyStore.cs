// -----------------------------------------------------------------------
// <copyright file="CompositeSlackFastPathIdempotencyStore.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// Two-level idempotency store composed of a fast in-process L1 cache
/// (catches sub-second retries from the same replica) and a durable
/// L2 backend (catches retries that span a restart or arrive at a
/// different replica). The composite returns
/// <see cref="SlackFastPathIdempotencyOutcome.Duplicate"/> the moment
/// EITHER level reports a duplicate and only returns
/// <see cref="SlackFastPathIdempotencyOutcome.Acquired"/> when BOTH
/// agree the key is new.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.1 evaluator iter-2 item 2 fix. The composite is what the
/// production Worker registers; tests and dev hosts that do not have
/// an EF context can register
/// <see cref="SlackInProcessIdempotencyStore"/> directly as the
/// <see cref="ISlackFastPathIdempotencyStore"/> binding.
/// </para>
/// <para>
/// When the L2 backend reports
/// <see cref="SlackFastPathIdempotencyOutcome.StoreUnavailable"/> the
/// composite still returns <see cref="SlackFastPathIdempotencyOutcome.Acquired"/>
/// (degraded mode: L1 only) but propagates the diagnostic so the
/// handler logs the degradation.
/// </para>
/// </remarks>
internal sealed class CompositeSlackFastPathIdempotencyStore : ISlackFastPathIdempotencyStore
{
    private readonly SlackInProcessIdempotencyStore l1;
    private readonly ISlackFastPathIdempotencyStore l2;
    private readonly ILogger<CompositeSlackFastPathIdempotencyStore> logger;

    public CompositeSlackFastPathIdempotencyStore(
        SlackInProcessIdempotencyStore l1,
        ISlackFastPathIdempotencyStore l2,
        ILogger<CompositeSlackFastPathIdempotencyStore> logger)
    {
        this.l1 = l1 ?? throw new ArgumentNullException(nameof(l1));
        this.l2 = l2 ?? throw new ArgumentNullException(nameof(l2));
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

        // L1: in-process, ~microseconds. Reject the obvious double-click
        // before paying for a DB round-trip.
        bool l1Acquired = await this.l1
            .TryAcquireAsync(key, lifetime, ct)
            .ConfigureAwait(false);
        if (!l1Acquired)
        {
            return SlackFastPathIdempotencyResult.Duplicate(
                $"in-process L1 cache reports key '{key}' is already held.");
        }

        // L2: durable, catches cross-process / cross-restart retries.
        SlackFastPathIdempotencyResult l2Result;
        try
        {
            l2Result = await this.l2
                .TryAcquireAsync(key, envelope, lifetime, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Defensive: never let an L2 exception escape -- the L1 token
            // would leak. Release and rethrow so the caller can decide.
            this.l1.Release(key);
            throw;
        }

        if (l2Result.IsDuplicate)
        {
            // L1 said new but L2 had the row -- another replica won.
            // Release the L1 token so a future retry that arrives after
            // the L2 row expires can succeed.
            this.l1.Release(key);
            return l2Result;
        }

        if (l2Result.Outcome == SlackFastPathIdempotencyOutcome.StoreUnavailable)
        {
            this.logger.LogWarning(
                "Slack fast-path durable idempotency store unavailable for key={IdempotencyKey} ({Diagnostic}); proceeding with L1-only dedup.",
                key,
                l2Result.Diagnostic);
            return SlackFastPathIdempotencyResult.Acquired();
        }

        return SlackFastPathIdempotencyResult.Acquired();
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        // Release L1 unconditionally so a retry can proceed even if the
        // L2 release throws.
        this.l1.Release(key);

        try
        {
            await this.l2.ReleaseAsync(key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                "Slack fast-path durable idempotency release failed for key={IdempotencyKey}; L1 token has been released so retries within the L2 TTL window may be ACKed as duplicates until the durable row expires.",
                key);
        }
    }
}
