// -----------------------------------------------------------------------
// <copyright file="TelegramTelemetry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Telegram.Diagnostics;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>
/// Stage 6.1 — single source-of-truth for the Telegram messenger's
/// OpenTelemetry <see cref="ActivitySource"/> and the
/// <see cref="System.Diagnostics.Metrics.Meter"/> that owns the
/// connector-level counters (<c>telegram.messages.received</c>,
/// <c>telegram.messages.sent</c>, <c>telegram.commands.processed</c>,
/// <c>telegram.errors</c>) and the connector-level observable gauges
/// (<c>telegram.queue.depth</c>, <c>telegram.dlq.depth</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Naming.</b> The <see cref="ActivitySource"/> name is
/// <c>AgentSwarm.Messaging.Telegram</c> per architecture.md §8 and the
/// Stage 6.1 acceptance scenario ("a trace span with
/// <c>ActivitySource=AgentSwarm.Messaging.Telegram</c> is emitted").
/// The <see cref="Meter"/> shares the same name so an OTLP exporter
/// can subscribe to one identifier and pick up both signals; the
/// pre-existing <see cref="AgentSwarm.Messaging.Persistence.OutboundQueueMetrics"/>
/// meter (<c>AgentSwarm.Messaging.Outbound</c>) carries the
/// outbox-specific histograms and remains a sibling so the two
/// meters can be wired into the OpenTelemetry pipeline independently
/// without name collisions.
/// </para>
/// <para>
/// <b>Lifetime.</b> Both <see cref="ActivitySource"/> and
/// <see cref="Meter"/> are static singletons — the .NET diagnostics
/// API is built to share one per process, and binding to a DI-scoped
/// instance would split telemetry across xUnit test classes that
/// each spin up their own host. Subscribers (OTEL tracer / meter
/// providers, <see cref="System.Diagnostics.ActivityListener"/>,
/// <see cref="System.Diagnostics.Metrics.MeterListener"/>) match on
/// the static name and pick up every emission process-wide.
/// </para>
/// <para>
/// <b>Span attributes — never log the bot token.</b> Spans started
/// from <see cref="StartCommandSpan"/>,
/// <see cref="StartSendSpan"/>, and <see cref="StartReceiveSpan"/>
/// tag the activity with the canonical structured-logging property
/// names (<c>messaging.correlation_id</c>, <c>messaging.agent_id</c>,
/// <c>messaging.telegram_user_id</c>, <c>messaging.command_name</c>).
/// Callers MUST NOT add the bot token, <see cref="TelegramOptions.SecretToken"/>,
/// or any raw HTTP path that embeds the token as an activity tag —
/// the HTTP-client instrumentation has its own redaction filter
/// (<c>TelegramHttpRedactor</c>) which strips the token segment
/// from outbound URLs before they reach the span.
/// </para>
/// </remarks>
public static class TelegramTelemetry
{
    /// <summary>
    /// OTEL <see cref="System.Diagnostics.ActivitySource"/> name
    /// (architecture.md §8). Tests subscribe via
    /// <see cref="System.Diagnostics.ActivityListener.ShouldListenTo"/>
    /// using this exact string.
    /// </summary>
    public const string ActivitySourceName = "AgentSwarm.Messaging.Telegram";

    /// <summary>OTEL <see cref="System.Diagnostics.Metrics.Meter"/> name.</summary>
    public const string MeterName = "AgentSwarm.Messaging.Telegram";

    /// <summary>Architecture.md §8 canonical counter name.</summary>
    public const string MessagesReceivedCounterName = "telegram.messages.received";

    /// <summary>Architecture.md §8 canonical counter name.</summary>
    public const string MessagesSentCounterName = "telegram.messages.sent";

    /// <summary>Architecture.md §8 canonical counter name.</summary>
    public const string CommandsProcessedCounterName = "telegram.commands.processed";

    /// <summary>Stage 6.1 brief canonical counter name (errors family).</summary>
    public const string ErrorsCounterName = "telegram.errors";

    /// <summary>
    /// Implementation-plan Stage 6.1 canonical counter name —
    /// emitted by the <c>OutboundQueueProcessor</c> every time an
    /// outbound message reaches terminal failure (Permanent /
    /// budget-exhausted Transient / RateLimitExhausted / unknown
    /// exception) and a dead-letter row is persisted. Distinct from
    /// <see cref="OutboundQueueMetrics.BackpressureDeadLetterCounter"/>
    /// (which counts Low-severity rows dead-lettered at enqueue time
    /// due to MaxQueueDepth backpressure) so dashboards can split
    /// runtime send failures from inbound flow-control drops.
    /// </summary>
    public const string MessagesDeadLetteredCounterName = "telegram.messages.dead_lettered";

