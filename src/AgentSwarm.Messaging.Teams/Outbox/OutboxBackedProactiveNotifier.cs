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
/// <b>Decorator + dispatcher boundary.</b>
/// This decorator takes NO inner-notifier dependency — every send method enqueues
/// directly to <see cref="IMessageOutbox.EnqueueAsync"/> and returns. Delivery is
/// performed asynchronously by <see cref="Core.OutboxRetryEngine"/> →
/// <see cref="TeamsOutboxDispatcher"/>, which owns
/// <see cref="Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter"/> directly
/// and does NOT resolve <see cref="IInnerTeamsProactiveNotifier"/> or
/// <see cref="TeamsProactiveNotifier"/>. The production wiring is:
/// (1) this decorator is what hosts resolve as <see cref="IProactiveNotifier"/>;
/// (2) every send becomes a single <see cref="OutboxEntry"/>;
/// (3) the dispatcher consumes the entry and calls
/// <c>CloudAdapter.ContinueConversationAsync</c> with the snapshot below — the
/// inner <see cref="TeamsProactiveNotifier"/> registration is reserved exclusively
/// for tests and audit / diagnostic resolution, and the
/// <see cref="TeamsDirectSendBypassGuard"/> registered by
/// <see cref="TeamsOutboxServiceCollectionExtensions.AddTeamsOutboxEngine"/> rejects
/// any direct send on the concrete notifier so production code cannot bypass the
/// outbox by accidental concrete-type resolution.
/// </para>
/// <para>
/// <b>Conversation-reference snapshot.</b> Each enqueue resolves the target's
/// <see cref="TeamsConversationReference"/> from <see cref="IConversationReferenceStore"/>
/// and serializes the underlying Bot Framework
/// <see cref="Microsoft.Bot.Schema.ConversationReference"/> into
/// <see cref="OutboxEntry.ConversationReferenceJson"/>. The snapshot is the <i>only</i>
/// reference the engine's <see cref="TeamsOutboxDispatcher"/> uses at delivery — it
/// deserialises the snapshot and hands it directly to
/// <c>CloudAdapter.ContinueConversationAsync</c> without re-consulting the live
/// <see cref="IConversationReferenceStore"/>. Two consequences flow from this:
/// <list type="bullet">
///   <item><description>
///     <b>Uninstall between enqueue and delivery.</b> Bot Framework itself will reject
///     the proactive call (the conversation no longer exists), so the dispatcher
///     classifies the failure as transient or permanent via
///     <c>ClassifyTransportFailure</c>. The Stage 5.1 <c>InstallationStateGate</c> runs
///     <i>before</i> enqueue (on the direct
///     <see cref="TeamsProactiveNotifier"/> path) and at dispatch time on the outbox
///     path, providing the uninstall guard that does not require re-resolution.
///   </description></item>
///   <item><description>
///     <b>Audit + offline replay.</b> The snapshot is a durable record of the reference
///     observed at enqueue time, so a future engine version can replay the row offline
///     (e.g. after-the-fact analysis) without needing the live store.
///   </description></item>
/// </list>
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
/// <para>
/// <b>Send-time guard parity.</b> The two question-sending entry points
/// (<see cref="SendProactiveQuestionAsync"/> and <see cref="SendQuestionToChannelAsync"/>)
/// run the shared <see cref="TeamsQuestionSendGuards"/> in the same order as
/// <see cref="TeamsProactiveNotifier"/>'s direct path —
/// <see cref="TeamsQuestionSendGuards.EnsureTenantMatchesQuestion"/>,
/// <see cref="TeamsQuestionSendGuards.EnsureScopeUserTargeted"/> /
/// <see cref="TeamsQuestionSendGuards.EnsureScopeChannelTargeted"/>,
/// <see cref="TeamsQuestionSendGuards.ValidateQuestion"/> — so the observable
/// exception type and parameter name are identical for identical misuse on either
/// surface. Tenant / scope guards precede payload validation so a caller that
/// supplies a mismatched routing parameter sees an <see cref="ArgumentException"/>
/// rather than a malformed-payload <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// <b>Pre-enqueue <see cref="AgentQuestion"/> persistence contract (canonical, per
/// <c>implementation-plan.md</c> §6.1).</b> After the guards succeed the decorator
/// persists a sanitised copy of the <see cref="AgentQuestion"/>
/// (<c>ConversationId = null</c>, <c>Status = "Open"</c>) via
/// <see cref="IAgentQuestionStore.SaveAsync"/> <b>before</b> calling
/// <see cref="IMessageOutbox.EnqueueAsync"/>. This ordering closes the race where the
/// outbox engine delivers the card and the user taps approve before the question row
/// exists, which would otherwise cause
/// <c>CardActionHandler.GetByIdAsync(questionId)</c> to return <c>null</c>. The
/// <c>ConversationId</c> stamp is added back at dispatch time inside
/// <see cref="TeamsOutboxDispatcher"/> after <c>ContinueConversationAsync</c> reveals
/// the Teams conversation id. The save is retry-safe via a check-then-save guard: if
/// a row already exists with <c>Status = "Open"</c> the duplicate
/// <see cref="IAgentQuestionStore.SaveAsync"/> is skipped after
/// <see cref="TeamsQuestionSendGuards.EnsureRetryMatchesStoredQuestion"/> confirms
/// the incoming payload matches the stored row; if it exists with a terminal status
/// the call throws <see cref="InvalidOperationException"/> rather than ship a stale
/// card.
/// </para>
/// </remarks>
public sealed class OutboxBackedProactiveNotifier : IProactiveNotifier
{
    private readonly IMessageOutbox _outbox;
    private readonly IConversationReferenceStore _conversationReferenceStore;
    private readonly IAgentQuestionStore _agentQuestionStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxBackedProactiveNotifier> _logger;

