using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Cards;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using RecordingAgentQuestionStore = AgentSwarm.Messaging.Teams.Tests.TeamsProactiveNotifierTests.RecordingAgentQuestionStore;
using RecordingAuditLogger = AgentSwarm.Messaging.Teams.Tests.TestDoubles.RecordingAuditLogger;
using RecordingCardStateStore = AgentSwarm.Messaging.Teams.Tests.TeamsProactiveNotifierTests.RecordingCardStateStore;
using RecordingConversationReferenceStore = AgentSwarm.Messaging.Teams.Tests.TeamsProactiveNotifierTests.RecordingConversationReferenceStore;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Stage 5.2 — validates that <see cref="TeamsProactiveNotifier"/> emits
/// <see cref="AuditEventTypes.ProactiveNotification"/> audit entries for every
/// proactive send path. Per <c>tech-spec.md</c> §4.3 the audit row carries the agent
/// identity (as both <see cref="AuditEntry.ActorId"/> and <see cref="AuditEntry.AgentId"/>),
/// tenant, correlation, conversation, and outcome so compliance reviewers can replay
/// every outbound notification end-to-end.
/// </summary>
public sealed class TeamsProactiveNotifierAuditTests
{
    private const string TenantId = "contoso-tenant-id";
    private const string MicrosoftAppId = "11111111-1111-1111-1111-111111111111";
    private const string PersonalConversationId = "19:conversation-bob";
    private const string ChannelConversationId = "19:channel-conv-devops";

