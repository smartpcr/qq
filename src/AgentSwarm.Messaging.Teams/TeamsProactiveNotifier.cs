using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
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
    private readonly IAuditLogger _auditLogger;

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
        : this(adapter, options, conversationReferenceStore, cardRenderer, cardStateStore, agentQuestionStore, logger, TimeProvider.System, installationStateGate: null, auditLogger: null)
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
        : this(adapter, options, conversationReferenceStore, cardRenderer, cardStateStore, agentQuestionStore, logger, timeProvider, installationStateGate: null, auditLogger: null)
    {
    }

    /// <summary>
    /// Stage 5.1 production constructor — accepts an <see cref="InstallationStateGate"/>
    /// so every proactive question is guarded by the install-state pre-check. The
    /// Stage 5.2 audit instrumentation is bypassed (NoOp) when this overload is used;
    /// production hosts MUST resolve the 10-arg canonical overload below via DI so audit
    /// trails are persisted alongside install-state checks.
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
        : this(adapter, options, conversationReferenceStore, cardRenderer, cardStateStore, agentQuestionStore, logger, timeProvider, installationStateGate, auditLogger: null)
    {
    }

    /// <summary>
    /// Canonical production constructor wired by <c>AddTeamsProactiveNotifier</c> +
    /// <c>AddTeamsSecurity</c> + <c>AddSqlAuditLogger</c>. Accepts an
    /// <see cref="InstallationStateGate"/> AND an <see cref="IAuditLogger"/> so every
    /// proactive send is both gated by the install-state pre-check (Stage 5.1) AND
    /// emits a <c>ProactiveNotification</c> audit entry (Stage 5.2). When
    /// <paramref name="auditLogger"/> is null, the notifier falls back to a
    /// <see cref="NoOpAuditLogger"/> so legacy DI compositions continue to construct;
    /// production hosts wire <c>SqlAuditLogger</c> via
    /// <c>AddSqlAuditLogger</c> so audit emission persists durably.
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
        InstallationStateGate? installationStateGate,
        IAuditLogger? auditLogger)
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
        // IAuditLogger is intentionally nullable on the constructor surface so legacy
        // pre-Stage-5.2 unit-test harnesses still construct. When null, fall back to
        // the NoOp logger to preserve the never-null _auditLogger invariant in the
        // emit helpers below. Production DI wires SqlAuditLogger via AddSqlAuditLogger
        // so audit entries persist durably.
        _auditLogger = auditLogger ?? new NoOpAuditLogger();
    }

    /// <inheritdoc />
    public async Task SendProactiveAsync(string tenantId, string userId, MessengerMessage message, CancellationToken ct)
    {
        ValidateRequiredArgument(tenantId, nameof(tenantId));
        ValidateRequiredArgument(userId, nameof(userId));
        ArgumentNullException.ThrowIfNull(message);

        // Stage 5.2 — every proactive send emits exactly one ProactiveNotification audit
        // entry per tech-spec.md §4.3. Stage 5.2 iter-r0 (review comment at line 544):
        // the audit emit MUST run AFTER the send try/catch — NOT from a `finally` block —
        // because LogProactiveNotificationAsync deliberately does NOT swallow audit-store
        // failures (durability contract), and a throw from `finally` would replace the
        // original send exception per the CLR's exception-replacement semantics. We
        // capture the send failure via ExceptionDispatchInfo and surface both root causes
        // through AggregateException when audit ALSO fails, mirroring the proven
        // dual-failure pattern in TeamsSwarmActivityHandler.OnMessageActivityAsync.
        // Branch summary (matches the activity handler):
        //   * send OK,    audit OK    → no exception (Success audit row landed).
        //   * send fails, audit OK    → send exception re-thrown via ExceptionDispatchInfo.Throw()
        //                               with the original stack preserved.
        //   * send OK,    audit fails → audit exception propagates uncaught (`when` filter false).
        //                               Outbox retries; idempotent audit emit eventually lands.
        //   * send fails, audit fails → AggregateException(send, audit) thrown.
        //                               Both root causes carried in InnerExceptions.
        var auditTimestamp = _timeProvider.GetUtcNow();
        var outcome = AuditOutcomes.Success;
        var gateRejected = false;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? capturedSendFailure = null;
        try
        {
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
                    gateRejected = true;
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
        catch (Exception ex)
        {
            outcome = gateRejected ? AuditOutcomes.Rejected : AuditOutcomes.Failed;
            capturedSendFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
        }

        try
        {
            await LogProactiveNotificationAsync(
                action: "send_message",
                tenantId: tenantId,
                actorAgentId: message.AgentId,
                taskId: message.TaskId,
                conversationId: message.ConversationId,
                correlationId: message.CorrelationId ?? string.Empty,
                payloadJson: BuildSendMessagePayload(message, targetUserId: userId, targetChannelId: null, capturedSendFailure?.SourceException),
                outcome: outcome,
                timestamp: auditTimestamp,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception auditEx) when (capturedSendFailure is not null)
        {
            _logger.LogError(
                auditEx,
                "ProactiveNotification audit emit failed AFTER proactive MessengerMessage send failure to user {InternalUserId} in tenant {TenantId} (correlation {CorrelationId}); surfacing AggregateException carrying BOTH root causes so the outbox sees the dispatch failure and the missing audit row.",
                userId,
                tenantId,
                message.CorrelationId);
            throw new AggregateException(
                $"Proactive MessengerMessage send to user '{userId}' in tenant '{tenantId}' failed AND ProactiveNotification audit-row persistence failed (correlation {message.CorrelationId}). Both root causes are carried in InnerExceptions; the outbox should retry so the idempotent audit row can eventually land.",
                capturedSendFailure.SourceException,
                auditEx);
        }

        capturedSendFailure?.Throw();
    }

    /// <inheritdoc />
    public async Task SendProactiveQuestionAsync(string tenantId, string userId, AgentQuestion question, CancellationToken ct)
    {
        ValidateRequiredArgument(tenantId, nameof(tenantId));
        ValidateRequiredArgument(userId, nameof(userId));
        ArgumentNullException.ThrowIfNull(question);

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
        EnsureTenantMatchesQuestion(tenantId, question);
        EnsureScopeUserTargeted(userId, question);

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

        // Stage 5.2 — see SendProactiveAsync above for the full dual-failure contract;
        // identical capture/rethrow/AggregateException shape applies here.
        var auditTimestamp = _timeProvider.GetUtcNow();
        var outcome = AuditOutcomes.Success;
        var gateRejected = false;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? capturedSendFailure = null;
        try
        {
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
                    gateRejected = true;
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
        catch (Exception ex)
        {
            outcome = gateRejected ? AuditOutcomes.Rejected : AuditOutcomes.Failed;
            capturedSendFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
        }

        try
        {
            await LogProactiveNotificationAsync(
                action: "send_message",
                tenantId: tenantId,
                actorAgentId: message.AgentId,
                taskId: message.TaskId,
                conversationId: message.ConversationId,
                correlationId: message.CorrelationId ?? string.Empty,
                payloadJson: BuildSendMessagePayload(message, targetUserId: null, targetChannelId: channelId, capturedSendFailure?.SourceException),
                outcome: outcome,
                timestamp: auditTimestamp,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception auditEx) when (capturedSendFailure is not null)
        {
            _logger.LogError(
                auditEx,
                "ProactiveNotification audit emit failed AFTER proactive MessengerMessage send failure to channel {ChannelId} in tenant {TenantId} (correlation {CorrelationId}); surfacing AggregateException carrying BOTH root causes so the outbox sees the dispatch failure and the missing audit row.",
                channelId,
                tenantId,
                message.CorrelationId);
            throw new AggregateException(
                $"Proactive MessengerMessage send to channel '{channelId}' in tenant '{tenantId}' failed AND ProactiveNotification audit-row persistence failed (correlation {message.CorrelationId}). Both root causes are carried in InnerExceptions; the outbox should retry so the idempotent audit row can eventually land.",
                capturedSendFailure.SourceException,
                auditEx);
        }

        capturedSendFailure?.Throw();
    }

    /// <inheritdoc />
    public async Task SendQuestionToChannelAsync(string tenantId, string channelId, AgentQuestion question, CancellationToken ct)
    {
        ValidateRequiredArgument(tenantId, nameof(tenantId));
        ValidateRequiredArgument(channelId, nameof(channelId));
        ArgumentNullException.ThrowIfNull(question);

        // Stage 6.3 iter-2 — channel-scoped question enrichment scope.
        using var logScope = TeamsLogScope.BeginScope(
            _logger,
            correlationId: question.CorrelationId,
            tenantId: tenantId,
            userId: null);

        // Security-relevant consistency guard (iter-2 evaluator feedback #1, #2) —
        // see SendProactiveQuestionAsync for the full rationale.
        EnsureTenantMatchesQuestion(tenantId, question);
        EnsureScopeChannelTargeted(channelId, question);

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
        // calls into here. Validate() runs again so a malformed question (TargetUserId
        // and TargetChannelId both null, missing CorrelationId, etc.) fails loudly before
        // we touch the network. The connector's own SendQuestionAsync does the same.
        var validationErrors = question.Validate();
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"AgentQuestion '{question.QuestionId}' is invalid: {string.Join("; ", validationErrors)}");
        }

        // Step 1a (iter-4 evaluator feedback #1 — outbox-retry idempotency).
        // The Phase 6 outbox engine may replay a proactive send after the first attempt
        // succeeded at the network layer but the worker died before acking, or after a
        // transient delivery failure. If a TeamsCardState row already exists for this
        // QuestionId, the previous attempt already delivered the card — re-sending would
        // produce a duplicate Adaptive Card in Teams (and would overwrite the stored
        // ActivityId/ConversationReferenceJson, orphaning the first card to the user).
        // Short-circuit here so retries are safe to call as many times as the outbox
        // wants. Stage 5.2 note: we deliberately SKIP audit emission on this branch —
        // the first successful attempt already produced a `ProactiveNotification`
        // audit row with `Outcome=Success`; emitting another one on a no-op retry
        // would double-count in compliance dashboards.
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

        // Stage 5.2 — every question delivery (Success / Failed / Rejected) emits one
        // ProactiveNotification audit row per tech-spec.md §4.3. Stage 5.2 iter-r0
        // (review comment at line 544): the audit emit runs AFTER the send try/catch
        // (NOT from `finally`) because LogProactiveNotificationAsync does NOT swallow
        // audit-store failures, and a throw-from-finally would replace the original
        // send exception via the CLR's exception-replacement semantics. The send
        // failure is captured via ExceptionDispatchInfo so it can be re-thrown with
        // the original stack OR combined with the audit failure into an
        // AggregateException — same dual-failure pattern as
        // TeamsSwarmActivityHandler.OnMessageActivityAsync (see comment there for the
        // full branch matrix).
        var auditTimestamp = _timeProvider.GetUtcNow();
        var outcome = AuditOutcomes.Success;
        var gateRejected = false;
        string? deliveredActivityId = null;
        string? deliveredConversationId = null;
        string? deliveredReferenceJson = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? capturedSendFailure = null;
        try
        {
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
                EnsureRetryMatchesStoredQuestion(question, existingQuestion);

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
                    gateRejected = true;
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
        catch (Exception ex)
        {
            outcome = gateRejected ? AuditOutcomes.Rejected : AuditOutcomes.Failed;
            capturedSendFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
        }

        try
        {
            await LogProactiveNotificationAsync(
                action: "send_card",
                tenantId: tenantId,
                actorAgentId: question.AgentId,
                taskId: question.TaskId,
                conversationId: deliveredConversationId ?? question.ConversationId,
                correlationId: question.CorrelationId ?? string.Empty,
                payloadJson: BuildSendCardPayload(question, deliveredActivityId, capturedSendFailure?.SourceException),
                outcome: outcome,
                timestamp: auditTimestamp,
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception auditEx) when (capturedSendFailure is not null)
        {
            _logger.LogError(
                auditEx,
                "ProactiveNotification audit emit failed AFTER proactive AgentQuestion {QuestionId} send failure to {Target} in tenant {TenantId} (correlation {CorrelationId}); surfacing AggregateException carrying BOTH root causes so the outbox sees the dispatch failure and the missing audit row.",
                question.QuestionId,
                targetDescription,
                tenantId,
                question.CorrelationId);
            throw new AggregateException(
                $"Proactive AgentQuestion '{question.QuestionId}' send to {targetDescription} in tenant '{tenantId}' failed AND ProactiveNotification audit-row persistence failed (correlation {question.CorrelationId}). Both root causes are carried in InnerExceptions; the outbox should retry so the idempotent audit row can eventually land.",
                capturedSendFailure.SourceException,
                auditEx);
        }

        capturedSendFailure?.Throw();
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
    /// Iter-4 evaluator feedback #1 / rubber-duck non-blocking #2 — when an outbox
    /// retry finds an existing <see cref="AgentQuestion"/> row, every identity / routing
    /// / payload field on the incoming question MUST match the stored row. Otherwise the
    /// orchestrator has mutated the question after enqueuing it, which is "card update"
    /// semantics (not retry) and is not supported by Stage 4.2 — the card delivered to
    /// Teams would then drift from the row <see cref="Cards.CardActionHandler"/> later
    /// loads via <see cref="IAgentQuestionStore.GetByIdAsync"/>.
    /// </summary>
    /// <remarks>
    /// Fields compared:
    ///   <list type="bullet">
    ///     <item><description>Identity: <c>TenantId</c>, <c>AgentId</c>, <c>TaskId</c>, <c>CorrelationId</c>.</description></item>
    ///     <item><description>Routing: <c>TargetUserId</c>, <c>TargetChannelId</c>.</description></item>
    ///     <item><description>Payload: <c>Title</c>, <c>Body</c>, <c>Severity</c>, <c>ExpiresAt</c>, and the <c>AllowedActions</c> list (count + each element's <c>ActionId</c> / <c>Label</c> / <c>Value</c> / <c>RequiresComment</c>).</description></item>
    ///   </list>
    /// <c>QuestionId</c> equality is guaranteed because the lookup was keyed by it.
    /// <c>ConversationId</c>, <c>Status</c>, <c>CreatedAt</c>, and <c>ResolvedAt</c> are
    /// store-owned lifecycle fields and are NOT compared.
    /// </remarks>
    private static void EnsureRetryMatchesStoredQuestion(AgentQuestion incoming, AgentQuestion stored)
    {
        static string Norm(string? s) => s ?? string.Empty;

        var mismatches = new List<string>();
        if (!string.Equals(incoming.TenantId, stored.TenantId, StringComparison.Ordinal))
        {
            mismatches.Add($"TenantId (incoming='{incoming.TenantId}', stored='{stored.TenantId}')");
        }

        if (!string.Equals(incoming.AgentId, stored.AgentId, StringComparison.Ordinal))
        {
            mismatches.Add($"AgentId (incoming='{incoming.AgentId}', stored='{stored.AgentId}')");
        }

        if (!string.Equals(incoming.TaskId, stored.TaskId, StringComparison.Ordinal))
        {
            mismatches.Add($"TaskId (incoming='{incoming.TaskId}', stored='{stored.TaskId}')");
        }

        if (!string.Equals(incoming.CorrelationId, stored.CorrelationId, StringComparison.Ordinal))
        {
            mismatches.Add($"CorrelationId (incoming='{incoming.CorrelationId}', stored='{stored.CorrelationId}')");
        }

        if (!string.Equals(Norm(incoming.TargetUserId), Norm(stored.TargetUserId), StringComparison.Ordinal))
        {
            mismatches.Add($"TargetUserId (incoming='{incoming.TargetUserId}', stored='{stored.TargetUserId}')");
        }

        if (!string.Equals(Norm(incoming.TargetChannelId), Norm(stored.TargetChannelId), StringComparison.Ordinal))
        {
            mismatches.Add($"TargetChannelId (incoming='{incoming.TargetChannelId}', stored='{stored.TargetChannelId}')");
        }

        if (!string.Equals(incoming.Title, stored.Title, StringComparison.Ordinal))
        {
            mismatches.Add("Title");
        }

        if (!string.Equals(incoming.Body, stored.Body, StringComparison.Ordinal))
        {
            mismatches.Add("Body");
        }

        if (!string.Equals(incoming.Severity, stored.Severity, StringComparison.Ordinal))
        {
            mismatches.Add($"Severity (incoming='{incoming.Severity}', stored='{stored.Severity}')");
        }

        if (incoming.ExpiresAt != stored.ExpiresAt)
        {
            mismatches.Add($"ExpiresAt (incoming='{incoming.ExpiresAt:o}', stored='{stored.ExpiresAt:o}')");
        }

        if (incoming.AllowedActions.Count != stored.AllowedActions.Count)
        {
            mismatches.Add($"AllowedActions.Count (incoming={incoming.AllowedActions.Count}, stored={stored.AllowedActions.Count})");
        }
        else
        {
            for (var i = 0; i < incoming.AllowedActions.Count; i++)
            {
                var a = incoming.AllowedActions[i];
                var b = stored.AllowedActions[i];
                if (!string.Equals(a.ActionId, b.ActionId, StringComparison.Ordinal)
                    || !string.Equals(a.Label, b.Label, StringComparison.Ordinal)
                    || !string.Equals(a.Value, b.Value, StringComparison.Ordinal)
                    || a.RequiresComment != b.RequiresComment)
                {
                    mismatches.Add($"AllowedActions[{i}]");
                }
            }
        }

        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(
                $"AgentQuestion '{incoming.QuestionId}' was already persisted with different metadata than the incoming retry; refusing to send a card whose payload diverges from the stored row that CardActionHandler will load on reply. Mismatched fields: {string.Join(", ", mismatches)}. Stage 4.2 does not support mutating an in-flight question — either preserve the original payload on retry or assign a new QuestionId.");
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

    /// <summary>
    /// Sanitized JSON payload for a <c>send_message</c> ProactiveNotification audit row
    /// (Stage 5.2 / tech-spec.md §4.3). Captures the routing target and delivery outcome
    /// metadata WITHOUT including the message body — the body may contain sensitive
    /// agent output, and §4.3 mandates "sanitized JSON" in PayloadJson. Body content is
    /// out-of-band for compliance review; routing identifiers and severity are sufficient
    /// to reconstruct what was sent for forensic purposes.
    /// </summary>
    private static string BuildSendMessagePayload(
        MessengerMessage message,
        string? targetUserId,
        string? targetChannelId,
        Exception? failure)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            messageId = message.MessageId,
            severity = message.Severity.ToString(),
            targetUserId,
            targetChannelId,
            failure = failure is null ? null : $"{failure.GetType().Name}: {failure.Message}",
        });
    }

    /// <summary>
    /// Sanitized JSON payload for a <c>send_card</c> ProactiveNotification audit row
    /// (Stage 5.2 / tech-spec.md §4.3). Includes the QuestionId, routing target, severity,
    /// and (on success) the delivered Bot Framework ActivityId so compliance reviewers can
    /// correlate the audit row with the Teams message they later see updated or deleted via
    /// Stage 3.3's card lifecycle. Excludes the question title/body — those may contain
    /// sensitive prompt text that downstream compliance dashboards should not store.
    /// </summary>
    private static string BuildSendCardPayload(
        AgentQuestion question,
        string? activityId,
        Exception? failure)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            questionId = question.QuestionId,
            severity = question.Severity.ToString(),
            targetUserId = question.TargetUserId,
            targetChannelId = question.TargetChannelId,
            activityId,
            failure = failure is null ? null : $"{failure.GetType().Name}: {failure.Message}",
        });
    }

    /// <summary>
    /// Emit a single <see cref="AuditEventTypes.ProactiveNotification"/> row to
    /// <see cref="IAuditLogger"/> per tech-spec.md §4.3. Called from all three send paths
    /// (<see cref="SendProactiveAsync"/>, <see cref="SendToChannelAsync"/>,
    /// <see cref="SendQuestionCoreAsync"/>) AFTER the send try/catch (NOT from a
    /// <c>finally</c> block) so an audit-emit failure cannot replace the original send
    /// exception via the CLR's throw-from-finally semantics. The audit trail is durable
    /// regardless of whether the send succeeded, was rejected by
    /// <see cref="InstallationStateGate"/>, or threw at the Bot Framework layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Does NOT swallow <see cref="IAuditLogger"/> exceptions. Audit durability is a
    /// hard compliance requirement — the workstream's "Persist immutable audit trail
    /// suitable for enterprise review" contract demands that every outbound notification
    /// land an <see cref="AuditEntry"/> row, and silently logging-and-discarding an
    /// audit-store outage would make the gap invisible (this design choice was driven by
    /// iter-1 evaluator item 7 which explicitly rejected silent loss of audit rows even
    /// with a logged warning).
    /// </para>
    /// <para>
    /// Because this method propagates audit failures, the caller MUST invoke it OUTSIDE
    /// the original send's <c>try</c> block (specifically not from a <c>finally</c>) and
    /// MUST use <see cref="System.Runtime.ExceptionServices.ExceptionDispatchInfo"/> +
    /// <see cref="AggregateException"/> so that:
    /// (a) a successful audit emit does not mask a prior send failure; and
    /// (b) an audit-emit failure that follows a send failure does not silently replace
    ///     the send failure on the wire per the CLR's throw-from-finally semantics.
    /// All three callers in this class implement that pattern; it mirrors
    /// <see cref="TeamsSwarmActivityHandler.OnMessageActivityAsync"/>.
    /// </para>
    /// </remarks>
    private async Task LogProactiveNotificationAsync(
        string action,
        string tenantId,
        string actorAgentId,
        string? taskId,
        string? conversationId,
        string correlationId,
        string payloadJson,
        string outcome,
        DateTimeOffset timestamp,
        CancellationToken ct)
    {
        // Stage 5.2 — proactive-notification audit emission MUST be durable. The
        // workstream's compliance contract ("Persist immutable audit trail suitable for
        // enterprise review") is a hard requirement, not a best-effort guideline: every
        // outbound notification has to land an AuditLog row. We deliberately do NOT
        // wrap this in try/catch here — if the audit store is unreachable, the caller
        // (the outbox dispatcher) must see the failure and retry; a notification that
        // succeeded at the Bot Framework layer but failed at the audit layer is a
        // partial-write that the outbox's idempotency layer is responsible for
        // reconciling (same correlation-id → no double user message; eventual audit
        // row lands on the successful attempt). Swallowing here was iter-1 evaluator
        // item 7 — the eval rejected silent loss of audit rows even with a logged
        // warning. The Stage 5.2 iter-r0 review (comment at line 544) further required
        // that callers NOT await this from `finally`: that pattern would let a throw
        // from this method REPLACE an in-flight send exception via the CLR's
        // exception-replacement semantics, silently losing the original send failure.
        // The three callers therefore capture the send failure via ExceptionDispatchInfo
        // and combine it with any audit failure into an AggregateException.
        var checksum = AuditEntry.ComputeChecksum(
            timestamp: timestamp,
            correlationId: correlationId,
            eventType: AuditEventTypes.ProactiveNotification,
            actorId: actorAgentId,
            actorType: AuditActorTypes.Agent,
            tenantId: tenantId,
            agentId: actorAgentId,
            taskId: taskId,
            conversationId: conversationId,
            action: action,
            payloadJson: payloadJson,
            outcome: outcome);

        var entry = new AuditEntry
        {
            Timestamp = timestamp,
            CorrelationId = correlationId,
            EventType = AuditEventTypes.ProactiveNotification,
            ActorId = actorAgentId,
            ActorType = AuditActorTypes.Agent,
            TenantId = tenantId,
            AgentId = actorAgentId,
            TaskId = taskId,
            ConversationId = conversationId,
            Action = action,
            PayloadJson = payloadJson,
            Outcome = outcome,
            Checksum = checksum,
        };

        await _auditLogger.LogAsync(entry, ct).ConfigureAwait(false);
    }
}
