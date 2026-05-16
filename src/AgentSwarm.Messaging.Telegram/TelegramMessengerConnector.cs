using System.Globalization;
using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram.Pipeline;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Stage 2.6 concrete <see cref="IMessengerConnector"/> for the Telegram
/// platform. Acts as the boundary between the agent swarm event loop
/// (callers of <see cref="SendMessageAsync"/> /
/// <see cref="SendQuestionAsync"/>) and the durable outbound pipeline
/// (<see cref="IOutboundQueue"/>); the inbound half drains
/// pipeline-processed events via <see cref="ReceiveAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Outbound — render at enqueue time, send at process time.</b>
/// architecture.md §4.12 / implementation-plan.md Stage 1.4 require that
/// non-question outbound messages (<c>StatusUpdate</c>, <c>Alert</c>,
/// <c>CommandAck</c>) carry their <i>already-rendered</i> Telegram text
/// in <see cref="OutboundMessage.Payload"/> so the Stage 4.1
/// <c>OutboundQueueProcessor</c> can hand the payload directly to
/// <see cref="Core.IMessageSender.SendTextAsync"/> without re-escaping.
/// This connector therefore copies <see cref="MessengerMessage.Text"/>
/// verbatim into <see cref="OutboundMessage.Payload"/>; the caller is
/// expected to supply MarkdownV2-rendered text. For questions, the
/// payload is a short diagnostic preview (<c>[Severity] Title</c>) —
/// the real render is performed at send time by
/// <see cref="Sending.TelegramMessageSender.SendQuestionAsync"/>, which
/// rehydrates the full <see cref="AgentQuestionEnvelope"/> from
/// <see cref="OutboundMessage.SourceEnvelopeJson"/>.
/// </para>
/// <para>
/// <b>Idempotency key derivation (architecture.md §3.1).</b>
/// <list type="bullet">
///   <item><description><c>Question</c> → <c>q:{AgentId}:{QuestionId}</c></description></item>
///   <item><description><c>Alert</c> → <c>alert:{AgentId}:{AlertId}</c>
///     (AlertId is supplied via <see cref="MessengerMessage.Metadata"/>
///     under <see cref="AlertIdMetadataKey"/>)</description></item>
///   <item><description><c>StatusUpdate</c> → <c>s:{AgentId}:{CorrelationId}</c></description></item>
///   <item><description><c>CommandAck</c> → <c>c:{CorrelationId}</c></description></item>
/// </list>
/// The default <see cref="OutboundSourceType"/> for
/// <see cref="SendMessageAsync"/> is <see cref="OutboundSourceType.StatusUpdate"/>
/// per the Stage 2.6 scenario "SendMessageAsync delegates to outbound
/// queue"; callers signal a non-default via
/// <see cref="MessengerMessage.Metadata"/>[<see cref="SourceTypeMetadataKey"/>]
/// (case-insensitive parse of the
/// <see cref="OutboundSourceType"/> enum name). Each derivation is total
/// on its inputs — when a required input is missing (e.g. AlertId for an
/// <c>Alert</c>) the connector throws <see cref="ArgumentException"/>
/// loudly rather than synthesizing a key that would silently collapse
/// distinct events into one outbox row.
/// </para>
/// <para>
/// <b>Routing — <see cref="OutboundMessage.ChatId"/>.</b> Both methods
/// resolve the target Telegram chat from a routing-metadata sidecar
/// (<see cref="MessengerMessage.Metadata"/> for messages,
/// <see cref="AgentQuestionEnvelope.RoutingMetadata"/> for questions)
/// keyed by <see cref="TelegramChatIdMetadataKey"/>. A missing or
/// unparseable chat id throws <see cref="ArgumentException"/>: silently
/// defaulting to chat 0 would either NPE in the Telegram client or send
/// to the wrong operator. The chat-id type is <see cref="long"/> per
/// architecture.md §3.1's canonical-type convention.
/// </para>
/// <para>
/// <b>Inbound — drain semantics.</b>
/// <see cref="ReceiveAsync"/> returns whatever the shared
/// <see cref="ProcessedMessengerEventChannel"/> currently buffers and
/// does NOT block when the channel is empty (the contract is poll-based:
/// callers loop, the connector is a passive drain). The pipeline writes
/// each <see cref="MessengerEvent"/> immediately after
/// <see cref="ITelegramUpdatePipeline.ProcessAsync"/> returns,
/// regardless of <see cref="PipelineResult.Handled"/> /
/// <see cref="PipelineResult.Succeeded"/> — every pipeline outcome is a
/// definitive "this event has been processed" signal that downstream
/// observers (audit, metrics) may consume.
/// </para>
/// <para>
/// <b>Inbound — per-call batch cap.</b> Each <see cref="ReceiveAsync"/>
/// invocation drains at most <see cref="MaxDrainBatchSize"/> events
/// before returning, even if more are buffered. The story-level
/// requirement is "burst alerts from 100+ agents without message loss"
/// (functional requirements / Performance row), and the upstream
/// <see cref="ProcessedMessengerEventChannel"/> is unbounded precisely
/// so the producer side never drops — but on the consumer side, a
/// single drain returning thousands of events in one
/// <see cref="IReadOnlyList{T}"/> allocation creates a latency spike
/// and GC pressure for the agent-swarm event loop. Capping per call
/// preserves the no-loss guarantee (the residue stays buffered for the
/// next poll) while bounding the worst-case allocation; this fits the
/// existing poll-based contract because callers already loop on
/// <see cref="ReceiveAsync"/>.
/// </para>
/// <para>
/// <b>Lifecycle.</b> Registered as a singleton in
/// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/>: the
/// connector is stateless beyond its injected dependencies and the
/// underlying <see cref="ProcessedMessengerEventChannel"/> is itself
/// singleton-scoped so the writer (pipeline) and reader (this
/// connector) share one buffer.
/// </para>
/// </remarks>
public sealed class TelegramMessengerConnector : IMessengerConnector
{
    /// <summary>
    /// Metadata key carrying the target Telegram chat id. Required on
    /// both <see cref="MessengerMessage.Metadata"/> (for
    /// <see cref="SendMessageAsync"/>) and
    /// <see cref="AgentQuestionEnvelope.RoutingMetadata"/> (for
    /// <see cref="SendQuestionAsync"/>). The value must parse as a
    /// <see cref="long"/> per architecture.md §3.1's chat-id type
    /// convention.
    /// </summary>
    public const string TelegramChatIdMetadataKey = "TelegramChatId";