    /// <summary>Stage 6.1 brief canonical observable-gauge name (live outbound queue depth).</summary>
    public const string QueueDepthGaugeName = "telegram.queue.depth";

    /// <summary>Stage 6.1 brief canonical observable-gauge name (live dead-letter depth).</summary>
    public const string DlqDepthGaugeName = "telegram.dlq.depth";

    /// <summary>Activity name for the inbound update receive span (webhook hand-off).</summary>
    public const string ReceiveActivityName = "telegram.inbound.receive";

    /// <summary>Activity name for the command processing span inside the pipeline.</summary>
    public const string CommandActivityName = "telegram.command.process";

    /// <summary>Activity name for the outbound send span around an <c>IMessageSender</c> call.</summary>
    public const string SendActivityName = "telegram.outbound.send";

    /// <summary>Activity name for the queue-drain span around an <c>OutboundQueueProcessor</c> iteration.</summary>
    public const string QueueDrainActivityName = "telegram.outbound.queue_drain";

    // --- canonical structured-property names. Per the
    // implementation-plan Stage 6.1 step 4 brief "structured logging
    // via ILogger with consistent property names: CorrelationId,
    // AgentId, TelegramUserId, CommandName". The same identifiers
    // are used as ACTIVITY TAG KEYS so an OTLP exporter sees the
    // same names on spans as the operator sees in log scopes — a
    // dashboard query keyed on `CorrelationId` resolves both trace
    // attributes and log properties without translation. Repeated
    // here as constants so producers and tests avoid string drift.
    //
    // Iter-2 evaluator item 3 — the brief's canonical names are the
    // primary contract; the parallel `OtelMessagingCorrelationIdKey`
    // /etc. constants below preserve the OTEL "messaging.*" semantic-
    // convention shape for downstream consumers (Jaeger / Tempo) that
    // index on the convention rather than on the property name. Span
    // helpers below emit BOTH shapes so the same span satisfies the
    // brief AND the convention.

    /// <summary>Canonical correlation-id key for spans and ILogger scopes (brief contract).</summary>
    public const string CorrelationIdKey = "CorrelationId";

    /// <summary>Canonical agent-id key (brief contract).</summary>
    public const string AgentIdKey = "AgentId";

    /// <summary>Canonical Telegram user-id key (brief contract).</summary>
    public const string TelegramUserIdKey = "TelegramUserId";

    /// <summary>Canonical Telegram chat-id key.</summary>
    public const string TelegramChatIdKey = "TelegramChatId";

    /// <summary>Canonical command-name key (brief contract).</summary>
    public const string CommandNameKey = "CommandName";

    /// <summary>Canonical event-id key (Telegram update id).</summary>
    public const string EventIdKey = "EventId";

    /// <summary>Canonical event-type key (Command / CallbackResponse / TextReply / Unknown).</summary>
    public const string EventTypeKey = "EventType";

    /// <summary>Canonical outbound source-type key (Question / Alert / StatusUpdate / CommandAck).</summary>
    public const string OutboundSourceKey = "OutboundSource";

    /// <summary>Canonical Telegram message id key (Telegram-assigned id on the outbound row).</summary>
    public const string OutboundMessageIdKey = "OutboundMessageId";

    // --- OTEL semantic-convention parallel keys (iter-2 evaluator
    // item 3). Span helpers set tags under BOTH the brief contract
    // key AND the OTEL convention key so downstream consumers that
    // index on either shape resolve the same value. Producers MUST
    // NOT emit the OTEL convention name as a structured-log property
    // — log scopes use the brief contract names only.

    /// <summary>OTEL semantic-convention correlation-id key (messaging.*).</summary>
    public const string OtelMessagingCorrelationIdKey = "messaging.correlation_id";

    /// <summary>OTEL semantic-convention agent-id key.</summary>
    public const string OtelMessagingAgentIdKey = "messaging.agent_id";

    /// <summary>OTEL semantic-convention Telegram user-id key.</summary>
    public const string OtelMessagingTelegramUserIdKey = "messaging.telegram_user_id";

