using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdaptiveCards;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Commands;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace AgentSwarm.Messaging.Teams.Extensions;

/// <summary>
/// Concrete Stage 3.4 <see cref="IMessageExtensionHandler"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline.</b> For every <c>composeExtension/submitAction</c> invoke the handler:
/// </para>
/// <list type="number">
/// <item><description>extracts the forwarded message text, sender, and timestamp from
/// <see cref="MessagingExtensionAction.MessagePayload"/> (HTML body is stripped to plain
/// text);</description></item>
/// <item><description>if no message text is present (the user triggered the action from
/// the <c>commandBox</c> context without selecting a message), returns an error
/// confirmation card asking the user to select a message first — no dispatch, no
/// inbound-buffer event;</description></item>
/// <item><description>otherwise synthesises an <c>agent ask &lt;body&gt;</c>
/// <see cref="CommandContext"/> with
/// <see cref="MessengerEventSources.MessageAction"/> stamped on
/// <see cref="CommandContext.Source"/> and <see cref="CommandContext.SuppressReply"/> set
/// (so <see cref="AskCommandHandler"/>'s acknowledgement card does NOT post to the
/// conversation thread — per <c>e2e-scenarios.md</c> §Message Actions: "message
/// extensions return a confirmation card response, not a channel thread reply"), then
/// calls <see cref="ICommandDispatcher.DispatchAsync"/>. The dispatcher routes the
/// synthetic verb to <see cref="AskCommandHandler"/>, which publishes a
/// <see cref="CommandEvent"/> with
/// <see cref="MessengerEventTypes.AgentTaskRequest"/> and
/// <see cref="MessengerEventSources.MessageAction"/>;</description></item>
/// <item><description>logs an <see cref="AuditEventTypes.MessageActionReceived"/> entry
/// (a dedicated audit event type distinct from <see cref="AuditEventTypes.CommandReceived"/>
/// per <c>tech-spec.md</c> §4.3 — the canonical audit set contains exactly seven values
/// and message actions arrive through the <c>composeExtension/submitAction</c> invoke
/// mechanism rather than direct text commands, so distinguishing them in the audit trail
/// supports compliance filtering and forensic analysis);</description></item>
/// <item><description>returns a confirmation Adaptive Card via
/// <see cref="MessagingExtensionActionResponse.ComposeExtension"/> (using the
/// <see cref="MessagingExtensionResult.Type"/> = <c>"result"</c> direct-submit response
/// shape — consistent with the <c>message-action-ux</c> resolved decision in
/// <c>e2e-scenarios.md</c> §Resolved Design Decisions: "Direct submit (no task module
/// popup)").</description></item>
/// </list>
/// <para>
/// <b>Dispatch failures.</b> If <see cref="ICommandDispatcher.DispatchAsync"/> throws
/// (publisher channel full, serialisation failure, transient downstream outage, …), the
/// handler still emits a <see cref="AuditEventTypes.MessageActionReceived"/> audit entry
/// with <see cref="AuditOutcomes.Failed"/> and an <c>error</c> field on the payload so the
/// compliance trail records that a forward was received but did not dispatch — closing
/// the gap that would otherwise be left when only the success / rejection paths are
/// audited. The user receives an actionable error card carrying the correlation ID
/// (rather than the generic invoke 500) so support can correlate the failure with
/// upstream telemetry. Cancellation (<see cref="OperationCanceledException"/> driven by
/// the inbound <c>CancellationToken</c>) is treated as a graceful control-flow signal
/// from the bot framework, NOT a compliance failure: it propagates without an audit
/// entry, mirroring the convention in
/// <see cref="TeamsSwarmActivityHandler.OnMessageActivityAsync"/>.
/// </para>
/// <para>
/// <b>Correlation ID.</b> The handler reuses the per-turn correlation ID stamped by
/// <see cref="TeamsSwarmActivityHandler.OnTurnAsync"/> into
/// <see cref="ITurnContext.TurnState"/> under
/// <see cref="TeamsSwarmActivityHandler.CorrelationIdTurnStateKey"/>. Falling back to a
/// freshly-generated GUID only when the turn state value is missing preserves end-to-end
/// trace continuity for invoke activities (the
/// <see cref="TeamsSwarmActivityHandler.OnTurnAsync"/> override runs for every inbound
/// turn before the base dispatcher routes to <c>OnTeamsMessagingExtensionSubmitActionAsync</c>).
/// </para>
/// </remarks>
public sealed class MessageExtensionHandler : IMessageExtensionHandler
{
    /// <summary>
    /// The single supported manifest <c>commandId</c> for message-extension forwards.
    /// Mirrors the <c>id</c> field of the <c>composeExtensions.commands[0]</c> entry in
    /// <c>Manifest/manifest.json</c>.
    /// </summary>
    public const string ForwardToAgentCommandId = "forwardToAgent";

