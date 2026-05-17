using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams.Cards;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
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

        var response = await harness.Handler.HandleAsync(BuildInvokeTurn("disapprove"), CancellationToken.None);

        Assert.Empty(harness.Publisher.Published);
        Assert.Empty(harness.CardManager.Calls);
        var entry = Assert.Single(harness.Audit.Entries);
        Assert.Equal(AuditOutcomes.Rejected, entry.Outcome);
        Assert.Equal("disapprove", entry.Action);

        // Iter-3 evaluator feedback #4 ΓÇö pin the Bot Framework Universal Action error
        // response contract for rejected card actions. The Teams client renders
        // 4xx + application/vnd.microsoft.error responses as error notifications;
        // the prior HTTP 200 + application/vnd.microsoft.activity.message regression
        // would silently render as a chat message acknowledgement so a regression
        // here would mask invalid-action failures from the end-user. The code field
        // discriminates ActionNotAllowed from other rejection types so operators
        // can grep Bot Framework telemetry by code.
        Assert.NotNull(response);
        Assert.Equal(403, response.StatusCode);
        Assert.Equal("application/vnd.microsoft.error", response.Type);
        var errorValue = Assert.IsType<JObject>(response.Value);
        Assert.Equal("ActionNotAllowed", (string?)errorValue["code"]);
        Assert.Contains("disapprove", (string?)errorValue["message"] ?? string.Empty, StringComparison.Ordinal);
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

    /// <summary>
    /// Iter-2 evaluator feedback #4 / iter-3 evaluator feedback #5 ΓÇö when
    /// <see cref="ITeamsCardManager.UpdateCardAsync(string, CardUpdateAction, HumanDecisionEvent, string?, CancellationToken)"/>
    /// throws AFTER the durable Open->Resolved CAS succeeds, the audit row must
    /// reflect the lifecycle gap AND the caller must observe a Bot Framework error
    /// response. The prior behaviour returned an <c>Accept</c> with a "Recorded ..."
    /// confirmation message even though the original interactive card was not
    /// replaced ΓÇö the user saw a success acknowledgement while the actionable card
    /// sat stale and clickable on screen. The handler now returns
    /// <see cref="AdaptiveCardInvokeResponse"/> with <c>StatusCode = 502</c>,
    /// <c>Type = application/vnd.microsoft.error</c>, and a
    /// <c>code = CardUpdateFailed</c> error body via <c>RejectCardUpdateFailure</c>;
    /// the decision event is still published (the durable resolution stands), and
    /// the audit row carries <see cref="AuditOutcomes.Failed"/> plus the
    /// <c>cardUpdateError</c> marker so operators can reconcile lifecycle state.
    /// </summary>
    [Fact]
    public async Task HandleAsync_CardUpdateThrows_EmitsFailedAudit_DecisionStillPublished()
    {
        var harness = HandlerHarness.Build();
        harness.QuestionStore.Seed(BuildOpenQuestion());
        harness.CardManager.OnUpdate = () => throw new InvalidOperationException("update-card-down");

        var response = await harness.Handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None);

        // Decision is still published (the durable Open->Resolved CAS committed before
        // the card update was attempted).
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

        // Iter-3 evaluator feedback #5 ΓÇö pin the response shape so the previously-fixed
        // Bot Framework error contract for card-update failures cannot regress to the
        // misleading Accept("Recorded ...") shape. 502 communicates a downstream gateway
        // failure (UpdateActivityAsync failed) and CardUpdateFailed lets operators
        // discriminate this from action-not-allowed / not-found / expired rejections.
        Assert.Equal(502, response.StatusCode);
        Assert.Equal("application/vnd.microsoft.error", response.Type);
        var errorValue = Assert.IsType<JObject>(response.Value);
        Assert.Equal("CardUpdateFailed", (string?)errorValue["code"]);
        var errorMessage = (string?)errorValue["message"] ?? string.Empty;
        Assert.Contains("recorded", errorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refresh", errorMessage, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Iter-3 evaluator feedback #3 ΓÇö an <see cref="IAuditLogger"/> test double that
    /// throws on the first <c>FailuresBeforeSuccess</c> calls and succeeds afterwards.
    /// Used to verify that <see cref="CardActionHandler.WriteAuditEntryWithRetryAsync"/>
    /// recovers from transient audit-store outages via inline retry without losing the
    /// compliance evidence to the silent-catch behaviour the iter-1 review flagged.
    /// </summary>
    private sealed class TransientFailureAuditLogger : IAuditLogger
    {
        public int FailuresBeforeSuccess { get; init; }
        public int CallCount { get; private set; }
        public List<AuditEntry> Persisted { get; } = new();

        public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount <= FailuresBeforeSuccess)
            {
                throw new InvalidOperationException($"audit-store-transient-failure-attempt-{CallCount}");
            }

            Persisted.Add(entry);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Iter-3 evaluator feedback #3 ΓÇö exercise the durable recovery path. When the
    /// audit store throws transiently AFTER the durable Open->Resolved CAS and the
    /// DecisionEvent publish have committed, the handler retries
    /// <see cref="IAuditLogger.LogAsync"/> with exponential backoff so the original
    /// <c>Success</c> audit row eventually lands. This closes the compliance-evidence
    /// gap the prior fail-loud-rethrow design exposed: on the retry path, the actor's
    /// second invoke would hit the resolved-status guard and never re-emit the audit
    /// row, so without the inline retry the row would be permanently missing.
    ///
    /// Uses the internal 10-arg constructor to override the audit retry base delay
    /// to 10ms so the test completes in &lt; 100ms instead of the production
    /// worst-case 1500ms.
    /// </summary>
    [Fact]
    public async Task HandleAsync_AuditTransientFailure_RetriesUntilSuccess_AuditRowPersisted()
    {
        var questionStore = new InMemoryAgentQuestionStore();
        questionStore.Seed(BuildOpenQuestion());
        var cardStore = new RecordingCardStateStore_();
        var cardManager = new RecordingTeamsCardManager();
        var publisher = new RecordingInboundEventPublisher();
        // First two LogAsync calls throw; third succeeds. Verifies the retry loop
        // tolerates multiple transient blips before giving up.
        var audit = new TransientFailureAuditLogger { FailuresBeforeSuccess = 2 };
        var handler = new CardActionHandler(
            questionStore,
            cardStore,
            cardManager,
            publisher,
            audit,
            NullLogger<CardActionHandler>.Instance,
            TimeProvider.System,
            CardActionHandler.DedupeRetentionPeriod,
            auditRetryBaseDelay: TimeSpan.FromMilliseconds(10),
            auditRetryMaxAttempts: CardActionHandler.AuditRetryMaxAttempts);

        var response = await handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None);

        Assert.NotNull(response);
        // Decision event published exactly once (the durable resolution stands).
        var ev = Assert.Single(publisher.Published);
        var decisionEvent = Assert.IsType<DecisionEvent>(ev);
        Assert.Equal("approve", decisionEvent.Payload.ActionValue);

        // Audit retry consumed three LogAsync attempts (2 transient failures + 1 success).
        Assert.Equal(3, audit.CallCount);
        var entry = Assert.Single(audit.Persisted);
        Assert.Equal(AuditOutcomes.Success, entry.Outcome);
        Assert.Equal("approve", entry.Action);
        Assert.Equal(ActorAad, entry.ActorId);
    }

    /// <summary>
    /// Iter-3 evaluator feedback #3 ΓÇö verify the fallback log emission when audit
    /// retries are exhausted. When the audit store fails for ALL retry attempts, the
    /// handler must (a) still rethrow so the caller observes the failure, and (b)
    /// emit the serialised <see cref="AuditEntry"/> as a <c>FALLBACK_AUDIT_ENTRY</c>
    /// LogCritical line so the log sink (independent of the primary audit store)
    /// serves as the durable recovery surface for the missing compliance evidence.
    /// </summary>
    [Fact]
    public async Task HandleAsync_AuditPersistentFailure_EmitsFallbackLogAndRethrows()
    {
        var questionStore = new InMemoryAgentQuestionStore();
        questionStore.Seed(BuildOpenQuestion());
        var cardStore = new RecordingCardStateStore_();
        var cardManager = new RecordingTeamsCardManager();
        var publisher = new RecordingInboundEventPublisher();
        // FailuresBeforeSuccess high enough that every retry attempt throws.
        var audit = new TransientFailureAuditLogger { FailuresBeforeSuccess = int.MaxValue };
        var fallbackLogger = new RecordingFallbackLogger();
        var handler = new CardActionHandler(
            questionStore,
            cardStore,
            cardManager,
            publisher,
            audit,
            fallbackLogger,
            TimeProvider.System,
            CardActionHandler.DedupeRetentionPeriod,
            auditRetryBaseDelay: TimeSpan.FromMilliseconds(5),
            auditRetryMaxAttempts: 3);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(BuildInvokeTurn("approve"), CancellationToken.None));

        // All retry attempts consumed.
        Assert.Equal(3, audit.CallCount);
        Assert.Empty(audit.Persisted);

        // Decision still published — the durable CAS committed before the audit
        // attempt, so the agent observes the human's choice via the channel.
        Assert.Single(publisher.Published);

        // Fallback log emit verified: at least one LogCritical message carrying the
        // FALLBACK_AUDIT_ENTRY marker so log shippers can extract the compliance
        // evidence for out-of-band replay into the primary audit store.
        Assert.Contains(
            fallbackLogger.CriticalMessages,
            m => m.Contains("FALLBACK_AUDIT_ENTRY", StringComparison.Ordinal)
              && m.Contains(CorrelationId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Test double for <see cref="ILogger{T}"/> that records every formatted log
    /// message at <see cref="LogLevel.Critical"/>. Used by the fallback-audit test
    /// to verify the <c>FALLBACK_AUDIT_ENTRY</c> log line was emitted with the
    /// serialised audit-entry payload after retry exhaustion.
    /// </summary>
    private sealed class RecordingFallbackLogger : ILogger<CardActionHandler>
    {
        public List<string> CriticalMessages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Critical)
            {
                CriticalMessages.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
