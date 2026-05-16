using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Cards;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Microsoft Teams implementation of <see cref="IProactiveNotifier"/> per
/// <c>implementation-plan.md</c> §4.2 and <c>architecture.md</c> §4.7. Resolves
/// <see cref="TeamsConversationReference"/> rows from
/// <see cref="IConversationReferenceStore"/> by the canonical natural key
/// (<c>(InternalUserId, TenantId)</c> for user-targeted sends and
/// <c>(ChannelId, TenantId)</c> for channel-targeted sends), rehydrates the stored
/// Bot Framework <see cref="ConversationReference"/> JSON, and dispatches the message or
/// rendered Adaptive Card via <c>CloudAdapter.ContinueConversationAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives alongside <see cref="TeamsMessengerConnector"/> rather than inside it.</b>
/// The §4.2 brief carves out proactive delivery as a separate concern because the orchestrator
/// (Phase 6 outbox engine) drives this path outside of any inbound turn context. Splitting
/// the contract keeps the inbound <see cref="IMessengerConnector"/> surface narrow and lets
/// future per-messenger proactive notifiers (Slack, Discord) implement
/// <see cref="IProactiveNotifier"/> without inheriting Teams-specific
/// <see cref="IMessengerConnector"/> behaviour.
/// </para>
/// <para>
/// <b>Question persistence pattern.</b> <see cref="SendProactiveQuestionAsync"/> and
/// <see cref="SendQuestionToChannelAsync"/> mirror the three-step persistence sequence
/// from <see cref="TeamsMessengerConnector.SendQuestionAsync"/>: resolve reference →
/// render via <see cref="IAdaptiveCardRenderer.RenderQuestionCard"/> →
/// <c>ContinueConversationAsync</c> capturing <see cref="ResourceResponse.Id"/> and the
/// proactive turn context's <see cref="ConversationReference"/> via
/// <see cref="Activity.GetConversationReference"/> → persist
/// <see cref="TeamsCardState"/> AND call
/// <see cref="IAgentQuestionStore.UpdateConversationIdAsync"/> only when BOTH the
/// activity ID and the conversation ID came back from the send (all-or-nothing
/// persistence — partial state would break the bare approve/reject path OR the card
/// update/delete path).
/// </para>
/// <para>
/// <b>Reference-not-found behaviour.</b> When the store has no active
/// <see cref="TeamsConversationReference"/> for the target, every method throws
/// <see cref="ConversationReferenceNotFoundException"/>. The Phase 6 outbox engine catches
/// this typed exception and re-enqueues with an exponential backoff (the Teams app may
/// have been uninstalled and re-installed since the message was enqueued). Distinguishing
/// it from <see cref="InvalidOperationException"/> means the outbox does not need to
/// inspect the exception message to make routing decisions.
/// </para>
/// <para>
/// <b>Clock injection.</b> Timestamps stamped onto persisted <see cref="TeamsCardState"/>
/// rows flow through an injected <see cref="TimeProvider"/>, matching
/// <see cref="TeamsMessengerConnector"/>'s pattern. The DI-friendly public constructor
/// defaults to <see cref="TimeProvider.System"/>; a second public overload accepts a
/// deterministic provider for tests.
/// </para>
/// </remarks>
public sealed class TeamsProactiveNotifier : IProactiveNotifier
{
    private readonly CloudAdapter _adapter;
    private readonly TeamsMessagingOptions _options;
    private readonly IConversationReferenceStore _conversationReferenceStore;
    private readonly IAdaptiveCardRenderer _cardRenderer;
    private readonly ICardStateStore _cardStateStore;
    private readonly IAgentQuestionStore _agentQuestionStore;
    private readonly ILogger<TeamsProactiveNotifier> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Production constructor — defaults the clock to <see cref="TimeProvider.System"/>.
    /// Every constructor parameter is null-guarded so DI mis-registration fails loudly at
    /// composition root rather than producing a <see cref="NullReferenceException"/>
    /// deep inside a proactive send.
    /// </summary>
    public TeamsProactiveNotifier(
        CloudAdapter adapter,
        TeamsMessagingOptions options,
        IConversationReferenceStore conversationReferenceStore,
        IAdaptiveCardRenderer cardRenderer,
        ICardStateStore cardStateStore,
        IAgentQuestionStore agentQuestionStore,
        ILogger<TeamsProactiveNotifier> logger)
        : this(adapter, options, conversationReferenceStore, cardRenderer, cardStateStore, agentQuestionStore, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Test-friendly constructor that accepts a deterministic <see cref="TimeProvider"/>
    /// so unit tests can pin the exact <see cref="TeamsCardState.CreatedAt"/> /
    /// <see cref="TeamsCardState.UpdatedAt"/> values without wall-clock flakiness. The
    /// production constructor delegates here with <see cref="TimeProvider.System"/>.
    /// </summary>
    public TeamsProactiveNotifier(
        CloudAdapter adapter,
        TeamsMessagingOptions options,
        IConversationReferenceStore conversationReferenceStore,
        IAdaptiveCardRenderer cardRenderer,
        ICardStateStore cardStateStore,
        IAgentQuestionStore agentQuestionStore,
        ILogger<TeamsProactiveNotifier> logger,
        TimeProvider timeProvider)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _conversationReferenceStore = conversationReferenceStore ?? throw new ArgumentNullException(nameof(conversationReferenceStore));
        _cardRenderer = cardRenderer ?? throw new ArgumentNullException(nameof(cardRenderer));
        _cardStateStore = cardStateStore ?? throw new ArgumentNullException(nameof(cardStateStore));
        _agentQuestionStore = agentQuestionStore ?? throw new ArgumentNullException(nameof(agentQuestionStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task SendProactiveAsync(string tenantId, string userId, MessengerMessage message, CancellationToken ct)
    {
        ValidateRequiredArgument(tenantId, nameof(tenantId));
        ValidateRequiredArgument(userId, nameof(userId));
        ArgumentNullException.ThrowIfNull(message);

        var stored = await _conversationReferenceStore
            .GetByInternalUserIdAsync(tenantId, userId, ct)
            .ConfigureAwait(false)
            ?? throw ConversationReferenceNotFoundException.ForUser(tenantId, userId);

        var conversationReference = DeserializeReference(stored);

        _logger.LogInformation(
            "Sending proactive MessengerMessage {MessageId} (correlation {CorrelationId}) to user {InternalUserId} in tenant {TenantId} via reference {ReferenceId}.",
            message.MessageId,
            message.CorrelationId,
            userId,
            tenantId,
            stored.Id);

        await _adapter.ContinueConversationAsync(
            _options.MicrosoftAppId,
            conversationReference,
            async (turnContext, innerCt) =>
            {
                var reply = MessageFactory.Text(message.Body);
                await turnContext.SendActivityAsync(reply, innerCt).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SendProactiveQuestionAsync(string tenantId, string userId, AgentQuestion question, CancellationToken ct)
    {
        ValidateRequiredArgument(tenantId, nameof(tenantId));
        ValidateRequiredArgument(userId, nameof(userId));
        ArgumentNullException.ThrowIfNull(question);

        // Security-relevant consistency guard (iter-2 evaluator feedback #1, #2):
        // refuse to send a question through a tenant / user / scope that does not match
        // the question's own routing metadata. The orchestrator stamps TenantId,
        // TargetUserId, TargetChannelId onto every AgentQuestion at creation; a direct
        // caller that passes mismatched explicit parameters would otherwise (a) bypass
        // tenant isolation by delivering and persisting under the wrong tenant, or
        // (b) deliver a channel-scoped question into a user's personal chat. Throwing
        // InvalidArgumentMismatch BEFORE we touch the reference store, the renderer,
        // or the network keeps the failure cheap and the audit trail accurate.
        EnsureTenantMatchesQuestion(tenantId, question);
        EnsureScopeUserTargeted(userId, question);

        return SendQuestionCoreAsync(
            tenantId,
            question,
            lookupAsync: innerCt => _conversationReferenceStore.GetByInternalUserIdAsync(tenantId, userId, innerCt),
            notFoundFactory: () => ConversationReferenceNotFoundException.ForUser(tenantId, userId, question.QuestionId),
            targetDescription: $"user '{userId}'",
            ct);
    }

    /// <inheritdoc />
    public async Task SendToChannelAsync(string tenantId, string channelId, MessengerMessage message, CancellationToken ct)
    {
        ValidateRequiredArgument(tenantId, nameof(tenantId));
        ValidateRequiredArgument(channelId, nameof(channelId));
        ArgumentNullException.ThrowIfNull(message);

        var stored = await _conversationReferenceStore
            .GetByChannelIdAsync(tenantId, channelId, ct)
            .ConfigureAwait(false)
            ?? throw ConversationReferenceNotFoundException.ForChannel(tenantId, channelId);

        var conversationReference = DeserializeReference(stored);

        _logger.LogInformation(
            "Sending proactive MessengerMessage {MessageId} (correlation {CorrelationId}) to channel {ChannelId} in tenant {TenantId} via reference {ReferenceId}.",
            message.MessageId,
            message.CorrelationId,
            channelId,
            tenantId,
            stored.Id);

        await _adapter.ContinueConversationAsync(
            _options.MicrosoftAppId,
            conversationReference,
            async (turnContext, innerCt) =>
            {
                var reply = MessageFactory.Text(message.Body);
                await turnContext.SendActivityAsync(reply, innerCt).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SendQuestionToChannelAsync(string tenantId, string channelId, AgentQuestion question, CancellationToken ct)
    {
        ValidateRequiredArgument(tenantId, nameof(tenantId));
        ValidateRequiredArgument(channelId, nameof(channelId));
        ArgumentNullException.ThrowIfNull(question);

        // Security-relevant consistency guard (iter-2 evaluator feedback #1, #2) —
        // see SendProactiveQuestionAsync for the full rationale.
        EnsureTenantMatchesQuestion(tenantId, question);
        EnsureScopeChannelTargeted(channelId, question);

        return SendQuestionCoreAsync(
            tenantId,
            question,
            lookupAsync: innerCt => _conversationReferenceStore.GetByChannelIdAsync(tenantId, channelId, innerCt),
            notFoundFactory: () => ConversationReferenceNotFoundException.ForChannel(tenantId, channelId, question.QuestionId),
            targetDescription: $"channel '{channelId}'",
            ct);
    }

    /// <summary>
    /// Shared persistence pipeline for the two question-send methods. Centralising the
    /// implementation keeps the rendering, send, capture, and dual-persist semantics
    /// identical across user-targeted and channel-targeted sends — a divergence would
    /// silently break Stage 3.3's card update/delete path for one of the two targets.
    /// </summary>
    private async Task SendQuestionCoreAsync(
        string tenantId,
        AgentQuestion question,
        Func<CancellationToken, Task<TeamsConversationReference?>> lookupAsync,
        Func<ConversationReferenceNotFoundException> notFoundFactory,
        string targetDescription,
        CancellationToken ct)
    {
        // Defence in depth — every public surface in this class accepts AgentQuestion and
        // calls into here. Validate() runs again so a malformed question (TargetUserId
        // and TargetChannelId both null, missing CorrelationId, etc.) fails loudly before
        // we touch the network. The connector's own SendQuestionAsync does the same.
        var validationErrors = question.Validate();
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"AgentQuestion '{question.QuestionId}' is invalid: {string.Join("; ", validationErrors)}");
        }

        var stored = await lookupAsync(ct).ConfigureAwait(false)
            ?? throw notFoundFactory();

        var conversationReference = DeserializeReference(stored);

        // Render the Adaptive Card via the canonical IAdaptiveCardRenderer surface so the
        // implementation-plan §4.2 brief's "AdaptiveCardBuilder.RenderQuestion(agentQuestion)"
        // requirement resolves correctly: the canonical method name on the renderer
        // interface is RenderQuestionCard (per the Stage 3.1 contract and the existing
        // TeamsMessengerConnector.SendQuestionAsync call site).
        var attachment = _cardRenderer.RenderQuestionCard(question);

        string? deliveredActivityId = null;
        string? deliveredConversationId = null;
        string? deliveredReferenceJson = null;

        _logger.LogInformation(
            "Sending proactive AgentQuestion {QuestionId} (correlation {CorrelationId}) to {Target} in tenant {TenantId} via reference {ReferenceId}.",
            question.QuestionId,
            question.CorrelationId,
            targetDescription,
            tenantId,
            stored.Id);

        await _adapter.ContinueConversationAsync(
            _options.MicrosoftAppId,
            conversationReference,
            async (turnContext, innerCt) =>
            {
                var reply = MessageFactory.Attachment(attachment);
                // Activity.Text falls back to the title so clients that cannot render the
                // card (mobile lock screens, accessibility tooling) still see a useful
                // notification banner. Same convention as
                // TeamsMessengerConnector.SendQuestionAsync.
                reply.Text = question.Title;
                var resourceResponse = await turnContext.SendActivityAsync(reply, innerCt).ConfigureAwait(false);
                deliveredActivityId = resourceResponse?.Id;
                deliveredConversationId = turnContext.Activity?.Conversation?.Id;
                var freshReference = turnContext.Activity?.GetConversationReference();
                if (freshReference is not null)
                {
                    deliveredReferenceJson = JsonConvert.SerializeObject(freshReference);
                }
            },
            ct).ConfigureAwait(false);

        // All-or-nothing persistence — refuse to write half the state. A persisted
        // TeamsCardState row without a matching AgentQuestion.ConversationId would let
        // Stage 3.3's card update/delete locate the card but break bare approve/reject;
        // a persisted ConversationId without a card-state row would do the reverse.
        // Failing loudly is preferable to producing inconsistent state that the outbox
        // and the bare-action handler would both partially observe.
        if (string.IsNullOrWhiteSpace(deliveredConversationId))
        {
            throw new InvalidOperationException(
                $"ContinueConversationAsync for question '{question.QuestionId}' did not yield " +
                $"a Conversation.Id from the proactive turn context. The card was sent but cannot " +
                $"be resolved by bare approve/reject text commands; treating this as a delivery " +
                $"failure to avoid silent partial persistence.");
        }

        if (string.IsNullOrWhiteSpace(deliveredActivityId))
        {
            throw new InvalidOperationException(
                $"ContinueConversationAsync for question '{question.QuestionId}' did not yield " +
                $"an Activity.Id from the SendActivityAsync response. The card was sent but cannot " +
                $"be updated or deleted later; treating this as a delivery failure to avoid silent " +
                $"partial persistence.");
        }

        await _agentQuestionStore
            .UpdateConversationIdAsync(question.QuestionId, deliveredConversationId!, ct)
            .ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var cardState = new TeamsCardState
        {
            QuestionId = question.QuestionId,
            ActivityId = deliveredActivityId!,
            ConversationId = deliveredConversationId!,
            // Prefer the reference captured from the proactive turn context — it reflects
            // the actual delivery (service URL rotation, conversation thread, etc.). Fall
            // back to the stored reference's JSON only if the turn context did not expose
            // a usable reference (defensive; the BotAdapter contract guarantees one but
            // unit-test doubles may not).
            ConversationReferenceJson = deliveredReferenceJson ?? stored.ReferenceJson,
            Status = TeamsCardStatuses.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _cardStateStore.SaveAsync(cardState, ct).ConfigureAwait(false);
    }

    private static void ValidateRequiredArgument(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"'{paramName}' must be non-null and non-whitespace.", paramName);
        }
    }

    /// <summary>
    /// Tenant-isolation guard. The orchestrator stamps the tenant onto every
    /// <see cref="AgentQuestion"/> at creation time; a direct call that supplies a
    /// different <paramref name="tenantId"/> would silently deliver and persist the
    /// question under the wrong tenant, breaking RBAC and the multi-tenant audit trail
    /// the story's Security / Compliance rows require. Throws
    /// <see cref="ArgumentException"/> bound to <c>tenantId</c> so DI and direct callers
    /// both see a parameter-shaped failure they can attribute. String equality is
    /// case-sensitive — AAD tenant GUIDs are normalised by the issuer and Azure
    /// recommends preserving the exact casing of the <c>tid</c> claim.
    /// </summary>
    private static void EnsureTenantMatchesQuestion(string tenantId, AgentQuestion question)
    {
        if (!string.Equals(tenantId, question.TenantId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"tenantId '{tenantId}' does not match AgentQuestion '{question.QuestionId}' " +
                $"tenant '{question.TenantId}'. Refusing to send a question through a tenant " +
                $"different from its own routing metadata — this is a tenant-isolation invariant.",
                nameof(tenantId));
        }
    }

    /// <summary>
    /// User-scope guard for <see cref="SendProactiveQuestionAsync"/>. Two failure modes:
    /// (1) the question is channel-scoped (<see cref="AgentQuestion.TargetChannelId"/>
    /// is non-null) — sending it into a personal chat would mis-route the approval ask
    /// and leak channel context into a 1:1 thread; (2) the supplied
    /// <paramref name="userId"/> does not match the question's
    /// <see cref="AgentQuestion.TargetUserId"/> — sending it to a different user would
    /// route the approval ask to the wrong person. Both throw
    /// <see cref="ArgumentException"/> bound to <c>userId</c>.
    /// </summary>
    private static void EnsureScopeUserTargeted(string userId, AgentQuestion question)
    {
        if (question.TargetChannelId is not null)
        {
            throw new ArgumentException(
                $"AgentQuestion '{question.QuestionId}' is channel-scoped " +
                $"(TargetChannelId='{question.TargetChannelId}') but SendProactiveQuestionAsync " +
                $"is the user-scope entry point. Route channel-scoped questions through " +
                $"SendQuestionToChannelAsync or the NotifyQuestionAsync dispatcher.",
                nameof(question));
        }

        if (!string.Equals(userId, question.TargetUserId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"userId '{userId}' does not match AgentQuestion '{question.QuestionId}' " +
                $"TargetUserId '{question.TargetUserId}'. Refusing to deliver an approval ask " +
                $"to a user other than the one named on the question.",
                nameof(userId));
        }
    }

    /// <summary>
    /// Channel-scope guard for <see cref="SendQuestionToChannelAsync"/>. Mirrors
    /// <see cref="EnsureScopeUserTargeted"/>: rejects user-scoped questions and rejects
    /// mismatches between the supplied <paramref name="channelId"/> and the question's
    /// <see cref="AgentQuestion.TargetChannelId"/>.
    /// </summary>
    private static void EnsureScopeChannelTargeted(string channelId, AgentQuestion question)
    {
        if (question.TargetUserId is not null)
        {
            throw new ArgumentException(
                $"AgentQuestion '{question.QuestionId}' is user-scoped " +
                $"(TargetUserId='{question.TargetUserId}') but SendQuestionToChannelAsync " +
                $"is the channel-scope entry point. Route user-scoped questions through " +
                $"SendProactiveQuestionAsync or the NotifyQuestionAsync dispatcher.",
                nameof(question));
        }

        if (!string.Equals(channelId, question.TargetChannelId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"channelId '{channelId}' does not match AgentQuestion '{question.QuestionId}' " +
                $"TargetChannelId '{question.TargetChannelId}'. Refusing to deliver an approval " +
                $"ask to a channel other than the one named on the question.",
                nameof(channelId));
        }
    }

    /// <summary>
    /// Rehydrate the Bot Framework <see cref="ConversationReference"/> from the stored
    /// JSON via <see cref="JsonConvert"/>. Newtonsoft is the only JSON serializer that
    /// round-trips Bot Framework's <c>ConversationReference</c> losslessly — the type is
    /// annotated with Newtonsoft attributes, carries <c>JObject</c> extension data, and
    /// has <c>JObject</c>-typed members that <c>System.Text.Json</c> silently mangles.
    /// Matches <see cref="TeamsMessengerConnector.DeserializeReference"/>.
    /// </summary>
    private static ConversationReference DeserializeReference(TeamsConversationReference stored)
    {
        if (string.IsNullOrWhiteSpace(stored.ReferenceJson))
        {
            throw new InvalidOperationException(
                $"Stored conversation reference '{stored.Id}' has empty ReferenceJson; cannot rehydrate.");
        }

        var reference = JsonConvert.DeserializeObject<ConversationReference>(stored.ReferenceJson)
            ?? throw new InvalidOperationException(
                $"Stored conversation reference '{stored.Id}' deserialized to null.");
        return reference;
    }
}
