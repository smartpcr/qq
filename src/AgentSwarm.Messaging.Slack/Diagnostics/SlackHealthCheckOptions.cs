// -----------------------------------------------------------------------
// <copyright file="SlackHealthCheckOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Diagnostics;

/// <summary>
/// Strongly-typed knobs for the Stage 7.3 health-check pipeline:
/// outbound-queue and DLQ depth thresholds, plus the readiness /
/// liveness endpoint paths the connector mounts on the host's
/// ASP.NET Core <see cref="Microsoft.AspNetCore.Routing.IEndpointRouteBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// Bound from the <see cref="SectionName"/> section of
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// by <see cref="SlackHealthChecksServiceCollectionExtensions.AddSlackHealthChecks"/>.
/// Defaults match the literal values in the brief:
/// </para>
/// <list type="bullet">
///   <item><description><c>OutboundQueueDegradedThreshold = 1000</c>
///   (implementation-plan Stage 7.3 step 2).</description></item>
///   <item><description><c>DeadLetterUnhealthyThreshold = 100</c>
///   (implementation-plan Stage 7.3 step 3).</description></item>
///   <item><description><c>ReadyEndpointPath = "/health/ready"</c>,
///   <c>LiveEndpointPath = "/health/live"</c>
///   (implementation-plan Stage 7.3 step 4).</description></item>
/// </list>
/// </remarks>
public sealed class SlackHealthCheckOptions
{
    /// <summary>
    /// Configuration section name (<c>"Slack:Health"</c>) the options
    /// are bound from. Exposed as a constant so the extension method
    /// and consumers can agree without duplicating the literal.
    /// </summary>
    public const string SectionName = "Slack:Health";

    /// <summary>
    /// Outbound-queue depth above which the
    /// <c>slack-outbound-queue-depth</c> check transitions from
    /// <c>Healthy</c> to <c>Degraded</c>. Defaults to <c>1000</c>
    /// per the Stage 7.3 brief. Values are clamped to a minimum of
    /// <c>1</c> at consumption time so a misconfigured zero does not
    /// permanently degrade readiness.
    /// </summary>
    public int OutboundQueueDegradedThreshold { get; set; } = 1000;

    /// <summary>
    /// DLQ depth above which the <c>slack-dead-letter-queue-depth</c>
    /// check transitions from <c>Healthy</c> to <c>Unhealthy</c>.
    /// Defaults to <c>100</c> per the Stage 7.3 brief. Values are
    /// clamped to a minimum of <c>1</c> at consumption time.
    /// </summary>
    public int DeadLetterUnhealthyThreshold { get; set; } = 100;

    /// <summary>
    /// HTTP path the readiness endpoint is mounted at. Includes every
    /// registered Slack health check. Kubernetes <c>readinessProbe</c>
    /// SHOULD point at this path; receiving non-2xx removes the pod
    /// from service. Defaults to <c>/health/ready</c>.
    /// </summary>
    public string ReadyEndpointPath { get; set; } = "/health/ready";

    /// <summary>
    /// HTTP path the liveness endpoint is mounted at. Reports the
    /// host process is reachable but does not inspect downstream
    /// dependencies; Kubernetes <c>livenessProbe</c> SHOULD point
    /// at this path so a Slack outage does not cause pod restarts.
    /// Defaults to <c>/health/live</c>.
    /// </summary>
    public string LiveEndpointPath { get; set; } = "/health/live";

    /// <summary>
    /// Whether the Slack-API connectivity check should call
    /// <c>auth.test</c> against every enabled workspace
    /// (<see langword="true"/>, default) or short-circuit after the
    /// first success (<see langword="false"/>). Enterprise hosts
    /// with many workspaces flip this to <see langword="false"/>
    /// so each probe pays exactly one Slack round-trip.
    /// </summary>
    public bool AuthTestAllWorkspaces { get; set; } = true;

    /// <summary>
    /// Per-workspace timeout for the synchronous <c>auth.test</c>
    /// round-trip used by <see cref="SlackApiConnectivityHealthCheck"/>.
    /// Defaults to <c>3</c> seconds so a Slack outage does not stall
    /// the Kubernetes probe long enough to trip the kubelet's own
    /// probe timeout. Values are clamped to a minimum of <c>250</c>
    /// milliseconds at consumption time.
    /// </summary>
    public System.TimeSpan AuthTestTimeout { get; set; } = System.TimeSpan.FromSeconds(3);

    /// <summary>
    /// Returns the outbound-queue threshold clamped to its minimum.
    /// </summary>
    internal int EffectiveOutboundDegradedThreshold =>
        this.OutboundQueueDegradedThreshold > 0 ? this.OutboundQueueDegradedThreshold : 1;

    /// <summary>
    /// Returns the DLQ threshold clamped to its minimum.
    /// </summary>
    internal int EffectiveDeadLetterUnhealthyThreshold =>
        this.DeadLetterUnhealthyThreshold > 0 ? this.DeadLetterUnhealthyThreshold : 1;

    /// <summary>
    /// Returns the auth-test timeout clamped to its minimum.
    /// </summary>
    internal System.TimeSpan EffectiveAuthTestTimeout =>
        this.AuthTestTimeout >= System.TimeSpan.FromMilliseconds(250)
            ? this.AuthTestTimeout
            : System.TimeSpan.FromMilliseconds(250);
}