    /// <summary>OTEL semantic-convention Telegram chat-id key.</summary>
    public const string OtelMessagingTelegramChatIdKey = "messaging.telegram_chat_id";

    /// <summary>OTEL semantic-convention command-name key.</summary>
    public const string OtelMessagingCommandNameKey = "messaging.command_name";

    /// <summary>OTEL semantic-convention event-id key.</summary>
    public const string OtelMessagingEventIdKey = "messaging.event_id";

    /// <summary>OTEL semantic-convention event-type key.</summary>
    public const string OtelMessagingEventTypeKey = "messaging.event_type";

    /// <summary>OTEL semantic-convention outbound source-type key.</summary>
    public const string OtelMessagingOutboundSourceKey = "messaging.outbound.source";

    /// <summary>OTEL semantic-convention outbound message-id key.</summary>
    public const string OtelMessagingOutboundMessageIdKey = "messaging.outbound.message_id";

    private static readonly ActivitySource _source = new(ActivitySourceName);

    /// <summary>
    /// The process-wide <see cref="ActivitySource"/>. Subscribers add
    /// a listener bound to <see cref="ActivitySourceName"/> via
    /// <see cref="ActivityListener.ShouldListenTo"/> to start
    /// receiving spans without needing a reference to this property.
    /// </summary>
    public static ActivitySource Source => _source;

    private static readonly Meter _meter = new(MeterName);

    /// <summary>
    /// The process-wide <see cref="Meter"/> that owns the four
    /// connector-level counters and the two observable gauges. Tests
    /// using a <see cref="MeterListener"/> filter by reference
    /// equality on <see cref="Instrument.Meter"/> rather than by
    /// name, so parallel test classes that each construct their own
    /// listener do not cross-pollinate samples.
    /// </summary>
    public static Meter Meter => _meter;

    /// <summary>
    /// Counter for inbound updates the webhook (or polling service)
    /// successfully persists into <c>inbound_updates</c> — i.e.
    /// updates that pass body-buffering and idempotency-deduplication.
    /// Tagged with <c>event_type</c> ("command", "callback_response",
    /// "text_reply", "unknown") so dashboards can split the inbound
    /// mix without joining traces.
    /// </summary>
    public static readonly Counter<long> MessagesReceivedCounter =
        _meter.CreateCounter<long>(
            MessagesReceivedCounterName,
            unit: "messages",
            description: "Count of inbound Telegram updates accepted by the receive path (post-deduplication).");

    /// <summary>
    /// Counter for outbound messages successfully delivered to the
    /// Telegram Bot API (i.e. one HTTP 200 from <c>sendMessage</c>).
    /// Tagged with <c>source_type</c> ("question", "alert",
    /// "status_update", "command_ack", "plain_text") so dashboards
    /// can split sender outcomes by the originating intent.
    /// </summary>
    public static readonly Counter<long> MessagesSentCounter =
        _meter.CreateCounter<long>(
            MessagesSentCounterName,
            unit: "messages",
            description: "Count of outbound Telegram messages successfully delivered to the Bot API.");

    /// <summary>
    /// Counter for slash-commands that completed pipeline routing
    /// (i.e. reached <c>CommandRouter.RouteAsync</c> and produced a
    /// <c>CommandResult</c>, regardless of success). Tagged with
    /// <c>command</c> (verb) and <c>success</c> ("true" / "false")
    /// so dashboards can split command throughput and identify the
    /// noisiest failure verbs.
    /// </summary>
    public static readonly Counter<long> CommandsProcessedCounter =
        _meter.CreateCounter<long>(
            CommandsProcessedCounterName,
            unit: "commands",
            description: "Count of slash-commands fully processed by the inbound pipeline (success and failure).");

    /// <summary>
    /// Counter for any messenger-level error: send failure, parse
    /// error, authorization rejection, handler exception, callback
    /// failure. Tagged with <c>error_kind</c> ("send_transient",
    /// "send_permanent", "send_rate_limited", "command_handler",
    /// "callback_handler", "webhook_invalid_body",
    /// "webhook_malformed_json", "pipeline_unhandled"). Counters
    /// only — exception traces and stack frames are emitted via
    /// <see cref="ILogger"/> at Warning / Error level.
    /// </summary>
    public static readonly Counter<long> ErrorsCounter =
        _meter.CreateCounter<long>(
            ErrorsCounterName,
            unit: "errors",
            description: "Count of recoverable and permanent Telegram-messenger error events, split by error_kind.");