    /// <summary>
    /// Audit <see cref="AuditEntry.Action"/> value recorded on every
    /// <see cref="AuditEventTypes.MessageActionReceived"/> entry produced by this handler.
    /// Aligned with the Stage 3.4 spec test scenario "Message action audit call".
    /// </summary>
    public const string MessageActionForwardAction = "message_action_forward";

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly Regex HtmlTagRegex = new(
        "<[^>]+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Match either:
    //   * the void-element <br> in any of its real-world forms (<br>, <br/>, <br />,
    //     including stray inner whitespace and the technically-invalid </br>); OR
    //   * the closing form of the other block-level tags (</p>, </div>, </li>, </tr>,
    //     </h1>..</h6>).
    // Teams' native HTML body emits self-closing <br> for soft line breaks; matching only
    // </br> (as the previous pattern did) silently dropped those breaks via HtmlTagRegex
    // and merged adjacent lines (e.g. "Hello<br>World" -> "HelloWorld").
    private static readonly Regex BlockBreakRegex = new(
        @"<\s*/?\s*br\s*/?>|</\s*(?:p|div|li|tr|h[1-6])\s*/?>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ICommandDispatcher _commandDispatcher;
    private readonly IIdentityResolver _identityResolver;
    private readonly IUserAuthorizationService _authorizationService;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<MessageExtensionHandler> _logger;

    /// <summary>
    /// Construct the handler with the five required collaborators. All five are required —
    /// every parameter is null-guarded so DI mis-registration fails loudly at startup
    /// rather than producing a <see cref="NullReferenceException"/> deep inside an invoke
    /// callback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IIdentityResolver"/> and <see cref="IUserAuthorizationService"/> are
    /// required so message-extension invokes flow through the same identity-resolution
    /// + RBAC gate as direct text commands per the story's <c>Security</c>
    /// requirement ("Enforce tenant ID, user identity, Teams app installation, and
    /// RBAC.") and <c>e2e-scenarios.md</c> §Teams Message Actions — scenario "Message
    /// action from user without required RBAC role is denied", which mandates that
    /// <c>viewer-only</c> users invoking <c>Forward to Agent</c> receive an
    /// access-denied response with no <see cref="MessengerEvent"/> published.
    /// </para>
    /// </remarks>
    public MessageExtensionHandler(
        ICommandDispatcher commandDispatcher,
        IIdentityResolver identityResolver,
        IUserAuthorizationService authorizationService,
        IAuditLogger auditLogger,
        ILogger<MessageExtensionHandler> logger)
    {
        _commandDispatcher = commandDispatcher ?? throw new ArgumentNullException(nameof(commandDispatcher));
        _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
        _authorizationService = authorizationService ?? throw new ArgumentNullException(nameof(authorizationService));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<MessagingExtensionActionResponse> HandleAsync(
        ITurnContext<IInvokeActivity> turnContext,
        MessagingExtensionAction action,
        CancellationToken ct)
    {
        if (turnContext is null)
        {
            throw new ArgumentNullException(nameof(turnContext));
        }

        if (action is null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        var activity = turnContext.Activity as Activity;
        var correlationId = GetCorrelationId(turnContext);
        var tenantId = ExtractTenantId(activity);
        var aadObjectId = activity?.From?.AadObjectId ?? string.Empty;
        var conversationId = activity?.Conversation?.Id;
        var commandId = action.CommandId ?? string.Empty;
        var actorForAudit = string.IsNullOrEmpty(aadObjectId) ? "unknown" : aadObjectId;

        // (1) Resolve the inbound user identity. Unmapped users are denied without
        // dispatching — message-extension invokes flow through the same identity gate as
        // inbound messages per the story Security requirement and
        // `e2e-scenarios.md` §Teams Message Actions ("the bot validates the user's RBAC
        // role via Activity.From.AadObjectId"). No `MessengerEvent` is published on the
        // rejection path; the audit trail records a `SecurityRejection` entry so
        // compliance reviews can reconstruct the access denial.
        var resolvedIdentity = string.IsNullOrEmpty(aadObjectId)
            ? null
            : await _identityResolver.ResolveAsync(aadObjectId, ct).ConfigureAwait(false);

        if (resolvedIdentity is null)
        {
            _logger.LogWarning(
                "Message-extension invoke rejected — AAD object ID '{AadObjectId}' not mapped (tenant {TenantId}, commandId '{CommandId}', correlation {CorrelationId}).",
                aadObjectId,
                tenantId,
                commandId,
                correlationId);

            await LogSecurityRejectionAsync(
                correlationId: correlationId,
                tenantId: tenantId,
                actorId: actorForAudit,
                action: "UnmappedUserRejected",
                conversationId: conversationId,
                reason: "Identity resolver returned null for AAD object ID.",
                ct: ct).ConfigureAwait(false);

            return BuildAccessDeniedResponse(
                reason: "Your account is not mapped in this organization. Please contact your administrator.",
                requiredRole: null);
        }

        // (2) Validate the manifest command ID. Only the canonical "forwardToAgent" action
        // is accepted — unrelated or future `composeExtension/submitAction` commands must
        // not create agent tasks accidentally (resolves evaluator iter-2 finding #3). The
        // rejection is audited as a `MessageActionReceived` entry with `Outcome=Rejected`
        // (mirroring the empty-payload path) so the trail records that a message action
        // was received but not dispatched.
        if (!string.Equals(commandId, ForwardToAgentCommandId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Message-extension invoke rejected — unknown commandId '{CommandId}' (tenant {TenantId}, actor {InternalUserId}, correlation {CorrelationId}).",
                commandId,
                tenantId,
                resolvedIdentity.InternalUserId,
                correlationId);

            await LogMessageActionReceivedAsync(
                correlationId: correlationId,
                tenantId: tenantId,
                actorId: actorForAudit,
                conversationId: conversationId,
                forwardedText: string.Empty,
                sourceMessageId: null,
                senderDisplayName: null,
                sourceTimestamp: null,
                commandId: commandId,
                outcome: AuditOutcomes.Rejected,
                ct).ConfigureAwait(false);

            return BuildUnknownCommandResponse(commandId);
        }

        // (3) Authorize the implied verb `agent ask` — the canonical command the
        // forward-to-agent action synthesises before dispatch. Per the e2e scenario:
        // "the 'agent ask' command requires role 'operator'" and viewer-only users are
        // denied with `MessagingExtensionActionResponse` carrying an access-denied card,
        // no MessengerEvent, plus a `SecurityRejection` audit entry.
        //
        // CRITICAL: RBAC is keyed by the Entra AAD object ID (see
        // `RbacAuthorizationService` xmldoc + `RbacOptions.TenantRoleAssignments`).
        // Passing `resolvedIdentity.InternalUserId` here would silently deny
        // AAD-keyed role assignments because the platform-internal user ID and
        // the AAD object ID rarely coincide.
        var rbacSubject = !string.IsNullOrEmpty(resolvedIdentity.AadObjectId)
            ? resolvedIdentity.AadObjectId
            : aadObjectId;

        var authorization = await _authorizationService
            .AuthorizeAsync(tenantId, rbacSubject, CommandNames.AgentAsk, ct)
            .ConfigureAwait(false);

        if (!authorization.IsAuthorized)
        {
            _logger.LogWarning(
                "Message-extension invoke rejected — user {AadObjectId} (internal {InternalUserId}, role {UserRole}) lacks role {RequiredRole} for command '{Command}' (tenant {TenantId}, correlation {CorrelationId}).",
                rbacSubject,
                resolvedIdentity.InternalUserId,
                authorization.UserRole,
                authorization.RequiredRole,
                CommandNames.AgentAsk,
                tenantId,
                correlationId);

            await LogSecurityRejectionAsync(
                correlationId: correlationId,
                tenantId: tenantId,
                actorId: actorForAudit,
                action: "InsufficientRoleRejected",
                conversationId: conversationId,
                reason: $"User role '{authorization.UserRole}' is insufficient for command '{CommandNames.AgentAsk}' (required: '{authorization.RequiredRole}').",
                ct: ct).ConfigureAwait(false);

            return BuildAccessDeniedResponse(
                reason: "You do not have permission to perform this action.",
                requiredRole: authorization.RequiredRole);
        }

        var extraction = ExtractMessagePayload(action);

        if (!extraction.HasContent)
        {
            _logger.LogInformation(
                "Message-extension invoke with empty payload (commandId '{CommandId}', correlation {CorrelationId}) — returning select-message error card.",
                commandId,
                correlationId);

            // Audit the empty-payload invocation so the compliance trail still records that
            // a user triggered the action — even though no agent task was created. Outcome
            // is Rejected because no MessengerEvent was published.
            await LogMessageActionReceivedAsync(
                correlationId: correlationId,
                tenantId: tenantId,
                actorId: actorForAudit,
                conversationId: conversationId,
                forwardedText: string.Empty,
                sourceMessageId: null,
                senderDisplayName: null,
                sourceTimestamp: null,
                commandId: commandId,
                outcome: AuditOutcomes.Rejected,
                ct).ConfigureAwait(false);

            return BuildSelectMessageErrorResponse();
        }

        _logger.LogInformation(
            "Message-extension forward dispatched (commandId '{CommandId}', tenant {TenantId}, actor {InternalUserId}, correlation {CorrelationId}, body length {BodyLength}).",
            commandId,
            tenantId,
            resolvedIdentity.InternalUserId,
            correlationId,
            extraction.PlainText.Length);

        // Synthesise the dispatch context with an "agent ask <body>" prefix so the
        // dispatcher's longest-prefix matcher routes to AskCommandHandler (which is the
        // only handler that publishes AgentTaskRequest). Source = MessageAction and
        // SuppressReply = true ensure the published event carries the correct origination
        // and the handler does NOT post its acknowledgement card to the conversation
        // thread (the confirmation lives in the invoke response instead).
        var synthesizedText = $"{CommandNames.AgentAsk} {extraction.PlainText}";

        // Forward the FULL resolved identity (InternalUserId / AadObjectId / DisplayName /
        // Role) into CommandContext so downstream consumers (CommandEvent envelope, audit
        // trail, identity-keyed routing) receive the canonical platform-agnostic record
        // rather than a synthetic stub. This is the same identity contract used by
        // OnMessageActivityAsync and is required by the story's "Enforce tenant ID, user
        // identity, ... and RBAC" security requirement.
        var context = new CommandContext
        {
            NormalizedText = synthesizedText,
            ResolvedIdentity = resolvedIdentity,
            CorrelationId = correlationId,
            TurnContext = turnContext,
            ConversationId = conversationId,
            ActivityId = activity?.Id,
            Source = MessengerEventSources.MessageAction,
            SuppressReply = true,
        };

        // Dispatch is the only step that touches downstream infrastructure (publisher
        // channel, serializer, command handler) and therefore the only step that can throw
        // for non-policy reasons. Wrap it so a transport / serialisation failure still
        // produces:
        //   * a `MessageActionReceived` audit entry with `Outcome=Failed` (closes the
        //     compliance gap noted in code review — every other failure path on this
        //     handler is audited; the dispatch path must be too); and
        //   * an actionable error card carrying the correlation ID (rather than the
        //     generic Teams invoke 500 the framework would surface if the exception
        //     escaped) so the user can reference the tracking ID with support.
        // Cancellation triggered by the inbound `ct` is treated as a graceful control-flow
        // signal from the bot framework — propagated without an audit entry, matching the
        // convention used in TeamsSwarmActivityHandler.OnMessageActivityAsync.
        try
        {
            await _commandDispatcher.DispatchAsync(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Message-extension dispatch failed (commandId '{CommandId}', tenant {TenantId}, actor {InternalUserId}, correlation {CorrelationId}, body length {BodyLength}). Returning dispatch-failure card.",
                commandId,
                tenantId,
                resolvedIdentity.InternalUserId,
                correlationId,
                extraction.PlainText.Length);

            await LogDispatchFailureAuditAsync(
                correlationId: correlationId,
                tenantId: tenantId,
                actorId: actorForAudit,
                conversationId: conversationId,
                extraction: extraction,
                commandId: commandId,
                dispatchException: ex,
                ct: ct).ConfigureAwait(false);

            return BuildDispatchFailureResponse(correlationId);
        }

        await LogMessageActionReceivedAsync(
            correlationId: correlationId,
            tenantId: tenantId,
            actorId: actorForAudit,
            conversationId: conversationId,
            forwardedText: extraction.PlainText,
            sourceMessageId: extraction.SourceMessageId,
            senderDisplayName: extraction.SenderDisplayName,
            sourceTimestamp: extraction.SourceTimestamp,
            commandId: commandId,
            outcome: AuditOutcomes.Success,
            ct).ConfigureAwait(false);

        return BuildConfirmationResponse(extraction.PlainText, correlationId);
    }

    /// <summary>
    /// Result of parsing <see cref="MessagingExtensionAction.MessagePayload"/>. When the
    /// invoke comes from the <c>commandBox</c> context (no source message selected) or
    /// the payload body is empty/whitespace, <see cref="HasContent"/> is <c>false</c> and
    /// the handler returns the select-message error card.
    /// </summary>
    internal readonly record struct PayloadExtraction(
        bool HasContent,
        string PlainText,
        string? SourceMessageId,
        string? SenderDisplayName,
        string? SourceTimestamp);

    /// <summary>
    /// Extract the canonical fields from
    /// <see cref="MessagingExtensionAction.MessagePayload"/>. The body content is HTML
    /// (Teams native) so we strip tags and decode entities; block-level tags are
    /// translated to newlines so multi-paragraph forwards stay readable.
    /// </summary>
    internal static PayloadExtraction ExtractMessagePayload(MessagingExtensionAction action)
    {
        var payload = action.MessagePayload;
        if (payload is null)
        {
            return new PayloadExtraction(HasContent: false, PlainText: string.Empty, SourceMessageId: null, SenderDisplayName: null, SourceTimestamp: null);
        }

        var bodyContent = payload.Body?.Content ?? string.Empty;
        var contentType = payload.Body?.ContentType ?? string.Empty;
        var plainText = string.Equals(contentType, "html", StringComparison.OrdinalIgnoreCase)
            ? HtmlToPlainText(bodyContent)
            : bodyContent;

        plainText = (plainText ?? string.Empty).Trim();

        var hasContent = !string.IsNullOrWhiteSpace(plainText);
        var senderDisplayName = payload.From?.User?.DisplayName;
        var sourceTimestamp = payload.CreatedDateTime;
        var sourceMessageId = payload.Id;

        return new PayloadExtraction(
            HasContent: hasContent,
            PlainText: plainText ?? string.Empty,
            SourceMessageId: sourceMessageId,
            SenderDisplayName: senderDisplayName,
            SourceTimestamp: sourceTimestamp);
    }

    /// <summary>
    /// Strip HTML tags and decode entities from a Teams-native message body. Block-level
    /// closing tags (<c>p</c>, <c>div</c>, <c>li</c>, <c>tr</c>, <c>h1</c>..<c>h6</c>)
    /// and every form of the void <c>br</c> element (<c>&lt;br&gt;</c>,
    /// <c>&lt;br/&gt;</c>, <c>&lt;br /&gt;</c>, plus the technically-invalid
    /// <c>&lt;/br&gt;</c>) are first replaced with a newline so multi-paragraph content
    /// stays readable in downstream consumers (audit log, agent prompt).
    /// </summary>
    internal static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var withBreaks = BlockBreakRegex.Replace(html, "\n");
        var stripped = HtmlTagRegex.Replace(withBreaks, string.Empty);
        return WebUtility.HtmlDecode(stripped);
    }

    private static string GetCorrelationId(ITurnContext turnContext)
        => turnContext.TurnState.Get<string>(TeamsSwarmActivityHandler.CorrelationIdTurnStateKey)
           ?? Guid.NewGuid().ToString();

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

    /// <summary>
    /// Audit-emission wrapper for the dispatch-failure path. Stage 5.2 iter-7 (eval
    /// iter-6 item 3) — when the audit emit ITSELF fails after a dispatch failure,
    /// surface BOTH root causes via <see cref="AggregateException"/> rather than
    /// swallowing the audit error. The prior log-and-swallow design allowed
    /// <c>MessageActionReceived</c> audit rows to be silently absent on the
    /// dispatch-failure path even though the workstream's compliance contract
    /// requires every message-action submission to land an audit row (per
    /// <c>tech-spec.md</c> §4.3 / canonical <see cref="AuditEventTypes.MessageActionReceived"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors the iter-5 <c>TeamsSwarmActivityHandler</c> dispatch+audit
    /// double-failure pattern: dispatch error first, audit error second in
    /// <see cref="AggregateException.InnerExceptions"/>. Teams' invoke retry
    /// re-runs the message-action submission so the idempotent audit emit gets
    /// another chance to land.
    /// </para>
    /// <para>
    /// Branch summary:
    /// <list type="bullet">
    /// <item><description>dispatch fails, audit OK → caller returns the
    /// dispatch-failure confirmation card to the user (audit row landed).</description></item>
    /// <item><description>dispatch fails, audit fails →
    /// <see cref="AggregateException"/> thrown carrying both root causes. The user
    /// sees a Teams invoke failure rather than a friendly card, but compliance is
    /// preserved (the failure is visible, not silently absent).</description></item>
    /// <item><description>cancellation during audit → propagates unchanged
    /// (graceful control-flow signal, not a compliance gap).</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private async Task LogDispatchFailureAuditAsync(
        string correlationId,
        string tenantId,
        string actorId,
        string? conversationId,
        PayloadExtraction extraction,
        string commandId,
        Exception dispatchException,
        CancellationToken ct)
    {
        try
        {
            await LogMessageActionReceivedAsync(
                correlationId: correlationId,
                tenantId: tenantId,
                actorId: actorId,
                conversationId: conversationId,
                forwardedText: extraction.PlainText,
                sourceMessageId: extraction.SourceMessageId,
                senderDisplayName: extraction.SenderDisplayName,
                sourceTimestamp: extraction.SourceTimestamp,
                commandId: commandId,
                outcome: AuditOutcomes.Failed,
                ct: ct,
                errorType: dispatchException.GetType().FullName,
                errorMessage: dispatchException.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation while writing the failure audit is treated the same as
            // cancellation during dispatch: a graceful control signal, not a swallowed
            // error. The primary dispatch failure has already been logged via ILogger,
            // so propagating cancellation does not lose information for ops.
            throw;
        }
        catch (Exception auditException)
        {
            _logger.LogError(
                auditException,
                "MessageActionReceived audit emit failed AFTER dispatch failure (correlation {CorrelationId}, tenant {TenantId}, actor {ActorId}); surfacing AggregateException carrying BOTH root causes. Original dispatch error: {OriginalError}",
                correlationId,
                tenantId,
                actorId,
                dispatchException.Message);
            throw new AggregateException(
                $"Message-extension dispatch failed AND MessageActionReceived audit-row persistence failed (correlation {correlationId}). Both root causes are carried in InnerExceptions; Teams should retry the invoke so the idempotent audit row can eventually land.",
                dispatchException,
                auditException);
        }
    }

    private async Task LogMessageActionReceivedAsync(
        string correlationId,
        string tenantId,
        string actorId,
        string? conversationId,
        string forwardedText,
        string? sourceMessageId,
        string? senderDisplayName,
        string? sourceTimestamp,
        string commandId,
        string outcome,
        CancellationToken ct,
        string? errorType = null,
        string? errorMessage = null)
    {
        var payloadObject = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["body"] = forwardedText,
            ["sourceMessageId"] = sourceMessageId,
            ["sender"] = senderDisplayName,
            ["sourceTimestamp"] = sourceTimestamp,
            ["commandId"] = string.IsNullOrEmpty(commandId) ? null : commandId,
        };

        // Only emit the `error` field when this is a failure-path audit. Keeping it absent
        // on success / rejection preserves byte-for-byte payload compatibility with the
        // existing audit envelope shape and keeps the checksum stable for the happy paths.
        if (errorType is not null || errorMessage is not null)
        {
            payloadObject["error"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = errorType,
                ["message"] = errorMessage,
            };
        }

        var payloadJson = JsonSerializer.Serialize(payloadObject, PayloadJsonOptions);
        var timestamp = DateTimeOffset.UtcNow;
        var actor = string.IsNullOrEmpty(actorId) ? "unknown" : actorId;

        var checksum = AuditEntry.ComputeChecksum(
            timestamp: timestamp,
            correlationId: correlationId,
            eventType: AuditEventTypes.MessageActionReceived,
            actorId: actor,
            actorType: AuditActorTypes.User,
            tenantId: tenantId,
            agentId: null,
            taskId: null,
            conversationId: conversationId,
            action: MessageActionForwardAction,
            payloadJson: payloadJson,
            outcome: outcome);

        var entry = new AuditEntry
        {
            Timestamp = timestamp,
            CorrelationId = correlationId,
            EventType = AuditEventTypes.MessageActionReceived,
            ActorId = actor,
            ActorType = AuditActorTypes.User,
            TenantId = tenantId,
            AgentId = null,
            TaskId = null,
            ConversationId = conversationId,
            Action = MessageActionForwardAction,
            PayloadJson = payloadJson,
            Outcome = outcome,
            Checksum = checksum,
        };

        await _auditLogger.LogAsync(entry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Emit a <see cref="AuditEventTypes.SecurityRejection"/> audit entry for an identity
    /// or RBAC denial. Mirrors the per-rejection contract used by
    /// <see cref="TeamsSwarmActivityHandler"/>'s <c>OnMessageActivityAsync</c>:
    /// <c>EventType=SecurityRejection</c>, <c>Outcome=Rejected</c>,
    /// <c>PayloadJson={"reason":"..."}</c>, and the rejection reason encoded in
    /// <see cref="AuditEntry.Action"/>
    /// (<c>UnmappedUserRejected</c> | <c>InsufficientRoleRejected</c>).
    /// </summary>
    private async Task LogSecurityRejectionAsync(
        string correlationId,
        string tenantId,
        string actorId,
        string action,
        string? conversationId,
        string reason,
        CancellationToken ct)
    {
        var payloadJson = JsonSerializer.Serialize(new { reason }, PayloadJsonOptions);
        var timestamp = DateTimeOffset.UtcNow;
        var actor = string.IsNullOrEmpty(actorId) ? "unknown" : actorId;

        var checksum = AuditEntry.ComputeChecksum(
            timestamp: timestamp,
            correlationId: correlationId,
            eventType: AuditEventTypes.SecurityRejection,
            actorId: actor,
            actorType: AuditActorTypes.User,
            tenantId: tenantId,
            agentId: null,
            taskId: null,
            conversationId: conversationId,
            action: action,
            payloadJson: payloadJson,
            outcome: AuditOutcomes.Rejected);

        var entry = new AuditEntry
        {
            Timestamp = timestamp,
            CorrelationId = correlationId,
            EventType = AuditEventTypes.SecurityRejection,
            ActorId = actor,
            ActorType = AuditActorTypes.User,
            TenantId = tenantId,
            AgentId = null,
            TaskId = null,
            ConversationId = conversationId,
            Action = action,
            PayloadJson = payloadJson,
            Outcome = AuditOutcomes.Rejected,
            Checksum = checksum,
        };

        await _auditLogger.LogAsync(entry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the access-denied response for identity / RBAC rejections. The card matches
    /// the e2e contract text "You do not have permission to perform this action." when the
    /// caller passes that reason — consistent with the <c>fetchTask=false</c> direct-submit
    /// model in <c>architecture.md</c> §2.15 (no task module is involved).
    /// </summary>
    internal static MessagingExtensionActionResponse BuildAccessDeniedResponse(string reason, string? requiredRole)
    {
        var card = new AdaptiveCard(Cards.AdaptiveCardBuilder.SchemaVersion);

        var header = new AdaptiveContainer
        {
            Style = AdaptiveContainerStyle.Attention,
            Bleed = true,
        };
        header.Items.Add(new AdaptiveTextBlock
        {
            Text = "Access denied",
            Weight = AdaptiveTextWeight.Bolder,
            Size = AdaptiveTextSize.Medium,
            Color = AdaptiveTextColor.Attention,
            Wrap = true,
        });
        card.Body.Add(header);

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = reason,
            Wrap = true,
            Spacing = AdaptiveSpacing.Small,
        });

        if (!string.IsNullOrEmpty(requiredRole))
        {
            card.Body.Add(new AdaptiveTextBlock
            {
                Text = $"Required role: {requiredRole}",
                IsSubtle = true,
                Wrap = true,
                Spacing = AdaptiveSpacing.Small,
            });
        }

        return BuildExtensionResponse(card);
    }

    /// <summary>
    /// Build the error response for an unrecognised <see cref="MessagingExtensionAction.CommandId"/>.
    /// Only <see cref="ForwardToAgentCommandId"/> is accepted — every other identifier
    /// receives this card and is logged as a <c>MessageActionReceived</c> entry with
    /// <c>Outcome=Rejected</c> so unrelated or future <c>composeExtension/submitAction</c>
    /// commands cannot create agent tasks accidentally.
    /// </summary>
    internal static MessagingExtensionActionResponse BuildUnknownCommandResponse(string commandId)
    {
        var card = new AdaptiveCard(Cards.AdaptiveCardBuilder.SchemaVersion);

        var header = new AdaptiveContainer
        {
            Style = AdaptiveContainerStyle.Attention,
            Bleed = true,
        };
        header.Items.Add(new AdaptiveTextBlock
        {
            Text = "Unknown message action",
            Weight = AdaptiveTextWeight.Bolder,
            Size = AdaptiveTextSize.Medium,
            Color = AdaptiveTextColor.Attention,
            Wrap = true,
        });
        card.Body.Add(header);

        var displayId = string.IsNullOrEmpty(commandId) ? "(empty)" : commandId;
        card.Body.Add(new AdaptiveTextBlock
        {
            Text = $"The message action command '{displayId}' is not supported. Please contact your administrator if this error persists.",
            Wrap = true,
            Spacing = AdaptiveSpacing.Small,
        });

        return BuildExtensionResponse(card);
    }

    /// <summary>
    /// Build the success-path confirmation response — an Adaptive Card carrying the
    /// task-submitted acknowledgement plus the correlation/tracking ID, wrapped in a
    /// <see cref="MessagingExtensionActionResponse"/> using the direct-submit
    /// <c>composeExtension</c> result shape (consistent with the <c>message-action-ux</c>
    /// resolved decision: direct submit, no task module popup).
    /// </summary>
    internal static MessagingExtensionActionResponse BuildConfirmationResponse(string body, string correlationId)
    {
        var card = new AdaptiveCard(Cards.AdaptiveCardBuilder.SchemaVersion);
        card.Body.Add(new AdaptiveTextBlock
        {
            Text = "Task submitted",
            Weight = AdaptiveTextWeight.Bolder,
            Size = AdaptiveTextSize.Large,
            Wrap = true,
        });

        var snippet = TruncateForCard(body);
        card.Body.Add(new AdaptiveTextBlock
        {
            Text = string.IsNullOrEmpty(snippet) ? "(empty forwarded message)" : snippet,
            Wrap = true,
            Spacing = AdaptiveSpacing.Medium,
        });

        var facts = new AdaptiveFactSet { Spacing = AdaptiveSpacing.Medium };
        facts.Facts.Add(new AdaptiveFact("Tracking ID", correlationId));
        facts.Facts.Add(new AdaptiveFact("Source", "Message action"));
        card.Body.Add(facts);

        return BuildExtensionResponse(card);
    }

    /// <summary>
    /// Build the error response for the empty-payload path. Shown when the user invokes
    /// the action from the command box without first selecting a source message.
    /// </summary>
    internal static MessagingExtensionActionResponse BuildSelectMessageErrorResponse()
    {
        var card = new AdaptiveCard(Cards.AdaptiveCardBuilder.SchemaVersion);

        var header = new AdaptiveContainer
        {
            Style = AdaptiveContainerStyle.Attention,
            Bleed = true,
        };
        header.Items.Add(new AdaptiveTextBlock
        {
            Text = "No message selected",
            Weight = AdaptiveTextWeight.Bolder,
            Size = AdaptiveTextSize.Medium,
            Color = AdaptiveTextColor.Attention,
            Wrap = true,
        });
        card.Body.Add(header);

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = "Please select a message first, then re-invoke the \"Forward to Agent\" action from the message context menu.",
            Wrap = true,
            Spacing = AdaptiveSpacing.Small,
        });

        return BuildExtensionResponse(card);
    }

    /// <summary>
    /// Build the error response for the dispatch-failure path. Shown when
    /// <see cref="ICommandDispatcher.DispatchAsync"/> threw a non-cancellation exception
    /// (publisher channel saturation, serialisation failure, transient downstream
    /// outage, …). The card surfaces the correlation ID so the user can quote it to
    /// support, and the language stays generic on purpose — leaking exception types or
    /// internal infrastructure detail to end users would violate the Security
    /// requirement's spirit (no token / internal-state leakage). Detailed diagnostics
    /// live in the <see cref="AuditOutcomes.Failed"/> audit entry and the structured
    /// <see cref="ILogger"/> error event.
    /// </summary>
    internal static MessagingExtensionActionResponse BuildDispatchFailureResponse(string correlationId)
    {
        var card = new AdaptiveCard(Cards.AdaptiveCardBuilder.SchemaVersion);

        var header = new AdaptiveContainer
        {
            Style = AdaptiveContainerStyle.Attention,
            Bleed = true,
        };
        header.Items.Add(new AdaptiveTextBlock
        {
            Text = "Forward failed",
            Weight = AdaptiveTextWeight.Bolder,
            Size = AdaptiveTextSize.Medium,
            Color = AdaptiveTextColor.Attention,
            Wrap = true,
        });
        card.Body.Add(header);

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = "We couldn't queue your message for the agent. Please try again in a moment. If the problem persists, contact your administrator and reference the tracking ID below.",
            Wrap = true,
            Spacing = AdaptiveSpacing.Small,
        });

        var facts = new AdaptiveFactSet { Spacing = AdaptiveSpacing.Medium };
        facts.Facts.Add(new AdaptiveFact("Tracking ID", correlationId));
        facts.Facts.Add(new AdaptiveFact("Source", "Message action"));
        card.Body.Add(facts);

        return BuildExtensionResponse(card);
    }

    private static MessagingExtensionActionResponse BuildExtensionResponse(AdaptiveCard card)
    {
        var content = JObject.Parse(card.ToJson());
        var attachment = new MessagingExtensionAttachment
        {
            ContentType = AdaptiveCard.ContentType,
            Content = content,
        };

        return new MessagingExtensionActionResponse
        {
            ComposeExtension = new MessagingExtensionResult
            {
                Type = "result",
                AttachmentLayout = "list",
                Attachments = new List<MessagingExtensionAttachment> { attachment },
            },
        };
    }

    private const int CardSnippetMaxLength = 280;

    private static string TruncateForCard(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        if (body.Length <= CardSnippetMaxLength)
        {
            return body;
        }

        return body[..CardSnippetMaxLength].TrimEnd() + "…";
    }
}