    /// <summary>
    /// Optional metadata key on <see cref="MessengerMessage.Metadata"/>
    /// naming an <see cref="OutboundSourceType"/> override
    /// (case-insensitive). Absent values fall back to
    /// <see cref="OutboundSourceType.StatusUpdate"/>; <b>unrecognised</b>
    /// values throw <see cref="ArgumentException"/> per iter-2 evaluator
    /// item 1 — a typo in the metadata would otherwise enqueue an alert
    /// or ack with the wrong <see cref="OutboundSourceType"/> and the
    /// wrong idempotency key (e.g. <c>s:{AgentId}:{CorrelationId}</c>
    /// instead of <c>alert:{AgentId}:{AlertId}</c>), which the
    /// downstream UNIQUE-key dedup cannot reconcile.
    /// </summary>
    public const string SourceTypeMetadataKey = "SourceType";

    /// <summary>
    /// Metadata key carrying the alert identifier when
    /// <see cref="SourceTypeMetadataKey"/> resolves to
    /// <see cref="OutboundSourceType.Alert"/>. Required for that
    /// source-type so the idempotency key
    /// <c>alert:{AgentId}:{AlertId}</c> is well-formed.
    /// </summary>
    public const string AlertIdMetadataKey = "AlertId";

    /// <summary>
    /// Maximum number of <see cref="MessengerEvent"/> instances drained
    /// by a single <see cref="ReceiveAsync"/> invocation. Bounds the
    /// worst-case <see cref="List{T}"/> allocation and per-call latency
    /// when the upstream <see cref="ProcessedMessengerEventChannel"/>
    /// has buffered a large burst (e.g. the 100+ agent scenario in the
    /// story functional requirements). Any residue stays in the
    /// channel for the next poll; the consumer contract is already
    /// loop-based, so capping per call does not regress the
    /// "no message loss" guarantee — it only changes how many polls
    /// the consumer needs to fully drain a burst.
    /// </summary>
    /// <remarks>
    /// 500 is sized for the documented 100+ agent burst: even when 100
    /// agents each enqueue ~5 events between drain ticks the entire
    /// burst fits in a single call. Choosing a hard cap rather than a
    /// constructor / per-call parameter keeps the
    /// <see cref="IMessengerConnector"/> abstraction stable; tuning is
    /// available as a follow-up if real-world telemetry shows the cap
    /// is too tight for a specific deployment.
    /// </remarks>
    internal const int MaxDrainBatchSize = 500;

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new()
    {
        // Deterministic, compact JSON suitable for OutboundMessage.SourceEnvelopeJson.
        // PropertyNamingPolicy stays default (PascalCase) so the QuestionRecoverySweep
        // round-trip parses with the same JsonSerializerOptions on read.
        WriteIndented = false,
    };

