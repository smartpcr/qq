using System.Globalization;
using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Teams;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Teams-specific <see cref="TeamsActivityHandler"/> subclass that wires the canonical
/// inbound-activity overrides into the agent-swarm gateway. Aligned with
/// <c>implementation-plan.md</c> §2.2 and <c>architecture.md</c> §2.4.
/// </summary>
/// <remarks>
/// <para>
/// The handler implements a deliberate <b>two-tier authorization model</b>:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Install lifecycle events</b> (<see cref="OnTeamsMembersAddedAsync"/>,
/// <see cref="OnTeamsMembersRemovedAsync"/>, <see cref="OnInstallationUpdateActivityAsync"/>)
/// — tenant validation ONLY (enforced upstream by <c>TenantValidationMiddleware</c> in the
/// ASP.NET Core HTTP pipeline). No identity resolution or user-level RBAC. The captured
/// reference proves the app is installed; subsequent command-time authorization gates
/// privileged actions.
/// </description></item>
/// <item><description>
/// <b>Command events</b> (<see cref="OnMessageActivityAsync"/>) — tenant + identity + RBAC.
/// Unmapped users and insufficient roles are rejected with an access-denied reply, an
/// audit <c>SecurityRejection</c> entry, and NO conversation-reference save (the reference
/// is captured only when the user is authorized).
/// </description></item>
/// </list>
/// <para>
/// The handler is also the SOLE location that performs <c>@mention</c> stripping
/// (via <c>Activity.RemoveRecipientMention()</c>). Downstream consumers
/// (<see cref="ICommandDispatcher"/>) receive already-cleaned text — they must NOT
/// re-strip.
/// </para>
/// </remarks>
public sealed class TeamsSwarmActivityHandler : TeamsActivityHandler
{
    /// <summary>
    /// Key used to stash the per-turn correlation ID into
    /// <see cref="ITurnContext.TurnState"/>. Downstream middleware and command handlers
    /// retrieve the same value via <c>TurnState.Get&lt;string&gt;(key)</c>.
    /// </summary>
    public const string CorrelationIdTurnStateKey = "CorrelationId";

    /// <summary>
    /// Property name searched on the inbound <see cref="Activity.Properties"/> JObject for
    /// an upstream-supplied correlation ID. Lookup is case-insensitive (we probe both
    /// <c>correlationId</c> and <c>CorrelationId</c>).
    /// </summary>
    private const string CorrelationIdPropertyName = "correlationId";

    private static readonly IReadOnlyList<string> KnownCommandVerbs = new[]
    {
        "agent ask",
        "agent status",
        "approve",
        "reject",
        "escalate",
        "pause",
        "resume",
    };

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IConversationReferenceStore _conversationReferenceStore;
    private readonly ICommandDispatcher _commandDispatcher;
    private readonly IIdentityResolver _identityResolver;
    private readonly IUserAuthorizationService _authorizationService;
#pragma warning disable IDE0052 // injected per implementation-plan §2.2; consumed once card-action wiring exposes question lookups in Stage 3.3.
    private readonly IAgentQuestionStore _agentQuestionStore;
#pragma warning restore IDE0052
    private readonly IAuditLogger _auditLogger;
    private readonly ICardActionHandler _cardActionHandler;
#pragma warning disable IDE0052 // dependency retained for backward compat; per impl-plan §3.2 step 7 the CommandDispatcher (not this handler) owns inbound event publication.
    private readonly IInboundEventPublisher _inboundEventPublisher;
#pragma warning restore IDE0052
    private readonly Extensions.IMessageExtensionHandler _messageExtensionHandler;
    private readonly ILogger<TeamsSwarmActivityHandler> _logger;

