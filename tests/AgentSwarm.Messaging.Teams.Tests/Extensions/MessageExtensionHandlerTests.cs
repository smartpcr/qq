using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Commands;
using AgentSwarm.Messaging.Teams.Extensions;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using static AgentSwarm.Messaging.Teams.Tests.HandlerFactory;
using static AgentSwarm.Messaging.Teams.Tests.TestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests.Extensions;

/// <summary>
/// Stage 3.4 message-extension handler scenarios from the workstream brief:
/// (1) forward-to-agent dispatches a <c>CommandEvent</c> with
///     <c>EventType = AgentTaskRequest</c> and <c>Source = MessageAction</c>;
/// (2) <c>OnTeamsMessagingExtensionSubmitActionAsync</c> returns a confirmation card via
///     <see cref="MessagingExtensionActionResponse"/>;
/// (3) an empty message payload returns a descriptive error card;
/// (4) <see cref="IAuditLogger.LogAsync"/> is called with
///     <c>EventType = MessageActionReceived</c>, the user's AAD object ID, tenant,
///     <c>Action = "message_action_forward"</c>, and a payload JSON containing the
///     forwarded body text.
/// </summary>
public sealed class MessageExtensionHandlerTests
{
    private const string TenantId = HandlerFactory.TenantId;
    private const string ActorAadObjectId = "aad-obj-dave-001";
    private const string ConversationId = "conv-dave-001";
    private const string ForwardedBody = "Investigate the deployment failure in update-service.";
    private const string SourceMessageId = "msg-7890";
    private const string SenderDisplayName = "Carol Sender";

    // ─── Scenario 1: forwarding dispatches a CommandEvent ──────────────────────────────

    [Fact]
    public async Task HandleAsync_ForwardedMessage_DispatchesAgentTaskRequestWithMessageActionSource()
    {
        var publisher = new RecordingInboundEventPublisher();
        var (handler, _, _) = BuildE2EHandler(publisher);
        var turnContext = NewSubmitActionTurnContext(out var action, body: ForwardedBody);

        await handler.HandleAsync(turnContext, action, CancellationToken.None);

        var published = Assert.Single(publisher.Published);
        var commandEvent = Assert.IsType<CommandEvent>(published);
        Assert.Equal(MessengerEventTypes.AgentTaskRequest, commandEvent.EventType);
        Assert.Equal(MessengerEventSources.MessageAction, commandEvent.Source);
        Assert.Equal("Teams", commandEvent.Messenger);
        Assert.Equal(ActorAadObjectId, commandEvent.ExternalUserId);
        // AskCommandHandler stamps the verb on CommandPayload.CommandType.
        Assert.Equal(CommandNames.AgentAsk, commandEvent.Payload.CommandType);
        // The forwarded body becomes the Ask payload after the synthetic "agent ask "
        // prefix is stripped — verifies the dispatcher saw a real Ask command (not a
        // TextEvent / help-card fallback).
        Assert.Equal(ForwardedBody, commandEvent.Payload.Payload);
    }

    [Fact]
    public async Task HandleAsync_Forwarded_DoesNotPostReplyToConversationThread()
    {
        // SuppressReply = true must prevent AskCommandHandler from posting its
        // acknowledgement card to the conversation thread; the confirmation card lives in
        // the invoke response instead.
        var publisher = new RecordingInboundEventPublisher();
        var (handler, _, adapter) = BuildE2EHandler(publisher);
        var turnContext = NewSubmitActionTurnContext(out var action, body: ForwardedBody, adapter: adapter);

        await handler.HandleAsync(turnContext, action, CancellationToken.None);

        Assert.Empty(adapter.Sent);
    }

