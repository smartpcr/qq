using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Outbox;

/// <summary>
/// <see cref="IMessengerConnector"/> decorator that routes
/// <see cref="SendMessageAsync"/> and <see cref="SendQuestionAsync"/> through
/// <see cref="IMessageOutbox.EnqueueAsync"/> rather than invoking the inner
/// connector directly. <see cref="ReceiveAsync"/> is delegated to the inner connector
/// unchanged — inbound events do not flow through the outbox.
/// </summary>
/// <remarks>
/// <para>
/// Implements the Stage 6.1 brief verbatim: "Refactor TeamsProactiveNotifier and
/// TeamsMessengerConnector to remove direct ContinueConversationAsync calls; every send
/// method enqueues an OutboxEntry instead". The wrapped <see cref="TeamsMessengerConnector"/>
/// remains the canonical delivery implementation but is only invoked from
/// <see cref="TeamsOutboxDispatcher"/> after the engine dequeues the entry.
/// </para>
/// <para>
/// <b>Why <see cref="SendMessageAsync"/> resolves the conversation reference here.</b>
/// The outbound <see cref="MessengerMessage.ConversationId"/> uniquely identifies the
/// target Bot Framework conversation; the decorator looks up the persisted
/// <see cref="TeamsConversationReference"/> via <see cref="IConversationReferenceRouter"/>
/// and stamps tenant ID + scope on the entry so the dispatcher can route without a
/// second lookup. The actual reference JSON snapshot is also stamped so the outbox row
/// is fully self-describing.
/// </para>
/// </remarks>
public sealed class OutboxBackedMessengerConnector : IMessengerConnector
{
    private readonly IMessengerConnector _innerConnector;
    private readonly IMessageOutbox _outbox;
    private readonly IConversationReferenceRouter _conversationReferenceRouter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxBackedMessengerConnector> _logger;

    /// <summary>Construct the decorator.</summary>
    /// <param name="innerConnector">The wrapped <see cref="TeamsMessengerConnector"/>. Used for <see cref="ReceiveAsync"/> only.</param>
    /// <param name="outbox">Outbox queue for outbound deliveries.</param>
    /// <param name="conversationReferenceRouter">Router used to resolve the tenant scope for outbound messages.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Optional clock (defaults to <see cref="TimeProvider.System"/>).</param>
    public OutboxBackedMessengerConnector(
        IMessengerConnector innerConnector,
        IMessageOutbox outbox,
        IConversationReferenceRouter conversationReferenceRouter,
        ILogger<OutboxBackedMessengerConnector> logger,
        TimeProvider? timeProvider = null)
    {
        _innerConnector = innerConnector ?? throw new ArgumentNullException(nameof(innerConnector));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _conversationReferenceRouter = conversationReferenceRouter ?? throw new ArgumentNullException(nameof(conversationReferenceRouter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task SendMessageAsync(MessengerMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Resolve the persisted reference via the router so we can capture the snapshot
        // and the tenant ID. The lookup is intentionally identical to what
        // TeamsMessengerConnector.SendMessageAsync does at dispatch time — failing here
        // surfaces missing-reference errors at enqueue rather than after the delivery
        // window opens.
        var stored = await _conversationReferenceRouter
            .GetByConversationIdAsync(message.ConversationId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No TeamsConversationReference is registered for ConversationId '{message.ConversationId}'; refusing to enqueue an outbound MessengerMessage that cannot be routed.");

        var entry = new OutboxEntry
        {
            OutboxEntryId = Guid.NewGuid().ToString("N"),
            CorrelationId = message.CorrelationId,
            Destination = $"teams://{Uri.EscapeDataString(stored.TenantId)}/conversation/{Uri.EscapeDataString(message.ConversationId)}",
            DestinationType = null,
            DestinationId = message.ConversationId,
            PayloadType = OutboxPayloadTypes.MessengerMessage,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new TeamsOutboxPayloadEnvelope { Message = message },
                TeamsOutboxPayloadEnvelope.JsonOptions),
            ConversationReferenceJson = stored.ReferenceJson,
            CreatedAt = _timeProvider.GetUtcNow(),
        };

        await _outbox.EnqueueAsync(entry, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Enqueued outbox entry {OutboxEntryId} for outbound MessengerMessage {MessageId} (correlation {CorrelationId}) -> conversation {ConversationId}.",
            entry.OutboxEntryId,
            message.MessageId,
            message.CorrelationId,
            message.ConversationId);
    }

    /// <inheritdoc />
    public async Task SendQuestionAsync(AgentQuestion question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(question);

        var validationErrors = question.Validate();
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"AgentQuestion '{question.QuestionId}' is invalid: {string.Join("; ", validationErrors)}");
        }

        // Resolve via the question's own routing fields — exactly one of TargetUserId /
        // TargetChannelId is set (enforced by Validate()).
        TeamsConversationReference stored;
        string destinationType;
        string destinationId;
        string destination;
        if (!string.IsNullOrWhiteSpace(question.TargetUserId))
        {
            stored = await _conversationReferenceRouter
                .GetByConversationIdAsync(question.TargetUserId!, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"AgentQuestion '{question.QuestionId}' targets user '{question.TargetUserId}' but no TeamsConversationReference is registered for that ID.");
            destinationType = OutboxDestinationTypes.Personal;
            destinationId = question.TargetUserId!;
            destination = $"teams://{Uri.EscapeDataString(question.TenantId)}/user/{Uri.EscapeDataString(destinationId)}";
        }
        else
        {
            stored = await _conversationReferenceRouter
                .GetByConversationIdAsync(question.TargetChannelId!, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"AgentQuestion '{question.QuestionId}' targets channel '{question.TargetChannelId}' but no TeamsConversationReference is registered for that ID.");
            destinationType = OutboxDestinationTypes.Channel;
            destinationId = question.TargetChannelId!;
            destination = $"teams://{Uri.EscapeDataString(question.TenantId)}/channel/{Uri.EscapeDataString(destinationId)}";
        }

        var entry = new OutboxEntry
        {
            OutboxEntryId = Guid.NewGuid().ToString("N"),
            CorrelationId = question.CorrelationId,
            Destination = destination,
            DestinationType = destinationType,
            DestinationId = destinationId,
            PayloadType = OutboxPayloadTypes.AgentQuestion,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new TeamsOutboxPayloadEnvelope { Question = question },
                TeamsOutboxPayloadEnvelope.JsonOptions),
            ConversationReferenceJson = stored.ReferenceJson,
            CreatedAt = _timeProvider.GetUtcNow(),
        };

        await _outbox.EnqueueAsync(entry, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Enqueued outbox entry {OutboxEntryId} for AgentQuestion {QuestionId} (correlation {CorrelationId}) -> {DestinationType} {DestinationId}.",
            entry.OutboxEntryId,
            question.QuestionId,
            question.CorrelationId,
            destinationType,
            destinationId);
    }

    /// <inheritdoc />
    public Task<MessengerEvent> ReceiveAsync(CancellationToken ct)
        => _innerConnector.ReceiveAsync(ct);
}
