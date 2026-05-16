using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Security;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using static AgentSwarm.Messaging.Teams.Tests.Security.SecurityTestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Iter-2 evaluator feedback #1 + #6 — integration tests proving
/// <see cref="TeamsMessengerConnector.SendQuestionAsync"/> invokes the Stage 5.1
/// <see cref="InstallationStateGate"/> BEFORE <c>CloudAdapter.ContinueConversationAsync</c>
/// when the production 10-arg constructor is resolved with a non-null gate.
/// </summary>
/// <remarks>
/// <para>
/// The existing <c>TeamsMessengerConnectorTests</c> suite uses the legacy 9-arg
/// constructor which leaves <c>installationStateGate</c> null (Stage 4.2-era behaviour).
/// This file resolves the canonical Stage 5.1 production constructor with a real
/// <see cref="InstallationStateGate"/> sharing the connector's
/// <see cref="IConversationReferenceStore"/> singleton so the gate's
/// <c>IsActiveBy*Async</c> probe sees the same data the connector's lookup did.
/// </para>
/// </remarks>
public sealed class TeamsMessengerConnectorInstallationGateTests
{
    private const string TenantId = "contoso-tenant-id";
    private const string MicrosoftAppId = "11111111-1111-1111-1111-111111111111";
    private const string PersonalConversationId = "19:conversation-alice";

    [Fact]
    public async Task SendQuestionAsync_UserTargetInactive_GateRejectsBeforeBotFrameworkAndAuditsRejection()
    {
        var harness = ConnectorGateHarness.Build();
        harness.ConversationStore.UserActiveMap[(TenantId, "internal-alice")] = false;
        harness.ConversationStore.PreloadByInternalUserId[(TenantId, "internal-alice")] =
            NewPersonalReference("ref-alice", aadObjectId: "aad-alice", internalUserId: "internal-alice");

        var question = NewQuestion("Q-conn-gate-1", targetUserId: "internal-alice");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Connector.SendQuestionAsync(question, CancellationToken.None));
        Assert.Contains("InstallationStateGate", ex.Message, StringComparison.Ordinal);

        // Gate probe was invoked.
        var probe = Assert.Single(harness.ConversationStore.UserProbeCalls);
        Assert.Equal((TenantId, "internal-alice"), probe);

        // Stage 5.1 iter-5 evaluator feedback item 1 — the active-only reference lookup
        // was NEVER invoked. The gate runs FIRST and short-circuits before
        // GetByInternalUserIdAsync, so a real SqlConversationReferenceStore filtering
        // inactive rows would never have a chance to return null and mask the gate's
        // audit row.
        Assert.Empty(harness.ConversationStore.InternalUserLookups);

