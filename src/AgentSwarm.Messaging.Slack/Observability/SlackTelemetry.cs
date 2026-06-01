// -----------------------------------------------------------------------
// <copyright file="SlackTelemetry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Observability;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.Extensions.Logging;

/// <summary>
/// Process-wide OpenTelemetry-compatible telemetry primitives for the
/// Slack messenger connector. Owns the singleton
/// <see cref="System.Diagnostics.ActivitySource"/> and
/// <see cref="System.Diagnostics.Metrics.Meter"/> instances called out by
/// architecture.md §6.3 and Stage 7.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both the <see cref="System.Diagnostics.ActivitySource"/> ("traces")
/// and <see cref="System.Diagnostics.Metrics.Meter"/> ("metrics") are
/// exposed as process-static singletons because that is the contract
/// every OpenTelemetry .NET exporter expects: a stable name is the
/// only handle a downstream
/// <see cref="System.Diagnostics.ActivityListener"/> or
/// <see cref="System.Diagnostics.Metrics.MeterListener"/> uses to
/// subscribe. Both objects are also surfaced through
/// <see cref="SlackTelemetryServiceCollectionExtensions"/> so DI
/// consumers (and tests) can resolve the same instance the production
/// code emits to.
/// </para>
/// <para>
/// <b>No OpenTelemetry SDK dependency.</b> Per the Stage 7.2 brief and
/// tech-spec.md §2.6 we use the in-box
/// <see cref="System.Diagnostics"/> + <see cref="System.Diagnostics.Metrics"/>
/// APIs only. The Worker host (or any downstream composition root) is
/// free to add the OpenTelemetry .NET SDK and subscribe to these
/// instruments by name; Stage 7.2 deliberately does not introduce that
/// transitive dependency on the Slack project.
/// </para>
/// <para>
/// <b>Span attribute conventions (architecture.md §6.3).</b> Every
/// span produced by Slack components is decorated with the
/// <c>correlation_id</c>, <c>task_id</c>, <c>agent_id</c>,
/// <c>team_id</c>, and <c>channel_id</c> attributes via
/// <see cref="StampInboundEnvelope"/> /
/// <see cref="StampOutboundEnvelope"/>. The same fields are added as
/// <see cref="System.Diagnostics.Activity.AddBaggage"/> entries so
/// they propagate to downstream services that opt into
/// W3C baggage. Logger scopes use the same key set
/// (<see cref="CreateScope(ILogger, SlackInboundEnvelope, string?, string?, string?)"/>)
/// so structured log enrichers can correlate trace IDs and log lines
/// without re-parsing the payload.
/// </para>
/// </remarks>
public static class SlackTelemetry
{
    /// <summary>
    /// Public name of the <see cref="System.Diagnostics.ActivitySource"/>.
    /// Pinned by architecture.md §6.3 and asserted by the Stage 7.2
    /// scenario tests; consumers (OpenTelemetry exporters, in-test
    /// listeners) subscribe by this exact string.
    /// </summary>
    public const string SourceName = "AgentSwarm.Messaging.Slack";

    /// <summary>
    /// Public name of the <see cref="System.Diagnostics.Metrics.Meter"/>.
    /// Same value as <see cref="SourceName"/> per the brief
    /// ("Register `System.Diagnostics.Metrics` meter named
    /// `AgentSwarm.Messaging.Slack`").
    /// </summary>
    public const string MeterName = "AgentSwarm.Messaging.Slack";

    /// <summary>Inbound receive span name (covers the BackgroundService dequeue + pipeline run).</summary>
    public const string InboundReceiveSpanName = "slack.inbound.receive";

    /// <summary>Signature-validation span name (Stage 3.1 HMAC verification middleware).</summary>
    public const string SignatureValidationSpanName = "slack.signature.validate";

    /// <summary>Authorization-filter span name (Stage 3.2 workspace/channel/user-group ACL).</summary>
    public const string AuthorizationSpanName = "slack.authorization";

    /// <summary>Idempotency-check span name (Stage 4.3 guard).</summary>
    public const string IdempotencyCheckSpanName = "slack.idempotency.check";

    /// <summary>Command-dispatch span name (Stage 5.1 / 5.2 dispatcher).</summary>
    public const string CommandDispatchSpanName = "slack.command.dispatch";

