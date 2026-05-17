using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Microsoft.Bot.Schema;
using static AgentSwarm.Messaging.Teams.Tests.Security.SecurityTestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Iter-2 evaluator feedback #1 + #6 — integration tests that prove the proactive send
/// paths (<see cref="TeamsProactiveNotifier.SendProactiveQuestionAsync"/> and
/// <see cref="TeamsProactiveNotifier.SendQuestionToChannelAsync"/>) invoke the Stage 5.1
/// <see cref="InstallationStateGate"/> BEFORE <c>CloudAdapter.ContinueConversationAsync</c>
/// and short-circuit (skipping the Bot Framework call entirely) when the gate rejects.
/// </summary>
/// <remarks>
/// <para>
/// These tests resolve the canonical production constructor with a non-null
/// <see cref="InstallationStateGate"/>; the existing <c>TeamsProactiveNotifierTests</c>
/// suite resolves the legacy constructor that passes <c>installationStateGate: null</c>
/// to validate Stage 4.2 behaviour. Splitting the suites keeps the legacy ordering
/// assertions independent from the install-state assertions and makes the regression
/// surface (gate-aware vs gate-bypass) explicit in the file layout.
/// </para>
/// <para>
/// The recording stores used here re-use <c>TeamsProactiveNotifierTests</c>' nested
/// recording doubles (cloud adapter, agent question store, card state store) but supply a
/// fresh <see cref="InstallationGateAwareConversationReferenceStore"/> that exposes
/// BOTH the lookup path (<see cref="IConversationReferenceStore.GetByInternalUserIdAsync"/>
/// / <see cref="IConversationReferenceStore.GetByChannelIdAsync"/>) AND the gate-probe
/// path (<see cref="IConversationReferenceStore.IsActiveByInternalUserIdAsync"/> /
/// <see cref="IConversationReferenceStore.IsActiveByChannelAsync"/>) so the notifier and
/// the gate share the same data source — matching the production wiring where both
/// methods are served by the same <c>SqlConversationReferenceStore</c> singleton.
/// </para>
/// </remarks>
public sealed class TeamsProactiveNotifierInstallationGateTests
{
    private const string TenantId = "contoso-tenant-id";
    private const string MicrosoftAppId = "11111111-1111-1111-1111-111111111111";
    private const string PersonalConversationId = "19:conversation-alice";
    private const string ChannelConversationId = "19:channel-conv-general";

    [Fact]
    public async Task SendProactiveQuestionAsync_UserTargetInactive_GateRejectsBeforeBotFrameworkAndAuditsRejection()
    {
        var harness = GateAwareHarness.Build();
        // Gate-probe returns INACTIVE for this user — so the gate must reject and the
        // notifier must NOT call ContinueConversationAsync.
        harness.ConversationStore.UserActiveMap[(TenantId, "user-1")] = false;
        // The reference lookup ALSO has a preloaded row, but the active-map filter on
        // the store double's GetByInternalUserIdAsync (mirroring the real SQL store's
        // e.IsActive WHERE clause) means the getter would return null anyway. The gate
        // is what intercepts FIRST — proven by the Assert.Empty(InternalUserLookups)
        // assertion below. The preload is retained so a future regression to
        // lookup-before-gate ordering would show up as the lookup-returning-null
        // throw path WITHOUT the InstallationGateRejected audit row (the exact
        // production defect iter-4 / iter-5 surfaced).
        harness.ConversationStore.PreloadByInternalUserId[(TenantId, "user-1")] =
            NewPersonalReference("ref-alice", aadObjectId: "aad-alice", internalUserId: "user-1");
        var question = NewQuestion("Q-gate-1", targetUserId: "user-1");

        // The notifier wraps the gate's rejection in ConversationReferenceNotFoundException
        // so the outbox treats it as "do not retry".
        await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-1", question, CancellationToken.None));

        // The gate-probe was called with the canonical (TenantId, InternalUserId) key.
        var probe = Assert.Single(harness.ConversationStore.UserProbeCalls);
        Assert.Equal((TenantId, "user-1"), probe);

        // Stage 5.1 iter-5 evaluator feedback item 1 — the active-only lookup was NEVER
        // invoked: the gate runs FIRST, rejects, and short-circuits before the notifier
        // reaches GetByInternalUserIdAsync. This proves the structural fix that prevents
        // ConversationReferenceNotFoundException from masking the InstallationGateRejected
        // audit row in production.
        Assert.Empty(harness.ConversationStore.InternalUserLookups);

        // Bot Framework was NEVER called — the install-state rejection short-circuited
        // before the proactive send. This is the core integration invariant.
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.Adapter.Sent);