    // ─── Scenario 2: confirmation card returned ────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ForwardedMessage_ReturnsComposeExtensionConfirmationCard()
    {
        var publisher = new RecordingInboundEventPublisher();
        var (handler, _, _) = BuildE2EHandler(publisher);
        var turnContext = NewSubmitActionTurnContext(out var action, body: ForwardedBody, correlationId: "corr-3-4-2");

        var response = await handler.HandleAsync(turnContext, action, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.ComposeExtension);
        Assert.Equal("result", response.ComposeExtension!.Type);
        Assert.Equal("list", response.ComposeExtension.AttachmentLayout);
        // Direct submit shape — no task module continuation.
        Assert.Null(response.Task);
        var attachment = Assert.Single(response.ComposeExtension.Attachments);
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.ContentType);
        var content = Assert.IsType<JObject>(attachment.Content);
        var json = content.ToString();
        Assert.Contains("Task submitted", json);
        Assert.Contains("corr-3-4-2", json);
        Assert.Contains("Tracking ID", json);
    }

    // ─── Scenario 3: empty message payload ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_NullMessagePayload_ReturnsSelectMessageErrorCard_AndDoesNotDispatch()
    {
        var publisher = new RecordingInboundEventPublisher();
        var dispatcher = new RecordingCommandDispatcher();
        var auditLogger = new RecordingAuditLogger();
        var identityResolver = new FakeIdentityResolver();
        identityResolver.Map(ActorAadObjectId, new UserIdentity(
            InternalUserId: "internal-dave",
            AadObjectId: ActorAadObjectId,
            DisplayName: "Dave Contoso",
            Role: "operator"));
        var handler = new MessageExtensionHandler(
            dispatcher,
            identityResolver,
            new AlwaysAuthorizationService(),
            auditLogger,
            NullLogger<MessageExtensionHandler>.Instance);

        var turnContext = NewSubmitActionTurnContext(out var action, body: null);
        action.MessagePayload = null;

        var response = await handler.HandleAsync(turnContext, action, CancellationToken.None);

        Assert.NotNull(response.ComposeExtension);
        var attachment = Assert.Single(response.ComposeExtension!.Attachments);
        var json = Assert.IsType<JObject>(attachment.Content).ToString();
        Assert.Contains("No message selected", json);
        Assert.Contains("Please select a message first", json);

        Assert.Empty(dispatcher.Dispatched);
        Assert.Empty(publisher.Published);

        // Empty-payload invocations are still audited (Outcome = Rejected) so the
        // compliance trail records the trigger; this is additive coverage on top of the
        // four spec'd scenarios.
        var entry = Assert.Single(auditLogger.Entries);
        Assert.Equal(AuditEventTypes.MessageActionReceived, entry.EventType);
        Assert.Equal(AuditOutcomes.Rejected, entry.Outcome);
    }

    [Fact]
    public async Task HandleAsync_WhitespaceOnlyBody_ReturnsSelectMessageErrorCard_AndDoesNotDispatch()
    {
        var publisher = new RecordingInboundEventPublisher();
        var dispatcher = new RecordingCommandDispatcher();
        var auditLogger = new RecordingAuditLogger();
        var identityResolver = new FakeIdentityResolver();
        identityResolver.Map(ActorAadObjectId, new UserIdentity(
            InternalUserId: "internal-dave",
            AadObjectId: ActorAadObjectId,
            DisplayName: "Dave Contoso",
            Role: "operator"));
        var handler = new MessageExtensionHandler(
            dispatcher,
            identityResolver,
            new AlwaysAuthorizationService(),
            auditLogger,
            NullLogger<MessageExtensionHandler>.Instance);

        var turnContext = NewSubmitActionTurnContext(out var action, body: "   \r\n  ");

        var response = await handler.HandleAsync(turnContext, action, CancellationToken.None);

        Assert.NotNull(response.ComposeExtension);
        var attachment = Assert.Single(response.ComposeExtension!.Attachments);
        var json = Assert.IsType<JObject>(attachment.Content).ToString();
        Assert.Contains("Please select a message first", json);

        Assert.Empty(dispatcher.Dispatched);
        Assert.Empty(publisher.Published);
    }

    // ─── Scenario 4: audit call ────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_OnForward_LogsMessageActionReceivedAuditEntryWithCanonicalFields()
    {
        var publisher = new RecordingInboundEventPublisher();
        var (handler, audit, _) = BuildE2EHandler(publisher);
        var turnContext = NewSubmitActionTurnContext(out var action, body: ForwardedBody);

        await handler.HandleAsync(turnContext, action, CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditEventTypes.MessageActionReceived, entry.EventType);
        Assert.Equal(ActorAadObjectId, entry.ActorId);
        Assert.Equal(AuditActorTypes.User, entry.ActorType);
        Assert.Equal(TenantId, entry.TenantId);
        Assert.Equal(MessageExtensionHandler.MessageActionForwardAction, entry.Action);
        Assert.Equal(AuditOutcomes.Success, entry.Outcome);
        Assert.NotNull(entry.PayloadJson);
        Assert.Contains(ForwardedBody, entry.PayloadJson);
        Assert.Contains(SourceMessageId, entry.PayloadJson!);
        Assert.Contains(SenderDisplayName, entry.PayloadJson);
        Assert.False(string.IsNullOrEmpty(entry.Checksum));
        Assert.Equal(ConversationId, entry.ConversationId);
    }

    // ─── HTML body extraction ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_HtmlBodyContent_IsStrippedBeforeDispatchAndAudit()
    {
        var publisher = new RecordingInboundEventPublisher();
        var (handler, audit, _) = BuildE2EHandler(publisher);
        const string htmlBody = "<p>Please <b>investigate</b> failure</p><p>thanks</p>";
        var turnContext = NewSubmitActionTurnContext(out var action, body: htmlBody, contentType: "html");

        await handler.HandleAsync(turnContext, action, CancellationToken.None);

        var commandEvent = Assert.IsType<CommandEvent>(Assert.Single(publisher.Published));
        Assert.DoesNotContain("<p>", commandEvent.Payload.Payload);
        Assert.DoesNotContain("<b>", commandEvent.Payload.Payload);
        Assert.Contains("Please investigate failure", commandEvent.Payload.Payload);

        var entry = Assert.Single(audit.Entries);
        Assert.DoesNotContain("<p>", entry.PayloadJson);
    }

    // ─── Correlation ID reuse ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ReusesTurnStateCorrelationId_IfPresent()
    {
        const string upstreamCorrelationId = "corr-from-onturn-12345";
        var publisher = new RecordingInboundEventPublisher();
        var (handler, audit, _) = BuildE2EHandler(publisher);
        var turnContext = NewSubmitActionTurnContext(out var action, body: ForwardedBody, correlationId: upstreamCorrelationId);

        await handler.HandleAsync(turnContext, action, CancellationToken.None);

        var commandEvent = Assert.IsType<CommandEvent>(Assert.Single(publisher.Published));
        Assert.Equal(upstreamCorrelationId, commandEvent.CorrelationId);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(upstreamCorrelationId, entry.CorrelationId);
    }

    // ─── Constructor null-guards ───────────────────────────────────────────────────────

    [Fact]
    public void Constructor_RejectsNullCommandDispatcher()
        => Assert.Throws<ArgumentNullException>(() => new MessageExtensionHandler(
            null!, new FakeIdentityResolver(), new AlwaysAuthorizationService(), new RecordingAuditLogger(), NullLogger<MessageExtensionHandler>.Instance));

    [Fact]
    public void Constructor_RejectsNullIdentityResolver()
        => Assert.Throws<ArgumentNullException>(() => new MessageExtensionHandler(
            new RecordingCommandDispatcher(), null!, new AlwaysAuthorizationService(), new RecordingAuditLogger(), NullLogger<MessageExtensionHandler>.Instance));

    [Fact]
    public void Constructor_RejectsNullAuthorizationService()
        => Assert.Throws<ArgumentNullException>(() => new MessageExtensionHandler(
            new RecordingCommandDispatcher(), new FakeIdentityResolver(), null!, new RecordingAuditLogger(), NullLogger<MessageExtensionHandler>.Instance));

    [Fact]
    public void Constructor_RejectsNullAuditLogger()
        => Assert.Throws<ArgumentNullException>(() => new MessageExtensionHandler(
            new RecordingCommandDispatcher(), new FakeIdentityResolver(), new AlwaysAuthorizationService(), null!, NullLogger<MessageExtensionHandler>.Instance));

    [Fact]
    public void Constructor_RejectsNullLogger()
        => Assert.Throws<ArgumentNullException>(() => new MessageExtensionHandler(
            new RecordingCommandDispatcher(), new FakeIdentityResolver(), new AlwaysAuthorizationService(), new RecordingAuditLogger(), null!));

    [Fact]
    public async Task HandleAsync_RejectsNullTurnContext()
    {
        var handler = new MessageExtensionHandler(
            new RecordingCommandDispatcher(),
            new FakeIdentityResolver(),
            new AlwaysAuthorizationService(),
            new RecordingAuditLogger(),
            NullLogger<MessageExtensionHandler>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(
            null!, new MessagingExtensionAction(), CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_RejectsNullAction()
    {
        var handler = new MessageExtensionHandler(
            new RecordingCommandDispatcher(),
            new FakeIdentityResolver(),
            new AlwaysAuthorizationService(),
            new RecordingAuditLogger(),
            NullLogger<MessageExtensionHandler>.Instance);
        var turnContext = NewSubmitActionTurnContext(out _, body: ForwardedBody);

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(
            turnContext, null!, CancellationToken.None));
    }

    // ─── Identity / RBAC gating (Stage 3.4 step 4 + e2e §Message Actions) ──────────────

    [Fact]
    public async Task HandleAsync_UnmappedAadObjectId_ReturnsAccessDeniedCard_LogsSecurityRejection_AndDoesNotDispatch()
    {
        // Per `e2e-scenarios.md` §Teams Message Actions and the story Security requirement
        // ("Enforce tenant ID, user identity, ... and RBAC"), an inbound submit-action
        // invoke whose `From.AadObjectId` is not mapped by `IIdentityResolver` MUST be
        // rejected without dispatching, must publish NO `MessengerEvent`, and must log a
        // `SecurityRejection` audit entry.
        var publisher = new RecordingInboundEventPublisher();
        var dispatcher = new RecordingCommandDispatcher();
        var auditLogger = new RecordingAuditLogger();
        var identityResolver = new FakeIdentityResolver(); // no mapping → returns null
        var handler = new MessageExtensionHandler(
            dispatcher,
            identityResolver,
            new AlwaysAuthorizationService(),
            auditLogger,
            NullLogger<MessageExtensionHandler>.Instance);

        var turnContext = NewSubmitActionTurnContext(out var action, body: ForwardedBody);

        var response = await handler.HandleAsync(turnContext, action, CancellationToken.None);

        Assert.NotNull(response.ComposeExtension);
        var attachment = Assert.Single(response.ComposeExtension!.Attachments);
        var json = Assert.IsType<JObject>(attachment.Content).ToString();
        Assert.Contains("Access denied", json);
        Assert.Contains("not mapped", json);

        Assert.Empty(dispatcher.Dispatched);
        Assert.Empty(publisher.Published);

        var entry = Assert.Single(auditLogger.Entries);
        Assert.Equal(AuditEventTypes.SecurityRejection, entry.EventType);
        Assert.Equal("UnmappedUserRejected", entry.Action);
        Assert.Equal(AuditOutcomes.Rejected, entry.Outcome);
        Assert.Equal(ActorAadObjectId, entry.ActorId);
        Assert.Equal(TenantId, entry.TenantId);
    }

    [Fact]
    public async Task HandleAsync_ViewerRoleNotAuthorizedForAgentAsk_ReturnsAccessDeniedCard_LogsSecurityRejection_AndDoesNotDispatch()
    {
        // Mirrors `e2e-scenarios.md` §Teams Message Actions, scenario "Message action
        // from user without required RBAC role is denied":
        //   Given user has RBAC role "viewer"
        //   When user invokes the "Forward to Agent" message action
        //   Then the bot validates the user's RBAC role via Activity.From.AadObjectId
        //   And the "agent ask" command requires role "operator"
        //   And the bot returns a MessagingExtensionActionResponse containing an error
        //       Adaptive Card with text "You do not have permission to perform this action."
        //   And no MessengerEvent is created
        //   And an audit record is persisted for the access denial
        var publisher = new RecordingInboundEventPublisher();
        var dispatcher = new RecordingCommandDispatcher();
        var auditLogger = new RecordingAuditLogger();
        var identityResolver = new FakeIdentityResolver();
        identityResolver.Map(ActorAadObjectId, new UserIdentity(
            InternalUserId: "internal-viewer",
            AadObjectId: ActorAadObjectId,
            DisplayName: "Viewer Only",
            Role: "viewer"));
        var authorization = new AlwaysAuthorizationService
        {
            IsAuthorized = false,
            UserRole = "viewer",
            RequiredRole = "operator",
        };
        var handler = new MessageExtensionHandler(
            dispatcher,
            identityResolver,
            authorization,
            auditLogger,
            NullLogger<MessageExtensionHandler>.Instance);

        var turnContext = NewSubmitActionTurnContext(out var action, body: ForwardedBody);

        var response = await handler.HandleAsync(turnContext, action, CancellationToken.None);

        Assert.NotNull(response.ComposeExtension);
        var attachment = Assert.Single(response.ComposeExtension!.Attachments);
        var json = Assert.IsType<JObject>(attachment.Content).ToString();
        // Exact text from e2e-scenarios.md §Teams Message Actions:
        Assert.Contains("You do not have permission to perform this action.", json);
        // Required-role hint included when the auth service supplies it.
        Assert.Contains("operator", json);

        Assert.Empty(dispatcher.Dispatched);
        Assert.Empty(publisher.Published);

        // Authorization was actually consulted with the canonical verb "agent ask"
        // AND with the AAD object ID — RBAC is keyed by Entra `oid` claim, not by the
        // platform-internal user ID. Previously this assertion expected
        // "internal-viewer"; the iter-2 evaluator caught that the handler was passing
        // the wrong identifier, so the call site (and this assertion) now use AAD.
        var call = Assert.Single(authorization.Calls);
        Assert.Equal(TenantId, call.TenantId);
        Assert.Equal(ActorAadObjectId, call.UserId);
        Assert.Equal("agent ask", call.Command);

        var entry = Assert.Single(auditLogger.Entries);
        Assert.Equal(AuditEventTypes.SecurityRejection, entry.EventType);
        Assert.Equal("InsufficientRoleRejected", entry.Action);
        Assert.Equal(AuditOutcomes.Rejected, entry.Outcome);
        Assert.Equal(ActorAadObjectId, entry.ActorId);
        Assert.Equal(TenantId, entry.TenantId);
        Assert.NotNull(entry.PayloadJson);
        Assert.Contains("viewer", entry.PayloadJson);
        Assert.Contains("operator", entry.PayloadJson);
    }

    // ─── CommandId validation (Stage 3.4 step 4 — reject unknown action commands) ──────

    [Fact]
    public async Task HandleAsync_UnknownCommandId_ReturnsErrorCard_LogsRejectedAudit_AndDoesNotDispatch()
    {
        // Unrelated or future `composeExtension/submitAction` commands must NOT create
        // agent tasks accidentally. Only the canonical `forwardToAgent` action is
        // accepted; everything else returns an unknown-command card and is audited as a
        // `MessageActionReceived` entry with `Outcome=Rejected`.
        var publisher = new RecordingInboundEventPublisher();
        var dispatcher = new RecordingCommandDispatcher();
        var auditLogger = new RecordingAuditLogger();
        var identityResolver = new FakeIdentityResolver();
        identityResolver.Map(ActorAadObjectId, new UserIdentity(
            InternalUserId: "internal-dave",
            AadObjectId: ActorAadObjectId,
            DisplayName: "Dave Contoso",
            Role: "operator"));
        var handler = new MessageExtensionHandler(
            dispatcher,
            identityResolver,
            new AlwaysAuthorizationService(),
            auditLogger,
            NullLogger<MessageExtensionHandler>.Instance);

        var turnContext = NewSubmitActionTurnContext(out var action, body: ForwardedBody);
        action.CommandId = "someUnknownCommand"; // not "forwardToAgent"

        var response = await handler.HandleAsync(turnContext, action, CancellationToken.None);

        Assert.NotNull(response.ComposeExtension);
        var attachment = Assert.Single(response.ComposeExtension!.Attachments);
        var json = Assert.IsType<JObject>(attachment.Content).ToString();
        Assert.Contains("Unknown message action", json);
        Assert.Contains("someUnknownCommand", json);

        Assert.Empty(dispatcher.Dispatched);
        Assert.Empty(publisher.Published);

        var entry = Assert.Single(auditLogger.Entries);
        Assert.Equal(AuditEventTypes.MessageActionReceived, entry.EventType);
        Assert.Equal(AuditOutcomes.Rejected, entry.Outcome);
        Assert.NotNull(entry.PayloadJson);
        Assert.Contains("someUnknownCommand", entry.PayloadJson!);
    }

    [Fact]
    public async Task HandleAsync_EmptyCommandId_IsRejectedAsUnknownCommand()
    {
        var publisher = new RecordingInboundEventPublisher();
        var dispatcher = new RecordingCommandDispatcher();
        var auditLogger = new RecordingAuditLogger();
        var identityResolver = new FakeIdentityResolver();
        identityResolver.Map(ActorAadObjectId, new UserIdentity(
            InternalUserId: "internal-dave",
            AadObjectId: ActorAadObjectId,
            DisplayName: "Dave Contoso",
            Role: "operator"));
        var handler = new MessageExtensionHandler(
            dispatcher,
            identityResolver,
            new AlwaysAuthorizationService(),
            auditLogger,
            NullLogger<MessageExtensionHandler>.Instance);

        var turnContext = NewSubmitActionTurnContext(out var action, body: ForwardedBody);
        action.CommandId = string.Empty;

        var response = await handler.HandleAsync(turnContext, action, CancellationToken.None);

        var attachment = Assert.Single(response.ComposeExtension!.Attachments);
        var json = Assert.IsType<JObject>(attachment.Content).ToString();
        Assert.Contains("Unknown message action", json);

        Assert.Empty(dispatcher.Dispatched);
        Assert.Empty(publisher.Published);
    }

    // ─── Stage 5.2 iter-7 (eval iter-6 item 3): dispatch+audit double-failure ─────────

    /// <summary>
    /// When BOTH the command dispatcher throws AND the <see cref="IAuditLogger"/> throws
    /// during the dispatch-failure audit emit, the handler MUST surface BOTH root causes
    /// via <see cref="AggregateException"/> rather than swallowing the audit error. The
    /// prior log-and-swallow design allowed <c>MessageActionReceived</c> audit rows to
    /// be silently absent for the dispatch-failure path even though the workstream's
    /// compliance contract requires every message-action submission to land an audit row
    /// (per <c>tech-spec.md</c> §4.3 / canonical <see cref="AuditEventTypes.MessageActionReceived"/>).
    /// Mirrors the iter-5 <c>TeamsSwarmActivityHandler</c> dispatch+audit double-failure
    /// pattern: dispatch error first, audit error second.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DispatchAndAuditBothFail_ThrowsAggregateExceptionCarryingBothInnerExceptions()
    {
        var dispatchError = new InvalidOperationException("simulated dispatcher failure");
        var auditError = new InvalidOperationException("simulated audit-store outage");

        var throwingDispatcher = new ThrowingCommandDispatcher(dispatchError);
        var throwingAudit = new ThrowingAuditLogger(auditError);
        var identityResolver = new FakeIdentityResolver();
        identityResolver.Map(ActorAadObjectId, new UserIdentity(
            InternalUserId: "internal-dave",
            AadObjectId: ActorAadObjectId,
            DisplayName: "Dave Contoso",
            Role: "operator"));
        var handler = new MessageExtensionHandler(
            throwingDispatcher,
            identityResolver,
            new AlwaysAuthorizationService(),
            throwingAudit,
            NullLogger<MessageExtensionHandler>.Instance);

        var turnContext = NewSubmitActionTurnContext(out var action, body: ForwardedBody);

        var ex = await Assert.ThrowsAsync<AggregateException>(() =>
            handler.HandleAsync(turnContext, action, CancellationToken.None));

        Assert.Equal(2, ex.InnerExceptions.Count);
        // Ordering contract: dispatch root cause first, audit failure second
        // (mirrors TeamsSwarmActivityHandler.OnMessageActivityAsync iter-5 pattern).
        Assert.Same(dispatchError, ex.InnerExceptions[0]);
        Assert.Same(auditError, ex.InnerExceptions[1]);
        // Even though the emit threw, the audit logger DID attempt to log once
        // (with the Failed outcome computed from the dispatch throw) — proves the
        // post-dispatch audit attempt actually ran.
        Assert.Single(throwingAudit.AttemptedEntries);
        Assert.Equal(AuditOutcomes.Failed, throwingAudit.AttemptedEntries[0].Outcome);
        Assert.Equal(AuditEventTypes.MessageActionReceived, throwingAudit.AttemptedEntries[0].EventType);
    }

    /// <summary>
    /// When dispatch fails but audit succeeds, the handler returns the dispatch-failure
    /// confirmation card (no exception thrown) and the <c>MessageActionReceived</c>
    /// audit row IS persisted with <c>Outcome=Failed</c>. Pins the "audit landed → user
    /// gets friendly card" branch of the iter-7 dispatch+audit matrix.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DispatchFailsButAuditSucceeds_ReturnsDispatchFailureCard_AndLandsFailedAuditRow()
    {
        var dispatchError = new InvalidOperationException("simulated dispatcher failure");

        var throwingDispatcher = new ThrowingCommandDispatcher(dispatchError);
        var auditLogger = new RecordingAuditLogger();
        var identityResolver = new FakeIdentityResolver();
        identityResolver.Map(ActorAadObjectId, new UserIdentity(
            InternalUserId: "internal-dave",
            AadObjectId: ActorAadObjectId,
            DisplayName: "Dave Contoso",
            Role: "operator"));
        var handler = new MessageExtensionHandler(
            throwingDispatcher,
            identityResolver,
            new AlwaysAuthorizationService(),
            auditLogger,
            NullLogger<MessageExtensionHandler>.Instance);

        var turnContext = NewSubmitActionTurnContext(out var action, body: ForwardedBody);

        var response = await handler.HandleAsync(turnContext, action, CancellationToken.None);

        Assert.NotNull(response.ComposeExtension);
        var entry = Assert.Single(auditLogger.Entries);
        Assert.Equal(AuditEventTypes.MessageActionReceived, entry.EventType);
        Assert.Equal(AuditOutcomes.Failed, entry.Outcome);
        Assert.Equal(ActorAadObjectId, entry.ActorId);
        Assert.Equal(TenantId, entry.TenantId);
        Assert.NotNull(entry.PayloadJson);
        // Error context is included in payload so compliance review can correlate.
        Assert.Contains("simulated dispatcher failure", entry.PayloadJson!);
    }

    private sealed class ThrowingCommandDispatcher : ICommandDispatcher
    {
        private readonly Exception _toThrow;
        public ThrowingCommandDispatcher(Exception toThrow) => _toThrow = toThrow;
        public Task DispatchAsync(CommandContext context, CancellationToken ct)
            => throw _toThrow;
    }

    private sealed class ThrowingAuditLogger : IAuditLogger
    {
        private readonly Exception _toThrow;
        public ThrowingAuditLogger(Exception toThrow) => _toThrow = toThrow;
        public List<AuditEntry> AttemptedEntries { get; } = new();
        public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            AttemptedEntries.Add(entry);
            throw _toThrow;
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a <see cref="MessageExtensionHandler"/> wired with a real
    /// <see cref="CommandDispatcher"/> + <see cref="AskCommandHandler"/> chain so the test
    /// can assert against an actual <see cref="CommandEvent"/> produced by the
    /// dispatcher path (mirrors the E2E harness pattern used by other tests).
    /// </summary>
    /// <remarks>
    /// The identity resolver is pre-seeded with a mapping for
    /// <see cref="ActorAadObjectId"/> → operator-role <see cref="UserIdentity"/>, and the
    /// authorization service is the always-allow stub so the Stage 3.4 happy-path tests
    /// exercise the full dispatch pipeline. RBAC-denial scenarios construct the handler
    /// directly with a tailored <see cref="AlwaysAuthorizationService"/> instance.
    /// </remarks>
    private static (MessageExtensionHandler Handler, RecordingAuditLogger AuditLogger, InertBotAdapter Adapter) BuildE2EHandler(
        IInboundEventPublisher publisher)
    {
        var auditLogger = new RecordingAuditLogger();
        var renderer = new AgentSwarm.Messaging.Teams.Cards.AdaptiveCardBuilder();
        var statusProvider = new NullAgentSwarmStatusProvider();
        var questionStore = new InMemoryAgentQuestionStore();

        var handlers = new ICommandHandler[]
        {
            new AskCommandHandler(publisher, NullLogger<AskCommandHandler>.Instance),
            new StatusCommandHandler(statusProvider, renderer, publisher, NullLogger<StatusCommandHandler>.Instance),
            new ApproveCommandHandler(questionStore, publisher, renderer, NullLogger<ApproveCommandHandler>.Instance),
            new RejectCommandHandler(questionStore, publisher, renderer, NullLogger<RejectCommandHandler>.Instance),
            new EscalateCommandHandler(publisher, NullLogger<EscalateCommandHandler>.Instance),
            new PauseCommandHandler(publisher, NullLogger<PauseCommandHandler>.Instance),
            new ResumeCommandHandler(publisher, NullLogger<ResumeCommandHandler>.Instance),
        };

        var dispatcher = new CommandDispatcher(
            handlers,
            publisher,
            NullLogger<CommandDispatcher>.Instance);

        var identityResolver = new FakeIdentityResolver();
        identityResolver.Map(ActorAadObjectId, new UserIdentity(
            InternalUserId: "internal-dave",
            AadObjectId: ActorAadObjectId,
            DisplayName: "Dave Contoso",
            Role: "operator"));

        var handler = new MessageExtensionHandler(
            dispatcher,
            identityResolver,
            new AlwaysAuthorizationService(),
            auditLogger,
            NullLogger<MessageExtensionHandler>.Instance);

        return (handler, auditLogger, new InertBotAdapter());
    }

    private static ITurnContext<IInvokeActivity> NewSubmitActionTurnContext(
        out MessagingExtensionAction action,
        string? body,
        string contentType = "text",
        string? correlationId = null,
        InertBotAdapter? adapter = null)
    {
        var activity = new Activity(ActivityTypes.Invoke)
        {
            Id = Guid.NewGuid().ToString(),
            Name = "composeExtension/submitAction",
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            From = new ChannelAccount(id: "29:1234", name: "Dave Contoso") { AadObjectId = ActorAadObjectId },
            Recipient = new ChannelAccount(id: HandlerFactory.BotId, name: HandlerFactory.BotName),
            Conversation = new ConversationAccount(id: ConversationId) { TenantId = TenantId },
        };

        activity.ChannelData = JObject.FromObject(new
        {
            tenant = new { id = TenantId },
        });

        action = new MessagingExtensionAction
        {
            CommandId = MessageExtensionHandler.ForwardToAgentCommandId,
            CommandContext = "message",
            MessagePayload = body is null
                ? null
                : new MessageActionsPayload
                {
                    Id = SourceMessageId,
                    MessageType = "message",
                    CreatedDateTime = "2024-08-10T12:34:56.789Z",
                    Body = new MessageActionsPayloadBody
                    {
                        ContentType = contentType,
                        Content = body,
                    },
                    From = new MessageActionsPayloadFrom
                    {
                        User = new MessageActionsPayloadUser
                        {
                            Id = "29:sender-aad",
                            DisplayName = SenderDisplayName,
                            UserIdentityType = "aadUser",
                        },
                    },
                },
        };

        activity.Value = JObject.FromObject(action);

        var turnContext = new TurnContext(adapter ?? new InertBotAdapter(), activity);
        if (correlationId is not null)
        {
            turnContext.TurnState.Set(TeamsSwarmActivityHandler.CorrelationIdTurnStateKey, correlationId);
        }

        return new InvokeTurnContext(turnContext);
    }

    /// <summary>
    /// Wraps <see cref="TurnContext"/> as <see cref="ITurnContext{IInvokeActivity}"/> so
    /// the handler's strongly-typed parameter is satisfied. Delegates every member to the
    /// inner context.
    /// </summary>
    private sealed class InvokeTurnContext : ITurnContext<IInvokeActivity>
    {
        private readonly ITurnContext _inner;

        public InvokeTurnContext(ITurnContext inner) => _inner = inner;

        public IInvokeActivity Activity => (IInvokeActivity)_inner.Activity;

        Activity ITurnContext.Activity => _inner.Activity;

        public BotAdapter Adapter => _inner.Adapter;

        public TurnContextStateCollection TurnState => _inner.TurnState;

        public bool Responded => _inner.Responded;

        public ITurnContext OnDeleteActivity(DeleteActivityHandler handler) => _inner.OnDeleteActivity(handler);

        public ITurnContext OnSendActivities(SendActivitiesHandler handler) => _inner.OnSendActivities(handler);

        public ITurnContext OnUpdateActivity(UpdateActivityHandler handler) => _inner.OnUpdateActivity(handler);

        public Task<ResourceResponse> SendActivityAsync(string textReplyToSend, string? speak = null, string inputHint = "acceptingInput", CancellationToken cancellationToken = default)
            => _inner.SendActivityAsync(textReplyToSend, speak, inputHint, cancellationToken);

        public Task<ResourceResponse> SendActivityAsync(IActivity activity, CancellationToken cancellationToken = default)
            => _inner.SendActivityAsync(activity, cancellationToken);

        public Task<ResourceResponse[]> SendActivitiesAsync(IActivity[] activities, CancellationToken cancellationToken = default)
            => _inner.SendActivitiesAsync(activities, cancellationToken);

        public Task<ResourceResponse> UpdateActivityAsync(IActivity activity, CancellationToken cancellationToken = default)
            => _inner.UpdateActivityAsync(activity, cancellationToken);

        public Task DeleteActivityAsync(string activityId, CancellationToken cancellationToken = default)
            => _inner.DeleteActivityAsync(activityId, cancellationToken);

        public Task DeleteActivityAsync(ConversationReference conversationReference, CancellationToken cancellationToken = default)
            => _inner.DeleteActivityAsync(conversationReference, cancellationToken);
    }
}
