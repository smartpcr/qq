using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AgentSwarm.Messaging.Teams.Outbox;

/// <summary>
/// <see cref="IProactiveNotifier"/> decorator that routes every Stage 4.2 proactive send
/// through <see cref="IMessageOutbox.EnqueueAsync"/> rather than invoking the inner
/// notifier (and ultimately <c>CloudAdapter.ContinueConversationAsync</c>) directly.
/// Implements the Stage 6.1 brief's requirement that "every send method on the
/// proactive notifier first persists an OutboxEntry instead of calling
/// ContinueConversationAsync".
/// </summary>
/// <remarks>
/// <para>
/// The decorator does NOT call the wrapped <see cref="TeamsProactiveNotifier"/>; the
/// outbox <see cref="OutboxRetryEngine"/> drains the queue and dispatches via
/// <see cref="TeamsOutboxDispatcher"/>, which is the only component that ever invokes
/// the inner notifier in production. Direct (non-outbox) callers of the inner notifier
/// are reserved for unit tests that pin the underlying send semantics.
/// </para>
/// <para>
/// <b>Conversation-reference snapshot.</b> Each enqueue resolves the target's
/// <see cref="TeamsConversationReference"/> from <see cref="IConversationReferenceStore"/>
/// and serializes the underlying Bot Framework
/// <see cref="Microsoft.Bot.Schema.ConversationReference"/> into
/// <see cref="OutboxEntry.ConversationReferenceJson"/>. The snapshot is informational —
/// the engine's <see cref="TeamsOutboxDispatcher"/> currently re-resolves the live store
/// at delivery to honour any uninstall/reinstall transitions, but the snapshot serves
/// as a durable audit record of the reference at enqueue time and lets a future engine
/// version perform offline replay without store access.
/// </para>
/// <para>
/// <b>Failure during snapshot.</b> A
/// <see cref="ConversationReferenceNotFoundException"/> at enqueue time short-circuits
/// the call — the message is never written to the outbox. This is intentional: the
/// orchestrator's proactive trigger sees the failure immediately rather than scheduling
/// a deliver-and-retry loop for a target that was never reachable. The same failure
/// raised at dispatch time (target uninstalled between enqueue and delivery) is
/// dead-lettered by the engine.
/// </para>
/// </remarks>
public sealed class OutboxBackedProactiveNotifier : IProactiveNotifier
{
    private readonly IMessageOutbox _outbox;
    private readonly IConversationReferenceStore _conversationReferenceStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxBackedProactiveNotifier> _logger;

