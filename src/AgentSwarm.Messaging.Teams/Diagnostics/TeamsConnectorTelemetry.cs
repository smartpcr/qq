using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentSwarm.Messaging.Teams.Diagnostics;

/// <summary>
/// OpenTelemetry-aligned instrumentation surface for the Teams connector — the
/// counterpart to <see cref="AgentSwarm.Messaging.Core.OutboxMetrics"/> for the
/// in-process Bot Framework call sites. Implements Stage 6.3 of
/// <c>implementation-plan.md</c> (Performance Monitoring and Health Checks):
/// <list type="bullet">
/// <item><description><see cref="ActivitySource"/> named <see cref="ActivitySourceName"/> — emits
/// <c>TeamsConnector.SendMessage</c>, <c>TeamsConnector.SendQuestion</c>, and
/// <c>TeamsConnector.Receive</c> spans tagged with <c>correlationId</c>,
/// <c>messageType</c>, and <c>destinationType</c>.</description></item>
/// <item><description><see cref="Meter"/> named <see cref="MeterName"/> — publishes the
/// <c>teams.messages.sent</c> / <c>teams.messages.received</c> counters, the
/// <c>teams.card.delivery.duration_ms</c> histogram (used for the §4.4 P95 budget on
/// the synchronous send path; the outbox-engine path emits its own histogram instance
/// via <see cref="AgentSwarm.Messaging.Core.OutboxMetrics"/> under the same canonical
/// instrument name), and the <c>teams.outbox.queue_depth</c> observable gauge backed
/// by an <see cref="IOutboxQueueDepthProvider"/>.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// The class is a singleton — a host wires exactly one instance into DI so the
/// trace/metric name graph matches what OpenTelemetry exporters subscribe to.
/// Multiple instances would silently fork the meter and double-count instruments.
/// </para>
/// <para>
/// <b>Why a dedicated class.</b> <see cref="AgentSwarm.Messaging.Core.OutboxMetrics"/>
/// covers the outbox engine path (dequeue → dispatcher → BF) and emits its own copy
/// of the <c>teams.card.delivery.duration_ms</c> histogram. The synchronous in-process
/// <see cref="TeamsMessengerConnector.SendMessageAsync"/> path does NOT route through
/// the outbox in test harnesses and legacy compositions, so an independent
/// <see cref="System.Diagnostics.Metrics.Meter"/> for the connector keeps the two
/// surfaces orthogonal — operators see both contributions to the histogram under the
/// same instrument name, and tests that exercise only the connector observe the
/// histogram via the meter on <see cref="MeterName"/>.
/// </para>
/// <para>
/// <b>Cardinality boundary.</b> <see cref="CorrelationIdTag"/> is stamped on
/// <see cref="Activity"/> spans only — spans are sampled per-request and never
/// aggregated by tag-tuple, so unique per-request values are safe. The counter and
/// histogram instruments deliberately omit <c>correlationId</c> from their tag sets:
/// metric backends (Prometheus, OTLP) materialise one time-series per unique
/// tag-tuple, and a per-request value would create unbounded cardinality that
/// eventually OOMs the exporter or kills scrape performance. The metric tag set is
/// restricted to the bounded classifiers <see cref="MessageTypeTag"/> and
/// <see cref="DestinationTypeTag"/>; per-request correlation is recovered by joining
/// metrics with traces on the canonical correlation ID exposed on the span.
/// </para>
/// </remarks>
public sealed class TeamsConnectorTelemetry : IDisposable
{
    /// <summary>Canonical OpenTelemetry <see cref="ActivitySource"/> name.</summary>
    public const string ActivitySourceName = "AgentSwarm.Messaging.Teams";

    /// <summary>Canonical OpenTelemetry <see cref="System.Diagnostics.Metrics.Meter"/> name.</summary>
    public const string MeterName = "AgentSwarm.Messaging.Teams";

    /// <summary>Span name emitted by <see cref="TeamsMessengerConnector.SendMessageAsync"/>.</summary>
    public const string SendMessageActivityName = "TeamsConnector.SendMessage";

