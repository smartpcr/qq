using System.Runtime.CompilerServices;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using FluentAssertions;

namespace AgentSwarm.Messaging.Tests.Contracts;

/// <summary>
/// Compile-time contract checks for the remaining Stage 1.3 interfaces.
/// Each fake is a minimal in-process implementation: the goal is for the file
/// to compile (proving the interface signatures are usable) and for the
/// invocations to round-trip the args back out for assertion.
/// </summary>
public class InterfaceContractCompileTests
{
    // ---- IOutboundQueue ----

    private sealed class FakeOutboundQueue : IOutboundQueue
    {
        public List<OutboundMessage> Enqueued { get; } = new();
        public List<(Guid Id, long PlatformMessageId)> Sent { get; } = new();
        public List<(Guid Id, string Error)> Failed { get; } = new();
        public List<Guid> DeadLettered { get; } = new();
        public Dictionary<MessageSeverity, int> PendingCounts { get; } = new();
        public Dictionary<MessageSeverity, IReadOnlyList<OutboundMessage>> Batches { get; } = new();

        public Task EnqueueAsync(OutboundMessage message, CancellationToken ct)
        {
            Enqueued.Add(message);
            return Task.CompletedTask;
        }

        public Task<OutboundMessage?> DequeueAsync(CancellationToken ct) =>
            Task.FromResult<OutboundMessage?>(Enqueued.FirstOrDefault());

        public Task MarkSentAsync(Guid messageId, long platformMessageId, CancellationToken ct)
        {
            Sent.Add((messageId, platformMessageId));
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid messageId, string error, CancellationToken ct)
        {
            Failed.Add((messageId, error));
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(Guid messageId, CancellationToken ct)
        {
            DeadLettered.Add(messageId);
            return Task.CompletedTask;
        }

        public Task<int> CountPendingAsync(MessageSeverity severity, CancellationToken ct) =>
            Task.FromResult(PendingCounts.TryGetValue(severity, out var c) ? c : 0);

        public Task<IReadOnlyList<OutboundMessage>> DequeueBatchAsync(
            MessageSeverity severity, int maxCount, CancellationToken ct) =>
            Task.FromResult(Batches.TryGetValue(severity, out var b)
                ? (IReadOnlyList<OutboundMessage>)b.Take(maxCount).ToArray()
                : Array.Empty<OutboundMessage>());
    }

    [Fact]
    public async Task OutboundQueue_AllMethodsAreCallable()
    {
        IOutboundQueue queue = new FakeOutboundQueue();
        var msg = NewMessage(MessageSeverity.Critical);

        await queue.EnqueueAsync(msg, CancellationToken.None);
        var dequeued = await queue.DequeueAsync(CancellationToken.None);
        await queue.MarkSentAsync(msg.MessageId, 999L, CancellationToken.None);
        await queue.MarkFailedAsync(msg.MessageId, "boom", CancellationToken.None);
        await queue.DeadLetterAsync(msg.MessageId, CancellationToken.None);
        var count = await queue.CountPendingAsync(MessageSeverity.Low, CancellationToken.None);
        var batch = await queue.DequeueBatchAsync(MessageSeverity.Low, 10, CancellationToken.None);

        dequeued.Should().NotBeNull();
        count.Should().Be(0);
        batch.Should().NotBeNull().And.BeEmpty();
    }

    private static OutboundMessage NewMessage(MessageSeverity severity) =>
        new(
            MessageId: Guid.NewGuid(),
            IdempotencyKey: "q:a:Q",
            ChatId: 1L,
            Severity: severity,
            Status: OutboundMessageStatus.Pending,
            SourceType: OutboundMessageSource.Question,
            Payload: "p",
            SourceEnvelopeJson: null,
            SourceId: "Q",
            AttemptCount: 0,
            MaxAttempts: OutboundMessage.DefaultMaxAttempts,
            NextRetryAt: null,
            PlatformMessageId: null,
            CorrelationId: "trace",
            CreatedAt: DateTimeOffset.UnixEpoch,
            SentAt: null,
            ErrorDetail: null);