    /// <summary>
    /// Construct the handler with the ten required dependencies per
    /// <c>implementation-plan.md</c> §2.2 step 1 and Stage 3.4 step 2. All arguments are
    /// required — every constructor parameter is null-guarded so DI mis-registration
    /// fails loudly at startup rather than producing a <see cref="NullReferenceException"/>
    /// deep inside an activity callback.
    /// </summary>
    public TeamsSwarmActivityHandler(
        IConversationReferenceStore conversationReferenceStore,
        ICommandDispatcher commandDispatcher,
        IIdentityResolver identityResolver,
        IUserAuthorizationService authorizationService,
        IAgentQuestionStore agentQuestionStore,
        IAuditLogger auditLogger,
        ICardActionHandler cardActionHandler,
        IInboundEventPublisher inboundEventPublisher,
        Extensions.IMessageExtensionHandler messageExtensionHandler,
        ILogger<TeamsSwarmActivityHandler> logger)
    {
        _conversationReferenceStore = conversationReferenceStore ?? throw new ArgumentNullException(nameof(conversationReferenceStore));
        _commandDispatcher = commandDispatcher ?? throw new ArgumentNullException(nameof(commandDispatcher));
        _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
        _authorizationService = authorizationService ?? throw new ArgumentNullException(nameof(authorizationService));
        _agentQuestionStore = agentQuestionStore ?? throw new ArgumentNullException(nameof(agentQuestionStore));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _cardActionHandler = cardActionHandler ?? throw new ArgumentNullException(nameof(cardActionHandler));
        _inboundEventPublisher = inboundEventPublisher ?? throw new ArgumentNullException(nameof(inboundEventPublisher));
        _messageExtensionHandler = messageExtensionHandler ?? throw new ArgumentNullException(nameof(messageExtensionHandler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stamps a correlation ID onto the turn context for distributed tracing. The value is
    /// taken from <see cref="Activity.Properties"/> (case-insensitive
    /// <see cref="CorrelationIdPropertyName"/> lookup) when present, falling back to a new
    /// GUID. The value is stored under <see cref="CorrelationIdTurnStateKey"/> on
    /// <see cref="ITurnContext.TurnState"/> so every downstream override and middleware
    /// reads the same value.
    /// </remarks>
    public override Task OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken = default)
    {
        if (turnContext is null)
        {
            throw new ArgumentNullException(nameof(turnContext));
        }

        var correlationId = ExtractCorrelationId(turnContext.Activity);
        turnContext.TurnState.Set(CorrelationIdTurnStateKey, correlationId);
        return base.OnTurnAsync(turnContext, cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task OnMessageActivityAsync(
        ITurnContext<IMessageActivity> turnContext,
        CancellationToken cancellationToken)
    {
        if (turnContext is null)
        {
            throw new ArgumentNullException(nameof(turnContext));
        }

        var activity = turnContext.Activity as Activity;
        var correlationId = GetCorrelationId(turnContext);
        var tenantId = ExtractTenantId(activity);
        var aadObjectId = activity?.From?.AadObjectId ?? string.Empty;

        // (1) Strip @mention markup from the message text. This is the SOLE call site
        // per implementation-plan §2.2 — downstream consumers receive cleaned text.
        var normalizedText = StripRecipientMention(turnContext);

        // (2) Resolve identity. Unmapped users are rejected without persisting the
        // conversation reference (impl-plan §2.2 + architecture §6.4.2).
        var resolvedIdentity = string.IsNullOrEmpty(aadObjectId)
            ? null
            : await _identityResolver.ResolveAsync(aadObjectId, cancellationToken).ConfigureAwait(false);

        if (resolvedIdentity is null)
        {
            _logger.LogWarning(
                "Inbound message rejected — AAD object ID {AadObjectId} not mapped (tenant {TenantId}, correlation {CorrelationId}).",
                aadObjectId,
                tenantId,
                correlationId);

            await LogSecurityRejectionAsync(
                correlationId: correlationId,
                tenantId: tenantId,
                actorId: string.IsNullOrEmpty(aadObjectId) ? "unknown" : aadObjectId,
                action: "UnmappedUserRejected",
                conversationId: activity?.Conversation?.Id,
                reason: "Identity resolver returned null for AAD object ID.",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var deniedCard = BuildAccessDeniedCardActivity(
                reason: "Your account is not mapped in this organization. Please contact your administrator.",
                requiredRole: null);
            await turnContext.SendActivityAsync(deniedCard, cancellationToken).ConfigureAwait(false);
            return;
        }

        // (2.5) Determine whether the inbound text matches a canonical command verb. Only
        // canonical verbs go through role-scoped RBAC (per architecture.md §5.2 — RBAC is
        // a per-command authorization decision keyed on the verb vocabulary). Free-text /
        // unrecognised input is NOT a privileged action: tenant validation (upstream
        // middleware) plus successful identity resolution above are sufficient to deliver
        // the text to the dispatcher's TextEvent path. This branch resolves evaluator
        // iter-1 finding #4: a realistic RBAC implementation must not reject `hello
        // there` before the dispatcher publishes the required `TextEvent` and help card.
        var commandVerb = ExtractCommandVerb(normalizedText);
        var isCanonical = IsCanonicalVerb(commandVerb);

        // (3) Authorize the command (canonical verbs only). The default-deny stub rejects
        // every request; Stage 5.1 swaps in role-scoped RBAC.
        //
        // CRITICAL: RBAC is keyed by the Entra AAD object ID (per
        // `RbacAuthorizationService` xmldoc and `RbacOptions.TenantRoleAssignments`),
        // NOT by the platform-internal user ID. Previously this call passed
        // `resolvedIdentity.InternalUserId` which silently denied real
        // AAD-keyed config because the two identifiers rarely match.
        if (isCanonical)
        {
            var rbacSubject = !string.IsNullOrEmpty(resolvedIdentity.AadObjectId)
                ? resolvedIdentity.AadObjectId
                : aadObjectId;

            var authorization = await _authorizationService
                .AuthorizeAsync(tenantId, rbacSubject, commandVerb, cancellationToken)
                .ConfigureAwait(false);

            if (!authorization.IsAuthorized)
            {
                _logger.LogWarning(
                    "Inbound message rejected — user {AadObjectId} (internal {InternalUserId}, role {UserRole}) lacks role {RequiredRole} for command '{Command}' (tenant {TenantId}, correlation {CorrelationId}).",
                    rbacSubject,
                    resolvedIdentity.InternalUserId,
                    authorization.UserRole,
                    authorization.RequiredRole,
                    commandVerb,
                    tenantId,
                    correlationId);

                await LogSecurityRejectionAsync(
                    correlationId: correlationId,
                    tenantId: tenantId,
                    actorId: aadObjectId,
                    action: "InsufficientRoleRejected",
                    conversationId: activity?.Conversation?.Id,
                    reason: $"User role '{authorization.UserRole}' is insufficient for command '{commandVerb}' (required: '{authorization.RequiredRole}').",
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var deniedReason = authorization.RequiredRole is null
                    ? "Insufficient permissions for this command."
                    : $"This command requires the '{authorization.RequiredRole}' role.";
                var deniedCard = BuildAccessDeniedCardActivity(deniedReason, authorization.RequiredRole);
                await turnContext.SendActivityAsync(deniedCard, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // (3.5) Authorized inbound — defer audit emission to AFTER dispatch so the
        // CommandReceived audit row (per tech-spec.md §4.3) carries the actual
        // outcome (Success / Failed) AND the AgentId / TaskId that the matched
        // command handler resolved or created (e.g. ApproveRejectCommandExecutor
        // stamps the resolved question's AgentId/TaskId onto context.AgentId/TaskId;
        // AskCommandHandler stamps the new task tracking ID). The earlier
        // pre-dispatch emit hardcoded outcome=Success and AgentId/TaskId=null —
        // both of those were violations of §4.3 (the schema requires per-event
        // outcome and per-event task/agent association whenever they apply).
        //
        // Persist/refresh the conversation reference BEFORE dispatch (formerly
        // step 4) so that downstream handlers that rely on
        // ConversationReferenceStore inside their dispatch path still see the
        // refresh — the reference write is a side effect of "the user spoke",
        // not of "the command succeeded".
        //
        // For non-canonical text, the commandVerb is empty so the audit `Action`
        // records "(text)" — distinguishing the shape from a real command without
        // inventing a new AuditEventType (the canonical set is fixed at seven per
        // tech-spec §4.3).

        // (4) Persist/refresh the conversation reference AFTER successful identity + RBAC.
        // This satisfies e2e-scenarios.md §Conversation Reference Persistence and the
        // story requirement that references are stored only for authorized users.
        await PersistConversationReferenceAsync(
            turnContext,
            resolvedIdentity,
            tenantId,
            cancellationToken).ConfigureAwait(false);

        // (5) Dispatch to the command dispatcher with a fully populated CommandContext.
        // Per impl-plan §2.2 step 2, CommandDispatcher receives ALREADY-CLEANED text and
        // must NOT perform any @mention stripping itself. The dispatcher and its handlers
        // own ALL inbound event publication for this turn — canonical command handlers
        // publish their own `CommandEvent` (per impl-plan §3.2 step 2), approve/reject
        // publish `DecisionEvent`, and the dispatcher itself publishes `TextEvent` for
        // unrecognised input. The activity handler therefore does NOT publish a
        // post-dispatch `CommandEvent` — that would double-emit for approve/reject and
        // make the dispatcher non-self-sufficient outside the activity-handler path
        // (resolves evaluator iter-1 findings #2 and #3).
        var context = new CommandContext
        {
            NormalizedText = normalizedText,
            ResolvedIdentity = resolvedIdentity,
            CorrelationId = correlationId,
            TurnContext = turnContext,
            ConversationId = activity?.Conversation?.Id,
            ActivityId = activity?.Id,
        };

        // Stage 5.2 step 3 — try/catch+finally so EVERY dispatch produces exactly
        // one CommandReceived audit row. Outcome is "Success" when DispatchAsync
        // returns normally and "Failed" when it throws; the exception still
        // propagates so the outer middleware sees the same error shape as before.
        // AgentId / TaskId are read from `context` in the finally — the matched
        // command handler may have stamped them on the context instance during
        // dispatch.
        // Stage 5.2 iter-3 — replace the prior try/catch+finally with an explicit
        // capture/rethrow so that:
        //   (a) the audit emit is OUTSIDE finally (a throw from a finally would
        //       replace the dispatch exception, hiding the real failure cause from
        //       Teams' inbound retry layer); and
        //   (b) audit failures STILL propagate (per iter-1 evaluator item 7 — every
        //       outbound notification MUST land a durable audit row; same applies
        //       to inbound CommandReceived).
        // Stage 5.2 iter-5 (eval iter-2 item 4) — when BOTH dispatch AND audit fail,
        // the prior iter-3 design logged-and-swallowed the audit error so only the
        // dispatch error surfaced. That suppressed the audit failure, which the
        // workstream's compliance contract ("every command emits an audit entry")
        // forbids. The fix: surface both via AggregateException so the dispatch
        // root cause and the audit-store outage are visible to upstream middleware
        // (and to Teams' inbound retry, which will re-run the activity and give
        // the idempotent audit row another chance to land).
        // Branch summary:
        //   * dispatch OK,    audit OK    → no exception (Success audit row landed).
        //   * dispatch fails, audit OK    → dispatch exception re-thrown with original
        //                                   stack via ExceptionDispatchInfo.Throw().
        //   * dispatch OK,    audit fails → audit exception propagates uncaught (the
        //                                   `when` filter is false). Teams retries the
        //                                   activity; the idempotent emit eventually lands.
        //   * dispatch fails, audit fails → AggregateException(dispatch, audit) thrown.
        //                                   Both root causes carried in InnerExceptions.
        var commandOutcome = AuditOutcomes.Success;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? capturedDispatchFailure = null;
        try
        {
            await _commandDispatcher.DispatchAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            commandOutcome = AuditOutcomes.Failed;
            capturedDispatchFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
        }

        try
        {
            await LogCommandReceivedAsync(
                correlationId: correlationId,
                tenantId: tenantId,
                actorId: aadObjectId,
                commandVerb: isCanonical ? commandVerb : "(text)",
                isCanonicalVerb: isCanonical,
                conversationId: activity?.Conversation?.Id,
                normalizedText: normalizedText,
                agentId: context.AgentId,
                taskId: context.TaskId,
                // Stage 5.2 iter-4 (eval item 7) — handlers may signal a non-Success
                // outcome (Rejected) for handled command-level failures that return
                // normally instead of throwing. context.Outcome wins when set; the
                // Failed-on-throw / Success-on-clean-return defaults from
                // `commandOutcome` apply only when the handler did NOT declare one.
                outcome: context.Outcome ?? commandOutcome,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception auditEx) when (capturedDispatchFailure is not null)
        {
            // Both dispatch AND audit failed. Iter-5 fix (eval iter-2 item 4): do
            // NOT swallow the audit error — the brief requires every command to
            // produce a durable audit row, and a silently-logged audit failure
            // makes the gap invisible. Surface both via AggregateException so:
            //   * The dispatch root cause is preserved (Inner[0]).
            //   * The audit failure is surfaced (Inner[1]) so the missing
            //     CommandReceived row is visible at the failure boundary.
            //   * Teams' inbound retry sees a failure and re-runs the activity,
            //     giving the idempotent audit emit another chance to land.
            // Also log loudly so operations see both root causes in the trace.
            _logger.LogError(
                auditEx,
                "CommandReceived audit emit failed AFTER dispatch failure (correlation {CorrelationId}); surfacing AggregateException carrying BOTH root causes.",
                correlationId);
            throw new AggregateException(
                $"Command dispatch failed AND CommandReceived audit-row persistence failed (correlation {correlationId}). Both root causes are carried in InnerExceptions; Teams should retry the activity so the idempotent audit row can eventually land.",
                capturedDispatchFailure.SourceException,
                auditEx);
        }

        capturedDispatchFailure?.Throw();
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="commandVerb"/> matches one of the
    /// canonical command verbs in <see cref="KnownCommandVerbs"/>. Used to gate role-scoped
    /// RBAC (per <c>architecture.md</c> §5.2) so unrecognised free-text input flows to the
    /// dispatcher's <c>TextEvent</c> path (per <c>implementation-plan.md</c> §3.2 step 7)
    /// instead of being rejected by a default-deny authorization stub.
    /// </summary>
    private static bool IsCanonicalVerb(string commandVerb)
        => !string.IsNullOrEmpty(commandVerb) && KnownCommandVerbs.Contains(commandVerb);

    /// <inheritdoc />
    protected override async Task OnTeamsMembersAddedAsync(
        IList<TeamsChannelAccount> membersAdded,
        TeamInfo teamInfo,
        ITurnContext<IConversationUpdateActivity> turnContext,
        CancellationToken cancellationToken)
    {
        if (turnContext is null)
        {
            throw new ArgumentNullException(nameof(turnContext));
        }

        var activity = turnContext.Activity as Activity;
        if (!BotWasAdded(membersAdded, activity))
        {
            return;
        }

        // Two-tier authorization: install events are tenant-only. No identity/RBAC.
        var tenantId = ExtractTenantId(activity);
        var correlationId = GetCorrelationId(turnContext);

        await PersistConversationReferenceAsync(
            turnContext,
            resolvedIdentity: null,
            tenantId: tenantId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await LogInstallAuditAsync(
            correlationId: correlationId,
            tenantId: tenantId,
            actorId: activity?.From?.AadObjectId ?? "unknown",
            action: teamInfo is null ? "BotAddedToPersonalChat" : "BotAddedToTeam",
            conversationId: activity?.Conversation?.Id,
            extraPayload: teamInfo is null
                ? null
                : new Dictionary<string, object?>
                  {
                      ["teamId"] = teamInfo.Id,
                      ["channelId"] = ExtractChannelId(activity),
                  },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task OnTeamsMembersRemovedAsync(
        IList<TeamsChannelAccount> membersRemoved,
        TeamInfo teamInfo,
        ITurnContext<IConversationUpdateActivity> turnContext,
        CancellationToken cancellationToken)
    {
        if (turnContext is null)
        {
            throw new ArgumentNullException(nameof(turnContext));
        }

        var activity = turnContext.Activity as Activity;
        if (!BotWasRemoved(membersRemoved, activity))
        {
            return;
        }

        var tenantId = ExtractTenantId(activity);
        var correlationId = GetCorrelationId(turnContext);
        var aadObjectId = activity?.From?.AadObjectId;
        // Team scope = the activity carries TeamInfo OR TeamsChannelData.Team.Id.
        // (TeamInfo passed by the base dispatcher is the strongest signal, but we cross-
        // check the channel data so out-of-band installation events still classify
        // correctly per item #2 from the iter-1 evaluator feedback.)
        var teamIdFromInfo = teamInfo?.Id;
        var teamIdFromChannelData = ExtractTeamId(activity);
        var teamId = !string.IsNullOrEmpty(teamIdFromInfo) ? teamIdFromInfo : teamIdFromChannelData;
        var isTeamScope = !string.IsNullOrEmpty(teamId) || IsTeamScope(activity);

        int channelsMarked;
        if (isTeamScope)
        {
            // Team-scope uninstall: enumerate EVERY stored channel reference for this team
            // and call MarkInactiveByChannelAsync per-channel (per impl-plan §2.2 step 5
            // and architecture §4.2).
            channelsMarked = await MarkTeamChannelsInactiveAsync(
                tenantId,
                teamId,
                ExtractChannelId(activity),
                cancellationToken).ConfigureAwait(false);
        }
        else if (!string.IsNullOrEmpty(aadObjectId))
        {
            // Personal-scope uninstall: mark the user-keyed reference inactive.
            await _conversationReferenceStore
                .MarkInactiveAsync(tenantId, aadObjectId, cancellationToken)
                .ConfigureAwait(false);
            channelsMarked = 0;
        }
        else
        {
            channelsMarked = 0;
        }

        await LogInstallAuditAsync(
            correlationId: correlationId,
            tenantId: tenantId,
            actorId: aadObjectId ?? "unknown",
            action: isTeamScope ? "BotRemovedFromTeam" : "BotRemovedFromPersonalChat",
            conversationId: activity?.Conversation?.Id,
            extraPayload: isTeamScope
                ? new Dictionary<string, object?>
                  {
                      ["teamId"] = teamId,
                      ["channelsMarkedInactive"] = channelsMarked,
                  }
                : null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task OnInstallationUpdateActivityAsync(
        ITurnContext<IInstallationUpdateActivity> turnContext,
        CancellationToken cancellationToken)
    {
        if (turnContext is null)
        {
            throw new ArgumentNullException(nameof(turnContext));
        }

        var activity = turnContext.Activity as Activity;
        var tenantId = ExtractTenantId(activity);
        var correlationId = GetCorrelationId(turnContext);
        var actionRaw = activity?.Action ?? string.Empty;
        var isAdd = string.Equals(actionRaw, "add", StringComparison.OrdinalIgnoreCase);
        var isRemove = string.Equals(actionRaw, "remove", StringComparison.OrdinalIgnoreCase);

        if (isAdd)
        {
            // Install: persist conversation reference (tenant-only auth — see two-tier model
            // in the OnTeamsMembersAddedAsync override).
            await PersistConversationReferenceAsync(
                turnContext,
                resolvedIdentity: null,
                tenantId: tenantId,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var addIsTeam = IsTeamScope(activity);
            await LogInstallAuditAsync(
                correlationId: correlationId,
                tenantId: tenantId,
                actorId: activity?.From?.AadObjectId ?? "unknown",
                action: addIsTeam ? "AppInstalledToTeam" : "AppInstalled",
                conversationId: activity?.Conversation?.Id,
                extraPayload: addIsTeam
                    ? new Dictionary<string, object?>
                      {
                          ["teamId"] = ExtractTeamId(activity),
                          ["channelId"] = ExtractChannelId(activity),
                      }
                    : null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else if (isRemove)
        {
            var aadObjectId = activity?.From?.AadObjectId;
            // Team scope is determined by the channel data carrying a TeamId OR a
            // ChannelId — NOT by the absence of an AAD object ID (the installer's AAD ID
            // is present even on team-level uninstalls, which previously caused item #2 in
            // the evaluator feedback).
            var teamId = ExtractTeamId(activity);
            var channelIdFromActivity = ExtractChannelId(activity);
            var isTeamScope = !string.IsNullOrEmpty(teamId) || !string.IsNullOrEmpty(channelIdFromActivity);

            int channelsMarked = 0;
            if (isTeamScope)
            {
                channelsMarked = await MarkTeamChannelsInactiveAsync(
                    tenantId,
                    teamId,
                    channelIdFromActivity,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!string.IsNullOrEmpty(aadObjectId))
            {
                await _conversationReferenceStore
                    .MarkInactiveAsync(tenantId, aadObjectId, cancellationToken)
                    .ConfigureAwait(false);
            }

            await LogInstallAuditAsync(
                correlationId: correlationId,
                tenantId: tenantId,
                actorId: aadObjectId ?? "unknown",
                action: isTeamScope ? "AppUninstalledFromTeam" : "AppUninstalled",
                conversationId: activity?.Conversation?.Id,
                extraPayload: isTeamScope
                    ? new Dictionary<string, object?>
                      {
                          ["teamId"] = teamId,
                          ["channelsMarkedInactive"] = channelsMarked,
                      }
                    : null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.LogDebug(
                "Ignoring installationUpdate with unrecognized action '{Action}' (tenant {TenantId}).",
                actionRaw,
                tenantId);
        }

        await base.OnInstallationUpdateActivityAsync(turnContext, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Delegates the full card-action pipeline (action-id extraction, question lookup,
    /// allowed-action validation, decision emission) to the injected
    /// <see cref="ICardActionHandler"/>. The <c>NoOpCardActionHandler</c> stub registered
    /// in Stage 2.1 returns a placeholder response until the concrete
    /// <c>CardActionHandler</c> ships in Stage 3.3.
    /// </remarks>
    protected override Task<AdaptiveCardInvokeResponse> OnAdaptiveCardInvokeAsync(
        ITurnContext<IInvokeActivity> turnContext,
        AdaptiveCardInvokeValue invokeValue,
        CancellationToken cancellationToken)
    {
        if (turnContext is null)
        {
            throw new ArgumentNullException(nameof(turnContext));
        }

        return _cardActionHandler.HandleAsync(turnContext, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stage 3.4 step 2 — delegates Teams message-extension <c>submitAction</c> invokes
    /// (for example, the user right-clicks a message and selects the "Forward to Agent"
    /// message action) to the injected
    /// <see cref="Extensions.IMessageExtensionHandler"/>. The handler extracts the
    /// forwarded message text, dispatches it through
    /// <see cref="ICommandDispatcher.DispatchAsync"/> as an <c>AgentTaskRequest</c>
    /// command event with <see cref="MessengerEventSources.MessageAction"/>, logs an
    /// <see cref="AuditEventTypes.MessageActionReceived"/> audit entry, and returns the
    /// task-submitted confirmation card via the
    /// <see cref="MessagingExtensionActionResponse"/> invoke reply.
    /// </remarks>
    protected override Task<MessagingExtensionActionResponse> OnTeamsMessagingExtensionSubmitActionAsync(
        ITurnContext<IInvokeActivity> turnContext,
        MessagingExtensionAction action,
        CancellationToken cancellationToken)
    {
        if (turnContext is null)
        {
            throw new ArgumentNullException(nameof(turnContext));
        }

        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        return _messageExtensionHandler.HandleAsync(turnContext, action, cancellationToken);
    }

    private static string ExtractCorrelationId(Activity? activity)
    {
        if (activity?.Properties is null)
        {
            return Guid.NewGuid().ToString();
        }

        // The Properties JObject may carry the correlation ID under any casing — probe a
        // few common spellings before falling back to a freshly generated GUID.
        foreach (var candidate in new[] { CorrelationIdPropertyName, "CorrelationId", "correlation_id" })
        {
            var token = activity.Properties[candidate];
            if (token is null)
            {
                continue;
            }

            var raw = token.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw;
            }
        }

        return Guid.NewGuid().ToString();
    }

    private static string GetCorrelationId(ITurnContext turnContext)
        => turnContext.TurnState.Get<string>(CorrelationIdTurnStateKey) ?? Guid.NewGuid().ToString();

    private static string ExtractTenantId(Activity? activity)
    {
        if (activity is null)
        {
            return string.Empty;
        }

        var channelData = activity.GetChannelData<TeamsChannelData>();
        if (channelData?.Tenant?.Id is { Length: > 0 } tenantFromChannelData)
        {
            return tenantFromChannelData;
        }

        return activity.Conversation?.TenantId ?? string.Empty;
    }

    private static string? ExtractChannelId(Activity? activity)
    {
        if (activity is null)
        {
            return null;
        }

        var channelData = activity.GetChannelData<TeamsChannelData>();
        return channelData?.Channel?.Id;
    }

    private static string? ExtractTeamId(Activity? activity)
    {
        if (activity is null)
        {
            return null;
        }

        var channelData = activity.GetChannelData<TeamsChannelData>();
        return channelData?.Team?.Id;
    }

    /// <summary>
    /// Classify the activity scope by inspecting the Teams channel data. Team scope is
    /// indicated by either a non-empty Team or Channel record; personal scope is the
    /// fallback.
    /// </summary>
    private static bool IsTeamScope(Activity? activity)
    {
        if (activity is null)
        {
            return false;
        }

        var channelData = activity.GetChannelData<TeamsChannelData>();
        return !string.IsNullOrEmpty(channelData?.Team?.Id)
            || !string.IsNullOrEmpty(channelData?.Channel?.Id);
    }

    private static string StripRecipientMention(ITurnContext<IMessageActivity> turnContext)
    {
        var raw = turnContext.Activity?.Text ?? string.Empty;
        var stripped = turnContext.Activity?.RemoveRecipientMention() ?? raw;
        return (stripped ?? string.Empty).Trim();
    }

    /// <summary>
    /// Lightweight verb extraction used solely to populate the <c>command</c> argument on
    /// <see cref="IUserAuthorizationService.AuthorizeAsync"/>. The full structural parse
    /// (payload extraction, validation, handler routing) remains in
    /// <see cref="ICommandDispatcher"/> per impl-plan §3.2 — we duplicate only enough
    /// logic here to authorize against the canonical command vocabulary in
    /// <c>architecture.md</c> §5.2.
    /// </summary>
    private static string ExtractCommandVerb(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return string.Empty;
        }

        var lower = normalizedText.Trim().ToLower(CultureInfo.InvariantCulture);
        foreach (var verb in KnownCommandVerbs)
        {
            if (lower.Equals(verb, StringComparison.Ordinal) ||
                lower.StartsWith(verb + " ", StringComparison.Ordinal))
            {
                return verb;
            }
        }

        // Unknown verb — return the first whitespace-delimited token so the auth service
        // still receives a non-empty string. The default-deny stub rejects either way.
        var firstSpace = lower.IndexOf(' ');
        return firstSpace < 0 ? lower : lower[..firstSpace];
    }

    private async Task PersistConversationReferenceAsync(
        ITurnContext turnContext,
        UserIdentity? resolvedIdentity,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var activity = turnContext.Activity as Activity;
        if (activity is null)
        {
            return;
        }

        var conversationReference = activity.GetConversationReference();
        var nowUtc = DateTimeOffset.UtcNow;
        var channelId = ExtractChannelId(activity);
        var teamId = ExtractTeamId(activity);
        var fromAadObjectId = activity.From?.AadObjectId;

        // Channel-scope references have a natural (TenantId, ChannelId) key — they must
        // NOT carry the installer/sender AAD object ID (per TeamsConversationReference
        // contract). Personal references key on (TenantId, AadObjectId) and leave Team /
        // Channel null.
        var isChannelScope = !string.IsNullOrEmpty(channelId);
        var aadObjectId = isChannelScope ? null : fromAadObjectId;

        TeamsConversationReference? existing = null;
        if (isChannelScope)
        {
            existing = await _conversationReferenceStore
                .GetByChannelIdAsync(tenantId, channelId!, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (!string.IsNullOrEmpty(aadObjectId))
        {
            existing = await _conversationReferenceStore
                .GetByAadObjectIdAsync(tenantId, aadObjectId, cancellationToken)
                .ConfigureAwait(false);
        }

        var referenceJson = SerializeConversationReference(conversationReference);
        var record = new TeamsConversationReference
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString(),
            TenantId = tenantId,
            AadObjectId = aadObjectId,
            // Preserve a previously-mapped InternalUserId if identity resolution didn't
            // run (install path); otherwise overwrite with the freshly resolved value.
            // Channel-scope refs never carry an InternalUserId (no single user owns a
            // channel reference).
            InternalUserId = isChannelScope
                ? null
                : (resolvedIdentity?.InternalUserId ?? existing?.InternalUserId),
            ChannelId = channelId,
            TeamId = isChannelScope ? teamId : null,
            ServiceUrl = conversationReference?.ServiceUrl ?? string.Empty,
            ConversationId = conversationReference?.Conversation?.Id ?? activity.Conversation?.Id ?? string.Empty,
            BotId = conversationReference?.Bot?.Id ?? activity.Recipient?.Id ?? string.Empty,
            ReferenceJson = referenceJson,
            IsActive = true,
            CreatedAt = existing?.CreatedAt ?? nowUtc,
            UpdatedAt = nowUtc,
        };

        await _conversationReferenceStore
            .SaveOrUpdateAsync(record, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string SerializeConversationReference(ConversationReference? reference)
    {
        if (reference is null)
        {
            return "{}";
        }

        // `Microsoft.Bot.Schema.ConversationReference` — like the rest of the Bot Framework
        // schema — is authored against Newtonsoft.Json: members carry
        // `[JsonProperty(PropertyName = "serviceUrl")]`-style attributes for the canonical
        // camelCase wire names, the inheritance hierarchy uses `[JsonExtensionData]` for
        // property bags, and several members are typed `JObject`. `System.Text.Json`
        // ignores ALL of those attributes — it would emit PascalCase property names
        // (`ServiceUrl`, `Conversation`), drop extension data, and either throw
        // `NotSupportedException` on `JObject` members (previously caught here, silently
        // turning the stored value into `"{}"` and breaking proactive messaging) or emit
        // structurally incorrect JSON.
        //
        // Persisted `ReferenceJson` must round-trip through
        // `JsonConvert.DeserializeObject<ConversationReference>(...)` on the background
        // proactive-messaging worker, so we serialize with the same Newtonsoft.Json
        // contract here. Any unexpected serializer failure is allowed to propagate so the
        // caller (and ops dashboards) see the issue rather than silently storing an empty
        // document. `Microsoft.Bot.Builder` already takes a hard dependency on
        // Newtonsoft.Json transitively, so no extra package reference is required.
        return Newtonsoft.Json.JsonConvert.SerializeObject(reference);
    }

    private async Task LogSecurityRejectionAsync(
        string correlationId,
        string tenantId,
        string actorId,
        string action,
        string? conversationId,
        string reason,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { reason }, PayloadJsonOptions);
        var entry = BuildAuditEntry(
            eventType: AuditEventTypes.SecurityRejection,
            correlationId: correlationId,
            tenantId: tenantId,
            actorId: actorId,
            action: action,
            conversationId: conversationId,
            payloadJson: payload,
            outcome: AuditOutcomes.Rejected);

        await _auditLogger.LogAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private async Task LogInstallAuditAsync(
        string correlationId,
        string tenantId,
        string actorId,
        string action,
        string? conversationId,
        IReadOnlyDictionary<string, object?>? extraPayload,
        CancellationToken cancellationToken)
    {
        var payloadObject = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action"] = action,
        };

        if (extraPayload is not null)
        {
            foreach (var kvp in extraPayload)
            {
                payloadObject[kvp.Key] = kvp.Value;
            }
        }

        var payload = JsonSerializer.Serialize(payloadObject, PayloadJsonOptions);
        // Install-lifecycle events log under the canonical AuditEventTypes.CommandReceived
        // because tech-spec.md §4.3 fixes the audit EventType vocabulary at exactly seven
        // values; "InstallationUpdate" is not in that set and the AuditEntry init setter
        // rejects non-canonical values. Operators distinguish install/uninstall rows from
        // human-issued commands via the Action column ("AppInstalled",
        // "AppUninstalledFromTeam", "BotAddedToTeam", "BotRemovedFromTeam", etc.) — that
        // disambiguation is the contract sibling docs (architecture.md, e2e-scenarios.md)
        // assume when describing per-class audit completeness.
        var entry = BuildAuditEntry(
            eventType: AuditEventTypes.CommandReceived,
            correlationId: correlationId,
            tenantId: tenantId,
            actorId: actorId,
            action: action,
            conversationId: conversationId,
            payloadJson: payload,
            outcome: AuditOutcomes.Success);

        await _auditLogger.LogAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enumerate every active channel reference for the supplied team and call
    /// <see cref="IConversationReferenceStore.MarkInactiveByChannelAsync"/> once per
    /// channel. When the team ID is unknown (the activity didn't carry one) we fall back
    /// to marking just the channel from the current activity so the request is not lost.
    /// Returns the number of channels marked inactive (used in the audit payload).
    /// </summary>
    private async Task<int> MarkTeamChannelsInactiveAsync(
        string tenantId,
        string? teamId,
        string? fallbackChannelId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(teamId))
        {
            var channels = await _conversationReferenceStore
                .GetActiveChannelsByTeamIdAsync(tenantId, teamId, cancellationToken)
                .ConfigureAwait(false);

            var marked = 0;
            foreach (var channel in channels)
            {
                if (string.IsNullOrEmpty(channel?.ChannelId))
                {
                    continue;
                }

                await _conversationReferenceStore
                    .MarkInactiveByChannelAsync(tenantId, channel.ChannelId, cancellationToken)
                    .ConfigureAwait(false);
                marked++;
            }

            return marked;
        }

        if (!string.IsNullOrEmpty(fallbackChannelId))
        {
            await _conversationReferenceStore
                .MarkInactiveByChannelAsync(tenantId, fallbackChannelId, cancellationToken)
                .ConfigureAwait(false);
            return 1;
        }

        return 0;
    }

    private async Task LogCommandReceivedAsync(
        string correlationId,
        string tenantId,
        string actorId,
        string commandVerb,
        bool isCanonicalVerb,
        string? conversationId,
        string normalizedText,
        string? agentId,
        string? taskId,
        string outcome,
        CancellationToken cancellationToken)
    {
        // Per `e2e-scenarios.md` §Compliance — Immutable Audit Trail, scenario
        // "All inbound commands are audit-logged":
        //   Action       = <canonical command verb> (e.g. "agent ask") OR "(text)" for
        //                  unrecognised free text routed to the dispatcher's TextEvent
        //                  path (no new AuditEventType is invented — the canonical set is
        //                  fixed at seven per tech-spec §4.3).
        //   PayloadJson  = {"body":"<remainder after the verb>"} for canonical commands;
        //                  {"body":"<full normalized text>"} for non-canonical input so
        //                  the audit record captures what the user actually typed.
        //   AgentId      = stamped by the command handler onto CommandContext.AgentId
        //                  AFTER the agent target has been resolved (approve/reject) or
        //                  null when no agent is associated yet (`agent ask` before
        //                  routing).
        //   TaskId       = stamped by the command handler onto CommandContext.TaskId
        //                  after the task has been created / resolved.
        //   Outcome      = "Success" when DispatchAsync returned without throwing;
        //                  "Failed" when the dispatch threw (the exception still
        //                  propagates — the audit emission lives in `finally`).
        // The earlier implementation-specific shape ({command, normalizedText}) did not
        // conform to the enterprise-reviewable audit contract — corrected here so the
        // `SqlAuditLogger` (Stage 5.2) and downstream compliance tooling can rely on the
        // canonical field layout.
        var body = isCanonicalVerb
            ? ExtractCommandBody(normalizedText, commandVerb)
            : (normalizedText ?? string.Empty).Trim();
        var payload = JsonSerializer.Serialize(new { body }, PayloadJsonOptions);

        var entry = BuildAuditEntry(
            eventType: AuditEventTypes.CommandReceived,
            correlationId: correlationId,
            tenantId: tenantId,
            actorId: actorId,
            action: commandVerb,
            conversationId: conversationId,
            payloadJson: payload,
            outcome: outcome,
            agentId: agentId,
            taskId: taskId);

        // Stage 5.2 — CommandReceived audit emission MUST be durable. Per the
        // workstream's compliance contract ("Persist immutable audit trail suitable
        // for enterprise review") and the iter-1 evaluator feedback item 7 (which
        // rejected silent loss of audit rows even with a logged warning), audit
        // failures propagate. Teams' inbound retry semantics on activity-handler
        // exceptions cover the retry path — a transient audit-store outage drops
        // the activity for re-delivery, at which point the same correlationId
        // produces an idempotent audit row. Swallowing here would leave commands
        // executed with no durable audit record, violating §4.3.
        await _auditLogger.LogAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extract the body portion of a command — the substring following the canonical verb,
    /// trimmed — preserving the original casing of the normalized text. Used by
    /// <see cref="LogCommandReceivedAsync"/> to populate the audit
    /// <c>PayloadJson = {"body": "..."}</c> contract defined in
    /// <c>e2e-scenarios.md</c> §Compliance.
    /// </summary>
    /// <remarks>
    /// The verb passed in is the lowercase canonical form returned by
    /// <see cref="ExtractCommandVerb"/>; the body is extracted via a case-insensitive
    /// prefix match so the original input casing is preserved in the audit payload.
    /// Returns the empty string when the normalized text is null/empty, when the verb is
    /// empty, when the verb consumes the entire text, or when the verb does not appear at
    /// the start of the trimmed text.
    /// </remarks>
    private static string ExtractCommandBody(string normalizedText, string commandVerb)
    {
        if (string.IsNullOrEmpty(commandVerb) || string.IsNullOrWhiteSpace(normalizedText))
        {
            return string.Empty;
        }

        var trimmed = normalizedText.Trim();
        if (!trimmed.StartsWith(commandVerb, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (trimmed.Length == commandVerb.Length)
        {
            return string.Empty;
        }

        if (!char.IsWhiteSpace(trimmed[commandVerb.Length]))
        {
            return string.Empty;
        }

        return trimmed[(commandVerb.Length + 1)..].TrimStart();
    }

    /// <summary>
    /// Build a Bot Framework <see cref="IMessageActivity"/> carrying a minimal access-denied
    /// Adaptive Card. The card layout mirrors the rejection card schema in
    /// <c>tech-spec.md</c> §Adaptive Cards (title + reason + optional required role).
    /// Producing an Adaptive Card (rather than plain text) satisfies the story requirement
    /// that "Unauthorized tenant/user is rejected" via the same rich UI the rest of the
    /// gateway uses for human-facing responses (per item #5 in the iter-1 evaluator feedback).
    /// </summary>
    private static IMessageActivity BuildAccessDeniedCardActivity(string reason, string? requiredRole)
    {
        var bodyItems = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = "Access denied",
                weight = "Bolder",
                size = "Medium",
                color = "Attention",
                wrap = true,
            },
            new
            {
                type = "TextBlock",
                text = reason,
                wrap = true,
                spacing = "Small",
            },
        };

        if (!string.IsNullOrEmpty(requiredRole))
        {
            bodyItems.Add(new
            {
                type = "TextBlock",
                text = $"Required role: {requiredRole}",
                isSubtle = true,
                wrap = true,
                spacing = "Small",
            });
        }

        var cardContent = new
        {
            type = "AdaptiveCard",
            version = "1.5",
            schema = "http://adaptivecards.io/schemas/adaptive-card.json",
            body = bodyItems,
        };

        var attachment = new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = cardContent,
        };

        var reply = Activity.CreateMessageActivity();
        reply.Attachments = new List<Attachment> { attachment };
        // Plain-text fallback string kept on Activity.Text for channels and surfaces
        // (mobile lock-screen banners, accessibility readers, low-bandwidth clients)
        // that cannot render an Adaptive Card attachment.
        reply.Text = reason;
        return reply;
    }

    private static AuditEntry BuildAuditEntry(
        string eventType,
        string correlationId,
        string tenantId,
        string actorId,
        string action,
        string? conversationId,
        string payloadJson,
        string outcome,
        string? agentId = null,
        string? taskId = null)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var checksum = AuditEntry.ComputeChecksum(
            timestamp: timestamp,
            correlationId: correlationId,
            eventType: eventType,
            actorId: actorId,
            actorType: AuditActorTypes.User,
            tenantId: tenantId,
            agentId: agentId,
            taskId: taskId,
            conversationId: conversationId,
            action: action,
            payloadJson: payloadJson,
            outcome: outcome);

        return new AuditEntry
        {
            Timestamp = timestamp,
            CorrelationId = correlationId,
            EventType = eventType,
            ActorId = actorId,
            ActorType = AuditActorTypes.User,
            TenantId = tenantId,
            AgentId = agentId,
            TaskId = taskId,
            ConversationId = conversationId,
            Action = action,
            PayloadJson = payloadJson,
            Outcome = outcome,
            Checksum = checksum,
        };
    }

    private static bool BotWasAdded(IList<TeamsChannelAccount>? membersAdded, Activity? activity)
    {
        if (membersAdded is null || activity?.Recipient?.Id is not { Length: > 0 } botId)
        {
            return false;
        }

        foreach (var member in membersAdded)
        {
            if (string.Equals(member?.Id, botId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool BotWasRemoved(IList<TeamsChannelAccount>? membersRemoved, Activity? activity)
    {
        if (membersRemoved is null || activity?.Recipient?.Id is not { Length: > 0 } botId)
        {
            return false;
        }

        foreach (var member in membersRemoved)
        {
            if (string.Equals(member?.Id, botId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