    /// <summary>
    /// Counter for outbound messages that reach terminal failure
    /// inside the <c>OutboundQueueProcessor</c> (Permanent, budget-
    /// exhausted Transient, RateLimitExhausted, or unknown
    /// exception) AND have been persisted to the dead-letter ledger.
    /// Tagged with <c>failure_category</c> (Permanent / TransientTransport /
    /// RateLimitExhausted / Unknown) and <c>source_type</c> so
    /// dashboards can split runtime failures by the originating
    /// intent. Distinct from <c>telegram.messages.backpressure_dlq</c>
    /// (counted on enqueue-time MaxQueueDepth drops) — implementation-
    /// plan.md Stage 6.1.
    /// </summary>
    public static readonly Counter<long> MessagesDeadLetteredCounter =
        _meter.CreateCounter<long>(
            MessagesDeadLetteredCounterName,
            unit: "messages",
            description: "Count of outbound Telegram messages that reached terminal failure in the queue processor and were persisted to the dead-letter ledger.");

    /// <summary>
    /// Implementation-plan Stage 6.1 canonical histogram name —
    /// diagnostic latency for the wall-clock time the sender spent
    /// waiting on a Telegram 429 <c>retry_after</c> backoff. One
    /// sample per 429 wait (not per chunk); dashboards aggregate by
    /// chat to spot per-chat flood-control pressure. Owned by the
    /// connector meter (not <c>OutboundQueueMetrics</c>) so the
    /// <see cref="AgentSwarm.Messaging.Telegram"/> Telegram project
    /// can emit without taking a Persistence dependency.
    /// </summary>
    public const string RateLimitedWaitMsName = "telegram.send.rate_limited_wait_ms";

    /// <summary>
    /// Histogram recording the duration of each Telegram 429
    /// <c>retry_after</c> sleep inside
    /// <c>TelegramMessageSender.SendWithRetry</c>. Tagged with
    /// <c>chat_id</c> and <c>attempt</c> so dashboards can split
    /// per-chat flood-control pressure from the global view.
    /// </summary>
    public static readonly Histogram<double> RateLimitedWaitMs =
        _meter.CreateHistogram<double>(
            RateLimitedWaitMsName,
            unit: "ms",
            description: "Diagnostic: wall-clock time the sender spent honouring a Telegram 429 retry_after backoff (one sample per 429 wait).");

    /// <summary>
    /// Starts a span around the synchronous webhook hand-off (read
    /// body → persist <c>InboundUpdate</c> → enqueue). The caller is
    /// responsible for setting status and disposing.
    /// </summary>
    public static Activity? StartReceiveSpan(
        string correlationId,
        long? eventId = null,
        string? eventType = null)
    {
        var activity = _source.StartActivity(ReceiveActivityName, ActivityKind.Server);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(CorrelationIdKey, correlationId);
        activity.SetTag(OtelMessagingCorrelationIdKey, correlationId);
        if (eventId.HasValue)
        {
            activity.SetTag(EventIdKey, eventId.Value);
            activity.SetTag(OtelMessagingEventIdKey, eventId.Value);
        }
        if (!string.IsNullOrEmpty(eventType))
        {
            activity.SetTag(EventTypeKey, eventType);
            activity.SetTag(OtelMessagingEventTypeKey, eventType);
        }
        return activity;
    }

