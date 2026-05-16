using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Security;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentSwarm.Messaging.Teams.Tests.Security.SecurityTestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests.Security;

public sealed class InstallationStateGateTests
{
    private const string Tenant = "tenant-1";
    private const string InternalUserId = "internal-dave";
    private const string ChannelId = "channel-general";

    [Fact]
    public async Task CheckAsync_UserScopedActive_ReturnsActive_NoDeadLetter_NoAudit()
    {
        var store = new StubConversationReferenceStore();
        store.UserActiveMap[(Tenant, InternalUserId)] = true;
        var outbox = new RecordingMessageOutbox();
        var audit = new RecordingAuditLogger();
        var gate = BuildGate(store, outbox, audit);
        var question = NewUserQuestion();

        var result = await gate.CheckAsync(question, "outbox-1", "corr-1", CancellationToken.None);

        Assert.True(result.IsActive);
        Assert.Empty(outbox.DeadLettered);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task CheckAsync_UserScopedInactive_DeadLetters_AuditsErrorFailed()
    {
        var store = new StubConversationReferenceStore();
        store.UserActiveMap[(Tenant, InternalUserId)] = false;
        var outbox = new RecordingMessageOutbox();
        var audit = new RecordingAuditLogger();
        var gate = BuildGate(store, outbox, audit);
        var question = NewUserQuestion();

        var result = await gate.CheckAsync(question, "outbox-1", "corr-1", CancellationToken.None);

        Assert.False(result.IsActive);
        Assert.Single(outbox.DeadLettered);
        Assert.Equal("outbox-1", outbox.DeadLettered[0].OutboxEntryId);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditEventTypes.Error, entry.EventType);
        Assert.Equal(AuditOutcomes.Failed, entry.Outcome);
        Assert.Equal("InstallationGateRejected", entry.Action);
        Assert.Equal(AuditActorTypes.Agent, entry.ActorType);
        Assert.Equal(question.AgentId, entry.ActorId);
        Assert.Equal(question.TenantId, entry.TenantId);
        Assert.Equal(question.AgentId, entry.AgentId);
        Assert.Equal(question.TaskId, entry.TaskId);
        Assert.Contains("user", entry.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(InternalUserId, entry.PayloadJson);
    }

    [Fact]
    public async Task CheckAsync_ChannelScopedActive_ReturnsActive_NoSideEffects()
    {
        var store = new StubConversationReferenceStore();
        store.ChannelActiveMap[(Tenant, ChannelId)] = true;
        var outbox = new RecordingMessageOutbox();
        var audit = new RecordingAuditLogger();
        var gate = BuildGate(store, outbox, audit);
        var question = NewChannelQuestion();

        var result = await gate.CheckAsync(question, "outbox-x", "corr-x", CancellationToken.None);

        Assert.True(result.IsActive);
        Assert.Empty(outbox.DeadLettered);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task CheckAsync_ChannelScopedInactive_DeadLettersAndAudits()
    {
        var store = new StubConversationReferenceStore();
        store.ChannelActiveMap[(Tenant, ChannelId)] = false;
        var outbox = new RecordingMessageOutbox();
        var audit = new RecordingAuditLogger();
        var gate = BuildGate(store, outbox, audit);
        var question = NewChannelQuestion();

        var result = await gate.CheckAsync(question, "outbox-c", "corr-c", CancellationToken.None);

        Assert.False(result.IsActive);
        Assert.Single(outbox.DeadLettered);
        Assert.Equal("outbox-c", outbox.DeadLettered[0].OutboxEntryId);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditEventTypes.Error, entry.EventType);
        Assert.Equal(AuditOutcomes.Failed, entry.Outcome);
        Assert.Contains("Channel", entry.PayloadJson);
        Assert.Contains(ChannelId, entry.PayloadJson);
    }

    [Fact]
    public async Task CheckAsync_MissingUserReference_DeadLettersAndAudits()
    {
        var store = new StubConversationReferenceStore(); // no entry — IsActive returns false
        var outbox = new RecordingMessageOutbox();
        var audit = new RecordingAuditLogger();
        var gate = BuildGate(store, outbox, audit);
        var question = NewUserQuestion();

        var result = await gate.CheckAsync(question, "outbox-missing", "corr-m", CancellationToken.None);

        Assert.False(result.IsActive);
        Assert.Single(outbox.DeadLettered);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task CheckAsync_UsesIsActiveByInternalUserIdAsync_NotReverseLookup()
    {
        var store = new StubConversationReferenceStore();
        store.UserActiveMap[(Tenant, InternalUserId)] = true;
        var gate = BuildGate(store, new RecordingMessageOutbox(), new RecordingAuditLogger());
        var question = NewUserQuestion();

        await gate.CheckAsync(question, "outbox", "corr", CancellationToken.None);

        Assert.Single(store.UserProbeCalls);
        Assert.Equal(Tenant, store.UserProbeCalls[0].TenantId);
        Assert.Equal(InternalUserId, store.UserProbeCalls[0].InternalUserId);
        Assert.Empty(store.ChannelProbeCalls);
    }

    [Fact]
    public async Task CheckAsync_UsesIsActiveByChannelAsync_ForChannelTarget()
    {
        var store = new StubConversationReferenceStore();
        store.ChannelActiveMap[(Tenant, ChannelId)] = true;
        var gate = BuildGate(store, new RecordingMessageOutbox(), new RecordingAuditLogger());
        var question = NewChannelQuestion();

        await gate.CheckAsync(question, "outbox", "corr", CancellationToken.None);

        Assert.Single(store.ChannelProbeCalls);
        Assert.Equal(ChannelId, store.ChannelProbeCalls[0].ChannelId);
        Assert.Empty(store.UserProbeCalls);
    }

    [Fact]
    public async Task CheckAsync_QuestionWithNeitherUserNorChannel_Throws()
    {
        var gate = BuildGate(new StubConversationReferenceStore(), new RecordingMessageOutbox(), new RecordingAuditLogger());
        var malformedQuestion = NewUserQuestion() with { TargetUserId = null, TargetChannelId = null };

        await Assert.ThrowsAsync<ArgumentException>(
            () => gate.CheckAsync(malformedQuestion, "outbox", "corr", CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_NullArguments_Throw()
    {
        var gate = BuildGate(new StubConversationReferenceStore(), new RecordingMessageOutbox(), new RecordingAuditLogger());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => gate.CheckAsync(null!, "outbox", "corr", CancellationToken.None));

        // outboxEntryId is intentionally nullable (Stage 5.1 iter-2 — the gate is also
        // invoked from synchronous/proactive send paths that have no outbox entry to
        // dead-letter against; the gate skips the IMessageOutbox.DeadLetterAsync call
        // and still emits the audit row). The call below must NOT throw.
        var nullEntry = await gate.CheckAsync(NewUserQuestion(), null, "corr", CancellationToken.None);
        Assert.NotNull(nullEntry);
    }

    [Fact]
    public async Task CheckAsync_RejectionEmptyCorrelationId_GeneratesGuid()
    {
        var store = new StubConversationReferenceStore();
        var outbox = new RecordingMessageOutbox();
        var audit = new RecordingAuditLogger();
        var gate = BuildGate(store, outbox, audit);
        var question = NewUserQuestion();

        await gate.CheckAsync(question, "outbox", string.Empty, CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.False(string.IsNullOrEmpty(entry.CorrelationId));
        Assert.True(Guid.TryParseExact(entry.CorrelationId, "D", out _));
    }

    [Fact]
    public async Task CheckAsync_OutboxDeadLetterThrows_ThrowsComplianceException_AfterEmittingAudit()
    {
        var store = new StubConversationReferenceStore();
        var outbox = new RecordingMessageOutbox
        {
            DeadLetterThrow = new InvalidOperationException("outbox-down"),
        };
        var audit = new RecordingAuditLogger();
        var gate = BuildGate(store, outbox, audit);
        var question = NewUserQuestion();

        // Stage 5.1 iter-4 evaluator feedback item 4 — when the outbox dead-letter sink
        // fails, the gate MUST fail closed (throw) so the caller does not silently
        // proceed without a durable rejection record. The audit row is still emitted
        // first (fan-out runs both sinks before throwing), so compliance evidence is
        // preserved even when one sink is degraded.
        var ex = await Assert.ThrowsAsync<InstallationStateGateComplianceException>(
            () => gate.CheckAsync(question, "outbox-flaky", "corr-f", CancellationToken.None));

        Assert.Contains("outbox dead-letter failed", ex.Message);
        Assert.Single(outbox.DeadLettered);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task CheckAsync_AuditLoggerThrows_ThrowsComplianceException_AfterDeadLetter()
    {
        var store = new StubConversationReferenceStore();
        var outbox = new RecordingMessageOutbox();
        var audit = new RecordingAuditLogger
        {
            Throw = new InvalidOperationException("audit-down"),
        };
        var gate = BuildGate(store, outbox, audit);
        var question = NewUserQuestion();

        // Stage 5.1 iter-4 evaluator feedback item 4 — symmetrical to the dead-letter
        // failure above: audit failure ALSO fails the gate closed. Dead-letter still
        // runs (fan-out semantics) so the outbox row is correctly terminated even when
        // the audit store is down.
        var ex = await Assert.ThrowsAsync<InstallationStateGateComplianceException>(
            () => gate.CheckAsync(question, "outbox-flaky", "corr-f", CancellationToken.None));

        Assert.Contains("audit logger failed", ex.Message);
        Assert.Single(outbox.DeadLettered);
    }

    [Fact]
    public async Task CheckAsync_BothSinksThrow_ThrowsComplianceException_WithBothFailures()
    {
        var store = new StubConversationReferenceStore();
        var outbox = new RecordingMessageOutbox
        {
            DeadLetterThrow = new InvalidOperationException("outbox-down"),
        };
        var audit = new RecordingAuditLogger
        {
            Throw = new InvalidOperationException("audit-down"),
        };
        var gate = BuildGate(store, outbox, audit);
        var question = NewUserQuestion();

        var ex = await Assert.ThrowsAsync<InstallationStateGateComplianceException>(
            () => gate.CheckAsync(question, "outbox-flaky", "corr-f", CancellationToken.None));

        Assert.Contains("audit logger and outbox dead-letter failed", ex.Message);
        Assert.IsType<AggregateException>(ex.InnerException);
        Assert.Equal(2, ((AggregateException)ex.InnerException!).InnerExceptions.Count);
    }

    [Fact]
    public async Task CheckTargetAsync_UserActive_ReturnsActive_NoSideEffects()
    {
        var store = new StubConversationReferenceStore();
        store.UserActiveMap[(Tenant, InternalUserId)] = true;
        var outbox = new RecordingMessageOutbox();
        var audit = new RecordingAuditLogger();
        var gate = BuildGate(store, outbox, audit);

        var result = await gate.CheckTargetAsync(
            tenantId: Tenant,
            userId: InternalUserId,
            channelId: null,
            correlationId: "corr-target-1",
            outboxEntryId: null,
            cancellationToken: CancellationToken.None);

        Assert.True(result.IsActive);
        Assert.Empty(outbox.DeadLettered);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task CheckTargetAsync_UserInactive_AuditsAndDeadLetters()
    {
        var store = new StubConversationReferenceStore();
        store.UserActiveMap[(Tenant, InternalUserId)] = false;
        var outbox = new RecordingMessageOutbox();
        var audit = new RecordingAuditLogger();
        var gate = BuildGate(store, outbox, audit);

        var result = await gate.CheckTargetAsync(
            tenantId: Tenant,
            userId: InternalUserId,
            channelId: null,
            correlationId: "corr-target-2",
            outboxEntryId: "outbox-t",
            cancellationToken: CancellationToken.None);

        Assert.False(result.IsActive);
        Assert.Single(outbox.DeadLettered);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("InstallationGateRejected", entry.Action);
        // Synthetic question marker is preserved so audit reviewers can distinguish
        // MessengerMessage gate rejections from real AgentQuestion rejections.
        Assert.Contains("messenger-message::", entry.PayloadJson);
    }

    [Fact]
    public async Task CheckTargetAsync_ChannelInactive_AuditsAndDeadLetters()
    {
        var store = new StubConversationReferenceStore();
        store.ChannelActiveMap[(Tenant, ChannelId)] = false;
        var outbox = new RecordingMessageOutbox();
        var audit = new RecordingAuditLogger();
        var gate = BuildGate(store, outbox, audit);

        var result = await gate.CheckTargetAsync(
            tenantId: Tenant,
            userId: null,
            channelId: ChannelId,
            correlationId: "corr-target-3",
            outboxEntryId: null,
            cancellationToken: CancellationToken.None);

        Assert.False(result.IsActive);
        Assert.Empty(outbox.DeadLettered); // outboxEntryId was null
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("InstallationGateRejected", entry.Action);
        Assert.Contains("Channel", entry.PayloadJson);
        Assert.Contains(ChannelId, entry.PayloadJson);
    }

    [Fact]
    public async Task CheckTargetAsync_BothUserAndChannel_Throws()
    {
        var gate = BuildGate(new StubConversationReferenceStore(), new RecordingMessageOutbox(), new RecordingAuditLogger());

        await Assert.ThrowsAsync<ArgumentException>(
            () => gate.CheckTargetAsync(Tenant, InternalUserId, ChannelId, "corr", null, CancellationToken.None));
    }

    [Fact]
    public async Task CheckTargetAsync_NeitherUserNorChannel_Throws()
    {
        var gate = BuildGate(new StubConversationReferenceStore(), new RecordingMessageOutbox(), new RecordingAuditLogger());

        await Assert.ThrowsAsync<ArgumentException>(
            () => gate.CheckTargetAsync(Tenant, null, null, "corr", null, CancellationToken.None));
    }

    [Fact]
    public void Constructor_NullArgs_Throw()
    {
        var store = new StubConversationReferenceStore();
        var outbox = new RecordingMessageOutbox();
        var audit = new RecordingAuditLogger();

        Assert.Throws<ArgumentNullException>(
            () => new InstallationStateGate(null!, outbox, audit, NullLogger<InstallationStateGate>.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new InstallationStateGate(store, null!, audit, NullLogger<InstallationStateGate>.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new InstallationStateGate(store, outbox, null!, NullLogger<InstallationStateGate>.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new InstallationStateGate(store, outbox, audit, null!));
    }

    private static InstallationStateGate BuildGate(
        StubConversationReferenceStore store,
        RecordingMessageOutbox outbox,
        RecordingAuditLogger audit)
    {
        return new InstallationStateGate(
            store,
            outbox,
            audit,
            NullLogger<InstallationStateGate>.Instance);
    }

    private static AgentQuestion NewUserQuestion()
        => new()
        {
            QuestionId = "Q-1001",
            AgentId = "agent-rev",
            TaskId = "task-1",
            TenantId = Tenant,
            TargetUserId = InternalUserId,
            Title = "Approve?",
            Body = "Please approve",
            Severity = MessageSeverities.Info,
            AllowedActions = new[] { new HumanAction("approve", "Approve", "approve", false) },
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            CorrelationId = "corr-1",
        };

    private static AgentQuestion NewChannelQuestion()
        => new()
        {
            QuestionId = "Q-1002",
            AgentId = "agent-rev",
            TaskId = "task-1",
            TenantId = Tenant,
            TargetChannelId = ChannelId,
            Title = "Approve?",
            Body = "Please approve",
            Severity = MessageSeverities.Info,
            AllowedActions = new[] { new HumanAction("approve", "Approve", "approve", false) },
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            CorrelationId = "corr-2",
        };
}