    /// <summary>Construct the decorator.</summary>
    /// <param name="outbox">Durable outbox queue receiving each enqueue.</param>
    /// <param name="conversationReferenceStore">Store from which the
    /// <see cref="ConversationReference"/> snapshot is resolved at enqueue time.</param>
    /// <param name="agentQuestionStore">Question store used for the canonical pre-enqueue
    /// <see cref="IAgentQuestionStore.SaveAsync"/> step (see remarks).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Optional clock; defaults to <see cref="TimeProvider.System"/>.</param>
    public OutboxBackedProactiveNotifier(
        IMessageOutbox outbox,
        IConversationReferenceStore conversationReferenceStore,
        IAgentQuestionStore agentQuestionStore,
        ILogger<OutboxBackedProactiveNotifier> logger,
        TimeProvider? timeProvider = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _conversationReferenceStore = conversationReferenceStore ?? throw new ArgumentNullException(nameof(conversationReferenceStore));
        _agentQuestionStore = agentQuestionStore ?? throw new ArgumentNullException(nameof(agentQuestionStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task SendProactiveAsync(string tenantId, string userId, MessengerMessage message, CancellationToken ct)
    {
        ValidateRequired(tenantId, nameof(tenantId));
        ValidateRequired(userId, nameof(userId));
        ArgumentNullException.ThrowIfNull(message);

        // Open the diagnostic enrichment scope before the enqueue so the structured
        // logging keys (correlation, tenant, user) are present on every log line
        // the EnqueueUserMessageAsync path produces.
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

        // Run the shared send-time guards in the canonical order — tenant /
        // scope first (parameter-shaped failures bound to tenantId / userId /
        // question), payload validation second (InvalidOperationException). The
        // direct TeamsProactiveNotifier.SendProactiveQuestionAsync calls the
        // same helper in the same order so the two paths emit identical
        // exceptions for identical misuse.
        TeamsQuestionSendGuards.EnsureTenantMatchesQuestion(tenantId, question);
        TeamsQuestionSendGuards.EnsureScopeUserTargeted(userId, question);
        TeamsQuestionSendGuards.ValidateQuestion(question);

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

        // Channel-scope sibling of SendProactiveQuestionAsync — see that method
        // for the rationale. Order: tenant, scope, payload.
        TeamsQuestionSendGuards.EnsureTenantMatchesQuestion(tenantId, question);
        TeamsQuestionSendGuards.EnsureScopeChannelTargeted(channelId, question);
        TeamsQuestionSendGuards.ValidateQuestion(question);

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

        // Stage 6.1 canonical pre-enqueue AgentQuestion persistence — see class
        // remarks. SaveAsync MUST land before EnqueueAsync so the OutboxRetryEngine
        // can deliver the card and CardActionHandler.GetByIdAsync(questionId) can
        // immediately resolve the row when the user taps approve/reject.
        await PreEnqueueSaveQuestionAsync(question, ct).ConfigureAwait(false);

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

        // Stage 6.1 canonical pre-enqueue AgentQuestion persistence — see class
        // remarks. SaveAsync MUST land before EnqueueAsync so the OutboxRetryEngine
        // can deliver the card and CardActionHandler.GetByIdAsync(questionId) can
        // immediately resolve the row when the user taps approve/reject.
        await PreEnqueueSaveQuestionAsync(question, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Canonical pre-enqueue <see cref="IAgentQuestionStore.SaveAsync"/> step per
    /// <c>implementation-plan.md</c> §6.1. Persists a sanitised copy of the question
    /// (<c>ConversationId = null</c>, <c>Status = "Open"</c>) so
    /// <c>CardActionHandler.GetByIdAsync(questionId)</c> can immediately resolve the
    /// row when the user taps approve/reject after the outbox engine delivers the
    /// card, even when the outbox engine delivers the card within milliseconds.
    /// Implements a check-then-save guard mirroring
    /// <c>TeamsProactiveNotifier</c>'s retry-safe pattern: if a row already exists
    /// with <c>Status = "Open"</c> the duplicate save is skipped but the incoming
    /// payload MUST match the stored row
    /// (<see cref="TeamsQuestionSendGuards.EnsureRetryMatchesStoredQuestion"/>),
    /// otherwise the orchestrator has mutated routing/payload between retries and
    /// the card the dispatcher delivers would drift from the row CardActionHandler
    /// loads on reply. If the existing row holds a terminal status the call throws
    /// <see cref="InvalidOperationException"/> rather than enqueue a stale card.
    /// </summary>
    private async Task PreEnqueueSaveQuestionAsync(AgentQuestion question, CancellationToken ct)
    {
        var sanitised = question with { ConversationId = null, Status = AgentQuestionStatuses.Open };
        var existing = await _agentQuestionStore.GetByIdAsync(question.QuestionId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            await _agentQuestionStore.SaveAsync(sanitised, ct).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(existing.Status, AgentQuestionStatuses.Open, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"AgentQuestion '{question.QuestionId}' already exists with terminal status '{existing.Status}'; refusing to enqueue a stale Adaptive Card. The orchestrator should not retry resolved or expired questions.");
        }

        // Mirror TeamsProactiveNotifier.SendQuestionCoreAsync's retry-drift check so
        // a retry whose routing or payload mutated between attempts is rejected
        // loudly instead of silently shipping a card that diverges from the persisted
        // row.
        TeamsQuestionSendGuards.EnsureRetryMatchesStoredQuestion(sanitised, existing);

        _logger.LogInformation(
            "Proactive AgentQuestion {QuestionId} (correlation {CorrelationId}) row already present in IAgentQuestionStore with Status=Open; skipping duplicate pre-enqueue SaveAsync.",
            question.QuestionId,
            question.CorrelationId);
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