    /// <summary>Interaction-dispatch span name (Stage 5.3 dispatcher).</summary>
    public const string InteractionDispatchSpanName = "slack.interaction.dispatch";

    /// <summary>Event-dispatch span name (Stage 5.2 app_mention dispatcher).</summary>
    public const string EventDispatchSpanName = "slack.event.dispatch";

    /// <summary>Outbound-send span name (Stage 6.3 dispatcher / API client).</summary>
    public const string OutboundSendSpanName = "slack.outbound.send";

    /// <summary>Modal-open span name (Stage 6.4 / fast-path views.open).</summary>
    public const string ModalOpenSpanName = "slack.modal.open";

    /// <summary>Span attribute key: end-to-end correlation id (matches log-scope key).</summary>
    public const string AttributeCorrelationId = "correlation_id";

    /// <summary>Span attribute key: agent-task identifier.</summary>
    public const string AttributeTaskId = "task_id";

    /// <summary>Span attribute key: agent identifier.</summary>
    public const string AttributeAgentId = "agent_id";

    /// <summary>Span attribute key: Slack workspace (team) identifier.</summary>
    public const string AttributeTeamId = "team_id";

    /// <summary>Span attribute key: Slack channel identifier.</summary>
    public const string AttributeChannelId = "channel_id";

    /// <summary>Span attribute key: envelope idempotency key (for cross-referencing audit + dedup tables).</summary>
    public const string AttributeIdempotencyKey = "slack.idempotency_key";

    /// <summary>Span attribute key: <see cref="SlackInboundSourceType"/> discriminator.</summary>
    public const string AttributeSourceType = "slack.source_type";

    /// <summary>Span attribute key: parsed sub-command (<c>ask</c>, <c>status</c>, ...).</summary>
    public const string AttributeSubCommand = "slack.sub_command";

    /// <summary>Span attribute key: outbound Slack Web API method invoked.</summary>
    public const string AttributeOperationKind = "slack.operation_kind";

    /// <summary>Span attribute key: dispatcher outcome (<c>success</c>, <c>duplicate</c>, ...).</summary>
    public const string AttributeOutcome = "slack.outcome";

    /// <summary>Span attribute key: structured rejection reason (signature / authorization paths).</summary>
    public const string AttributeRejectionReason = "slack.rejection_reason";

    /// <summary>Counter name: every successfully parsed inbound envelope.</summary>
    public const string MetricInboundCount = "slack.inbound.count";

    /// <summary>Counter name: every outbound Slack Web API call.</summary>
    public const string MetricOutboundCount = "slack.outbound.count";

    /// <summary>Histogram name: outbound Slack Web API latency.</summary>
    public const string MetricOutboundLatencyMs = "slack.outbound.latency_ms";

    /// <summary>Counter name: every duplicate detected by <c>SlackIdempotencyGuard</c>.</summary>
    public const string MetricIdempotencyDuplicateCount = "slack.idempotency.duplicate_count";

    /// <summary>Counter name: every signature / authorization rejection.</summary>
    public const string MetricAuthRejectedCount = "slack.auth.rejected_count";

    /// <summary>Counter name: every HTTP 429 / rate-limit back-off applied by the dispatcher.</summary>
    public const string MetricRateLimitBackoffCount = "slack.ratelimit.backoff_count";