        // The gate emitted the InstallationGateRejected audit row with Failed outcome
        // and the question's tenant / agent / task fields populated.
        var entry = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.Error, entry.EventType);
        Assert.Equal(AuditOutcomes.Failed, entry.Outcome);
        Assert.Equal("InstallationGateRejected", entry.Action);
        Assert.Equal(TenantId, entry.TenantId);
        Assert.Equal(question.AgentId, entry.AgentId);
        Assert.Equal(question.TaskId, entry.TaskId);

        // The card-state row was NOT persisted (the gate's pre-send rejection means the
        // question never produced a deliverable card).
        Assert.Empty(harness.CardStateStore.Saved);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_UserTargetNeverInstalled_NoPreloadedRow_GateRejectsAndAudits()
    {
        // Stage 5.1 iter-5 evaluator feedback item 2 — production scenario where the
        // user NEVER installed the bot, so the conversation reference store contains no
        // row at all (real SqlConversationReferenceStore returns null because no entity
        // exists). This is the case the old "lookup-then-gate" ordering FAILED on most
        // subtly: GetByInternalUserIdAsync returned null → InvalidOperationException →
        // outbox saw a generic "not found" without the InstallationGateRejected audit.
        var harness = GateAwareHarness.Build();
        // NEITHER UserActiveMap NOR PreloadByInternalUserId has any entry for the target.
        var question = NewQuestion("Q-gate-uninstalled-1", targetUserId: "user-never-installed");

        await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-never-installed", question, CancellationToken.None));

        // Gate ran (probe was called).
        Assert.Single(harness.ConversationStore.UserProbeCalls);
        // Lookup was NEVER attempted — gate rejected first.
        Assert.Empty(harness.ConversationStore.InternalUserLookups);
        // Bot Framework was NOT invoked.
        Assert.Empty(harness.Adapter.ContinueCalls);
        // Audit row was emitted (this is the compliance evidence the old ordering lost).
        var entry = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal("InstallationGateRejected", entry.Action);
        Assert.Equal(AuditOutcomes.Failed, entry.Outcome);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_UserTargetActive_GateAllowsAndBotFrameworkInvoked()
    {
        var harness = GateAwareHarness.Build();
        // Gate-probe ACTIVE → the network send must proceed.
        harness.ConversationStore.UserActiveMap[(TenantId, "user-1")] = true;
        harness.ConversationStore.PreloadByInternalUserId[(TenantId, "user-1")] =
            NewPersonalReference("ref-alice", aadObjectId: "aad-alice", internalUserId: "user-1");
        var question = NewQuestion("Q-gate-allow-1", targetUserId: "user-1");

        await harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-1", question, CancellationToken.None);

        // The gate was probed.
        Assert.Single(harness.ConversationStore.UserProbeCalls);
        // The lookup ran ONCE (after the gate allowed).
        Assert.Single(harness.ConversationStore.InternalUserLookups);
        // Bot Framework was called exactly once.
        Assert.Single(harness.Adapter.ContinueCalls);
        Assert.Single(harness.Adapter.Sent);
        // No audit rejection.
        Assert.Empty(harness.AuditLogger.Entries);
        // Card state was persisted (Stage 4.2 happy path still works once gate allows).
        Assert.Single(harness.CardStateStore.Saved);
    }

    [Fact]
    public async Task SendQuestionToChannelAsync_ChannelTargetInactive_GateRejectsBeforeBotFrameworkAndAuditsRejection()
    {
        var harness = GateAwareHarness.Build();
        harness.ConversationStore.ChannelActiveMap[(TenantId, "channel-general")] = false;
        harness.ConversationStore.PreloadByChannelId[(TenantId, "channel-general")] =
            NewChannelReference("ref-chan", channelId: "channel-general");
        var question = NewQuestion("Q-gate-chan-1", targetChannelId: "channel-general");

        await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            harness.Notifier.SendQuestionToChannelAsync(TenantId, "channel-general", question, CancellationToken.None));

        var probe = Assert.Single(harness.ConversationStore.ChannelProbeCalls);
        Assert.Equal((TenantId, "channel-general"), probe);

        // Stage 5.1 iter-5 evaluator feedback item 1 — channel-scoped lookup is also
        // bypassed by the gate-first ordering.
        Assert.Empty(harness.ConversationStore.ChannelLookups);

        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.Adapter.Sent);

        var entry = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.Error, entry.EventType);
        Assert.Equal(AuditOutcomes.Failed, entry.Outcome);
        Assert.Equal("InstallationGateRejected", entry.Action);
        Assert.Contains("channel-general", entry.PayloadJson, StringComparison.Ordinal);

        Assert.Empty(harness.CardStateStore.Saved);
    }

    [Fact]
    public async Task SendQuestionToChannelAsync_ChannelNeverInstalled_NoPreloadedRow_GateRejectsAndAudits()
    {
        // Companion to the "user never installed" test above — channel-scoped variant.
        var harness = GateAwareHarness.Build();
        var question = NewQuestion("Q-gate-chan-uninstalled-1", targetChannelId: "channel-never-installed");

        await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            harness.Notifier.SendQuestionToChannelAsync(TenantId, "channel-never-installed", question, CancellationToken.None));

        Assert.Single(harness.ConversationStore.ChannelProbeCalls);
        Assert.Empty(harness.ConversationStore.ChannelLookups);
        Assert.Empty(harness.Adapter.ContinueCalls);
        var entry = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal("InstallationGateRejected", entry.Action);
    }

    // ---- Stage 5.1 iter-4 evaluator feedback item 3 — MessengerMessage proactive
    //      sends MUST also go through the gate. Previously SendProactiveAsync /
    //      SendToChannelAsync called ContinueConversationAsync directly without the
    //      install-state pre-check.

    [Fact]
    public async Task SendProactiveAsync_UserTargetInactive_GateRejectsBeforeBotFramework()
    {
        var harness = GateAwareHarness.Build();
        harness.ConversationStore.UserActiveMap[(TenantId, "user-msg-1")] = false;
        harness.ConversationStore.PreloadByInternalUserId[(TenantId, "user-msg-1")] =
            NewPersonalReference("ref-msg", aadObjectId: "aad-msg", internalUserId: "user-msg-1");
        var message = new MessengerMessage(
            MessageId: "msg-1",
            CorrelationId: "corr-msg-1",
            AgentId: "agent-build",
            TaskId: "task-42",
            ConversationId: "convo-1",
            Body: "ping",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            harness.Notifier.SendProactiveAsync(TenantId, "user-msg-1", message, CancellationToken.None));

        // Gate probe ran via CheckTargetAsync (synthetic AgentQuestion path).
        Assert.Single(harness.ConversationStore.UserProbeCalls);
        // Stage 5.1 iter-5 evaluator feedback item 1 — active-only lookup was NEVER
        // called. The gate's structural fix runs the probe FIRST and short-circuits
        // before GetByInternalUserIdAsync.
        Assert.Empty(harness.ConversationStore.InternalUserLookups);
        // Bot Framework was NOT called.
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.Adapter.Sent);
        // Audit row recorded with the synthetic MessengerMessage marker.
        var entry = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal("InstallationGateRejected", entry.Action);
        Assert.Contains("messenger-message::", entry.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendProactiveAsync_UserNeverInstalled_NoPreloadedRow_GateRejectsAndAudits()
    {
        // Stage 5.1 iter-5 evaluator feedback item 2 — MessengerMessage variant of the
        // "user never installed" scenario. Real store has no row, no active map entry.
        var harness = GateAwareHarness.Build();
        var message = new MessengerMessage(
            MessageId: "msg-uninst-1",
            CorrelationId: "corr-uninst-1",
            AgentId: "agent-build",
            TaskId: "task-42",
            ConversationId: "convo-uninst-1",
            Body: "ping",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            harness.Notifier.SendProactiveAsync(TenantId, "user-uninstalled", message, CancellationToken.None));

        Assert.Single(harness.ConversationStore.UserProbeCalls);
        Assert.Empty(harness.ConversationStore.InternalUserLookups);
        Assert.Empty(harness.Adapter.ContinueCalls);
        var entry = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal("InstallationGateRejected", entry.Action);
    }

    [Fact]
    public async Task SendProactiveAsync_UserTargetActive_GateAllowsAndBotFrameworkInvoked()
    {
        var harness = GateAwareHarness.Build();
        harness.ConversationStore.UserActiveMap[(TenantId, "user-msg-2")] = true;
        harness.ConversationStore.PreloadByInternalUserId[(TenantId, "user-msg-2")] =
            NewPersonalReference("ref-msg-2", aadObjectId: "aad-msg-2", internalUserId: "user-msg-2");
        var message = new MessengerMessage(
            MessageId: "msg-2",
            CorrelationId: "corr-msg-2",
            AgentId: "agent-build",
            TaskId: "task-42",
            ConversationId: "convo-2",
            Body: "ping",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        await harness.Notifier.SendProactiveAsync(TenantId, "user-msg-2", message, CancellationToken.None);

        Assert.Single(harness.ConversationStore.UserProbeCalls);
        Assert.Single(harness.Adapter.ContinueCalls);
        Assert.Single(harness.Adapter.Sent);
        Assert.Empty(harness.AuditLogger.Entries);
    }

    [Fact]
    public async Task SendToChannelAsync_ChannelTargetInactive_GateRejectsBeforeBotFramework()
    {
        var harness = GateAwareHarness.Build();
        harness.ConversationStore.ChannelActiveMap[(TenantId, "channel-msg-1")] = false;
        harness.ConversationStore.PreloadByChannelId[(TenantId, "channel-msg-1")] =
            NewChannelReference("ref-chan-msg", channelId: "channel-msg-1");
        var message = new MessengerMessage(
            MessageId: "msg-chan-1",
            CorrelationId: "corr-chan-msg-1",
            AgentId: "agent-build",
            TaskId: "task-42",
            ConversationId: "convo-chan-1",
            Body: "channel broadcast",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            harness.Notifier.SendToChannelAsync(TenantId, "channel-msg-1", message, CancellationToken.None));

        Assert.Single(harness.ConversationStore.ChannelProbeCalls);
        // Stage 5.1 iter-5 evaluator feedback item 1 — active-only lookup is bypassed.
        Assert.Empty(harness.ConversationStore.ChannelLookups);
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.Adapter.Sent);
        var entry = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal("InstallationGateRejected", entry.Action);
        Assert.Contains("channel-msg-1", entry.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendToChannelAsync_ChannelNeverInstalled_NoPreloadedRow_GateRejectsAndAudits()
    {
        // Channel-scoped "never installed" — real store has no row at all.
        var harness = GateAwareHarness.Build();
        var message = new MessengerMessage(
            MessageId: "msg-chan-uninst-1",
            CorrelationId: "corr-chan-uninst-1",
            AgentId: "agent-build",
            TaskId: "task-42",
            ConversationId: "convo-chan-uninst-1",
            Body: "broadcast",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            harness.Notifier.SendToChannelAsync(TenantId, "channel-never-installed", message, CancellationToken.None));

        Assert.Single(harness.ConversationStore.ChannelProbeCalls);
        Assert.Empty(harness.ConversationStore.ChannelLookups);
        Assert.Empty(harness.Adapter.ContinueCalls);
        var entry = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal("InstallationGateRejected", entry.Action);
    }

    // ---- Stage 5.1 iter-5 evaluator feedback items 2 + 3 — when the Phase 6 outbox
    //      engine wraps a dispatch in ProactiveSendContext.WithOutboxEntryId(...), the
    //      gate's rejection path MUST call IMessageOutbox.DeadLetterAsync. These tests
    //      assert the dead-letter contract for all three rejection paths
    //      (SendProactiveQuestionAsync / SendProactiveAsync / SendToChannelAsync /
    //      SendQuestionToChannelAsync).

    [Fact]
    public async Task SendProactiveQuestionAsync_UserInactive_WithOutboxContext_DeadLettersOutboxEntry()
    {
        var harness = GateAwareHarness.Build();
        harness.ConversationStore.UserActiveMap[(TenantId, "user-dl-1")] = false;
        harness.ConversationStore.PreloadByInternalUserId[(TenantId, "user-dl-1")] =
            NewPersonalReference("ref-dl-1", aadObjectId: "aad-dl-1", internalUserId: "user-dl-1");
        var question = NewQuestion("Q-dl-q-1", targetUserId: "user-dl-1");

        const string OutboxEntryId = "outbox-q-user-1";
        using (ProactiveSendContext.WithOutboxEntryId(OutboxEntryId))
        {
            await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
                harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-dl-1", question, CancellationToken.None));
        }

        // Dead-letter call landed on the outbox with the correct entry ID + reason from the gate.
        var dl = Assert.Single(harness.Outbox.DeadLettered);
        Assert.Equal(OutboxEntryId, dl.OutboxEntryId);
        Assert.Contains("user-dl-1", dl.Error, StringComparison.Ordinal);

        // Audit and BF still behave as in the no-context case.
        Assert.Single(harness.AuditLogger.Entries);
        Assert.Empty(harness.Adapter.ContinueCalls);
    }

    [Fact]
    public async Task SendQuestionToChannelAsync_ChannelInactive_WithOutboxContext_DeadLettersOutboxEntry()
    {
        var harness = GateAwareHarness.Build();
        harness.ConversationStore.ChannelActiveMap[(TenantId, "channel-dl-1")] = false;
        harness.ConversationStore.PreloadByChannelId[(TenantId, "channel-dl-1")] =
            NewChannelReference("ref-dl-chan-1", channelId: "channel-dl-1");
        var question = NewQuestion("Q-dl-chan-1", targetChannelId: "channel-dl-1");

        const string OutboxEntryId = "outbox-q-chan-1";
        using (ProactiveSendContext.WithOutboxEntryId(OutboxEntryId))
        {
            await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
                harness.Notifier.SendQuestionToChannelAsync(TenantId, "channel-dl-1", question, CancellationToken.None));
        }

        var dl = Assert.Single(harness.Outbox.DeadLettered);
        Assert.Equal(OutboxEntryId, dl.OutboxEntryId);
        Assert.Contains("channel-dl-1", dl.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendProactiveAsync_UserInactive_WithOutboxContext_DeadLettersOutboxEntry()
    {
        var harness = GateAwareHarness.Build();
        harness.ConversationStore.UserActiveMap[(TenantId, "user-dl-m-1")] = false;
        harness.ConversationStore.PreloadByInternalUserId[(TenantId, "user-dl-m-1")] =
            NewPersonalReference("ref-dl-m-1", aadObjectId: "aad-dl-m-1", internalUserId: "user-dl-m-1");
        var message = new MessengerMessage(
            MessageId: "msg-dl-1",
            CorrelationId: "corr-dl-m-1",
            AgentId: "agent-build",
            TaskId: "task-42",
            ConversationId: "convo-dl-1",
            Body: "ping",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        const string OutboxEntryId = "outbox-msg-user-1";
        using (ProactiveSendContext.WithOutboxEntryId(OutboxEntryId))
        {
            await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
                harness.Notifier.SendProactiveAsync(TenantId, "user-dl-m-1", message, CancellationToken.None));
        }

        var dl = Assert.Single(harness.Outbox.DeadLettered);
        Assert.Equal(OutboxEntryId, dl.OutboxEntryId);
        Assert.Contains("user-dl-m-1", dl.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendToChannelAsync_ChannelInactive_WithOutboxContext_DeadLettersOutboxEntry()
    {
        var harness = GateAwareHarness.Build();
        harness.ConversationStore.ChannelActiveMap[(TenantId, "channel-dl-m-1")] = false;
        harness.ConversationStore.PreloadByChannelId[(TenantId, "channel-dl-m-1")] =
            NewChannelReference("ref-dl-chan-m-1", channelId: "channel-dl-m-1");
        var message = new MessengerMessage(
            MessageId: "msg-dl-chan-1",
            CorrelationId: "corr-dl-chan-m-1",
            AgentId: "agent-build",
            TaskId: "task-42",
            ConversationId: "convo-dl-chan-1",
            Body: "ping",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        const string OutboxEntryId = "outbox-msg-chan-1";
        using (ProactiveSendContext.WithOutboxEntryId(OutboxEntryId))
        {
            await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
                harness.Notifier.SendToChannelAsync(TenantId, "channel-dl-m-1", message, CancellationToken.None));
        }

        var dl = Assert.Single(harness.Outbox.DeadLettered);
        Assert.Equal(OutboxEntryId, dl.OutboxEntryId);
        Assert.Contains("channel-dl-m-1", dl.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendProactiveAsync_UserInactive_WithoutOutboxContext_SkipsDeadLetterButStillAudits()
    {
        // Sanity check: WHEN the caller did NOT wrap in ProactiveSendContext (i.e. a
        // direct caller outside the outbox), the gate must still emit the audit row but
        // skip the outbox dead-letter — confirming the AsyncLocal envelope is the
        // ONLY plumbing path and old behaviour is preserved for direct callers.
        var harness = GateAwareHarness.Build();
        harness.ConversationStore.UserActiveMap[(TenantId, "user-direct-1")] = false;
        harness.ConversationStore.PreloadByInternalUserId[(TenantId, "user-direct-1")] =
            NewPersonalReference("ref-direct-1", aadObjectId: "aad-direct-1", internalUserId: "user-direct-1");
        var message = new MessengerMessage(
            MessageId: "msg-direct-1",
            CorrelationId: "corr-direct-1",
            AgentId: "agent-build",
            TaskId: "task-42",
            ConversationId: "convo-direct-1",
            Body: "ping",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            harness.Notifier.SendProactiveAsync(TenantId, "user-direct-1", message, CancellationToken.None));

        Assert.Empty(harness.Outbox.DeadLettered);
        Assert.Single(harness.AuditLogger.Entries);
    }

    // ---- Test helpers ------------------------------------------------------------

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

    private static TeamsConversationReference NewChannelReference(
        string id,
        string channelId,
        string conversationId = ChannelConversationId)
    {
        var bfReference = new ConversationReference
        {
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            Bot = new ChannelAccount(id: MicrosoftAppId, name: "AgentBot"),
            Conversation = new ConversationAccount(id: conversationId) { TenantId = TenantId, ConversationType = "channel" },
        };

        return new TeamsConversationReference
        {
            Id = id,
            TenantId = TenantId,
            ChannelId = channelId,
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
            Body = "Build #42 finished. Promote to staging environment?",
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

    /// <summary>
    /// Gate-aware harness that resolves <see cref="TeamsProactiveNotifier"/>'s 9-arg
    /// production constructor with a non-null <see cref="InstallationStateGate"/>.
    /// </summary>
    private sealed record GateAwareHarness(
        TeamsProactiveNotifier Notifier,
        TeamsProactiveNotifierTests.RecordingCloudAdapter Adapter,
        InstallationGateAwareConversationReferenceStore ConversationStore,
        TeamsProactiveNotifierTests.RecordingAgentQuestionStore AgentQuestionStore,
        TeamsProactiveNotifierTests.RecordingCardStateStore CardStateStore,
        RecordingAuditLogger AuditLogger,
        RecordingMessageOutbox Outbox,
        TeamsMessagingOptions Options)
    {
        public static GateAwareHarness Build()
        {
            var adapter = new TeamsProactiveNotifierTests.RecordingCloudAdapter();
            var options = new TeamsMessagingOptions { MicrosoftAppId = MicrosoftAppId };
            var convStore = new InstallationGateAwareConversationReferenceStore();
            var qStore = new TeamsProactiveNotifierTests.RecordingAgentQuestionStore();
            var cardStore = new TeamsProactiveNotifierTests.RecordingCardStateStore();
            var renderer = new AdaptiveCardBuilder();
            var auditLogger = new RecordingAuditLogger();
            var outbox = new RecordingMessageOutbox();
            var gate = new InstallationStateGate(
                convStore,
                outbox,
                auditLogger,
                NullLogger<InstallationStateGate>.Instance);

            var notifier = new TeamsProactiveNotifier(
                adapter,
                options,
                convStore,
                renderer,
                cardStore,
                qStore,
                NullLogger<TeamsProactiveNotifier>.Instance,
                TimeProvider.System,
                installationStateGate: gate);

            return new GateAwareHarness(notifier, adapter, convStore, qStore, cardStore, auditLogger, outbox, options);
        }
    }

    /// <summary>
    /// Hybrid <see cref="IConversationReferenceStore"/> that serves BOTH the notifier's
    /// reference-lookup path (preloaded by the test) and the gate-probe path
    /// (<c>IsActiveBy*Async</c> driven by <c>UserActiveMap</c> / <c>ChannelActiveMap</c>).
    /// Matches the production behaviour where a single
    /// <see cref="AgentSwarm.Messaging.Teams.EntityFrameworkCore.SqlConversationReferenceStore"/>
    /// singleton serves both contracts.
    /// </summary>
    /// <remarks>
    /// Stage 5.1 iter-5 evaluator feedback item 2 — the <c>GetByInternalUserIdAsync</c> /
    /// <c>GetByChannelIdAsync</c> getters now FILTER by the same active map that drives
    /// the gate probe. This mirrors the real
    /// <c>SqlConversationReferenceStore</c> which applies <c>e.IsActive</c> in its WHERE
    /// clause (see <c>SqlConversationReferenceStore.cs:248</c>) and returns null for
    /// inactive rows. Without this filter, tests that preload an inactive row would
    /// observe behaviour the real store cannot reproduce, MASKING regressions where the
    /// gate check has been re-ordered AFTER the active-only lookup.
    /// </remarks>
    public sealed class InstallationGateAwareConversationReferenceStore : IConversationReferenceStore
    {
        public Dictionary<(string TenantId, string InternalUserId), TeamsConversationReference> PreloadByInternalUserId { get; } = new();
        public Dictionary<(string TenantId, string ChannelId), TeamsConversationReference> PreloadByChannelId { get; } = new();
        public List<(string TenantId, string InternalUserId)> InternalUserLookups { get; } = new();
        public List<(string TenantId, string ChannelId)> ChannelLookups { get; } = new();

        public Dictionary<(string TenantId, string InternalUserId), bool> UserActiveMap { get; } = new();
        public Dictionary<(string TenantId, string ChannelId), bool> ChannelActiveMap { get; } = new();
        public List<(string TenantId, string InternalUserId)> UserProbeCalls { get; } = new();
        public List<(string TenantId, string ChannelId)> ChannelProbeCalls { get; } = new();

        public Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct) => Task.CompletedTask;
        public Task<TeamsConversationReference?> GetAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);
        public Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);

        public Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
        {
            InternalUserLookups.Add((tenantId, internalUserId));
            // Stage 5.1 iter-5 evaluator feedback item 2 — mirror the SQL store's
            // `e.IsActive` filter. Inactive (or unmapped) targets return null even if a
            // row was preloaded; the real store cannot expose an inactive row through
            // this getter either.
            UserActiveMap.TryGetValue((tenantId, internalUserId), out var active);
            if (!active)
            {
                return Task.FromResult<TeamsConversationReference?>(null);
            }

            PreloadByInternalUserId.TryGetValue((tenantId, internalUserId), out var hit);
            return Task.FromResult<TeamsConversationReference?>(hit);
        }

        public Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct)
        {
            ChannelLookups.Add((tenantId, channelId));
            // Stage 5.1 iter-5 evaluator feedback item 2 — mirror the SQL store's
            // `e.IsActive` filter. Inactive targets return null even if preloaded.
            ChannelActiveMap.TryGetValue((tenantId, channelId), out var active);
            if (!active)
            {
                return Task.FromResult<TeamsConversationReference?>(null);
            }

            PreloadByChannelId.TryGetValue((tenantId, channelId), out var hit);
            return Task.FromResult<TeamsConversationReference?>(hit);
        }

        public Task<IReadOnlyList<TeamsConversationReference>> GetActiveChannelsByTeamIdAsync(string tenantId, string teamId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TeamsConversationReference>>(Array.Empty<TeamsConversationReference>());

        public Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TeamsConversationReference>>(Array.Empty<TeamsConversationReference>());

        public Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
        {
            UserProbeCalls.Add((tenantId, internalUserId));
            UserActiveMap.TryGetValue((tenantId, internalUserId), out var active);
            return Task.FromResult(active);
        }

        public Task<bool> IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct)
        {
            ChannelProbeCalls.Add((tenantId, channelId));
            ChannelActiveMap.TryGetValue((tenantId, channelId), out var active);
            return Task.FromResult(active);
        }

        public Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.CompletedTask;
    }
}
