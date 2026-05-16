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

        public static HandlerHarness Build(ICardStateStore? cardStateStore = null, TimeProvider? timeProvider = null)
        {
            var questionStore = new InMemoryAgentQuestionStore();
            var cardStore = (cardStateStore as RecordingCardStateStore_) ?? new RecordingCardStateStore_();
            var cardManager = new RecordingTeamsCardManager();
            var publisher = new RecordingInboundEventPublisher();
            var audit = new RecordingAuditLogger();
            var handler = timeProvider is null
                ? new CardActionHandler(
                    questionStore,
                    cardStateStore ?? cardStore,
                    cardManager,
                    publisher,
                    audit,
                    NullLogger<CardActionHandler>.Instance)
                : new CardActionHandler(
                    questionStore,
                    cardStateStore ?? cardStore,
                    cardManager,
                    publisher,
                    audit,
                    NullLogger<CardActionHandler>.Instance,
                    timeProvider,
                    TimeSpan.FromMinutes(5));

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

    /// <summary>
    /// Minimal <see cref="TimeProvider"/> that always returns a pinned instant. Used by
    /// the iter-3 expiry tests so the deadline arithmetic is deterministic and the
    /// race-window assertions do not depend on wall-clock skew between
    /// <c>BuildOpenQuestion</c> and <c>HandleAsync</c>.
    /// </summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
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

    /// <summary>
    /// Iter-3 evaluator feedback #3 — when the durable <see cref="AgentQuestion.Status"/>
    /// is already <see cref="AgentQuestionStatuses.Expired"/> (the
    /// <see cref="AgentSwarm.Messaging.Teams.Lifecycle.QuestionExpiryProcessor"/> sweep
    /// has already run), the handler must reject with the distinct <c>"Expired"</c>
    /// code rather than the generic <c>"AlreadyResolved"</c> code — operators and the
    /// Teams client both need to distinguish these two terminal states.
    /// </summary>
    [Fact]
    public async Task HandleAsync_QuestionStatusExpired_EmitsRejectedAudit_WithExpiredCode()
    {
        var harness = HandlerHarness.Build();
        harness.QuestionStore.Seed(BuildOpenQuestion(status: AgentQuestionStatuses.Expired));

        var response = await harness.Handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None);

        // Inspect the response value for the rejection code (Universal Action error contract).
        Assert.NotNull(response);
        var json = System.Text.Json.JsonSerializer.Serialize(response.Value);
        Assert.Contains("Expired", json, StringComparison.Ordinal);

        Assert.Empty(harness.QuestionStore.StatusTransitionCalls);
        Assert.Empty(harness.Publisher.Published);
        Assert.Empty(harness.CardManager.Calls);
        var entry = Assert.Single(harness.Audit.Entries);
        Assert.Equal(AuditOutcomes.Rejected, entry.Outcome);
    }

    /// <summary>
    /// Iter-3 evaluator feedback #3 — race-window guard. After
    /// <see cref="AgentQuestion.ExpiresAt"/> elapses but BEFORE
    /// <see cref="AgentSwarm.Messaging.Teams.Lifecycle.QuestionExpiryProcessor"/>'s next
    /// sweep flips <see cref="AgentQuestion.Status"/> to
    /// <see cref="AgentQuestionStatuses.Expired"/>, the question is still
    /// <c>Open</c>. Accepting a card action in that window would mint a stale Resolved
    /// row past the deadline; the handler must reject explicitly with the
    /// <c>"Expired"</c> code. The strict <c>&lt;</c> comparison matches
    /// <see cref="AgentSwarm.Messaging.Teams.EntityFrameworkCore.SqlAgentQuestionStore.GetOpenExpiredAsync"/>
    /// so the boundary semantics are identical across the handler and the worker.
    /// </summary>
    [Fact]
    public async Task HandleAsync_OpenButPastExpiresAt_EmitsRejectedAudit_NoCasAttempt()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FixedTimeProvider(now);
        var harness = HandlerHarness.Build(timeProvider: time);
        // Open question whose deadline elapsed five seconds ago — the expiry sweep has
        // not yet flipped Status to Expired.
        var question = BuildOpenQuestion() with { ExpiresAt = now.AddSeconds(-5) };
        harness.QuestionStore.Seed(question);

        var response = await harness.Handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None);

        Assert.NotNull(response);
        var json = System.Text.Json.JsonSerializer.Serialize(response.Value);
        Assert.Contains("Expired", json, StringComparison.Ordinal);

        // No CAS was attempted — the deadline check fires BEFORE TryUpdateStatusAsync.
        Assert.Empty(harness.QuestionStore.StatusTransitionCalls);
        Assert.Empty(harness.Publisher.Published);
        Assert.Empty(harness.CardManager.Calls);

        var entry = Assert.Single(harness.Audit.Entries);
        Assert.Equal(AuditOutcomes.Rejected, entry.Outcome);
    }

    /// <summary>
    /// Iter-3 evaluator feedback #3 — boundary case: an Open question whose
    /// <see cref="AgentQuestion.ExpiresAt"/> equals the current instant must be
    /// accepted (the handler uses strict <c>&lt;</c>, matching
    /// <see cref="AgentSwarm.Messaging.Teams.EntityFrameworkCore.SqlAgentQuestionStore.GetOpenExpiredAsync"/>).
    /// Without this test, a future refactor that flipped the comparison to <c>&lt;=</c>
    /// would silently regress the boundary alignment with the worker.
    /// </summary>
    [Fact]
    public async Task HandleAsync_OpenAtExactExpiresAtInstant_AcceptsAction()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FixedTimeProvider(now);
        var harness = HandlerHarness.Build(timeProvider: time);
        var question = BuildOpenQuestion() with { ExpiresAt = now };
        harness.QuestionStore.Seed(question);

        await harness.Handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None);

        // Acceptance signal — CAS attempted, decision published, success audit row.
        Assert.Single(harness.QuestionStore.StatusTransitionCalls);
        Assert.Single(harness.Publisher.Published);
        var entry = Assert.Single(harness.Audit.Entries);
        Assert.Equal(AuditOutcomes.Success, entry.Outcome);
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

    /// <summary>
    /// Iter-8 fix #3 — when <see cref="ITeamsCardManager.UpdateCardAsync(string, CardUpdateAction, HumanDecisionEvent, string?, CancellationToken)"/>
    /// throws AFTER the durable Open→Resolved CAS succeeds, the audit row must reflect
    /// the lifecycle gap. The previous implementation wrote a <c>Success</c> outcome
    /// even though the original interactive card was not replaced — operators had no
    /// signal that the user-visible card was stale. The handler now writes
    /// <see cref="AuditOutcomes.Failed"/> with a <c>cardUpdateError</c> marker in the
    /// sanitised payload. The decision event is still published and the caller still
    /// gets an <c>Accept</c> response because the durable resolution stands.
    /// </summary>
    [Fact]
    public async Task HandleAsync_CardUpdateThrows_EmitsFailedAudit_DecisionStillPublished()
    {
        var harness = HandlerHarness.Build();
        harness.QuestionStore.Seed(BuildOpenQuestion());
        harness.CardManager.OnUpdate = () => throw new InvalidOperationException("update-card-down");

        var response = await harness.Handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None);

        // Decision is still published and caller still gets a non-null Accept response.
        Assert.NotNull(response);
        var ev = Assert.Single(harness.Publisher.Published);
        var decisionEvent = Assert.IsType<DecisionEvent>(ev);
        Assert.Equal("approve", decisionEvent.Payload.ActionValue);

        // CAS was attempted and succeeded.
        var transition = Assert.Single(harness.QuestionStore.StatusTransitionCalls);
        Assert.Equal(AgentQuestionStatuses.Open, transition.Expected);
        Assert.Equal(AgentQuestionStatuses.Resolved, transition.New);

        // Audit outcome is Failed (NOT Success) and the payload carries the error marker.
        var entry = Assert.Single(harness.Audit.Entries);
        Assert.Equal(AuditOutcomes.Failed, entry.Outcome);
        Assert.Contains("cardUpdateError", entry.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("update-card-down", entry.PayloadJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// Iter-8 fix #5 — architecture.md §2.6 layer 2 in-memory processed-action dedupe.
    /// When the same actor submits the same action for the same question twice within
    /// the dedupe TTL, the second call must short-circuit BEFORE touching any store
    /// (no question lookup, no CAS, no audit row). The first call still produces its
    /// full terminal outcome (decision event + Success audit). The architectural
    /// guarantee is that double-taps cannot produce two terminal outcomes nor two
    /// decision events.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DuplicateSubmissionWithinTtl_ShortCircuits_NoSecondDecisionOrAudit()
    {
        var harness = HandlerHarness.Build();
        harness.QuestionStore.Seed(BuildOpenQuestion());

        // First submission completes normally.
        await harness.Handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None);
        Assert.Single(harness.Publisher.Published);
        Assert.Single(harness.Audit.Entries);
        Assert.Single(harness.CardManager.Calls);
        var firstTransitionCount = harness.QuestionStore.StatusTransitionCalls.Count;

        // Second submission (same actor, same question, same action) hits the dedupe set.
        var response = await harness.Handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None);
        Assert.NotNull(response);

        // No additional decision event, no additional card update, no additional CAS,
        // no additional audit row — the dedupe layer short-circuits BEFORE any I/O.
        Assert.Single(harness.Publisher.Published);
        Assert.Single(harness.CardManager.Calls);
        Assert.Single(harness.Audit.Entries);
        Assert.Equal(firstTransitionCount, harness.QuestionStore.StatusTransitionCalls.Count);
    }

    /// <summary>
    /// Iter-8 fix #5 — a different actor (different <c>AadObjectId</c>) submitting the
    /// same question must NOT be deduped. The dedupe key is <c>(QuestionId, UserId)</c>
    /// so per-user replay protection is independent.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DuplicateSubmissionDifferentActor_NotDeduped()
    {
        var harness = HandlerHarness.Build();
        harness.QuestionStore.Seed(BuildOpenQuestion());

        // First actor resolves the question (lands at Resolved).
        await harness.Handler.HandleAsync(BuildInvokeTurn("approve", actorAad: "actor-1"), CancellationToken.None);

        // Second actor submits — different dedupe key, so the handler does run the full
        // pipeline. The question is now Resolved so the handler reports a Rejected audit
        // entry (status check). The crucial point is that the dedupe layer did NOT
        // short-circuit — we see TWO audit rows, not one.
        await harness.Handler.HandleAsync(BuildInvokeTurn("approve", actorAad: "actor-2"), CancellationToken.None);

        Assert.Equal(2, harness.Audit.Entries.Count);
    }

    /// <summary>
    /// Iter-8 fix #5 — when the handler throws (e.g. infrastructure outage) the dedupe
    /// entry is removed so the actor can retry. The architecture guarantee is "fast-path
    /// dedupe for duplicate submissions" — not "permanent reject on transient failure".
    /// </summary>
    [Fact]
    public async Task HandleAsync_UnhandledException_EvictsDedupeEntry_PermitsRetry()
    {
        // Cause an unhandled exception in the pipeline by making the question store throw
        // on GetByIdAsync. The handler does not catch general exceptions in the question
        // lookup path; the dedupe finally block must still evict the entry.
        var throwingStore = new ThrowingQuestionStore();
        var cardStore = new RecordingCardStateStore_();
        var cardManager = new RecordingTeamsCardManager();
        var publisher = new RecordingInboundEventPublisher();
        var audit = new RecordingAuditLogger();
        var handler = new CardActionHandler(
            throwingStore,
            cardStore,
            cardManager,
            publisher,
            audit,
            NullLogger<CardActionHandler>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None));

        // Retry must NOT be short-circuited by the dedupe set — the throwing store
        // proves we re-entered the pipeline because it throws again.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None));

        Assert.Equal(2, throwingStore.GetByIdCalls);
    }

    private sealed class ThrowingQuestionStore : IAgentQuestionStore
    {
        public int GetByIdCalls;
        public Task SaveAsync(AgentQuestion question, CancellationToken ct) => Task.CompletedTask;
        public Task<AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct)
        {
            GetByIdCalls++;
            throw new InvalidOperationException("store-down");
        }
        public Task<bool> TryUpdateStatusAsync(string questionId, string expectedStatus, string newStatus, CancellationToken ct) => Task.FromResult(false);
        public Task UpdateConversationIdAsync(string questionId, string conversationId, CancellationToken ct) => Task.CompletedTask;
        public Task<AgentQuestion?> GetMostRecentOpenByConversationAsync(string conversationId, CancellationToken ct) => Task.FromResult<AgentQuestion?>(null);
        public Task<IReadOnlyList<AgentQuestion>> GetOpenByConversationAsync(string conversationId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());
        public Task<IReadOnlyList<AgentQuestion>> GetOpenExpiredAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());
    }
}