    // ---- IAuditLogger ----

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public List<AuditEntry> Generic { get; } = new();
        public List<HumanResponseAuditEntry> HumanResponses { get; } = new();

        public Task LogAsync(AuditEntry entry, CancellationToken ct)
        {
            Generic.Add(entry);
            return Task.CompletedTask;
        }

        public Task LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken ct)
        {
            HumanResponses.Add(entry);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AuditLogger_BothMethodsAreCallable()
    {
        IAuditLogger logger = new FakeAuditLogger();

        await logger.LogAsync(
            new AuditEntry("Discord", "u", "m", "{}", DateTimeOffset.UnixEpoch, "trace"),
            CancellationToken.None);

        await logger.LogHumanResponseAsync(
            new HumanResponseAuditEntry(
                "Discord", "u", "m", "Q-1", "approve", "approve", null, "{}", DateTimeOffset.UnixEpoch, "trace"),
            CancellationToken.None);
    }

    // ---- IDeduplicationService ----

    private sealed class FakeDedup : IDeduplicationService
    {
        public HashSet<string> Reserved { get; } = new();
        public HashSet<string> Processed { get; } = new();

        public Task<bool> TryReserveAsync(string eventId) =>
            Task.FromResult(Reserved.Add(eventId));

        public Task<bool> IsProcessedAsync(string eventId) =>
            Task.FromResult(Processed.Contains(eventId));

        public Task MarkProcessedAsync(string eventId)
        {
            Processed.Add(eventId);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DeduplicationService_FirstReserveWins_SecondLoses()
    {
        IDeduplicationService dedup = new FakeDedup();

        var first = await dedup.TryReserveAsync("evt-1");
        var second = await dedup.TryReserveAsync("evt-1");
        await dedup.MarkProcessedAsync("evt-1");
        var processed = await dedup.IsProcessedAsync("evt-1");

        first.Should().BeTrue();
        second.Should().BeFalse();
        processed.Should().BeTrue();
    }

    // ---- IPendingQuestionStore ----

    private sealed class FakePendingQuestionStore : IPendingQuestionStore
    {
        public Dictionary<string, PendingQuestion> Store { get; } = new();

        public Task StoreAsync(
            AgentQuestionEnvelope envelope, long channelId, long platformMessageId, CancellationToken ct)
        {
            var pq = new PendingQuestion(
                QuestionId: envelope.Question.QuestionId,
                Question: envelope.Question,
                ChannelId: channelId,
                PlatformMessageId: platformMessageId,
                ThreadId: null,
                DefaultActionId: envelope.ProposedDefaultActionId,
                DefaultActionValue: null,
                ExpiresAt: envelope.Question.ExpiresAt,
                Status: PendingQuestionStatus.Pending,
                SelectedActionId: null,
                SelectedActionValue: null,
                RespondentUserId: null,
                StoredAt: DateTimeOffset.UnixEpoch,
                CorrelationId: envelope.Question.CorrelationId);
            Store[pq.QuestionId] = pq;
            return Task.CompletedTask;
        }

        public Task<PendingQuestion?> GetAsync(string questionId, CancellationToken ct) =>
            Task.FromResult(Store.TryGetValue(questionId, out var pq) ? pq : null);

        public Task MarkAnsweredAsync(string questionId, CancellationToken ct)
        {
            Store[questionId] = Store[questionId] with { Status = PendingQuestionStatus.Answered };
            return Task.CompletedTask;
        }

        public Task MarkAwaitingCommentAsync(string questionId, CancellationToken ct)
        {
            Store[questionId] = Store[questionId] with { Status = PendingQuestionStatus.AwaitingComment };
            return Task.CompletedTask;
        }

        public Task RecordSelectionAsync(
            string questionId, string selectedActionId, string selectedActionValue,
            long respondentUserId, CancellationToken ct)
        {
            Store[questionId] = Store[questionId] with
            {
                SelectedActionId = selectedActionId,
                SelectedActionValue = selectedActionValue,
                RespondentUserId = respondentUserId,
            };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PendingQuestion>> GetExpiredAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PendingQuestion>>(
                Store.Values
                    .Where(p => p.Status == PendingQuestionStatus.Pending
                                || p.Status == PendingQuestionStatus.AwaitingComment)
                    .ToArray());
    }

    [Fact]
    public async Task PendingQuestionStore_StoreThenLifecycleTransitions()
    {
        IPendingQuestionStore store = new FakePendingQuestionStore();
        var envelope = new AgentQuestionEnvelope(
            Question: new AgentQuestion(
                QuestionId: "Q-1",
                AgentId: "a",
                TaskId: "t",
                Title: "title",
                Body: "body",
                Severity: MessageSeverity.Normal,
                AllowedActions: new[] { new HumanAction("ok", "OK", "ok", false) },
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
                CorrelationId: "trace"),
            ProposedDefaultActionId: "ok",
            RoutingMetadata: new Dictionary<string, string> { ["DiscordChannelId"] = "1" });

        await store.StoreAsync(envelope, channelId: 1L, platformMessageId: 99L, CancellationToken.None);
        await store.RecordSelectionAsync("Q-1", "ok", "ok", respondentUserId: 7L, CancellationToken.None);
        await store.MarkAnsweredAsync("Q-1", CancellationToken.None);

        var pq = await store.GetAsync("Q-1", CancellationToken.None);

        pq.Should().NotBeNull();
        pq!.Status.Should().Be(PendingQuestionStatus.Answered);
        pq.SelectedActionId.Should().Be("ok");
        pq.RespondentUserId.Should().Be(7L);
    }

    // ---- ISwarmCommandBus ----

    private sealed class FakeSwarmCommandBus : ISwarmCommandBus
    {
        public List<SwarmCommand> Commands { get; } = new();
        public List<HumanDecisionEvent> Decisions { get; } = new();
        public List<SwarmEvent> Stream { get; } = new();

        public Task PublishCommandAsync(SwarmCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }

        public Task PublishHumanDecisionAsync(HumanDecisionEvent decision, CancellationToken ct)
        {
            Decisions.Add(decision);
            return Task.CompletedTask;
        }

        public Task<SwarmStatusSummary> QueryStatusAsync(SwarmStatusQuery query, CancellationToken ct) =>
            Task.FromResult(new SwarmStatusSummary(TotalAgents: 10, ActiveTasks: 6, BlockedCount: 1));

        public Task<IReadOnlyList<AgentInfo>> QueryAgentsAsync(SwarmAgentsQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentInfo>>(new[]
            {
                new AgentInfo("agent-1", "Architect", "design", 0.8, null),
            });

        public async IAsyncEnumerable<SwarmEvent> SubscribeAsync(
            string tenantId, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var ev in Stream)
            {
                ct.ThrowIfCancellationRequested();
                yield return ev;
                await Task.Yield();
            }
        }
    }

    [Fact]
    public async Task SwarmCommandBus_PublishAndQueryAreCallable()
    {
        ISwarmCommandBus bus = new FakeSwarmCommandBus();

        await bus.PublishCommandAsync(
            new SwarmCommand(
                Guid.NewGuid(), "ask", "architect",
                new Dictionary<string, string> { ["topic"] = "cache" },
                "trace", DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        await bus.PublishHumanDecisionAsync(
            new HumanDecisionEvent(
                "Discord", "user-1", "msg-1", "Q-1", "approve", "approve",
                "trace", DateTimeOffset.UnixEpoch),
            CancellationToken.None);

        var summary = await bus.QueryStatusAsync(new SwarmStatusQuery("tenant", null), CancellationToken.None);
        var agents = await bus.QueryAgentsAsync(new SwarmAgentsQuery("tenant", null), CancellationToken.None);

        summary.TotalAgents.Should().Be(10);
        summary.ActiveTasks.Should().Be(6);
        summary.BlockedCount.Should().Be(1);
        agents.Should().HaveCount(1);
    }

    [Fact]
    public async Task SwarmCommandBus_SubscribeAsync_YieldsEnqueuedEvents()
    {
        var bus = new FakeSwarmCommandBus();
        bus.Stream.Add(new SwarmEvent("AgentBlocked", "agent-1", "{}", "trace", DateTimeOffset.UnixEpoch));
        bus.Stream.Add(new SwarmEvent("ProgressUpdate", "agent-1", "{}", "trace", DateTimeOffset.UnixEpoch));

        var seen = new List<SwarmEvent>();
        await foreach (var ev in ((ISwarmCommandBus)bus).SubscribeAsync("tenant", CancellationToken.None))
        {
            seen.Add(ev);
        }

        seen.Should().HaveCount(2);
        seen[0].EventType.Should().Be("AgentBlocked");
        seen[1].EventType.Should().Be("ProgressUpdate");
    }

    // ---- IMessageSender ----

    private sealed class FakeMessageSender : IMessageSender
    {
        public Task<SendResult> SendTextAsync(long channelId, string text, CancellationToken ct) =>
            Task.FromResult(SendResult.Succeeded(channelId * 10));

        public Task<SendResult> SendQuestionAsync(
            long channelId, AgentQuestionEnvelope envelope, CancellationToken ct) =>
            Task.FromResult(SendResult.Succeeded(channelId * 100));

        public Task<SendResult> SendBatchAsync(
            long channelId, IReadOnlyList<OutboundMessage> messages, CancellationToken ct) =>
            messages.Count == 0
                ? Task.FromResult(SendResult.Failed("empty batch"))
                : Task.FromResult(SendResult.Succeeded(channelId * 1000));
    }

    [Fact]
    public async Task MessageSender_AllThreeMethodsAreCallable()
    {
        IMessageSender sender = new FakeMessageSender();

        var text = await sender.SendTextAsync(5L, "hi", CancellationToken.None);
        var question = await sender.SendQuestionAsync(
            5L,
            new AgentQuestionEnvelope(
                Question: new AgentQuestion(
                    "Q-1", "a", "t", "title", "body", MessageSeverity.High,
                    new[] { new HumanAction("ok", "OK", "ok", false) },
                    DateTimeOffset.UnixEpoch.AddHours(1), "trace"),
                ProposedDefaultActionId: null,
                RoutingMetadata: new Dictionary<string, string>()),
            CancellationToken.None);
        var batch = await sender.SendBatchAsync(
            5L,
            new[] { NewMessage(MessageSeverity.Low) },
            CancellationToken.None);

        text.Success.Should().BeTrue();
        text.PlatformMessageId.Should().Be(50L);
        question.PlatformMessageId.Should().Be(500L);
        batch.PlatformMessageId.Should().Be(5000L);
    }

    // ---- IUserAuthorizationService ----

    private sealed class FakeAuthService : IUserAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            string externalUserId, string? platformGroupId, string chatId,
            string commandName, CancellationToken ct) =>
            Task.FromResult(externalUserId == "denied"
                ? AuthorizationResult.Deny("blocked user")
                : AuthorizationResult.Allow(new GuildBinding(
                    Id: Guid.NewGuid(),
                    GuildId: 1UL,
                    ChannelId: 2UL,
                    ChannelPurpose: ChannelPurpose.Control,
                    TenantId: "tenant",
                    WorkspaceId: "workspace",
                    AllowedRoleIds: new ulong[] { 100UL },
                    CommandRestrictions: null,
                    RegisteredAt: DateTimeOffset.UnixEpoch,
                    IsActive: true)));
    }

    [Fact]
    public async Task AuthorizationService_AllowsAndDeniesPerInput()
    {
        IUserAuthorizationService svc = new FakeAuthService();

        var allow = await svc.AuthorizeAsync(
            externalUserId: "allowed",
            platformGroupId: "1",
            chatId: "2",
            commandName: "ask",
            CancellationToken.None);
        var deny = await svc.AuthorizeAsync(
            externalUserId: "denied",
            platformGroupId: "1",
            chatId: "2",
            commandName: "ask",
            CancellationToken.None);

        allow.IsAllowed.Should().BeTrue();
        allow.ResolvedBinding.Should().NotBeNull();
        deny.IsAllowed.Should().BeFalse();
        deny.DenialReason.Should().Be("blocked user");
    }
}
