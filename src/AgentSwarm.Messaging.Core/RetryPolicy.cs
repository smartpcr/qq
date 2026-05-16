// -----------------------------------------------------------------------
// <copyright file="RetryPolicy.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core;

using System;

/// <summary>
/// Stage 4.2 — canonical retry policy POCO that drives the
/// <c>OutboundQueueProcessor</c>'s exponential-backoff-with-jitter
/// re-enqueue path and the <c>PersistentOutboundQueue</c> /
/// <c>InMemoryOutboundQueue</c> retry scheduling. Defaults are
/// aligned with architecture.md §5.3 and e2e-scenarios.md "max 5
/// attempts" footer per implementation-plan.md Stage 4.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Property defaults (architecture.md §5.3 alignment).</b>
/// <list type="bullet">
///   <item><description>
///   <see cref="MaxAttempts"/> = <c>5</c> — aligned with
///   architecture.md §5.3 <c>OutboundQueue:MaxRetries</c> default of 5
///   and the e2e-scenarios.md "max 5 attempts" footer; matches the
///   <c>OutboundQueueOptions.MaxRetries</c> default so the two
///   knobs cannot drift in production.
///   </description></item>
///   <item><description>
///   <see cref="InitialDelayMs"/> = <c>2000</c> — aligned with
///   architecture.md <c>BaseRetryDelaySeconds</c> default of 2.
///   </description></item>
///   <item><description>
///   <see cref="BackoffMultiplier"/> = <c>2.0</c> — doubles the delay
///   after each transient failure (architecture.md §5.3 "exponential
///   2s, 4s, 8s, ...").
///   </description></item>
///   <item><description>
///   <see cref="MaxDelayMs"/> = <c>30000</c> — caps the exponential
///   curve so the 5th attempt cannot stall the row for >30s; the
///   architecture diagram caps the curve and our 4th retry already
///   computes to 16s, so the cap kicks in on the 5th retry.
///   </description></item>
///   <item><description>
///   <see cref="JitterPercent"/> = <c>25</c> — ±25% uniform jitter
///   around the computed delay, matching architecture.md §5.3 line
///   814 "with jitter (±25%)". The jitter band prevents the
///   thundering-herd problem where every dead Telegram chat
///   re-attempts simultaneously after a transient outage.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Configuration binding.</b> Bound from the <c>RetryPolicy</c>
/// configuration section via
/// <c>AddOptions&lt;RetryPolicy&gt;().Bind(...)</c> inside
/// <c>ServiceCollectionExtensions.AddMessagingPersistence</c>;
/// the worker's <c>appsettings.json</c> ships with the canonical
/// production defaults so a host that omits the section gets the
/// architecture's documented behaviour.
/// </para>
/// <para>
/// <b>Why a separate POCO (not folded into
/// <c>OutboundQueueOptions</c>).</b> The Stage 4.2 brief
/// mandates these five properties as a discrete <c>RetryPolicy</c>
/// configuration type. Keeping them on a separate POCO lets a
/// future stage swap the policy in tests / experiments without
/// touching the unrelated <c>OutboundQueueOptions</c> knobs
/// (<c>ProcessorConcurrency</c>, <c>MaxQueueDepth</c>, etc.).
/// </para>
/// </remarks>
public sealed class RetryPolicy
{
    /// <summary>
    /// Canonical configuration section name. Matches the
    /// <c>RetryPolicy</c> block already shipped in
    /// <c>appsettings.json</c> and <c>appsettings.Development.json</c>.
    /// </summary>
    public const string SectionName = "RetryPolicy";

    /// <summary>
    /// Maximum number of delivery attempts before the
    /// <c>OutboundQueueProcessor</c> stops re-enqueuing and moves the
    /// row to the dead-letter queue. Default <c>5</c>, aligned with
    /// architecture.md §5.3 <c>OutboundQueue:MaxRetries</c> and
    /// e2e-scenarios.md "max 5 attempts" footer.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Initial backoff delay in milliseconds applied between attempt 1
    /// (the original send) and attempt 2 (the first retry). Default
    /// <c>2000</c>, aligned with architecture.md
    /// <c>BaseRetryDelaySeconds</c> default of <c>2</c>.
    /// </summary>
    public int InitialDelayMs { get; set; } = 2000;

