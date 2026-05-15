using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Cards;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using static AgentSwarm.Messaging.Teams.Tests.TestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests.Cards;

/// <summary>
/// Tests for the concrete <see cref="CardActionHandler"/> implementation introduced in
/// Stage 3.3. Covers (a) the happy path — decision is published, card is updated with
/// the actor-attributed overload, success audit row is written; (b) every rejection
/// path — missing question, action not in <c>AllowedActions</c>, already-resolved
/// question, lost CAS race; (c) sanitised payload generation including comment redaction
/// and tolerance of card-state lookup failures.
/// </summary>
public sealed class CardActionHandlerTests
{
    private const string QuestionId = "q-handler-001";
    private const string TenantId = "tenant-z";
    private const string CorrelationId = "corr-handler-1";
    private const string ActorAad = "aad-user-z";

    private static AgentQuestion BuildOpenQuestion(
        string status = AgentQuestionStatuses.Open,
        params HumanAction[] actions)
    {
        if (actions.Length == 0)
        {
            actions = new[]
            {
                new HumanAction("a-approve", "Approve", "approve", false),
                new HumanAction("a-reject", "Reject", "reject", true),
            };
        }

        return new AgentQuestion
        {
            QuestionId = QuestionId,
            AgentId = "agent-svc",
            TaskId = "task-svc",
            TenantId = TenantId,
            TargetUserId = "user-1",
            Title = "Approve deploy?",
            Body = "Approve or reject the production deploy.",
            Severity = MessageSeverities.Warning,
            AllowedActions = actions,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            CorrelationId = CorrelationId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
    }

    private sealed class RecordingTeamsCardManager : ITeamsCardManager
    {
        public List<(string QuestionId, CardUpdateAction Action, HumanDecisionEvent? Decision, string? ActorDisplayName)> Calls { get; } = new();
        public Func<Task>? OnUpdate { get; set; }

        public Task UpdateCardAsync(string questionId, CardUpdateAction action, CancellationToken ct)
        {
            Calls.Add((questionId, action, null, null));
            return OnUpdate?.Invoke() ?? Task.CompletedTask;
        }

        public Task UpdateCardAsync(
            string questionId,
            CardUpdateAction action,
            HumanDecisionEvent decision,
            string? actorDisplayName,
            CancellationToken ct)
        {
            Calls.Add((questionId, action, decision, actorDisplayName));
            return OnUpdate?.Invoke() ?? Task.CompletedTask;
        }

        public Task DeleteCardAsync(string questionId, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class ThrowingCardStateStore : ICardStateStore
    {
        public Task SaveAsync(TeamsCardState state, CancellationToken ct) => Task.CompletedTask;
        public Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct)
            => throw new InvalidOperationException("card-state-store-down");
        public Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class HandlerHarness
    {
        public required CardActionHandler Handler { get; init; }
        public required InMemoryAgentQuestionStore QuestionStore { get; init; }
        public required RecordingCardStateStore_ CardStateStore { get; init; }
        public required RecordingTeamsCardManager CardManager { get; init; }
        public required RecordingInboundEventPublisher Publisher { get; init; }
        public required RecordingAuditLogger Audit { get; init; }

        public static HandlerHarness Build(ICardStateStore? cardStateStore = null)
        {
            var questionStore = new InMemoryAgentQuestionStore();
            var cardStore = (cardStateStore as RecordingCardStateStore_) ?? new RecordingCardStateStore_();
            var cardManager = new RecordingTeamsCardManager();
            var publisher = new RecordingInboundEventPublisher();
            var audit = new RecordingAuditLogger();
            var handler = new CardActionHandler(
                questionStore,
                cardStateStore ?? cardStore,
                cardManager,
                publisher,
                audit,
                NullLogger<CardActionHandler>.Instance);

            return new HandlerHarness
            {
                Handler = handler,
                QuestionStore = questionStore,
                CardStateStore = cardStore,
                CardManager = cardManager,
                Publisher = publisher,
                Audit = audit,
            };
        }
    }

    private sealed class RecordingCardStateStore_ : ICardStateStore
    {
        public Dictionary<string, TeamsCardState> Preload { get; } = new(StringComparer.Ordinal);
        public List<(string QuestionId, string Status)> StatusUpdates { get; } = new();
        public List<TeamsCardState> Saved { get; } = new();

        public Task SaveAsync(TeamsCardState state, CancellationToken ct)
        {
            Saved.Add(state);
            return Task.CompletedTask;
        }

        public Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct)
        {
            Preload.TryGetValue(questionId, out var hit);
            return Task.FromResult<TeamsCardState?>(hit);
        }

        public Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct)
        {
            StatusUpdates.Add((questionId, newStatus));
            return Task.CompletedTask;
        }
    }

    private static ITurnContext BuildInvokeTurn(
        string actionValue,
        string? comment = null,
        string actorAad = ActorAad,
        string actorName = "Alice Wong")
    {
        var data = new JObject
        {
            [CardActionDataKeys.QuestionId] = QuestionId,
            [CardActionDataKeys.ActionId] = $"a-{actionValue}",
            [CardActionDataKeys.ActionValue] = actionValue,
            [CardActionDataKeys.CorrelationId] = CorrelationId,
        };
        if (comment is not null)
        {
            data[CardActionDataKeys.Comment] = comment;
        }

        var activity = new Activity
        {
            Type = ActivityTypes.Invoke,
            Id = "act-inbound-1",
            Name = "adaptiveCard/action",
            Value = data,
            Timestamp = DateTimeOffset.UtcNow,
            From = new ChannelAccount(id: "29:user-z", aadObjectId: actorAad, name: actorName),
            Conversation = new ConversationAccount(id: "19:conv-z", tenantId: TenantId, conversationType: "personal"),
            ChannelData = JObject.FromObject(new { tenant = new { id = TenantId } }),
        };

        return new TurnContext(new HandlerFactory.InertBotAdapter(), activity);
    }

    [Fact]
    public async Task HandleAsync_HappyPath_PublishesDecision_UpdatesCard_AuditsSuccess()
    {
        var harness = HandlerHarness.Build();
        harness.QuestionStore.Seed(BuildOpenQuestion());

        var response = await harness.Handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None);

        Assert.NotNull(response);

        // Decision event is published.
        var ev = Assert.Single(harness.Publisher.Published);
        var decisionEvent = Assert.IsType<DecisionEvent>(ev);
        Assert.Equal("approve", decisionEvent.Payload.ActionValue);
        Assert.Equal(ActorAad, decisionEvent.Payload.ExternalUserId);

        // Card is updated via the 5-arg actor-attributed overload with the decision payload.
        var call = Assert.Single(harness.CardManager.Calls);
        Assert.Equal(CardUpdateAction.MarkAnswered, call.Action);
        Assert.NotNull(call.Decision);
        Assert.Equal("Alice Wong", call.ActorDisplayName);

        // CAS transitioned Open → Resolved.
        var transition = Assert.Single(harness.QuestionStore.StatusTransitionCalls);
        Assert.Equal(AgentQuestionStatuses.Open, transition.Expected);
        Assert.Equal(AgentQuestionStatuses.Resolved, transition.New);

        // Success audit row.
        var entry = Assert.Single(harness.Audit.Entries);
        Assert.Equal(AuditEventTypes.CardActionReceived, entry.EventType);
        Assert.Equal(AuditOutcomes.Success, entry.Outcome);
        Assert.Equal(ActorAad, entry.ActorId);
        Assert.Equal(TenantId, entry.TenantId);
        Assert.Equal("approve", entry.Action);
    }