    private static readonly string SlackAssemblyVersion =
        typeof(SlackTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(SlackTelemetry).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>
    /// Process-wide <see cref="System.Diagnostics.ActivitySource"/> for
    /// Slack components. Named per architecture.md §6.3 / tech-spec.md
    /// §2.6 and surfaced to DI via
    /// <see cref="SlackTelemetryServiceCollectionExtensions.AddSlackTelemetry"/>.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(SourceName, SlackAssemblyVersion);

    /// <summary>
    /// Process-wide <see cref="System.Diagnostics.Metrics.Meter"/> for
    /// Slack components. Named per architecture.md §6.3 / tech-spec.md
    /// §2.6 and surfaced to DI via
    /// <see cref="SlackTelemetryServiceCollectionExtensions.AddSlackTelemetry"/>.
    /// </summary>
    public static readonly Meter Meter = new(MeterName, SlackAssemblyVersion);

    /// <summary>
    /// <c>slack.inbound.count</c> -- incremented once per envelope
    /// successfully enqueued onto <see cref="Queues.ISlackInboundQueue"/>.
    /// Tagged with <c>slack.source_type</c> so dashboards can split
    /// command / interaction / event traffic.
    /// </summary>
    public static readonly Counter<long> InboundCount = Meter.CreateCounter<long>(
        MetricInboundCount,
        unit: "{request}",
        description: "Count of inbound Slack requests accepted by the connector.");

    /// <summary>
    /// <c>slack.outbound.count</c> -- incremented once per Slack Web API
    /// call (chat.postMessage / chat.update / views.update / views.open).
    /// Tagged with <c>slack.operation_kind</c> and <c>slack.outcome</c>
    /// so dashboards can correlate call volume to success vs. failure
    /// dispositions.
    /// </summary>
    public static readonly Counter<long> OutboundCount = Meter.CreateCounter<long>(
        MetricOutboundCount,
        unit: "{request}",
        description: "Count of outbound Slack Web API calls dispatched by the connector.");

    /// <summary>
    /// <c>slack.outbound.latency_ms</c> -- histogram of per-call
    /// outbound latency measured in milliseconds, tagged with
    /// <c>slack.operation_kind</c> and <c>slack.outcome</c>. Used by
    /// the P95 outbound-latency SLO called out by architecture.md
    /// §6.2.
    /// </summary>
    public static readonly Histogram<double> OutboundLatencyMs = Meter.CreateHistogram<double>(
        MetricOutboundLatencyMs,
        unit: "ms",
        description: "Latency of outbound Slack Web API calls in milliseconds.");

    /// <summary>
    /// <c>slack.idempotency.duplicate_count</c> -- incremented when
    /// <c>SlackIdempotencyGuard.TryAcquireAsync</c> returns
    /// <see langword="false"/> (true duplicate OR live-lease defer).
    /// Tagged with <c>slack.source_type</c>.
    /// </summary>
    public static readonly Counter<long> IdempotencyDuplicateCount = Meter.CreateCounter<long>(
        MetricIdempotencyDuplicateCount,
        unit: "{request}",
        description: "Count of duplicate inbound Slack envelopes suppressed by the idempotency guard.");

    /// <summary>
    /// <c>slack.auth.rejected_count</c> -- incremented for every
    /// signature-validation or authorization-filter rejection. Tagged
    /// with <c>slack.rejection_reason</c> so a single counter covers
    /// both Stage 3.1 (signature) and Stage 3.2 (ACL) failures.
    /// </summary>
    public static readonly Counter<long> AuthRejectedCount = Meter.CreateCounter<long>(
        MetricAuthRejectedCount,
        unit: "{request}",
        description: "Count of inbound Slack requests rejected by signature validation or the authorization filter.");

    /// <summary>
    /// <c>slack.ratelimit.backoff_count</c> -- incremented every time
    /// the dispatcher applies an HTTP 429 / Retry-After back-off
    /// against the rate limiter. Tagged with <c>slack.operation_kind</c>.
    /// </summary>
    public static readonly Counter<long> RateLimitBackoffCount = Meter.CreateCounter<long>(
        MetricRateLimitBackoffCount,
        unit: "{event}",
        description: "Count of HTTP 429 / Retry-After back-offs applied by the outbound dispatcher.");

    /// <summary>
    /// Stamps the architecture.md §6.3 attribute set onto the supplied
    /// <see cref="Activity"/>. Safe to call with a <see langword="null"/>
    /// activity (no listener subscribed) -- the call becomes a no-op
    /// so callers never have to null-check.
    /// </summary>
    /// <param name="activity">Activity to decorate; may be <see langword="null"/>.</param>
    /// <param name="envelope">Inbound envelope sourced from the transport layer.</param>
    /// <param name="correlationId">
    /// Optional correlation id override; when <see langword="null"/> we
    /// fall back to <see cref="SlackInboundEnvelope.IdempotencyKey"/>
    /// because the ingestor uses that key as the correlation handle
    /// until the orchestrator assigns a task id.
    /// </param>
    /// <param name="taskId">Optional agent task id (set once dispatch resolves it).</param>
    /// <param name="agentId">Optional agent id (set once dispatch resolves it).</param>
    internal static void StampInboundEnvelope(
        Activity? activity,
        SlackInboundEnvelope envelope,
        string? correlationId = null,
        string? taskId = null,
        string? agentId = null)
    {
        if (envelope is null)
        {
            return;
        }

        string effectiveCorrelationId = !string.IsNullOrEmpty(correlationId)
            ? correlationId!
            : envelope.IdempotencyKey;

        if (activity is not null)
        {
            activity.SetTag(AttributeCorrelationId, effectiveCorrelationId);
            activity.SetTag(AttributeIdempotencyKey, envelope.IdempotencyKey);
            activity.SetTag(AttributeSourceType, envelope.SourceType.ToString());
            if (!string.IsNullOrEmpty(envelope.TeamId))
            {
                activity.SetTag(AttributeTeamId, envelope.TeamId);
            }

            if (!string.IsNullOrEmpty(envelope.ChannelId))
            {
                activity.SetTag(AttributeChannelId, envelope.ChannelId);
            }

            if (!string.IsNullOrEmpty(taskId))
            {
                activity.SetTag(AttributeTaskId, taskId);
            }

            if (!string.IsNullOrEmpty(agentId))
            {
                activity.SetTag(AttributeAgentId, agentId);
            }

            // Architecture.md §6.3 calls out "baggage" alongside span
            // attributes -- baggage propagates the correlation id to
            // downstream services that subscribe to W3C baggage.
            activity.AddBaggage(AttributeCorrelationId, effectiveCorrelationId);
            if (!string.IsNullOrEmpty(envelope.TeamId))
            {
                activity.AddBaggage(AttributeTeamId, envelope.TeamId);
            }

            if (!string.IsNullOrEmpty(envelope.ChannelId))
            {
                activity.AddBaggage(AttributeChannelId, envelope.ChannelId);
            }

            if (!string.IsNullOrEmpty(taskId))
            {
                activity.AddBaggage(AttributeTaskId, taskId);
            }

            if (!string.IsNullOrEmpty(agentId))
            {
                activity.AddBaggage(AttributeAgentId, agentId);
            }
        }
    }

    /// <summary>
    /// Stamps the architecture.md §6.3 attribute set onto an outbound
    /// dispatch span sourced from a <see cref="SlackOutboundEnvelope"/>.
    /// Behaves like <see cref="StampInboundEnvelope"/>: safe to call
    /// with a <see langword="null"/> activity, falls back to
    /// <see cref="SlackOutboundEnvelope.CorrelationId"/> when no
    /// override is supplied.
    /// </summary>
    internal static void StampOutboundEnvelope(
        Activity? activity,
        SlackOutboundEnvelope envelope,
        string? teamId = null,
        string? channelId = null,
        string? agentId = null)
    {
        if (activity is null || envelope is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(envelope.CorrelationId))
        {
            activity.SetTag(AttributeCorrelationId, envelope.CorrelationId);
            activity.AddBaggage(AttributeCorrelationId, envelope.CorrelationId);
        }

        if (!string.IsNullOrEmpty(envelope.TaskId))
        {
            activity.SetTag(AttributeTaskId, envelope.TaskId);
            activity.AddBaggage(AttributeTaskId, envelope.TaskId);
        }

        activity.SetTag(AttributeOperationKind, envelope.MessageType.ToString());

        if (!string.IsNullOrEmpty(teamId))
        {
            activity.SetTag(AttributeTeamId, teamId);
            activity.AddBaggage(AttributeTeamId, teamId);
        }

        if (!string.IsNullOrEmpty(channelId))
        {
            activity.SetTag(AttributeChannelId, channelId);
            activity.AddBaggage(AttributeChannelId, channelId);
        }

        if (!string.IsNullOrEmpty(agentId))
        {
            activity.SetTag(AttributeAgentId, agentId);
            activity.AddBaggage(AttributeAgentId, agentId);
        }
    }

    /// <summary>
    /// Starts a new child span under the current
    /// <see cref="Activity.Current"/> (when present) for an inbound
    /// processing step, pre-decorated with the §6.3 attribute set.
    /// Returns <see langword="null"/> when no listener has subscribed
    /// to <see cref="ActivitySource"/>; callers must therefore tolerate
    /// a null return.
    /// </summary>
    /// <remarks>
    /// Span kind defaults to <see cref="ActivityKind.Internal"/>; the
    /// outermost <see cref="InboundReceiveSpanName"/> span uses
    /// <see cref="ActivityKind.Consumer"/> (set explicitly by the
    /// ingestor) to mark the queue-drain boundary.
    /// </remarks>
    internal static Activity? StartInboundSpan(
        string spanName,
        SlackInboundEnvelope envelope,
        ActivityKind kind = ActivityKind.Internal,
        string? correlationId = null,
        string? taskId = null,
        string? agentId = null)
    {
        Activity? activity = ActivitySource.StartActivity(spanName, kind);
        StampInboundEnvelope(activity, envelope, correlationId, taskId, agentId);
        return activity;
    }

    /// <summary>
    /// Starts a new child span for an outbound Slack Web API call,
    /// pre-decorated with the §6.3 attribute set. Returns
    /// <see langword="null"/> when no listener has subscribed.
    /// </summary>
    internal static Activity? StartOutboundSpan(
        SlackOutboundEnvelope envelope,
        string teamId,
        string? channelId,
        string? agentId = null)
    {
        Activity? activity = ActivitySource.StartActivity(OutboundSendSpanName, ActivityKind.Client);
        StampOutboundEnvelope(activity, envelope, teamId, channelId, agentId);
        return activity;
    }

    /// <summary>
    /// Returns an <see cref="IDisposable"/> log-scope that adds the
    /// §6.3 correlation key set to every log line emitted within the
    /// scope. Safe to call with a <see langword="null"/> envelope --
    /// returns <see cref="NullDisposable"/> so the call site keeps
    /// its <c>using</c> shape.
    /// </summary>
    internal static IDisposable CreateScope(
        ILogger logger,
        SlackInboundEnvelope? envelope,
        string? correlationId = null,
        string? taskId = null,
        string? agentId = null)
    {
        if (logger is null || envelope is null)
        {
            return NullDisposable.Instance;
        }

        return CreateScope(
            logger,
            correlationId ?? envelope.IdempotencyKey,
            taskId,
            agentId,
            envelope.TeamId,
            envelope.ChannelId,
            envelope.IdempotencyKey);
    }

    /// <summary>
    /// Returns an <see cref="IDisposable"/> log-scope for an outbound
    /// envelope (the §6.3 key set sourced from a
    /// <see cref="SlackOutboundEnvelope"/> rather than an inbound one).
    /// </summary>
    internal static IDisposable CreateScope(
        ILogger logger,
        SlackOutboundEnvelope? envelope,
        string? teamId = null,
        string? channelId = null,
        string? agentId = null)
    {
        if (logger is null || envelope is null)
        {
            return NullDisposable.Instance;
        }

        return CreateScope(
            logger,
            envelope.CorrelationId,
            envelope.TaskId,
            agentId,
            teamId,
            channelId,
            idempotencyKey: null);
    }

    /// <summary>
    /// Lower-level scope builder for callers that don't have an
    /// envelope to hand (e.g., the signature middleware which runs
    /// before envelope construction). Skips keys whose value is
    /// <see langword="null"/> or empty so log enrichers only see
    /// populated fields.
    /// </summary>
    public static IDisposable CreateScope(
        ILogger logger,
        string? correlationId,
        string? taskId,
        string? agentId,
        string? teamId,
        string? channelId,
        string? idempotencyKey = null)
    {
        if (logger is null)
        {
            return NullDisposable.Instance;
        }

        Dictionary<string, object> state = new(capacity: 6, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(correlationId))
        {
            state[AttributeCorrelationId] = correlationId!;
        }

        if (!string.IsNullOrEmpty(taskId))
        {
            state[AttributeTaskId] = taskId!;
        }

        if (!string.IsNullOrEmpty(agentId))
        {
            state[AttributeAgentId] = agentId!;
        }

        if (!string.IsNullOrEmpty(teamId))
        {
            state[AttributeTeamId] = teamId!;
        }

        if (!string.IsNullOrEmpty(channelId))
        {
            state[AttributeChannelId] = channelId!;
        }

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            state[AttributeIdempotencyKey] = idempotencyKey!;
        }

        if (state.Count == 0)
        {
            return NullDisposable.Instance;
        }

        return logger.BeginScope(state) ?? NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
            // intentional no-op: returned in place of an
            // ILogger.BeginScope handle when the scope payload is
            // empty or the caller passed null state.
        }
    }
}
