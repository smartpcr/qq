// -----------------------------------------------------------------------
// <copyright file="SlackApiTier.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Slack Web API rate-limit tiers as published at
/// <see href="https://api.slack.com/docs/rate-limits"/>. The
/// <see cref="ISlackRateLimiter"/> binds one token bucket per
/// (tier, scope) pair so <c>chat.postMessage</c> (Tier 2, per-channel)
/// and <c>views.update</c> (Tier 4, per-workspace) honour their
/// independent limits without starving one another.
/// </summary>
/// <remarks>
/// Stage 6.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// step 5. Architecture.md §2.12 spells the mapping out:
/// <c>chat.postMessage</c> → Tier 2 (~1 req/s per channel) and
/// <c>views.update</c> → Tier 4. <c>chat.update</c> is rated by Slack
/// at Tier 3, which the dispatcher uses as its default for the
/// non-brief-pinned tiers.
/// </remarks>
internal enum SlackApiTier
{
    /// <summary>Default value reserved for an uninitialised tier.</summary>
    Unspecified = 0,

    /// <summary>Tier 1 (~1+ req/min). Rare admin / org-level methods.</summary>
    Tier1 = 1,

    /// <summary>Tier 2 (~20+ req/min). <c>chat.postMessage</c>.</summary>
    Tier2 = 2,

    /// <summary>Tier 3 (~50+ req/min). Most reads and <c>chat.update</c>.</summary>
    Tier3 = 3,

    /// <summary>Tier 4 (~100+ req/min). <c>views.update</c>, <c>views.open</c>.</summary>
    Tier4 = 4,
}

/// <summary>
/// Static helpers that map between the brief's outbound
/// <see cref="SlackOutboundOperationKind"/> verb and the
/// <see cref="SlackApiTier"/> bucket Slack rates it at, and between
/// the runtime tier and the matching
/// <see cref="SlackRateLimitTier"/> configuration row.
/// </summary>
/// <remarks>
/// Centralising the lookups in one helper means
/// <see cref="SlackOutboundDispatcher"/> and the future
/// <c>SlackDirectApiClient</c> (Stage 6.4) can stay aligned on tier
/// assignment without each having to redeclare the mapping.
/// </remarks>
internal static class SlackOutboundTierMap
{
    /// <summary>
    /// Returns the Slack rate-limit tier the supplied outbound
    /// operation is rated at. <see cref="SlackOutboundOperationKind.PostMessage"/>
    /// is Tier 2 (per channel); <see cref="SlackOutboundOperationKind.UpdateMessage"/>
    /// is Tier 3 (per workspace); <see cref="SlackOutboundOperationKind.ViewsUpdate"/>
    /// is Tier 4 (per workspace).
    /// </summary>
    public static SlackApiTier ForOperation(SlackOutboundOperationKind operation) => operation switch
    {
        SlackOutboundOperationKind.PostMessage => SlackApiTier.Tier2,
        SlackOutboundOperationKind.UpdateMessage => SlackApiTier.Tier3,
        SlackOutboundOperationKind.ViewsUpdate => SlackApiTier.Tier4,
        _ => SlackApiTier.Tier3,
    };

    /// <summary>
    /// Returns the matching <see cref="SlackRateLimitTier"/> from the
    /// supplied <see cref="SlackRateLimitOptions"/> bag. Unknown
    /// tiers fall back to Tier 3 -- the same default the per-tier
    /// configuration ships with.
    /// </summary>
    public static SlackRateLimitTier ResolveTierConfig(SlackRateLimitOptions options, SlackApiTier tier)
    {
        ArgumentNullException.ThrowIfNull(options);
        return tier switch
        {
            SlackApiTier.Tier1 => options.Tier1,
            SlackApiTier.Tier2 => options.Tier2,
            SlackApiTier.Tier3 => options.Tier3,
            SlackApiTier.Tier4 => options.Tier4,
            _ => options.Tier3,
        };
    }
}