    [Fact]
    public async Task HandleAsync_RejectsInvalidAction_EmitsRejectedAudit_NoDecisionPublished()
    {
        var harness = HandlerHarness.Build();
        harness.QuestionStore.Seed(BuildOpenQuestion());

        await harness.Handler.HandleAsync(BuildInvokeTurn("disapprove"), CancellationToken.None);

        Assert.Empty(harness.Publisher.Published);
        Assert.Empty(harness.CardManager.Calls);
        var entry = Assert.Single(harness.Audit.Entries);
        Assert.Equal(AuditOutcomes.Rejected, entry.Outcome);
        Assert.Equal("disapprove", entry.Action);
    }

    [Fact]
    public async Task HandleAsync_MissingQuestion_EmitsRejectedAudit()
    {
        var harness = HandlerHarness.Build();

        await harness.Handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None);

        Assert.Empty(harness.Publisher.Published);
        var entry = Assert.Single(harness.Audit.Entries);
        Assert.Equal(AuditOutcomes.Rejected, entry.Outcome);
        // AgentId in audit is null since the question wasn't found.
        Assert.Null(entry.AgentId);
    }

    [Fact]
    public async Task HandleAsync_AlreadyResolved_EmitsRejectedAudit_NoTransitionAttempt()
    {
        var harness = HandlerHarness.Build();
        harness.QuestionStore.Seed(BuildOpenQuestion(status: AgentQuestionStatuses.Resolved));

        await harness.Handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None);

        // No CAS attempted — short-circuited by status check.
        Assert.Empty(harness.QuestionStore.StatusTransitionCalls);
        Assert.Empty(harness.Publisher.Published);
        Assert.Empty(harness.CardManager.Calls);
        var entry = Assert.Single(harness.Audit.Entries);
        Assert.Equal(AuditOutcomes.Rejected, entry.Outcome);
    }

    [Fact]
    public async Task HandleAsync_LostCasRace_EmitsRejectedAudit_NoDecisionOrCard()
    {
        var harness = HandlerHarness.Build();
        harness.QuestionStore.Seed(BuildOpenQuestion());
        harness.QuestionStore.ForceTransitionFailure = true;

        await harness.Handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None);

        Assert.Empty(harness.Publisher.Published);
        Assert.Empty(harness.CardManager.Calls);
        var entry = Assert.Single(harness.Audit.Entries);
        Assert.Equal(AuditOutcomes.Rejected, entry.Outcome);
    }

    [Fact]
    public async Task HandleAsync_SanitisesComment_OmitsRawTextFromAudit()
    {
        var harness = HandlerHarness.Build();
        harness.QuestionStore.Seed(BuildOpenQuestion());

        // User-supplied free text including a hypothetical leaked secret.
        var rawComment = "PII: ALICE@example.com SECRET=AKIA-FAKE-DEADBEEF";

        await harness.Handler.HandleAsync(BuildInvokeTurn("approve", comment: rawComment), CancellationToken.None);

        var entry = Assert.Single(harness.Audit.Entries);
        Assert.Equal(AuditOutcomes.Success, entry.Outcome);
        Assert.DoesNotContain("ALICE@example.com", entry.PayloadJson);
        Assert.DoesNotContain("AKIA-FAKE-DEADBEEF", entry.PayloadJson);
        // System.Text.Json escapes '<' and '>' to \u003C and \u003E by default; accept
        // either the escaped or unescaped form so the sanitisation guarantee is what we
        // pin, not the JSON encoder's escape choice.
        Assert.True(
            entry.PayloadJson.Contains("<redacted>", StringComparison.Ordinal)
                || entry.PayloadJson.Contains("\\u003Credacted\\u003E", StringComparison.Ordinal),
            $"Expected sanitised payload to contain the redaction sentinel; got: {entry.PayloadJson}");
    }

    [Fact]
    public async Task HandleAsync_CardStateStoreFails_DoesNotAbort_AuditStillWritten()
    {
        // The card-state lookup in BuildSanitizedPayloadJsonAsync is best-effort. A throw
        // from the store should NOT abort the overall handler — the success audit must
        // still be written and the decision must still be published.
        var throwing = new ThrowingCardStateStore();
        var harness = HandlerHarness.Build(cardStateStore: throwing);
        harness.QuestionStore.Seed(BuildOpenQuestion());

        var response = await harness.Handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None);
        Assert.NotNull(response);

        Assert.Single(harness.Publisher.Published);
        var entry = Assert.Single(harness.Audit.Entries);
        Assert.Equal(AuditOutcomes.Success, entry.Outcome);
    }

    [Fact]
    public async Task HandleAsync_CardStatePresent_AddsActivityIdToPayload()
    {
        var harness = HandlerHarness.Build();
        harness.QuestionStore.Seed(BuildOpenQuestion());
        harness.CardStateStore.Preload[QuestionId] = new TeamsCardState
        {
            QuestionId = QuestionId,
            ActivityId = "act-original-123",
            ConversationId = "19:conv-z",
            ConversationReferenceJson = "{}",
            Status = TeamsCardStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await harness.Handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None);

        var entry = Assert.Single(harness.Audit.Entries);
        Assert.Contains("act-original-123", entry.PayloadJson);
    }

    [Fact]
    public async Task HandleAsync_NullActivityValue_EmitsRejectedAudit()
    {
        var harness = HandlerHarness.Build();
        var activity = new Activity
        {
            Type = ActivityTypes.Invoke,
            Id = "act-bad",
            Name = "adaptiveCard/action",
            Value = null,
            Timestamp = DateTimeOffset.UtcNow,
            From = new ChannelAccount(id: "29:u", aadObjectId: ActorAad),
            Conversation = new ConversationAccount(id: "19:c", tenantId: TenantId),
        };
        var turn = new TurnContext(new HandlerFactory.InertBotAdapter(), activity);

        await harness.Handler.HandleAsync(turn, CancellationToken.None);

        var entry = Assert.Single(harness.Audit.Entries);
        Assert.Equal(AuditOutcomes.Rejected, entry.Outcome);
    }
}
