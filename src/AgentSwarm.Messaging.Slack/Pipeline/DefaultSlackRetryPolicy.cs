// -----------------------------------------------------------------------
// <copyright file="DefaultSlackRetryPolicy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Retry;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="ISlackRetryPolicy"/> backed by
/// <see cref="SlackRetryOptions"/>. Implements bounded exponential
/// backoff with a hard cap derived from
/// <see cref="SlackRetryOptions.MaxDelaySeconds"/>; treats
/// <see cref="OperationCanceledException"/> as terminal so a stopping
/// ingestor does not spin on retries.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The Stage 1.3 compile stub declared only the interface; the policy
/// implementation lands here so the Stage 4.3 ingestor has a working
/// retry knob (Stage 4.2 will reuse this policy for the outbound
/// dispatcher).
/// </para>
/// <para>
/// Backoff formula: <c>InitialDelayMilliseconds * 2^(attemptNumber-1)</c>,
/// capped at <see cref="SlackRetryOptions.MaxDelaySeconds"/>. Attempt
/// 1 waits <c>InitialDelayMilliseconds</c>, attempt 2 waits twice
/// that, and so on. The implementation never returns a negative
/// delay (a misconfigured options object with
/// <see cref="SlackRetryOptions.InitialDelayMilliseconds"/> = 0 will
/// produce <see cref="TimeSpan.Zero"/>; the host's options validation
/// already rejects negative values at start-up).
/// </para>
/// </remarks>
internal sealed class DefaultSlackRetryPolicy : ISlackRetryPolicy
{
    private readonly IOptionsMonitor<SlackConnectorOptions> optionsMonitor;

    public DefaultSlackRetryPolicy(IOptionsMonitor<SlackConnectorOptions> optionsMonitor)
    {
        this.optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
    }

    /// <inheritdoc />
    public bool ShouldRetry(int attemptNumber, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // OperationCanceledException always means the ingestor is
        // shutting down -- retrying would block clean shutdown and
        // produce false-positive transient-failure metrics.
        if (exception is OperationCanceledException)
        {
            return false;
        }

        if (attemptNumber < 1)
        {
            return false;
        }

        int maxAttempts = this.optionsMonitor.CurrentValue.Retry.MaxAttempts;
        return attemptNumber < maxAttempts;
    }

    /// <inheritdoc />
    public TimeSpan GetDelay(int attemptNumber)
    {
        if (attemptNumber < 1)
        {
            return TimeSpan.Zero;
        }

        SlackRetryOptions retry = this.optionsMonitor.CurrentValue.Retry;
        double initialMs = Math.Max(0, retry.InitialDelayMilliseconds);
        double capMs = Math.Max(initialMs, retry.MaxDelaySeconds * 1000.0);

        // 2^(attemptNumber-1) grows quickly; clamp before multiplying
        // to avoid double overflow on pathologically high attempt
        // numbers. 30 is enough headroom -- 2^30 * 200ms is already
        // ~6 days, far beyond the configured cap.
        int shift = Math.Min(attemptNumber - 1, 30);
        double rawMs = initialMs * Math.Pow(2, shift);
        double clamped = Math.Min(rawMs, capMs);

        return TimeSpan.FromMilliseconds(clamped);
    }
}
