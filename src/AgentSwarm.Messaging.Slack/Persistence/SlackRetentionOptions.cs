// -----------------------------------------------------------------------
// <copyright file="SlackRetentionOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using System.ComponentModel.DataAnnotations;

/// <summary>
/// Strongly-typed options for the Stage 7.1
/// <see cref="SlackRetentionCleanupService{TContext}"/> background
/// purge job. Bound from the <c>Slack:Retention</c> configuration
/// section.
/// </summary>
/// <remarks>
/// <para>
/// Stage 7.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// Defaults reflect tech-spec.md §2.7 (30-day retention, daily cadence,
/// resolved decision OQ-2) so a host that does not override anything
/// gets the canonical operator policy.
/// </para>
/// </remarks>
public sealed class SlackRetentionOptions
{
    /// <summary>Configuration section name. Matches <c>Slack:Retention</c>.</summary>
    public const string SectionName = "Slack:Retention";

    /// <summary>Default retention window (30 days per tech-spec §2.7 / OQ-2).</summary>
    public const int DefaultRetentionDays = 30;

    /// <summary>Default sweep cadence (24h -- "daily" per the brief).</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromHours(24);

    /// <summary>Default delay before the first sweep so host boot is not blocked.</summary>
    public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMinutes(5);

    /// <summary>Default per-call delete batch size for the EF purge.</summary>
    public const int DefaultBatchSize = 1000;

    /// <summary>
    /// Whether the cleanup background service is enabled. Defaults to
    /// <see langword="true"/>; set to <see langword="false"/> in test
    /// hosts that drive the cleanup manually.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Retention window in days. Rows whose <c>timestamp</c>
    /// (audit) or <c>first_seen_at</c> (inbound idempotency ledger)
    /// is older than <c>now - RetentionDays</c> are deleted by every
    /// sweep. Must be greater than zero.
    /// </summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = DefaultRetentionDays;

    /// <summary>
    /// Interval between successive sweeps. Must be positive. Defaults
    /// to 24h per the brief ("default daily").
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = DefaultSweepInterval;

    /// <summary>
    /// Delay between host start and the first sweep. Keeps the host
    /// start fast and lets transient startup load drain before the
    /// purge loop adds DB pressure. Set to <see cref="TimeSpan.Zero"/>
    /// to sweep immediately on boot (tests).
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = DefaultInitialDelay;

    /// <summary>
    /// Number of rows to delete per server roundtrip. The cleanup
    /// service issues a batched <c>DELETE</c> (per-provider syntax:
    /// <c>DELETE TOP (N)</c> on SQL Server, <c>DELETE WHERE id IN
    /// (SELECT id ... LIMIT N)</c> on SQLite / PostgreSQL) and loops
    /// until a batch returns fewer than this many rows. Bounded so a
    /// host purging months-of-backlog on its first run does not
    /// exhaust SQL Server's lock memory or block writers for an
    /// excessive duration. Must be greater than zero.
    /// </summary>
    [Range(1, 100000)]
    public int BatchSize { get; set; } = DefaultBatchSize;
}