    /// <summary>Construct the decorator.</summary>
    public OutboxBackedProactiveNotifier(
        IMessageOutbox outbox,
        IConversationReferenceStore conversationReferenceStore,
        ILogger<OutboxBackedProactiveNotifier> logger,
        TimeProvider? timeProvider = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _conversationReferenceStore = conversationReferenceStore ?? throw new ArgumentNullException(nameof(conversationReferenceStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task SendProactiveAsync(string tenantId, string userId, MessengerMessage message, CancellationToken ct)
    {
        ValidateRequired(tenantId, nameof(tenantId));
        ValidateRequired(userId, nameof(userId));
        ArgumentNullException.ThrowIfNull(message);

        // Stage 6.3 iter-2 — enrichment scope covers the enqueue + log.
        using var logScope = AgentSwarm.Messaging.Teams.Diagnostics.TeamsLogScope.BeginScope(
            _logger,
            correlationId: message.CorrelationId,
            tenantId: tenantId,
            userId: userId);

        await EnqueueUserMessageAsync(tenantId, userId, message, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendProactiveQuestionAsync(string tenantId, string userId, AgentQuestion question, CancellationToken ct)
    {
        ValidateRequired(tenantId, nameof(tenantId));
        ValidateRequired(userId, nameof(userId));
        ArgumentNullException.ThrowIfNull(question);

        using var logScope = AgentSwarm.Messaging.Teams.Diagnostics.TeamsLogScope.BeginScope(
            _logger,
            correlationId: question.CorrelationId,
            tenantId: tenantId,
            userId: userId);

        await EnqueueUserQuestionAsync(tenantId, userId, question, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendToChannelAsync(string tenantId, string channelId, MessengerMessage message, CancellationToken ct)
    {
        ValidateRequired(tenantId, nameof(tenantId));
        ValidateRequired(channelId, nameof(channelId));
        ArgumentNullException.ThrowIfNull(message);

        using var logScope = AgentSwarm.Messaging.Teams.Diagnostics.TeamsLogScope.BeginScope(
            _logger,
            correlationId: message.CorrelationId,
            tenantId: tenantId,
            userId: null);

        await EnqueueChannelMessageAsync(tenantId, channelId, message, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendQuestionToChannelAsync(string tenantId, string channelId, AgentQuestion question, CancellationToken ct)
    {
        ValidateRequired(tenantId, nameof(tenantId));
        ValidateRequired(channelId, nameof(channelId));
        ArgumentNullException.ThrowIfNull(question);

        using var logScope = AgentSwarm.Messaging.Teams.Diagnostics.TeamsLogScope.BeginScope(
            _logger,
            correlationId: question.CorrelationId,
            tenantId: tenantId,
            userId: null);

        await EnqueueChannelQuestionAsync(tenantId, channelId, question, ct).ConfigureAwait(false);
    }

    private async Task EnqueueUserMessageAsync(string tenantId, string userId, MessengerMessage message, CancellationToken ct)
    {
        var stored = await _conversationReferenceStore
            .GetByInternalUserIdAsync(tenantId, userId, ct)
            .ConfigureAwait(false)
            ?? throw ConversationReferenceNotFoundException.ForUser(tenantId, userId);

        var entry = BuildEntry(
            correlationId: message.CorrelationId,
            destinationType: OutboxDestinationTypes.Personal,
            destinationId: userId,
            destination: BuildPersonalDestination(tenantId, userId),
            payloadType: OutboxPayloadTypes.MessengerMessage,
            payload: new TeamsOutboxPayloadEnvelope { Message = message },
            referenceJson: stored.ReferenceJson);

        await _outbox.EnqueueAsync(entry, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Enqueued outbox entry {OutboxEntryId} for proactive MessengerMessage {MessageId} (correlation {CorrelationId}) -> user {UserId} in tenant {TenantId}.",
            entry.OutboxEntryId,
            message.MessageId,
            message.CorrelationId,
            userId,
            tenantId);
    }

    private async Task EnqueueUserQuestionAsync(string tenantId, string userId, AgentQuestion question, CancellationToken ct)
    {
        var stored = await _conversationReferenceStore
            .GetByInternalUserIdAsync(tenantId, userId, ct)
            .ConfigureAwait(false)
            ?? throw ConversationReferenceNotFoundException.ForUser(tenantId, userId, question.QuestionId);

        var entry = BuildEntry(
            correlationId: question.CorrelationId,
            destinationType: OutboxDestinationTypes.Personal,
            destinationId: userId,
            destination: BuildPersonalDestination(tenantId, userId),
            payloadType: OutboxPayloadTypes.AgentQuestion,
            payload: new TeamsOutboxPayloadEnvelope { Question = question },
            referenceJson: stored.ReferenceJson);

        await _outbox.EnqueueAsync(entry, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Enqueued outbox entry {OutboxEntryId} for proactive AgentQuestion {QuestionId} (correlation {CorrelationId}) -> user {UserId} in tenant {TenantId}.",
            entry.OutboxEntryId,
            question.QuestionId,
            question.CorrelationId,
            userId,
            tenantId);
    }

    private async Task EnqueueChannelMessageAsync(string tenantId, string channelId, MessengerMessage message, CancellationToken ct)
    {
        var stored = await _conversationReferenceStore
            .GetByChannelIdAsync(tenantId, channelId, ct)
            .ConfigureAwait(false)
            ?? throw ConversationReferenceNotFoundException.ForChannel(tenantId, channelId);

        var entry = BuildEntry(
            correlationId: message.CorrelationId,
            destinationType: OutboxDestinationTypes.Channel,
            destinationId: channelId,
            destination: BuildChannelDestination(tenantId, channelId),
            payloadType: OutboxPayloadTypes.MessengerMessage,
            payload: new TeamsOutboxPayloadEnvelope { Message = message },
            referenceJson: stored.ReferenceJson);

        await _outbox.EnqueueAsync(entry, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Enqueued outbox entry {OutboxEntryId} for proactive MessengerMessage {MessageId} (correlation {CorrelationId}) -> channel {ChannelId} in tenant {TenantId}.",
            entry.OutboxEntryId,
            message.MessageId,
            message.CorrelationId,
            channelId,
            tenantId);
    }

    private async Task EnqueueChannelQuestionAsync(string tenantId, string channelId, AgentQuestion question, CancellationToken ct)
    {
        var stored = await _conversationReferenceStore
            .GetByChannelIdAsync(tenantId, channelId, ct)
            .ConfigureAwait(false)
            ?? throw ConversationReferenceNotFoundException.ForChannel(tenantId, channelId, question.QuestionId);

        var entry = BuildEntry(
            correlationId: question.CorrelationId,
            destinationType: OutboxDestinationTypes.Channel,
            destinationId: channelId,
            destination: BuildChannelDestination(tenantId, channelId),
            payloadType: OutboxPayloadTypes.AgentQuestion,
            payload: new TeamsOutboxPayloadEnvelope { Question = question },
            referenceJson: stored.ReferenceJson);

        await _outbox.EnqueueAsync(entry, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Enqueued outbox entry {OutboxEntryId} for proactive AgentQuestion {QuestionId} (correlation {CorrelationId}) -> channel {ChannelId} in tenant {TenantId}.",
            entry.OutboxEntryId,
            question.QuestionId,
            question.CorrelationId,
            channelId,
            tenantId);
    }

    private OutboxEntry BuildEntry(
        string correlationId,
        string destinationType,
        string destinationId,
        string destination,
        string payloadType,
        TeamsOutboxPayloadEnvelope payload,
        string referenceJson)
    {
        return new OutboxEntry
        {
            OutboxEntryId = Guid.NewGuid().ToString("N"),
            CorrelationId = correlationId,
            Destination = destination,
            DestinationType = destinationType,
            DestinationId = destinationId,
            PayloadType = payloadType,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload, TeamsOutboxPayloadEnvelope.JsonOptions),
            ConversationReferenceJson = referenceJson,
            CreatedAt = _timeProvider.GetUtcNow(),
        };
    }

    private static string BuildPersonalDestination(string tenantId, string userId)
        => $"teams://{Uri.EscapeDataString(tenantId)}/user/{Uri.EscapeDataString(userId)}";

    private static string BuildChannelDestination(string tenantId, string channelId)
        => $"teams://{Uri.EscapeDataString(tenantId)}/channel/{Uri.EscapeDataString(channelId)}";

    private static void ValidateRequired(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.", name);
        }
    }
}
