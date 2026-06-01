// -----------------------------------------------------------------------
// <copyright file="SlackSocketModeBackoffPolicy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;

/// <summary>
/// Computes the wait between Socket Mode reconnect attempts using
/// exponential backoff with full jitter, capped at
/// <see cref="SlackSocketModeOptions.MaxReconnectDelay"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// requires "exponential backoff (initial 1s, max 30s) and jitter on
/// WebSocket disconnection". Full-jitter is the AWS recommended schedule
/// (<see href="https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter/"/>):
/// <c>delay = uniform(0, min(max, base * 2^(attempt-1)))</c>.
/// </para>
/// <para>
/// The policy is intentionally allocation-free on the hot path: it takes
/// the random source as a parameter so unit tests can pin a
/// deterministic sequence while the runtime falls back to
/// <see cref="Random.Shared"/>.
/// </para>
/// </remarks>
internal sealed class SlackSocketModeBackoffPolicy
{
    private readonly TimeSpan initialDelay;
    private readonly TimeSpan maxDelay;

    public SlackSocketModeBackoffPolicy(SlackSocketModeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.InitialReconnectDelay <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{nameof(SlackSocketModeOptions.InitialReconnectDelay)} must be positive.",
                nameof(options));
        }

        if (options.MaxReconnectDelay < options.InitialReconnectDelay)
        {
            throw new ArgumentException(
                $"{nameof(SlackSocketModeOptions.MaxReconnectDelay)} must be greater than or equal to {nameof(SlackSocketModeOptions.InitialReconnectDelay)}.",
                nameof(options));
        }

        this.initialDelay = options.InitialReconnectDelay;
        this.maxDelay = options.MaxReconnectDelay;
    }

    /// <summary>
    /// Returns the delay for the supplied reconnection
    /// <paramref name="attempt"/> (1-based). The first reconnect
    /// attempt (<c>attempt == 1</c>) uses the base
    /// <see cref="SlackSocketModeOptions.InitialReconnectDelay"/>
    /// as the upper jitter bound; subsequent attempts double the
    /// upper bound until it hits
    /// <see cref="SlackSocketModeOptions.MaxReconnectDelay"/>.
    /// </summary>
    /// <param name="attempt">1-based attempt counter. Values
    /// less than 1 are treated as 1.</param>
    /// <param name="random">Optional random source. Defaults to
    /// <see cref="Random.Shared"/>.</param>
    public TimeSpan ComputeDelay(int attempt, Random? random = null)
    {
        if (attempt < 1)
        {
            attempt = 1;
        }

        // Compute the exponential ceiling in milliseconds, taking care
        // not to overflow when the operator configured a long max delay
        // and a high attempt counter.
        double baseMs = this.initialDelay.TotalMilliseconds;
        double maxMs = this.maxDelay.TotalMilliseconds;
        int safeShift = attempt - 1;
        if (safeShift > 30)
        {
            // 2^30 milliseconds is already ~12 days; clamp before we
            // accidentally double.PositiveInfinity ourselves.
            safeShift = 30;
        }

        double exponentialCeilingMs = baseMs * Math.Pow(2.0, safeShift);
        double ceilingMs = Math.Min(maxMs, exponentialCeilingMs);

        Random rng = random ?? Random.Shared;
        double jitteredMs = rng.NextDouble() * ceilingMs;
        return TimeSpan.FromMilliseconds(jitteredMs);
    }

    /// <summary>
    /// Returns the upper jitter bound for the supplied
    /// <paramref name="attempt"/>, before random sampling. Exposed
    /// for unit tests that pin the ceiling without exercising the
    /// random component.
    /// </summary>
    public TimeSpan ComputeCeiling(int attempt)
    {
        if (attempt < 1)
        {
            attempt = 1;
        }

        int safeShift = attempt - 1;
        if (safeShift > 30)
        {
            safeShift = 30;
        }

        double exponentialCeilingMs = this.initialDelay.TotalMilliseconds * Math.Pow(2.0, safeShift);
        double ceilingMs = Math.Min(this.maxDelay.TotalMilliseconds, exponentialCeilingMs);
        return TimeSpan.FromMilliseconds(ceilingMs);
    }
}
