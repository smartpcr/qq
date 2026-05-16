// -----------------------------------------------------------------------
// <copyright file="DeduplicationOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core;

using System;

/// <summary>
/// Stage 4.3 — options bound from the <c>Deduplication</c> configuration
/// section. Tunes the sliding-window
/// <see cref="Abstractions.IDeduplicationService"/> backends: the
/// in-memory <see cref="SlidingWindowDeduplicationService"/> wired by
/// <c>AddTelegram</c> for dev/local, and the EF-backed persistent
/// implementation that <c>AddMessagingPersistence</c> replaces it with
/// for production. Both backends share the same
/// <see cref="EntryTimeToLive"/> / <see cref="PurgeInterval"/>
/// semantics — the only difference is where the row state lives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Project location.</b> This options type intentionally lives in
/// <c>AgentSwarm.Messaging.Core</c> rather than
/// <c>AgentSwarm.Messaging.Persistence</c> so the Telegram connector
/// (which only references Core, not Persistence) can wire the
/// in-memory <see cref="SlidingWindowDeduplicationService"/> for
/// dev/local without taking a build-graph dependency on EF Core. The
/// persistent <c>DeduplicationCleanupService</c> in Persistence reads
/// the same <see cref="DeduplicationOptions"/> instance because its
/// project references Core.
/// </para>
/// <para>
/// <b>TTL guidance.</b> The brief specifies a default of 1 hour for
/// <see cref="EntryTimeToLive"/>. That is much longer than Telegram's
/// at-most-a-few-minutes webhook retry envelope, so a duplicate
/// re-delivery is reliably suppressed without the table ever growing
/// past the burst-window size.
/// </para>
/// <para>
/// <b>Purge cadence.</b> <see cref="PurgeInterval"/> defaults to
/// 5 minutes — frequent enough that the table size stays bounded
/// under steady-state load (≤ <see cref="EntryTimeToLive"/> ÷
/// <see cref="PurgeInterval"/> generations of rows in flight) but
/// infrequent enough that the sweep does not contend with the live
/// pipeline's INSERTs. Operators can tune this independently of
/// <see cref="EntryTimeToLive"/>.
/// </para>
/// </remarks>
public sealed class DeduplicationOptions
{
    /// <summary>
    /// Configuration section name bound by both
    /// <c>AddTelegram</c> (in-memory backend) and
    /// <c>AddMessagingPersistence</c> (EF backend).
    /// </summary>
    public const string SectionName = "Deduplication";

    /// <summary>
    /// The sliding-window TTL after which a processed event row (or an
    /// abandoned reservation) is eligible for eviction. Defaults to
    /// 1 hour per the Stage 4.3 brief.
    /// </summary>
    public TimeSpan EntryTimeToLive { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The cadence at which the background cleanup loop wakes up and
    /// runs the purge query. Defaults to 5 minutes. Setting this to
    /// <see cref="TimeSpan.Zero"/> disables periodic cleanup (tests
    /// drive the sweep manually instead).
    /// </summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromMinutes(5);
}