    [Fact]
    public async Task SendProactiveAsync_HappyPath_EmitsProactiveNotificationAuditWithSuccess()
    {
        var harness = AuditHarness.Build();
        var stored = NewPersonalReference("ref-bob", aadObjectId: "aad-bob", internalUserId: "user-bob");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-bob")] = stored;

        var message = new MessengerMessage(
            MessageId: "msg-1",
            CorrelationId: "corr-msg-1",
            AgentId: "agent-build",
            TaskId: "task-7",
            ConversationId: "conv-1",
            Body: "Build #7 finished.",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        await harness.Notifier.SendProactiveAsync(TenantId, "user-bob", message, CancellationToken.None);

        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.ProactiveNotification, audit.EventType);
        Assert.Equal(AuditActorTypes.Agent, audit.ActorType);
        Assert.Equal("agent-build", audit.ActorId);
        Assert.Equal("agent-build", audit.AgentId);
        Assert.Equal(TenantId, audit.TenantId);
        Assert.Equal("task-7", audit.TaskId);
        Assert.Equal("corr-msg-1", audit.CorrelationId);
        Assert.Equal("send_message", audit.Action);
        Assert.Equal(AuditOutcomes.Success, audit.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(audit.Checksum));
        Assert.Contains("msg-1", audit.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("user-bob", audit.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendProactiveAsync_BotFrameworkThrows_EmitsFailedAuditAndRethrows()
    {
        var failingAdapter = new ThrowingCloudAdapter(new InvalidOperationException("upstream 500"));
        var harness = AuditHarness.Build(failingAdapter);
        var stored = NewPersonalReference("ref-x", aadObjectId: "aad-x", internalUserId: "user-x");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-x")] = stored;

        var message = new MessengerMessage(
            MessageId: "msg-fail",
            CorrelationId: "corr-fail",
            AgentId: "agent-build",
            TaskId: "task-fail",
            ConversationId: "conv-1",
            Body: "Body",
            Severity: MessageSeverities.Warning,
            Timestamp: DateTimeOffset.UtcNow);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Notifier.SendProactiveAsync(TenantId, "user-x", message, CancellationToken.None));

        Assert.Equal("upstream 500", ex.Message);
        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.ProactiveNotification, audit.EventType);
        Assert.Equal(AuditOutcomes.Failed, audit.Outcome);
        Assert.Equal("send_message", audit.Action);
        Assert.Contains("InvalidOperationException", audit.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendProactiveAsync_ReferenceMissing_EmitsFailedAudit()
    {
        var harness = AuditHarness.Build();
        var message = new MessengerMessage(
            MessageId: "msg-nope",
            CorrelationId: "corr-nope",
            AgentId: "agent-build",
            TaskId: "task-nope",
            ConversationId: "conv-1",
            Body: "Body",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(
            () => harness.Notifier.SendProactiveAsync(TenantId, "user-missing", message, CancellationToken.None));

        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.ProactiveNotification, audit.EventType);
        Assert.Equal(AuditOutcomes.Failed, audit.Outcome);
        Assert.Contains("ConversationReferenceNotFound", audit.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendToChannelAsync_HappyPath_EmitsProactiveNotificationAuditForChannel()
    {
        var harness = AuditHarness.Build();
        var stored = NewChannelReference("ref-chan", channelId: "19:channel-devops@thread.tacv2");
        harness.ConversationReferenceStore.PreloadByChannelId[(TenantId, "19:channel-devops@thread.tacv2")] = stored;

        var message = new MessengerMessage(
            MessageId: "msg-c",
            CorrelationId: "corr-c",
            AgentId: "agent-build",
            TaskId: "task-c",
            ConversationId: "conv-1",
            Body: "Channel body",
            Severity: MessageSeverities.Critical,
            Timestamp: DateTimeOffset.UtcNow);

        await harness.Notifier.SendToChannelAsync(TenantId, "19:channel-devops@thread.tacv2", message, CancellationToken.None);

        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.ProactiveNotification, audit.EventType);
        Assert.Equal(AuditOutcomes.Success, audit.Outcome);
        Assert.Equal("send_message", audit.Action);
        Assert.Contains("19:channel-devops@thread.tacv2", audit.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_HappyPath_EmitsSendCardAuditWithQuestionPayload()
    {
        var harness = AuditHarness.Build();
        var stored = NewPersonalReference("ref-alice", aadObjectId: "aad-alice", internalUserId: "user-alice");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-alice")] = stored;
        var question = NewQuestion("Q-audit-1", targetUserId: "user-alice");

        await harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-alice", question, CancellationToken.None);

        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.ProactiveNotification, audit.EventType);
        Assert.Equal(AuditActorTypes.Agent, audit.ActorType);
        Assert.Equal(question.AgentId, audit.ActorId);
        Assert.Equal(question.AgentId, audit.AgentId);
        Assert.Equal(question.TaskId, audit.TaskId);
        Assert.Equal(question.CorrelationId, audit.CorrelationId);
        Assert.Equal("send_card", audit.Action);
        Assert.Equal(AuditOutcomes.Success, audit.Outcome);
        Assert.Contains("Q-audit-1", audit.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("user-alice", audit.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_BotFrameworkThrows_EmitsFailedAuditAndRethrows()
    {
        var failingAdapter = new ThrowingCloudAdapter(new InvalidOperationException("connector 502"));
        var harness = AuditHarness.Build(failingAdapter);
        var stored = NewPersonalReference("ref-eve", aadObjectId: "aad-eve", internalUserId: "user-eve");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-eve")] = stored;
        var question = NewQuestion("Q-fail", targetUserId: "user-eve");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-eve", question, CancellationToken.None));

        Assert.Equal("connector 502", ex.Message);
        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.ProactiveNotification, audit.EventType);
        Assert.Equal(AuditOutcomes.Failed, audit.Outcome);
        Assert.Equal("send_card", audit.Action);
        Assert.Equal(question.CorrelationId, audit.CorrelationId);
    }

    [Fact]
    public async Task SendQuestionToChannelAsync_HappyPath_EmitsSendCardAuditForChannel()
    {
        var harness = AuditHarness.Build();
        var stored = NewChannelReference("ref-chan-q", channelId: "19:channel-questions@thread.tacv2");
        harness.ConversationReferenceStore.PreloadByChannelId[(TenantId, "19:channel-questions@thread.tacv2")] = stored;
        var question = NewQuestion("Q-channel-1", targetChannelId: "19:channel-questions@thread.tacv2");

        await harness.Notifier.SendQuestionToChannelAsync(
            TenantId, "19:channel-questions@thread.tacv2", question, CancellationToken.None);

        var audit = Assert.Single(harness.AuditLogger.Entries);
        Assert.Equal(AuditEventTypes.ProactiveNotification, audit.EventType);
        Assert.Equal(AuditOutcomes.Success, audit.Outcome);
        Assert.Equal("send_card", audit.Action);
        Assert.Contains("19:channel-questions@thread.tacv2", audit.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendProactiveQuestionAsync_ChecksumRecomputesFromStoredFields()
    {
        var harness = AuditHarness.Build();
        var stored = NewPersonalReference("ref-c", aadObjectId: "aad-c", internalUserId: "user-c");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-c")] = stored;
        var question = NewQuestion("Q-cksum", targetUserId: "user-c");

        await harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-c", question, CancellationToken.None);

        var audit = Assert.Single(harness.AuditLogger.Entries);
        var recomputed = AuditEntry.ComputeChecksum(
            timestamp: audit.Timestamp,
            correlationId: audit.CorrelationId,
            eventType: audit.EventType,
            actorId: audit.ActorId,
            actorType: audit.ActorType,
            tenantId: audit.TenantId,
            agentId: audit.AgentId,
            taskId: audit.TaskId,
            conversationId: audit.ConversationId,
            action: audit.Action,
            payloadJson: audit.PayloadJson,
            outcome: audit.Outcome);

        Assert.Equal(audit.Checksum, recomputed);
    }

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
            Conversation = new ConversationAccount(id: conversationId)
            {
                TenantId = TenantId,
                ConversationType = "channel",
            },
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
            AgentId = "agent-promote",
            TaskId = "task-99",
            TenantId = TenantId,
            TargetUserId = targetUserId,
            TargetChannelId = targetChannelId,
            Title = "Promote?",
            Body = "Promote?",
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

    private sealed record AuditHarness(
        TeamsProactiveNotifier Notifier,
        RecordingAuditLogger AuditLogger,
        RecordingConversationReferenceStore ConversationReferenceStore)
    {
        public static AuditHarness Build(CloudAdapter? adapter = null)
        {
            adapter ??= new CapturingCloudAdapter();
            var options = new TeamsMessagingOptions { MicrosoftAppId = MicrosoftAppId };
            var convStore = new RecordingConversationReferenceStore();
            var qStore = new RecordingAgentQuestionStore();
            var cardStore = new RecordingCardStateStore();
            var renderer = new AdaptiveCardBuilder();
            var auditLogger = new RecordingAuditLogger();
            var notifier = new TeamsProactiveNotifier(
                adapter,
                options,
                convStore,
                renderer,
                cardStore,
                qStore,
                NullLogger<TeamsProactiveNotifier>.Instance,
                TimeProvider.System,
                installationStateGate: null,
                auditLogger: auditLogger);
            return new AuditHarness(notifier, auditLogger, convStore);
        }
    }

    private sealed class CapturingCloudAdapter : CloudAdapter
    {
        public override Task ContinueConversationAsync(
            string botAppId,
            ConversationReference reference,
            BotCallbackHandler callback,
            CancellationToken cancellationToken)
        {
            var continuation = SynthesizeContinuationActivity(reference);
            var turnContext = new TurnContext(this, continuation);
            return callback(turnContext, cancellationToken);
        }

        private static Activity SynthesizeContinuationActivity(ConversationReference reference)
        {
            var activity = new Activity
            {
                Type = ActivityTypes.Event,
                ChannelId = reference.ChannelId,
                ServiceUrl = reference.ServiceUrl,
                Conversation = reference.Conversation,
                From = reference.Bot,
                Recipient = reference.User,
                Id = $"act-{Guid.NewGuid():N}",
            };
            return activity;
        }

        public override Task<ResourceResponse[]> SendActivitiesAsync(
            ITurnContext turnContext,
            Activity[] activities,
            CancellationToken cancellationToken)
        {
            var responses = activities
                .Select(a => new ResourceResponse(a.Id ?? $"sent-{Guid.NewGuid():N}"))
                .ToArray();
            return Task.FromResult(responses);
        }
    }

    private sealed class ThrowingCloudAdapter : CloudAdapter
    {
        private readonly Exception _exception;

        public ThrowingCloudAdapter(Exception exception)
        {
            _exception = exception;
        }

        public override Task ContinueConversationAsync(
            string botAppId,
            ConversationReference reference,
            BotCallbackHandler callback,
            CancellationToken cancellationToken)
        {
            return Task.FromException(_exception);
        }
    }
}