    /// <summary>
    /// Multiplier applied to the computed delay between attempts.
    /// Default <c>2.0</c> — doubles the delay after every transient
    /// failure (architecture.md §5.3 "exponential 2s, 4s, 8s, ...").
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Upper bound on the computed delay in milliseconds. The
    /// exponential curve grows fast: with
    /// <see cref="InitialDelayMs"/> = 2000 and
    /// <see cref="BackoffMultiplier"/> = 2.0 the 5th attempt would
    /// otherwise schedule 32 s out — capped here at <c>30000</c> ms by
    /// default so the dead-letter decision happens within a bounded
    /// wall-clock window after the first failure.
    /// </summary>
    public int MaxDelayMs { get; set; } = 30000;

    /// <summary>
    /// Jitter band as a percentage of the computed delay. Default
    /// <c>25</c> ≡ ±25%, matching architecture.md §5.3 line 814
    /// "with jitter (±25%)". A value of <c>0</c> disables jitter
    /// (useful for deterministic unit tests). Values are clamped to
    /// <c>[0, 100]</c> at compute time.
    /// </summary>
    public int JitterPercent { get; set; } = 25;

    /// <summary>
    /// Compute the next-retry wall-clock delay for the supplied
    /// <paramref name="attempt"/> using exponential backoff with
    /// jitter. <paramref name="attempt"/> is the 1-based attempt
    /// number of the failed send (i.e. on the first failure pass
    /// <c>attempt=1</c>; on the second failure pass <c>attempt=2</c>,
    /// etc.) — the delay applies BEFORE the next attempt is started.
    /// </summary>
    /// <param name="attempt">
    /// 1-based attempt number of the failed send. The delay returned
    /// is the wait time before attempt <c>(attempt + 1)</c> begins.
    /// </param>
    /// <param name="random">
    /// Random source used for the jitter draw. Tests pin this to a
    /// seeded instance for deterministic assertions; production
    /// resolves the shared thread-safe <see cref="System.Random.Shared"/>.
    /// </param>
    /// <returns>
    /// A non-negative <see cref="TimeSpan"/> bounded by
    /// <c>[max(0, base - jitter), base + jitter]</c> where
    /// <c>base = min(InitialDelayMs * BackoffMultiplier^(attempt-1), MaxDelayMs)</c>
    /// and <c>jitter = base * (JitterPercent / 100)</c>. A negative
    /// <paramref name="attempt"/> is treated as <c>1</c>.
    /// </returns>
    public TimeSpan ComputeDelay(int attempt, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        var effectiveAttempt = Math.Max(1, attempt);

        // Exponential curve: base * multiplier ^ (attempt - 1).
        // Math.Pow on doubles is fine for the small exponents we see
        // here (capped by MaxDelayMs after the multiplication).
        var rawDelayMs = InitialDelayMs * Math.Pow(BackoffMultiplier, effectiveAttempt - 1);

        // Cap by MaxDelayMs. The cap is the wall-clock guard the
        // architecture asks for so the 5th-attempt dead-letter
        // verdict lands within a bounded total window.
        var cappedDelayMs = Math.Min(rawDelayMs, Math.Max(0, MaxDelayMs));

        // Jitter band: ±JitterPercent of the capped delay. Uniform
        // distribution per architecture.md §5.3 — anti-thundering-
        // herd guard. Clamp the percent to [0, 100] so a misconfig
        // cannot inflate the band beyond the base delay.
        var jitterPct = Math.Clamp(JitterPercent, 0, 100) / 100.0;
        var jitterRangeMs = cappedDelayMs * jitterPct;
        var jitterDeltaMs = (random.NextDouble() * 2 - 1) * jitterRangeMs;

        var finalDelayMs = Math.Max(0, cappedDelayMs + jitterDeltaMs);
        return TimeSpan.FromMilliseconds(finalDelayMs);
    }
}