        // Bot Framework was NEVER called.
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.Adapter.Sent);

        // InstallationGateRejected audit row emitted.
        var entry = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.Error, entry.EventType);
        Assert.Equal(AuditOutcomes.Failed, entry.Outcome);
        Assert.Equal("InstallationGateRejected", entry.Action);
        Assert.Equal(TenantId, entry.TenantId);

        // Card state was NOT persisted (no deliverable card).
        Assert.Empty(harness.CardStateStore.Saved);
        // ConversationId update was NOT performed.
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
    }

    [Fact]
    public async Task SendQuestionAsync_UserTargetNeverInstalled_NoPreloadedRow_GateRejectsAndAudits()
    {
        // Stage 5.1 iter-5 evaluator feedback item 2 — connector synchronous-send variant
        // of the "user never installed" scenario. Real SqlConversationReferenceStore
        // contains no row at all.
        var harness = ConnectorGateHarness.Build();
        var question = NewQuestion("Q-conn-uninstalled-1", targetUserId: "internal-uninstalled");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Connector.SendQuestionAsync(question, CancellationToken.None));
        Assert.Contains("InstallationStateGate", ex.Message, StringComparison.Ordinal);

        Assert.Single(harness.ConversationStore.UserProbeCalls);
        Assert.Empty(harness.ConversationStore.InternalUserLookups);
        Assert.Empty(harness.Adapter.ContinueCalls);
        var entry = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal("InstallationGateRejected", entry.Action);
        Assert.Equal(AuditOutcomes.Failed, entry.Outcome);
    }

    [Fact]
    public async Task SendQuestionAsync_UserTargetActive_GateAllowsAndSendCompletes()
    {
        var harness = ConnectorGateHarness.Build();
        harness.ConversationStore.UserActiveMap[(TenantId, "internal-alice")] = true;
        harness.ConversationStore.PreloadByInternalUserId[(TenantId, "internal-alice")] =
            NewPersonalReference("ref-alice", aadObjectId: "aad-alice", internalUserId: "internal-alice");

        var question = NewQuestion("Q-conn-gate-allow", targetUserId: "internal-alice");

        await harness.Connector.SendQuestionAsync(question, CancellationToken.None);

        Assert.Single(harness.ConversationStore.UserProbeCalls);
        Assert.Single(harness.Adapter.ContinueCalls);
        Assert.Single(harness.Adapter.Sent);
        Assert.Empty(harness.AuditLogger.Entries);
        Assert.Single(harness.CardStateStore.Saved);
    }

    // ---- Stage 5.1 iter-5 evaluator feedback items 2 + 3 — outbox dead-letter wiring.
    //      When the connector's gate rejects a synchronous SendQuestionAsync call AND
    //      the Phase 6 outbox engine has wrapped the call in
    //      ProactiveSendContext.WithOutboxEntryId(...), IMessageOutbox.DeadLetterAsync
    //      MUST land. Without this, retry storms keep flooding the outbox.

    [Fact]
    public async Task SendQuestionAsync_UserTargetInactive_WithOutboxContext_DeadLettersOutboxEntry()
    {
        var harness = ConnectorGateHarness.Build();
        harness.ConversationStore.UserActiveMap[(TenantId, "internal-alice")] = false;
        harness.ConversationStore.PreloadByInternalUserId[(TenantId, "internal-alice")] =
            NewPersonalReference("ref-alice", aadObjectId: "aad-alice", internalUserId: "internal-alice");

        var question = NewQuestion("Q-conn-dl-1", targetUserId: "internal-alice");

        const string OutboxEntryId = "outbox-conn-q-1";
        using (ProactiveSendContext.WithOutboxEntryId(OutboxEntryId))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.Connector.SendQuestionAsync(question, CancellationToken.None));
        }

        var dl = Assert.Single(harness.Outbox.DeadLettered);
        Assert.Equal(OutboxEntryId, dl.OutboxEntryId);
        Assert.Contains("internal-alice", dl.Error, StringComparison.Ordinal);

        // Audit row still emitted.
        Assert.Single(harness.AuditLogger.Entries);
        // Bot Framework still never called.
        Assert.Empty(harness.Adapter.ContinueCalls);
        // Card state still NOT persisted.
        Assert.Empty(harness.CardStateStore.Saved);
    }

    [Fact]
    public async Task SendQuestionAsync_UserTargetInactive_WithoutOutboxContext_SkipsDeadLetter()
    {
        // Negative control: outside an outbox dispatch, dead-letter is intentionally
        // skipped (the synchronous caller surfaces the failure through the thrown
        // exception). Audit still lands so compliance review captures every rejection.
        var harness = ConnectorGateHarness.Build();
        harness.ConversationStore.UserActiveMap[(TenantId, "internal-alice-direct")] = false;
        harness.ConversationStore.PreloadByInternalUserId[(TenantId, "internal-alice-direct")] =
            NewPersonalReference("ref-alice-direct", aadObjectId: "aad-alice-direct", internalUserId: "internal-alice-direct");

        var question = NewQuestion("Q-conn-direct-1", targetUserId: "internal-alice-direct");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Connector.SendQuestionAsync(question, CancellationToken.None));

        Assert.Empty(harness.Outbox.DeadLettered);
        Assert.Single(harness.AuditLogger.Entries);
    }

    // ---- Stage 5.1 iter-7 evaluator feedback item 2 — SendMessageAsync install-gate
    //      wiring. The bare-MessengerMessage routing path used to throw
    //      InvalidOperationException on missing/inactive references without an audit row
    //      or outbox dead-letter, so an outbox-wrapped retry kept storming the same
    //      uninstalled conversation. The connector now calls
    //      InstallationStateGate.RejectMessageRoutingAsync when GetByConversationIdAsync
    //      returns null, producing the same InstallationGateRejected audit row that the
    //      user/channel paths emit and dead-lettering the outbox entry when the call
    //      ran inside ProactiveSendContext.WithOutboxEntryId(...).

    [Fact]
    public async Task SendMessageAsync_NoActiveReference_WithOutboxContext_GateAuditsAndDeadLettersAndThrows()
    {
        var harness = ConnectorGateHarness.Build();
        // Router has NO preload — GetByConversationIdAsync returns null, mimicking the
        // SqlConversationReferenceStore's IsActive=true filter excluding an
        // uninstalled-app row.
        var missingConversationId = "19:uninstalled-conversation";

        var message = new MessengerMessage(
            MessageId: "msg-dl-1",
            CorrelationId: "corr-dl-1",
            AgentId: "agent-build",
            TaskId: "task-77",
            ConversationId: missingConversationId,
            Body: "Should be rejected and dead-lettered.",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        const string OutboxEntryId = "outbox-conn-msg-1";
        InvalidOperationException ex;
        using (ProactiveSendContext.WithOutboxEntryId(OutboxEntryId))
        {
            ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => harness.Connector.SendMessageAsync(message, CancellationToken.None));
        }

        Assert.Contains("InstallationStateGate", ex.Message, StringComparison.Ordinal);
        Assert.Contains(missingConversationId, ex.Message, StringComparison.Ordinal);

        // The router lookup ran (we need it to discover the missing-reference condition)…
        Assert.Equal(missingConversationId, Assert.Single(harness.Router.Lookups));
        // …but Bot Framework was NEVER called.
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.Adapter.Sent);

        // InstallationGateRejected audit row emitted with the message's correlation /
        // agent / task metadata.
        var entry = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.Error, entry.EventType);
        Assert.Equal(AuditOutcomes.Failed, entry.Outcome);
        Assert.Equal("InstallationGateRejected", entry.Action);
        Assert.Equal("corr-dl-1", entry.CorrelationId);
        Assert.Equal("agent-build", entry.ActorId);
        Assert.Equal("task-77", entry.TaskId);
        Assert.Contains(missingConversationId, entry.PayloadJson, StringComparison.Ordinal);
        // Marker that proves the RejectMessageRoutingAsync path ran (vs CheckAsync /
        // CheckTargetAsync) — the synthetic QuestionId carries the "messenger-message-
        // routing::" prefix only emitted by the message-routing reject helper.
        Assert.Contains("messenger-message-routing::msg-dl-1", entry.PayloadJson, StringComparison.Ordinal);

        // Outbox entry dead-lettered with the reason string.
        var dl = Assert.Single(harness.Outbox.DeadLettered);
        Assert.Equal(OutboxEntryId, dl.OutboxEntryId);
        Assert.Contains(missingConversationId, dl.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendMessageAsync_NoActiveReference_NoOutboxContext_GateAuditsAndThrowsButSkipsDeadLetter()
    {
        // Negative control mirroring the question-path equivalent: outside an outbox
        // dispatch, dead-letter is intentionally skipped (the synchronous caller surfaces
        // the failure through the thrown exception). Audit still lands so compliance
        // review captures every rejection regardless of caller context.
        var harness = ConnectorGateHarness.Build();
        var missingConversationId = "19:direct-uninstalled";

        var message = new MessengerMessage(
            MessageId: "msg-direct-1",
            CorrelationId: "corr-direct-1",
            AgentId: "agent-build",
            TaskId: "task-78",
            ConversationId: missingConversationId,
            Body: "Direct send should fail loudly with audit but no dead-letter.",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Connector.SendMessageAsync(message, CancellationToken.None));

        Assert.Contains("InstallationStateGate", ex.Message, StringComparison.Ordinal);

        // Bot Framework still never called.
        Assert.Empty(harness.Adapter.ContinueCalls);
        // Audit row emitted.
        var entry = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal("InstallationGateRejected", entry.Action);
        Assert.Equal(AuditOutcomes.Failed, entry.Outcome);
        // Outbox dead-letter NOT invoked (no ProactiveSendContext scope wrapped the call).
        Assert.Empty(harness.Outbox.DeadLettered);
    }

    [Fact]
    public async Task SendMessageAsync_ActiveReference_BypassesGateRejection_AndDelivers()
    {
        // Positive control: a live preload on the router triggers a successful delivery
        // — the install-gate audit path MUST NOT fire when the lookup succeeds.
        var harness = ConnectorGateHarness.Build();
        const string ConversationId = "19:active-conversation";
        var stored = NewPersonalReference("ref-active", aadObjectId: "aad-active", internalUserId: "internal-active", conversationId: ConversationId);
        harness.Router.PreloadByConversationId[ConversationId] = stored;

        var message = new MessengerMessage(
            MessageId: "msg-ok-1",
            CorrelationId: "corr-ok-1",
            AgentId: "agent-build",
            TaskId: "task-79",
            ConversationId: ConversationId,
            Body: "Live conversation — should deliver.",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        const string OutboxEntryId = "outbox-conn-msg-ok-1";
        using (ProactiveSendContext.WithOutboxEntryId(OutboxEntryId))
        {
            await harness.Connector.SendMessageAsync(message, CancellationToken.None);
        }

        // Bot Framework was invoked exactly once.
        Assert.Single(harness.Adapter.ContinueCalls);
        Assert.Single(harness.Adapter.Sent);
        // No rejection audit row.
        Assert.Empty(harness.AuditLogger.Entries);
        // No dead-letter (delivery succeeded).
        Assert.Empty(harness.Outbox.DeadLettered);
    }

    // ---- helpers ---------------------------------------------------------------------

    private static TeamsConversationReference NewPersonalReference(
        string id,
        string aadObjectId,
        string internalUserId,
        string conversationId = PersonalConversationId)
    {
        var bfReference = new ConversationReference
        {
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            Bot = new ChannelAccount(id: MicrosoftAppId, name: "AgentBot"),
            User = new ChannelAccount(id: $"29:{aadObjectId}", name: "User") { AadObjectId = aadObjectId },
            Conversation = new ConversationAccount(id: conversationId) { TenantId = TenantId },
        };

        return new TeamsConversationReference
        {
            Id = id,
            TenantId = TenantId,
            AadObjectId = aadObjectId,
            InternalUserId = internalUserId,
            ServiceUrl = bfReference.ServiceUrl,
            ConversationId = conversationId,
            BotId = MicrosoftAppId,
            ReferenceJson = JsonConvert.SerializeObject(bfReference),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
    }

    private static AgentQuestion NewQuestion(
        string questionId,
        string? targetUserId = null,
        string? targetChannelId = null)
    {
        return new AgentQuestion
        {
            QuestionId = questionId,
            AgentId = "agent-build",
            TaskId = "task-42",
            TenantId = TenantId,
            TargetUserId = targetUserId,
            TargetChannelId = targetChannelId,
            Title = "Promote build to staging?",
            Body = "Build #42 finished.",
            Severity = MessageSeverities.Info,
            AllowedActions = new[]
            {
                new HumanAction("approve", "Approve", "approve", false),
                new HumanAction("reject", "Reject", "reject", true),
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            CorrelationId = $"corr-{questionId}",
        };
    }

    private sealed record ConnectorGateHarness(
        TeamsMessengerConnector Connector,
        TeamsMessengerConnectorTests.RecordingCloudAdapter Adapter,
        TeamsProactiveNotifierInstallationGateTests.InstallationGateAwareConversationReferenceStore ConversationStore,
        TestDoubles.RecordingConversationReferenceRouter Router,
        TeamsMessengerConnectorTests.RecordingAgentQuestionStore AgentQuestionStore,
        TeamsMessengerConnectorTests.RecordingCardStateStore CardStateStore,
        RecordingAuditLogger AuditLogger,
        RecordingMessageOutbox Outbox,
        TeamsMessagingOptions Options)
    {
        public static ConnectorGateHarness Build()
        {
            var adapter = new TeamsMessengerConnectorTests.RecordingCloudAdapter();
            var options = new TeamsMessagingOptions { MicrosoftAppId = MicrosoftAppId };
            var convStore = new TeamsProactiveNotifierInstallationGateTests.InstallationGateAwareConversationReferenceStore();
            // The connector wants an IConversationReferenceRouter for SendMessageAsync,
            // but SendQuestionAsync doesn't use it; supply a recording stub so the ctor
            // null-guard passes.
            var router = new TestDoubles.RecordingConversationReferenceRouter();
            var qStore = new TeamsMessengerConnectorTests.RecordingAgentQuestionStore();
            var cardStore = new TeamsMessengerConnectorTests.RecordingCardStateStore();
            var renderer = new AdaptiveCardBuilder();
            var reader = new ChannelInboundEventPublisher();
            var auditLogger = new RecordingAuditLogger();
            var outbox = new RecordingMessageOutbox();
            var gate = new InstallationStateGate(
                convStore,
                outbox,
                auditLogger,
                NullLogger<InstallationStateGate>.Instance);

            var connector = new TeamsMessengerConnector(
                adapter,
                options,
                convStore,
                router,
                qStore,
                cardStore,
                renderer,
                reader,
                NullLogger<TeamsMessengerConnector>.Instance,
                TimeProvider.System,
                installationStateGate: gate);

            return new ConnectorGateHarness(connector, adapter, convStore, router, qStore, cardStore, auditLogger, outbox, options);
        }
    }
}