    private readonly IOutboundQueue _outboundQueue;
    private readonly ProcessedMessengerEventChannel _processedEventChannel;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TelegramMessengerConnector> _logger;

    public TelegramMessengerConnector(
        IOutboundQueue outboundQueue,
        ProcessedMessengerEventChannel processedEventChannel,
        TimeProvider timeProvider,
        ILogger<TelegramMessengerConnector> logger)
    {
        _outboundQueue = outboundQueue ?? throw new ArgumentNullException(nameof(outboundQueue));
        _processedEventChannel = processedEventChannel ?? throw new ArgumentNullException(nameof(processedEventChannel));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task SendMessageAsync(MessengerMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var sourceType = ResolveSourceType(message.Metadata);
        var (idempotencyKey, sourceId) = BuildOutboundKey(sourceType, message);
        var chatId = ParseChatId(message.Metadata, message.CorrelationId, nameof(message));

        // architecture.md §3.1 (OutboundMessage.SourceEnvelopeJson):
        // "Populated only when SourceType = Question (stores the full
        // AgentQuestionEnvelope JSON) or SourceType = Alert (stores the
        // full AgentAlertEvent JSON)." At the IMessengerConnector
        // boundary the only event shape we have is MessengerMessage, so
        // for Alert we serialize the inbound MessengerMessage verbatim
        // — that becomes the recovery / dead-letter replay record. The
        // Stage 2.7 swarm ingress is the layer that maps
        // AgentAlertEvent → MessengerMessage; preserving the
        // MessengerMessage here is the most faithful record we can
        // produce without leaking SwarmEvent types into the connector.
        // CommandAck / StatusUpdate stay null per the §3.1 table.
        var sourceEnvelopeJson = sourceType == OutboundSourceType.Alert
            ? JsonSerializer.Serialize(message, EnvelopeJsonOptions)
            : null;

        var outbound = new OutboundMessage
        {
            MessageId = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            ChatId = chatId,
            Payload = message.Text,
            SourceEnvelopeJson = sourceEnvelopeJson,
            Severity = message.Severity,
            SourceType = sourceType,
            SourceId = sourceId,
            CreatedAt = _timeProvider.GetUtcNow(),
            CorrelationId = message.CorrelationId,
        };

        _logger.LogInformation(
            "TelegramMessengerConnector enqueueing outbound message. CorrelationId={CorrelationId} MessageId={MessageId} IdempotencyKey={IdempotencyKey} ChatId={ChatId} SourceType={SourceType} Severity={Severity} HasSourceEnvelopeJson={HasSourceEnvelopeJson}",
            message.CorrelationId,
            outbound.MessageId,
            outbound.IdempotencyKey,
            outbound.ChatId,
            outbound.SourceType,
            outbound.Severity,
            sourceEnvelopeJson is not null);

        return _outboundQueue.EnqueueAsync(outbound, ct);
    }

    /// <inheritdoc />
    public Task SendQuestionAsync(AgentQuestionEnvelope envelope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var question = envelope.Question;
        var chatId = ParseChatId(envelope.RoutingMetadata, question.CorrelationId, nameof(envelope));

        var idempotencyKey = string.Create(
            CultureInfo.InvariantCulture,
            $"q:{question.AgentId}:{question.QuestionId}");

        // Serialize the FULL envelope (Question + ProposedDefaultActionId +
        // RoutingMetadata) into SourceEnvelopeJson — the Stage 4.1
        // OutboundQueueProcessor / TelegramMessageSender / QuestionRecoverySweep
        // all rehydrate the envelope from this single field, so a partial
        // serialization would silently drop the proposed-default and
        // routing sidecar on the recovery path.
        var envelopeJson = JsonSerializer.Serialize(envelope, EnvelopeJsonOptions);

        // Payload is a short diagnostic preview, NOT the rendered Telegram
        // message — architecture.md §3.1 explicitly notes the question
        // payload is preserved for debugging / dead-letter inspection
        // while the real MarkdownV2 render happens at send time from
        // SourceEnvelopeJson.
        var payloadPreview = string.Create(
            CultureInfo.InvariantCulture,
            $"[{question.Severity}] {question.Title}");

        var outbound = new OutboundMessage
        {
            MessageId = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            ChatId = chatId,
            Payload = payloadPreview,
            SourceEnvelopeJson = envelopeJson,
            Severity = question.Severity,
            SourceType = OutboundSourceType.Question,
            SourceId = question.QuestionId,
            CreatedAt = _timeProvider.GetUtcNow(),
            CorrelationId = question.CorrelationId,
        };

        _logger.LogInformation(
            "TelegramMessengerConnector enqueueing outbound question. CorrelationId={CorrelationId} MessageId={MessageId} IdempotencyKey={IdempotencyKey} ChatId={ChatId} QuestionId={QuestionId} AgentId={AgentId} Severity={Severity} ProposedDefault={ProposedDefault}",
            question.CorrelationId,
            outbound.MessageId,
            outbound.IdempotencyKey,
            outbound.ChatId,
            question.QuestionId,
            question.AgentId,
            outbound.Severity,
            envelope.ProposedDefaultActionId);

        return _outboundQueue.EnqueueAsync(outbound, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MessengerEvent>> ReceiveAsync(CancellationToken ct)
    {
        // Poll-based contract: drain whatever the channel currently
        // buffers (up to MaxDrainBatchSize) and return immediately.
        // TryRead never blocks; a channel with no pending items returns
        // an empty list. The cap bounds the worst-case allocation /
        // latency when a 100+ agent burst has queued thousands of
        // events between ticks (story functional requirements:
        // "burst alerts from 100+ agents without message loss"); the
        // residue stays in the unbounded ProcessedMessengerEventChannel
        // and is delivered on the next poll, so the no-loss guarantee
        // is preserved. The CancellationToken is honoured by an early
        // exit if a caller races shutdown with a long drain, but
        // draining itself is a non-blocking operation so cancellation
        // rarely fires mid-loop.
        var drained = new List<MessengerEvent>();
        while (drained.Count < MaxDrainBatchSize
               && !ct.IsCancellationRequested
               && _processedEventChannel.Reader.TryRead(out var ev))
        {
            drained.Add(ev);
        }

        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<MessengerEvent>>(drained);
    }

    private static OutboundSourceType ResolveSourceType(IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata is null
            || !metadata.TryGetValue(SourceTypeMetadataKey, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return OutboundSourceType.StatusUpdate;
        }

        // Iter-2 evaluator item 1 — REJECT unrecognised metadata loudly
        // instead of silently coercing to StatusUpdate. The prior silent
        // fallback masked typos that would enqueue an Alert with the
        // wrong idempotency key (s:{AgentId}:{CorrelationId} instead of
        // alert:{AgentId}:{AlertId}); since the idempotency key is the
        // durable-queue de-dup primitive, that bug would silently
        // collapse logically-distinct alerts into one outbox row.
        if (!Enum.TryParse<OutboundSourceType>(raw, ignoreCase: true, out var parsed))
        {
            throw new ArgumentException(
                $"Metadata['{SourceTypeMetadataKey}']='{raw}' does not name a known {nameof(OutboundSourceType)} "
                + $"(expected one of: Alert, StatusUpdate, CommandAck). "
                + "Silently falling back to StatusUpdate would derive the wrong idempotency key and "
                + "violate architecture.md §3.1's per-SourceType key formulas.",
                nameof(metadata));
        }

        // Question is the dedicated SendQuestionAsync path; refusing it
        // here keeps the rejection point uniform (caller sees
        // ArgumentException at the routing boundary regardless of
        // whether the source-type came from metadata or programmatic
        // invocation).
        if (parsed == OutboundSourceType.Question)
        {
            throw new ArgumentException(
                $"Metadata['{SourceTypeMetadataKey}']='Question' is reserved for {nameof(SendQuestionAsync)}; "
                + "route via the envelope-accepting overload so the question's AgentId / QuestionId / "
                + "ProposedDefaultActionId / RoutingMetadata are not lost.",
                nameof(metadata));
        }

        return parsed;
    }

    private static (string IdempotencyKey, string? SourceId) BuildOutboundKey(
        OutboundSourceType sourceType,
        MessengerMessage message)
    {
        switch (sourceType)
        {
            case OutboundSourceType.Alert:
            {
                var agentId = RequireAgentId(message, sourceType);
                if (message.Metadata is null
                    || !message.Metadata.TryGetValue(AlertIdMetadataKey, out var alertId)
                    || string.IsNullOrWhiteSpace(alertId))
                {
                    throw new ArgumentException(
                        $"Alert messages require Metadata['{AlertIdMetadataKey}'] so the idempotency key 'alert:{{AgentId}}:{{AlertId}}' is well-formed; missing AlertId would collapse distinct alerts into one outbox row.",
                        nameof(message));
                }
                var key = string.Create(CultureInfo.InvariantCulture, $"alert:{agentId}:{alertId}");
                return (key, alertId);
            }
            case OutboundSourceType.StatusUpdate:
            {
                var agentId = RequireAgentId(message, sourceType);
                var key = string.Create(CultureInfo.InvariantCulture, $"s:{agentId}:{message.CorrelationId}");
                return (key, message.CorrelationId);
            }
            case OutboundSourceType.CommandAck:
            {
                var key = string.Create(CultureInfo.InvariantCulture, $"c:{message.CorrelationId}");
                return (key, message.CorrelationId);
            }
            case OutboundSourceType.Question:
                // SendMessageAsync should never produce Question messages —
                // SendQuestionAsync is the dedicated path for those because
                // the AgentQuestionEnvelope carries the QuestionId / AgentId
                // sidecar the q-key needs. Reject loudly so the caller
                // can't accidentally lose the envelope.
                throw new ArgumentException(
                    $"SourceType='Question' is reserved for {nameof(SendQuestionAsync)}; route via the envelope-accepting overload so the question's AgentId / QuestionId are not lost.",
                    nameof(message));
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(message),
                    sourceType,
                    $"Unsupported {nameof(OutboundSourceType)} for SendMessageAsync.");
        }
    }

    private static string RequireAgentId(MessengerMessage message, OutboundSourceType sourceType)
    {
        if (string.IsNullOrWhiteSpace(message.AgentId))
        {
            throw new ArgumentException(
                $"{sourceType} messages require {nameof(MessengerMessage.AgentId)}; idempotency key derivation depends on it.",
                nameof(message));
        }
        return message.AgentId;
    }

    private static long ParseChatId(
        IReadOnlyDictionary<string, string> metadata,
        string correlationId,
        string parameterName)
    {
        if (metadata is null
            || !metadata.TryGetValue(TelegramChatIdMetadataKey, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException(
                $"Routing metadata must contain '{TelegramChatIdMetadataKey}' so the outbound message can be addressed. CorrelationId={correlationId}.",
                parameterName);
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chatId))
        {
            throw new ArgumentException(
                $"Routing metadata '{TelegramChatIdMetadataKey}' must parse as a 64-bit signed integer (Telegram chat ids are int64 on the wire); got '{raw}'. CorrelationId={correlationId}.",
                parameterName);
        }

        return chatId;
    }
}