    /// <summary>Span name emitted by <see cref="TeamsMessengerConnector.SendQuestionAsync"/>.</summary>
    public const string SendQuestionActivityName = "TeamsConnector.SendQuestion";

    /// <summary>Span name emitted by <see cref="TeamsMessengerConnector.ReceiveAsync"/>.</summary>
    public const string ReceiveActivityName = "TeamsConnector.Receive";

    /// <summary>Counter instrument name for outbound messages.</summary>
    public const string MessagesSentInstrumentName = "teams.messages.sent";

    /// <summary>Counter instrument name for inbound messages.</summary>
    public const string MessagesReceivedInstrumentName = "teams.messages.received";

    /// <summary>Histogram instrument name for card delivery latency (canonical per tech-spec.md §4.4).</summary>
    public const string CardDeliveryDurationInstrumentName = "teams.card.delivery.duration_ms";

    /// <summary>Observable gauge instrument name for the outbox queue depth.</summary>
    public const string OutboxQueueDepthInstrumentName = "teams.outbox.queue_depth";

    /// <summary>
    /// Canonical tag key for the correlation ID. Stamped on <see cref="Activity"/> spans
    /// only — deliberately NOT applied to counter / histogram instruments because
    /// per-request values would explode the metric time-series cardinality.
    /// </summary>
    public const string CorrelationIdTag = "correlationId";

    /// <summary>Canonical tag key for the payload classification.</summary>
    public const string MessageTypeTag = "messageType";

    /// <summary>Canonical tag key for the destination classification.</summary>
    public const string DestinationTypeTag = "destinationType";

    /// <summary>Tag value for an outbound user-targeted destination.</summary>
    public const string DestinationTypeUser = "User";

    /// <summary>Tag value for an outbound channel-targeted destination.</summary>
    public const string DestinationTypeChannel = "Channel";

    /// <summary>Tag value for an outbound conversation-targeted destination (routed by ConversationId).</summary>
    public const string DestinationTypeConversation = "Conversation";

    /// <summary>Tag value used for the inbound receive span when the producer is unknown.</summary>
    public const string DestinationTypeInbound = "Inbound";

    /// <summary>Message type tag for the canonical <see cref="Abstractions.MessengerMessage"/>.</summary>
    public const string MessageTypeMessengerMessage = "MessengerMessage";

    /// <summary>Message type tag for the canonical <see cref="Abstractions.AgentQuestion"/>.</summary>
    public const string MessageTypeAgentQuestion = "AgentQuestion";

    /// <summary>Message type tag for an inbound event from the in-process channel.</summary>
    public const string MessageTypeInboundEvent = "InboundEvent";

    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly Counter<long> _messagesSent;
    private readonly Counter<long> _messagesReceived;
    private readonly Histogram<double> _cardDeliveryDurationMs;
    private readonly IOutboxQueueDepthProvider _queueDepthProvider;

    /// <summary>
    /// Construct a new telemetry surface. The host registers a single instance as a
    /// singleton so exporters subscribed to <see cref="ActivitySourceName"/> /
    /// <see cref="MeterName"/> see a single source.
    /// </summary>
    /// <param name="queueDepthProvider">
    /// Supplies the observed depth for the <see cref="OutboxQueueDepthInstrumentName"/>
    /// gauge. Hosts that have not wired a real outbox provider can register
    /// <see cref="NullOutboxQueueDepthProvider.Instance"/> — the gauge then reports
    /// zero. Required (not optional) so DI mis-wiring fails loudly at startup.
    /// </param>
    /// <exception cref="ArgumentNullException">If <paramref name="queueDepthProvider"/> is null.</exception>
    public TeamsConnectorTelemetry(IOutboxQueueDepthProvider queueDepthProvider)
    {
        _queueDepthProvider = queueDepthProvider ?? throw new ArgumentNullException(nameof(queueDepthProvider));

        _activitySource = new ActivitySource(ActivitySourceName);
        _meter = new Meter(MeterName);

        _messagesSent = _meter.CreateCounter<long>(
            name: MessagesSentInstrumentName,
            unit: "{message}",
            description: "Outbound messages sent through TeamsMessengerConnector (MessengerMessage + AgentQuestion).");

        _messagesReceived = _meter.CreateCounter<long>(
            name: MessagesReceivedInstrumentName,
            unit: "{message}",
            description: "Inbound MessengerEvents read from the in-process channel by TeamsMessengerConnector.ReceiveAsync.");

        _cardDeliveryDurationMs = _meter.CreateHistogram<double>(
            name: CardDeliveryDurationInstrumentName,
            unit: "ms",
            description: "Bot Framework card delivery latency observed at the TeamsMessengerConnector boundary. P95 budget < 3 s per tech-spec.md §4.4.");

        _meter.CreateObservableGauge<long>(
            name: OutboxQueueDepthInstrumentName,
            observeValue: () => _queueDepthProvider.GetQueueDepth(),
            unit: "{entry}",
            description: "Number of pending entries currently in the Teams outbox (last observed value).");
    }

