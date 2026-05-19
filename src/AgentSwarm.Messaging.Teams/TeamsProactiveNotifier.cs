using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Diagnostics;
using AgentSwarm.Messaging.Teams.Security;
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
    private readonly InstallationStateGate? _installationStateGate;

    /// <summary>
    /// Stage 6.1 iter-3 — optional <see cref="AgentSwarm.Messaging.Teams.Outbox.TeamsDirectSendBypassGuard"/>
    /// resolved by the DI factory in <see cref="TeamsServiceCollectionExtensions"/> when
    /// the host composes
    /// <see cref="AgentSwarm.Messaging.Teams.Outbox.TeamsOutboxServiceCollectionExtensions.AddTeamsOutboxEngine"/>.
    /// When non-<c>null</c>, every direct send method on this notifier
    /// (<see cref="SendProactiveAsync"/>, <see cref="SendProactiveQuestionAsync"/>,
    /// <see cref="SendToChannelAsync"/>, <see cref="SendQuestionToChannelAsync"/>) calls
    /// <see cref="AgentSwarm.Messaging.Teams.Outbox.TeamsDirectSendBypassGuard.ThrowIfDisallowed"/>
    /// at the top — which throws an
    /// <see cref="InvalidOperationException"/> with a remediation message pointing the
    /// caller back at the public <see cref="IProactiveNotifier"/> contract (which, when
    /// the outbox engine is wired, resolves to <see cref="Outbox.OutboxBackedProactiveNotifier"/>).
    /// </summary>
    /// <remarks>
    /// <b>Why a property and not a constructor parameter.</b> Mirrors the
    /// <see cref="TeamsMessengerConnector.Telemetry"/> pattern: keeping the canonical
    /// 9-arg constructor stable preserves the legacy test compositions and the
    /// installation-state gate wiring path. When the outbox engine is NOT composed the
    /// property is left <c>null</c> and every send method's guard call short-circuits to
    /// a no-op — preserving pre-Stage-6.1 direct-send semantics.
    /// </remarks>
    public AgentSwarm.Messaging.Teams.Outbox.TeamsDirectSendBypassGuard? DirectSendGuard { get; init; }

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
        : this(adapter, options, conversationReferenceStore, cardRenderer, cardStateStore, agentQuestionStore, logger, TimeProvider.System, installationStateGate: null)
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
        : this(adapter, options, conversationReferenceStore, cardRenderer, cardStateStore, agentQuestionStore, logger, timeProvider, installationStateGate: null)
    {
    }

    /// <summary>
    /// Canonical production constructor wired by <c>AddTeamsProactiveNotifier</c> +
    /// <c>AddTeamsSecurity</c>. Accepts an <see cref="InstallationStateGate"/> so every
    /// proactive question is guarded by the Stage 5.1 install-state pre-check before the
    /// Bot Framework call. The gate dead-letters and audits inactive targets; the notifier
    /// short-circuits without invoking <c>ContinueConversationAsync</c>. When the gate is
    /// supplied as <c>null</c> (legacy DI compositions and the test-only constructors
    /// above), the install-state probe is bypassed — this is intended ONLY for
    /// pre-Stage-5.1 unit tests; production hosts MUST resolve this constructor via
    /// DI so the gate is never null in deployed environments.
    /// </summary>
    public TeamsProactiveNotifier(
        CloudAdapter adapter,
        TeamsMessagingOptions options,
        IConversationReferenceStore conversationReferenceStore,
        IAdaptiveCardRenderer cardRenderer,
        ICardStateStore cardStateStore,
        IAgentQuestionStore agentQuestionStore,
        ILogger<TeamsProactiveNotifier> logger,
        TimeProvider timeProvider,
        InstallationStateGate? installationStateGate)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _conversationReferenceStore = conversationReferenceStore ?? throw new ArgumentNullException(nameof(conversationReferenceStore));
        _cardRenderer = cardRenderer ?? throw new ArgumentNullException(nameof(cardRenderer));
        _cardStateStore = cardStateStore ?? throw new ArgumentNullException(nameof(cardStateStore));
        _agentQuestionStore = agentQuestionStore ?? throw new ArgumentNullException(nameof(agentQuestionStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        // InstallationStateGate is intentionally allowed to be null for the test-only
        // legacy constructors above. Production DI passes a real gate so every Bot
        // Framework call is install-state-checked first.
        _installationStateGate = installationStateGate;
    }

    /// <inheritdoc />
    public async Task SendProactiveAsync(string tenantId, string userId, MessengerMessage message, CancellationToken ct)
    {
        ValidateRequiredArgument(tenantId, nameof(tenantId));
        ValidateRequiredArgument(userId, nameof(userId));
        ArgumentNullException.ThrowIfNull(message);

        // Stage 6.1 iter-3 — outbox-bypass guard. See DirectSendGuard property
        // remarks for the rationale.
        DirectSendGuard?.ThrowIfDisallowed(nameof(TeamsProactiveNotifier), nameof(SendProactiveAsync));

        // Stage 6.3 iter-2 — push the canonical CorrelationId/TenantId/UserId enrichment
        // onto every log entry emitted by this proactive send (both via ILogger.BeginScope
        // and via the AsyncLocal-backed Serilog enricher feed).
        using var logScope = TeamsLogScope.BeginScope(
            _logger,
            correlationId: message.CorrelationId,
            tenantId: tenantId,
            userId: userId);

        // Stage 5.1 iter-5 evaluator feedback item 1 — STRUCTURAL fix. The install-state
        // gate MUST run BEFORE the active-only GetByInternalUserIdAsync lookup. The real
        // SqlConversationReferenceStore filters by `e.IsActive`, so an inactive (or
        // never-installed) target returns null from the getter and the older "lookup-then-
        // gate" ordering threw ConversationReferenceNotFoundException BEFORE the gate
        // could emit the InstallationGateRejected audit row or dead-letter the outbox
        // entry. The gate uses IsActiveByInternalUserIdAsync (a boolean probe that
        // legitimately handles "missing" and "inactive" alike) so it does not need the
        // getter to have run first.
        if (_installationStateGate is not null)
        {
            var gateResult = await _installationStateGate.CheckTargetAsync(
                tenantId: tenantId,
                userId: userId,
                channelId: null,
                correlationId: message.CorrelationId ?? string.Empty,
                outboxEntryId: ProactiveSendContext.CurrentOutboxEntryId,
                cancellationToken: ct).ConfigureAwait(false);

            if (!gateResult.IsActive)
            {
                _logger.LogWarning(
                    "InstallationStateGate rejected proactive MessengerMessage {MessageId} (correlation {CorrelationId}) to user {InternalUserId} in tenant {TenantId}. Skipping Bot Framework call and reference lookup. Reason: {Reason}",
                    message.MessageId,
                    message.CorrelationId,
                    userId,
                    tenantId,
                    gateResult.Reason);
                throw ConversationReferenceNotFoundException.ForUser(tenantId, userId);
            }
        }

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
    public async Task SendProactiveQuestionAsync(string tenantId, string userId, AgentQuestion question, CancellationToken ct)
    {
        ValidateRequiredArgument(tenantId, nameof(tenantId));
        ValidateRequiredArgument(userId, nameof(userId));
        ArgumentNullException.ThrowIfNull(question);

        // Stage 6.1 iter-3 — outbox-bypass guard. See DirectSendGuard property
        // remarks for the rationale.
        DirectSendGuard?.ThrowIfDisallowed(nameof(TeamsProactiveNotifier), nameof(SendProactiveQuestionAsync));

        // Stage 6.3 iter-2 — enrichment scope is opened BEFORE argument-cross-check so
        // even validation-failure logs carry the canonical keys. Method is async (not
        // sync-returning-Task) so the scope persists across the entire SendQuestionCore
        // await chain.
        using var logScope = TeamsLogScope.BeginScope(
            _logger,
            correlationId: question.CorrelationId,
            tenantId: tenantId,
            userId: userId);

        // Security-relevant consistency guard (iter-2 evaluator feedback #1, #2):
        // refuse to send a question through a tenant / user / scope that does not match
        // the question's own routing metadata. The orchestrator stamps TenantId,
        // TargetUserId, TargetChannelId onto every AgentQuestion at creation; a direct
        // caller that passes mismatched explicit parameters would otherwise (a) bypass
        // tenant isolation by delivering and persisting under the wrong tenant, or
        // (b) deliver a channel-scoped question into a user's personal chat. Throwing
        // InvalidArgumentMismatch BEFORE we touch the reference store, the renderer,
        // or the network keeps the failure cheap and the audit trail accurate.
        TeamsQuestionSendGuards.EnsureTenantMatchesQuestion(tenantId, question);
        TeamsQuestionSendGuards.EnsureScopeUserTargeted(userId, question);

        await SendQuestionCoreAsync(
            tenantId,
            question,
            lookupAsync: innerCt => _conversationReferenceStore.GetByInternalUserIdAsync(tenantId, userId, innerCt),
            notFoundFactory: () => ConversationReferenceNotFoundException.ForUser(tenantId, userId, question.QuestionId),
            targetDescription: $"user '{userId}'",
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendToChannelAsync(string tenantId, string channelId, MessengerMessage message, CancellationToken ct)
    {
        ValidateRequiredArgument(tenantId, nameof(tenantId));
        ValidateRequiredArgument(channelId, nameof(channelId));
        ArgumentNullException.ThrowIfNull(message);

        // Stage 6.1 iter-3 — outbox-bypass guard. See DirectSendGuard property
        // remarks for the rationale.
        DirectSendGuard?.ThrowIfDisallowed(nameof(TeamsProactiveNotifier), nameof(SendToChannelAsync));

        // Stage 6.3 iter-2 — channel sends carry the same enrichment minus UserId; the
        // tenant + channel + correlation triple is what dashboards key on for
        // channel-scoped delivery audits.
        using var logScope = TeamsLogScope.BeginScope(
            _logger,
            correlationId: message.CorrelationId,
            tenantId: tenantId,
            userId: null);

        // Stage 5.1 iter-5 evaluator feedback item 1 — gate BEFORE the active-only
        // channel lookup. Same structural fix as SendProactiveAsync above; see comment
        // there for rationale.
        if (_installationStateGate is not null)
        {
            var gateResult = await _installationStateGate.CheckTargetAsync(
                tenantId: tenantId,
                userId: null,
                channelId: channelId,
                correlationId: message.CorrelationId ?? string.Empty,
                outboxEntryId: ProactiveSendContext.CurrentOutboxEntryId,
                cancellationToken: ct).ConfigureAwait(false);

            if (!gateResult.IsActive)
            {
                _logger.LogWarning(
                    "InstallationStateGate rejected proactive MessengerMessage {MessageId} (correlation {CorrelationId}) to channel {ChannelId} in tenant {TenantId}. Skipping Bot Framework call and reference lookup. Reason: {Reason}",
                    message.MessageId,
                    message.CorrelationId,
                    channelId,
                    tenantId,
                    gateResult.Reason);
                throw ConversationReferenceNotFoundException.ForChannel(tenantId, channelId);
            }
        }

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
    public async Task SendQuestionToChannelAsync(string tenantId, string channelId, AgentQuestion question, CancellationToken ct)
    {
        ValidateRequiredArgument(tenantId, nameof(tenantId));
        ValidateRequiredArgument(channelId, nameof(channelId));
        ArgumentNullException.ThrowIfNull(question);

        // Stage 6.1 iter-3 — outbox-bypass guard. See DirectSendGuard property
        // remarks for the rationale.
        DirectSendGuard?.ThrowIfDisallowed(nameof(TeamsProactiveNotifier), nameof(SendQuestionToChannelAsync));

        // Stage 6.3 iter-2 — channel-scoped question enrichment scope.
        using var logScope = TeamsLogScope.BeginScope(
            _logger,
            correlationId: question.CorrelationId,
            tenantId: tenantId,
            userId: null);

        // Security-relevant consistency guard (iter-2 evaluator feedback #1, #2) —
        // see SendProactiveQuestionAsync for the full rationale.
        TeamsQuestionSendGuards.EnsureTenantMatchesQuestion(tenantId, question);
        TeamsQuestionSendGuards.EnsureScopeChannelTargeted(channelId, question);

        await SendQuestionCoreAsync(
            tenantId,
            question,
            lookupAsync: innerCt => _conversationReferenceStore.GetByChannelIdAsync(tenantId, channelId, innerCt),
            notFoundFactory: () => ConversationReferenceNotFoundException.ForChannel(tenantId, channelId, question.QuestionId),
            targetDescription: $"channel '{channelId}'",
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared persistence pipeline for the two question-send methods. Centralising the
    /// implementation keeps the rendering, send, capture, and dual-persist semantics
    /// identical across user-targeted and channel-targeted sends — a divergence would
    /// silently break Stage 3.3's card update/delete path for one of the two targets.
    /// </summary>
    /// <remarks>
    /// Persistence ordering mirrors <see cref="TeamsMessengerConnector.SendQuestionAsync"/>:
    /// the question row is inserted into <see cref="IAgentQuestionStore"/> BEFORE the
    /// reference lookup / network send, and the <see cref="AgentQuestion.ConversationId"/>
    /// is stamped LATER via <see cref="IAgentQuestionStore.UpdateConversationIdAsync"/>
    /// once the proactive turn context surfaces a real conversation ID. Saving up front is
    /// the only ordering that satisfies BOTH downstream paths: (1)
    /// <see cref="Cards.CardActionHandler"/> calls
    /// <see cref="IAgentQuestionStore.GetByIdAsync"/> on every inbound Adaptive Card
    /// action — if the question row does not exist yet, the handler rejects with
    /// <c>QuestionNotFound</c>; (2)
    /// <see cref="IAgentQuestionStore.UpdateConversationIdAsync"/> is implemented as a
    /// SQL <c>ExecuteUpdate</c> that silently affects zero rows when the question is
    /// missing (see <c>AgentSwarm.Messaging.Teams.EntityFrameworkCore.SqlAgentQuestionStore.UpdateConversationIdAsync</c>),
    /// so post-send save would also silently lose the <c>ConversationId</c> stamp and
    /// break bare approve/reject text resolution. Persisting up front also means a
    /// missing-reference failure surfaces a durable record the Phase 6 outbox engine can
    /// replay against (the question exists; only the delivery did not).
    /// </remarks>
    private async Task SendQuestionCoreAsync(
        string tenantId,
        AgentQuestion question,
        Func<CancellationToken, Task<TeamsConversationReference?>> lookupAsync,
        Func<ConversationReferenceNotFoundException> notFoundFactory,
        string targetDescription,
        CancellationToken ct)
    {
        // Defence in depth — every public surface in this class accepts AgentQuestion and
        // calls into here. Validate() runs again via the shared guard so a malformed
        // question (TargetUserId and TargetChannelId both null, missing CorrelationId,
        // etc.) fails loudly before we touch the network. The connector's own
        // SendQuestionAsync and the outbox-backed decorators call the same helper to
        // guarantee identical observable failure shapes across every send surface.
        TeamsQuestionSendGuards.ValidateQuestion(question);

        // Step 1a (iter-4 evaluator feedback #1 — outbox-retry idempotency).
        // The Phase 6 outbox engine may replay a proactive send after the first attempt
        // succeeded at the network layer but the worker died before acking, or after a
        // transient delivery failure. If a TeamsCardState row already exists for this
        // QuestionId, the previous attempt already delivered the card — re-sending would
        // produce a duplicate Adaptive Card in Teams (and would overwrite the stored
        // ActivityId/ConversationReferenceJson, orphaning the first card to the user).
        // Short-circuit here so retries are safe to call as many times as the outbox
        // wants.
        var existingCardState = await _cardStateStore.GetByQuestionIdAsync(question.QuestionId, ct).ConfigureAwait(false);
        if (existingCardState is not null)
        {
            _logger.LogInformation(
                "Skipping proactive AgentQuestion {QuestionId} (correlation {CorrelationId}) to {Target} in tenant {TenantId}: TeamsCardState already present (ActivityId {ActivityId}, ConversationId {ConversationId}); treating as already delivered.",
                question.QuestionId,
                question.CorrelationId,
                targetDescription,
                tenantId,
                existingCardState.ActivityId,
                existingCardState.ConversationId);
            return;
        }

        // Step 1b (iter-4 evaluator feedback #1 — duplicate-PK protection).
        // SqlAgentQuestionStore.SaveAsync is insert-only (ctx.AgentQuestions.Add(entity))
        // and would throw a unique-PK DbUpdateException on a naive retry where the prior
        // attempt persisted the question but threw ConversationReferenceNotFoundException
        // before card delivery. Check existence first; only SaveAsync if absent.
        //
        // When an existing row is found, enforce two invariants before continuing:
        //   (i) Status MUST be Open — sending a card for a Resolved/Expired question
        //       would produce a stale approval prompt the user could not actually
        //       interact with (CardActionHandler.HandleAsync would reject the action
        //       as AlreadyResolved or Expired). Throw InvalidOperationException so the
        //       outbox stops retrying this entry.
        //   (ii) The incoming question's identity / routing / payload fields MUST match
        //       the stored row. A mismatched retry means the orchestrator mutated the
        //       question after enqueuing it — that is "card update" semantics, not retry,
        //       and is not supported by Stage 4.2. Throw so the orchestrator surfaces
        //       the mismatch rather than silently shipping a card that drifts from the
        //       row CardActionHandler will load.
        //
        // Note: this check-then-save sequence is best-effort race resilience — two truly
        // concurrent retries with the same QuestionId could both see null and both call
        // SaveAsync, in which case one will still throw DbUpdateException. The
        // orchestrator owns single-flight semantics; defending against that scenario
        // here would require a store-level atomic TryCreateAsync (out of scope for
        // Stage 4.2).
        var sanitizedQuestion = question with { ConversationId = null };
        var existingQuestion = await _agentQuestionStore.GetByIdAsync(question.QuestionId, ct).ConfigureAwait(false);
        if (existingQuestion is null)
        {
            // Step 2 (iter-3 evaluator feedback #1) — persist a SANITIZED copy with
            // ConversationId forced to null BEFORE the lookup / send. Two reasons:
            //   * UpdateConversationIdAsync (later step) is a no-op when the row is
            //     missing, so without the up-front Save the post-send ConversationId
            //     stamp would silently disappear and CardActionHandler.GetByIdAsync
            //     would reject subsequent Adaptive Card actions with QuestionNotFound.
            //   * Forcing ConversationId = null avoids letting a caller-supplied stale
            //     value poison bare approve/reject text-command resolution that joins
            //     on ConversationId.
            // Reference equality is unchanged for callers that already passed a null
            // ConversationId — the `with` expression returns the same effective record.
            await _agentQuestionStore.SaveAsync(sanitizedQuestion, ct).ConfigureAwait(false);
        }
        else
        {
            TeamsQuestionSendGuards.EnsureRetryMatchesStoredQuestion(question, existingQuestion);

            if (!string.Equals(existingQuestion.Status, AgentQuestionStatuses.Open, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"AgentQuestion '{question.QuestionId}' already exists with terminal status '{existingQuestion.Status}'; refusing to deliver a stale Adaptive Card. The outbox should not retry resolved or expired questions.");
            }

            _logger.LogInformation(
                "Proactive AgentQuestion {QuestionId} (correlation {CorrelationId}) row already present in IAgentQuestionStore with Status=Open; skipping duplicate SaveAsync and proceeding with lookup/send (outbox retry).",
                question.QuestionId,
                question.CorrelationId);
        }

        // Stage 5.1 iter-5 evaluator feedback item 1 — gate BEFORE the active-only
        // lookupAsync. lookupAsync wraps GetByInternalUserIdAsync / GetByChannelIdAsync,
        // both of which filter by IsActive in the SQL store, so the old "lookup-then-gate"
        // ordering threw ConversationReferenceNotFoundException for inactive targets
        // BEFORE the gate could emit the InstallationGateRejected audit row. The gate
        // uses IsActiveByXxxAsync probes that handle missing/inactive alike, so it does
        // not need the lookup to have succeeded first.
        if (_installationStateGate is not null)
        {
            var gateResult = await _installationStateGate.CheckAsync(
                question,
                outboxEntryId: ProactiveSendContext.CurrentOutboxEntryId,
                correlationId: question.CorrelationId ?? string.Empty,
                ct).ConfigureAwait(false);

            if (!gateResult.IsActive)
            {
                _logger.LogWarning(
                    "InstallationStateGate rejected proactive AgentQuestion {QuestionId} (correlation {CorrelationId}) to {Target} in tenant {TenantId}. Skipping reference lookup and Bot Framework call. Reason: {Reason}",
                    question.QuestionId,
                    question.CorrelationId,
                    targetDescription,
                    tenantId,
                    gateResult.Reason);
                throw notFoundFactory();
            }
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
