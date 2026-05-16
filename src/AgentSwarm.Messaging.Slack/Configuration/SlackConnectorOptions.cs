// -----------------------------------------------------------------------
// <copyright file="SlackConnectorOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Configuration;

/// <summary>
/// Strongly-typed options bag for the Slack connector. Bound from the
/// <see cref="SectionName"/> section of <c>IConfiguration</c> via
/// <see cref="SlackConnectorServiceCollectionExtensions.AddSlackConnectorOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>. The
/// nested option classes (<see cref="SlackRetryOptions"/>,
/// <see cref="SlackRateLimitOptions"/>, <see cref="SlackRateLimitTier"/>)
/// are deliberately public so they can be bound by the standard options
/// pattern and overridden per-section in <c>appsettings.json</c>.
/// </para>
/// <para>
/// Defaults match the values in
/// <c>src/AgentSwarm.Messaging.Worker/appsettings.json</c>. Tier defaults
/// reflect Slack's published Web API rate-limit tiers (see architecture.md
/// §2.12: <c>chat.postMessage</c> is Tier 2; <c>views.update</c> is
/// Tier 4).
/// </para>
/// </remarks>
public sealed class SlackConnectorOptions
{
    /// <summary>
    /// Configuration section name (<c>"Slack"</c>) the options are bound
    /// from. Exposed as a constant so the extension method and consumers
    /// can agree without duplicating the literal.
    /// </summary>
    public const string SectionName = "Slack";

    /// <summary>
    /// Maximum number of Slack workspaces the connector will host. Socket
    /// Mode maintains one WebSocket per workspace; the connection pool
    /// sizing must respect this limit (tech-spec.md §5.2 row "Max
    /// workspaces per deployment"). Defaults to <c>15</c>.
    /// </summary>
    public int MaxWorkspaces { get; set; } = 15;

    /// <summary>
    /// Time-to-live (in minutes) for the Slack user-group membership
    /// cache used by <c>SlackAuthorizationFilter</c>. Defaults to
    /// <c>5</c> minutes.
    /// </summary>
    public int MembershipCacheTtlMinutes { get; set; } = 5;

    /// <summary>
    /// Retry parameters applied by the inbound ingestor and outbound
    /// dispatcher when a transient Slack API failure occurs.
    /// </summary>
    public SlackRetryOptions Retry { get; set; } = new();

    /// <summary>
    /// Token-bucket rate-limit configuration per Slack Web API tier,
    /// shared between <c>SlackOutboundDispatcher</c> and
    /// <c>SlackDirectApiClient</c>.
    /// </summary>
    public SlackRateLimitOptions RateLimits { get; set; } = new();
}

/// <summary>
/// Retry parameters for transient Slack API failures (HTTP 5xx, network
/// errors, HTTP 429). Consumed by <c>ISlackRetryPolicy</c> implementations.
/// </summary>
public sealed class SlackRetryOptions
{
    /// <summary>
    /// Configuration section name (<c>"Slack:Retry"</c>) the options are
    /// bound from. Exposed as a constant so the extension method and
    /// consumers can agree without duplicating the literal.
    /// </summary>
    public const string SectionName = "Slack:Retry";

    /// <summary>
    /// Maximum number of attempts (including the initial try) before
    /// dead-lettering the message. Defaults to <c>5</c>.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Initial backoff in milliseconds before the second attempt. Used as
    /// the base for exponential growth. Defaults to <c>200</c>.
    /// </summary>
    public int InitialDelayMilliseconds { get; set; } = 200;

    /// <summary>
    /// Cap on the exponential backoff delay, in seconds. Defaults to
    /// <c>30</c>.
    /// </summary>
    public int MaxDelaySeconds { get; set; } = 30;
}

/// <summary>
/// Token-bucket rate-limit configuration grouped by Slack's published API
/// tiers. Tier mappings are documented at
/// <see href="https://api.slack.com/docs/rate-limits"/>; defaults below
/// follow Slack's per-tier minimums.
/// </summary>
/// <remarks>
/// The tier model intentionally exposes a per-tier scope (<see
/// cref="SlackRateLimitTier.Scope"/>) so the runtime limiter can apply
/// global-workspace limits to most tiers while honouring the per-channel
/// limit documented for <c>chat.postMessage</c> (Tier 2).
/// </remarks>
public sealed class SlackRateLimitOptions
{
    /// <summary>
    /// Tier 1 (~1+ request/min). Used by rare admin / org-level methods.
    /// </summary>
    public SlackRateLimitTier Tier1 { get; set; } = new()
    {
        RequestsPerMinute = 1,
        BurstCapacity = 1,
        Scope = SlackRateLimitScope.Workspace,
    };

    /// <summary>
    /// Tier 2 (~20+ requests/min). Applies to <c>chat.postMessage</c>,
    /// which Slack documents as "roughly 1 message per second per
    /// channel". Default <see cref="SlackRateLimitTier.Scope"/> is
    /// <see cref="SlackRateLimitScope.Channel"/>.
    /// </summary>
    public SlackRateLimitTier Tier2 { get; set; } = new()
    {
        RequestsPerMinute = 20,
        BurstCapacity = 5,
        Scope = SlackRateLimitScope.Channel,
    };

    /// <summary>
    /// Tier 3 (~50+ requests/min). Used by most reads.
    /// </summary>
    public SlackRateLimitTier Tier3 { get; set; } = new()
    {
        RequestsPerMinute = 50,
        BurstCapacity = 10,
        Scope = SlackRateLimitScope.Workspace,
    };

    /// <summary>
    /// Tier 4 (~100+ requests/min). Applies to <c>views.update</c>.
    /// </summary>
    public SlackRateLimitTier Tier4 { get; set; } = new()
    {
        RequestsPerMinute = 100,
        BurstCapacity = 20,
        Scope = SlackRateLimitScope.Workspace,
    };
}

/// <summary>
/// Per-tier rate-limit knob: steady-state requests per minute and the
/// burst capacity of the token bucket.
/// </summary>
public sealed class SlackRateLimitTier
{
    /// <summary>
    /// Steady-state refill rate, in requests per minute.
    /// </summary>
    public int RequestsPerMinute { get; set; }

    /// <summary>
    /// Maximum number of requests that may be issued in a single burst
    /// before throttling kicks in.
    /// </summary>
    public int BurstCapacity { get; set; }

    /// <summary>
    /// Whether <see cref="RequestsPerMinute"/> applies per workspace or
    /// per channel. <c>chat.postMessage</c> (Tier 2) is per-channel; the
    /// other tiers are per-workspace.
    /// </summary>
    public SlackRateLimitScope Scope { get; set; } = SlackRateLimitScope.Workspace;
}

/// <summary>
/// Aggregation scope for a Slack rate-limit tier.
/// </summary>
public enum SlackRateLimitScope
{
    /// <summary>
    /// Default value reserved for an unbound options block. Treated as
    /// <see cref="Workspace"/> at runtime so a misconfigured section does
    /// not crash startup.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// One token bucket per workspace (<c>team_id</c>).
    /// </summary>
    Workspace = 1,

    /// <summary>
    /// One token bucket per Slack channel within a workspace.
    /// </summary>
    Channel = 2,
}