    /// <summary>
    /// Start an <see cref="ActivityKind.Client"/> span for an outbound send operation
    /// tagged with the canonical attributes. Returns <c>null</c> when no listener is
    /// subscribed — callers must null-check before stamping additional tags.
    /// </summary>
    public Activity? StartSendActivity(string name, string? correlationId, string messageType, string destinationType)
    {
        var activity = _activitySource.StartActivity(name, ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(CorrelationIdTag, correlationId ?? string.Empty);
        activity.SetTag(MessageTypeTag, messageType);
        activity.SetTag(DestinationTypeTag, destinationType);
        return activity;
    }

    /// <summary>
    /// Start an <see cref="ActivityKind.Consumer"/> span for an inbound receive operation.
    /// </summary>
    public Activity? StartReceiveActivity(string? correlationId = null)
    {
        var activity = _activitySource.StartActivity(ReceiveActivityName, ActivityKind.Consumer);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(CorrelationIdTag, correlationId ?? string.Empty);
        activity.SetTag(MessageTypeTag, MessageTypeInboundEvent);
        activity.SetTag(DestinationTypeTag, DestinationTypeInbound);
        return activity;
    }

    /// <summary>
    /// Increment <see cref="MessagesSentInstrumentName"/> by 1 with the canonical tag set.
    /// The tag set is intentionally restricted to <see cref="MessageTypeTag"/> and
    /// <see cref="DestinationTypeTag"/> to keep the time-series cardinality bounded;
    /// per-request correlation lives on the span emitted by
    /// <see cref="StartSendActivity"/>.
    /// </summary>
    public void RecordMessageSent(string messageType, string destinationType)
    {
        _messagesSent.Add(1, BuildTags(messageType, destinationType));
    }

    /// <summary>
    /// Increment <see cref="MessagesReceivedInstrumentName"/> by 1 with the canonical
    /// tag set. See <see cref="RecordMessageSent"/> for the cardinality rationale.
    /// </summary>
    public void RecordMessageReceived(string messageType, string destinationType)
    {
        _messagesReceived.Add(1, BuildTags(messageType, destinationType));
    }

    /// <summary>
    /// Record a card-delivery latency sample on the <see cref="CardDeliveryDurationInstrumentName"/>
    /// histogram. The same instrument name is also published by
    /// <see cref="AgentSwarm.Messaging.Core.OutboxMetrics"/>; OpenTelemetry exporters
    /// aggregate both contributions against the §4.4 P95 budget. The tag set is
    /// restricted to the bounded classifiers (see <see cref="RecordMessageSent"/>);
    /// the per-request correlation ID is carried on the surrounding span.
    /// </summary>
    public void RecordCardDeliveryDurationMs(double durationMs, string messageType, string destinationType)
    {
        _cardDeliveryDurationMs.Record(durationMs, BuildTags(messageType, destinationType));
    }

    private static KeyValuePair<string, object?>[] BuildTags(string messageType, string destinationType)
    {
        return new KeyValuePair<string, object?>[]
        {
            new(MessageTypeTag, messageType),
            new(DestinationTypeTag, destinationType),
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _meter.Dispose();
        _activitySource.Dispose();
    }
}