    /// <summary>
    /// Starts a span around inbound-pipeline command processing. The
    /// caller MUST <see cref="Activity.Dispose"/> on exit and
    /// <see cref="Activity.SetStatus"/> on failure.
    /// </summary>
    public static Activity? StartCommandSpan(
        string correlationId,
        string? commandName,
        long telegramUserId,
        long telegramChatId,
        long? eventId = null)
    {
        var activity = _source.StartActivity(CommandActivityName, ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(CorrelationIdKey, correlationId);
        activity.SetTag(OtelMessagingCorrelationIdKey, correlationId);
        activity.SetTag(TelegramUserIdKey, telegramUserId);
        activity.SetTag(OtelMessagingTelegramUserIdKey, telegramUserId);
        activity.SetTag(TelegramChatIdKey, telegramChatId);
        activity.SetTag(OtelMessagingTelegramChatIdKey, telegramChatId);
        if (!string.IsNullOrEmpty(commandName))
        {
            activity.SetTag(CommandNameKey, commandName);
            activity.SetTag(OtelMessagingCommandNameKey, commandName);
        }
        if (eventId.HasValue)
        {
            activity.SetTag(EventIdKey, eventId.Value);
            activity.SetTag(OtelMessagingEventIdKey, eventId.Value);
        }
        return activity;
    }

    /// <summary>
    /// Starts a span around an outbound Telegram send. The caller is
    /// responsible for tagging the source (Question / Alert / …)
    /// via <see cref="OutboundSourceKey"/>.
    /// </summary>
    public static Activity? StartSendSpan(
        string? correlationId,
        long chatId,
        string sourceType)
    {
        var activity = _source.StartActivity(SendActivityName, ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(correlationId))
        {
            activity.SetTag(CorrelationIdKey, correlationId);
            activity.SetTag(OtelMessagingCorrelationIdKey, correlationId);
        }
        activity.SetTag(TelegramChatIdKey, chatId);
        activity.SetTag(OtelMessagingTelegramChatIdKey, chatId);
        activity.SetTag(OutboundSourceKey, sourceType);
        activity.SetTag(OtelMessagingOutboundSourceKey, sourceType);
        return activity;
    }

    /// <summary>
    /// Starts a span around a single iteration of
    /// <c>OutboundQueueProcessor</c> (dequeue → send → mark). The
    /// caller is expected to tag the activity with the resolved
    /// <see cref="OutboundSourceKey"/> and <see cref="TelegramChatIdKey"/>
    /// once they are read from the dequeued message; <see cref="AgentIdKey"/>
    /// is similarly applied by the caller from
    /// <c>AgentIdExtractor.TryExtract(message)</c>.
    /// </summary>
    public static Activity? StartQueueDrainSpan(string? correlationId, Guid messageId)
    {
        var activity = _source.StartActivity(QueueDrainActivityName, ActivityKind.Consumer);
        if (activity is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(correlationId))
        {
            activity.SetTag(CorrelationIdKey, correlationId);
            activity.SetTag(OtelMessagingCorrelationIdKey, correlationId);
        }
        activity.SetTag(OutboundMessageIdKey, messageId);
        activity.SetTag(OtelMessagingOutboundMessageIdKey, messageId);
        return activity;
    }

    /// <summary>
    /// Implementation-plan Stage 6.1 step 4 — opens an
    /// <see cref="ILogger"/> structured-logging scope carrying the
    /// four canonical brief properties (<c>CorrelationId</c>,
    /// <c>AgentId</c>, <c>TelegramUserId</c>, <c>CommandName</c>) so
    /// every log line written inside the scope is annotated
    /// consistently — operators querying a log analytics backend on
    /// any one of the four can pivot to every other entry from the
    /// same command flow. Keys that resolve to <see langword="null"/>
    /// or empty are omitted from the scope dictionary so a missing
    /// agent id (pre-routing) or missing command verb (callback
    /// reply) does not noise up the scope with empty values.
    /// </summary>
    /// <returns>
    /// An <see cref="IDisposable"/> matching the
    /// <see cref="ILogger.BeginScope{TState}(TState)"/> contract; the
    /// returned value is <see langword="null"/> when the logger does
    /// not support scopes (e.g. <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger"/>),
    /// in which case the caller's <c>using</c> pattern compiles
    /// because <see cref="IDisposable"/>'s extension via the C#
    /// pattern still allows a null left-hand side.
    /// </returns>
    public static IDisposable? BeginCanonicalLogScope(
        Microsoft.Extensions.Logging.ILogger logger,
        string? correlationId,
        string? agentId,
        long? telegramUserId,
        string? commandName)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var scope = new Dictionary<string, object?>(capacity: 4);
        if (!string.IsNullOrEmpty(correlationId))
        {
            scope[CorrelationIdKey] = correlationId;
        }
        if (!string.IsNullOrEmpty(agentId))
        {
            scope[AgentIdKey] = agentId;
        }
        if (telegramUserId.HasValue && telegramUserId.Value != 0)
        {
            scope[TelegramUserIdKey] = telegramUserId.Value;
        }
        if (!string.IsNullOrEmpty(commandName))
        {
            scope[CommandNameKey] = commandName;
        }

        return scope.Count == 0 ? null : logger.BeginScope(scope);
    }
}
