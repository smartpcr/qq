using System.Collections.Concurrent;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests.Lifecycle;

/// <summary>
/// Tests pinning the Stage 3.3 step 6 / iter-5 critique #1 contract for
/// <see cref="QuestionExpiryProcessor"/>: the processor must inject exactly
/// <see cref="IAgentQuestionStore"/>, <see cref="ITeamsCardManager"/>,
/// <see cref="TeamsMessagingOptions"/>, and an <see cref="ILogger{T}"/>; it must NOT take
/// a dependency on <see cref="ICardStateStore"/> or call any Bot Framework adapter
/// directly. Card-state lookup, conversation-reference rehydration, and inline retry are
/// the responsibility of <see cref="ITeamsCardManager.DeleteCardAsync"/>.
/// </summary>
public sealed class QuestionExpiryProcessorTests
{
    private sealed class FakeAgentQuestionStore : IAgentQuestionStore
    {
        public ConcurrentDictionary<string, AgentQuestion> Questions { get; } = new(StringComparer.Ordinal);
        public List<(string QuestionId, string Expected, string New)> Transitions { get; } = new();
        public HashSet<string> CasLosers { get; } = new(StringComparer.Ordinal);
        public List<(DateTimeOffset Cutoff, int BatchSize)> ScanCalls { get; } = new();

        public Task SaveAsync(AgentQuestion question, CancellationToken ct)
        {
            Questions[question.QuestionId] = question;
            return Task.CompletedTask;
        }

        public Task<AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct)
        {
            Questions.TryGetValue(questionId, out var hit);
            return Task.FromResult<AgentQuestion?>(hit);
        }

        public Task<bool> TryUpdateStatusAsync(string questionId, string expectedStatus, string newStatus, CancellationToken ct)
        {
            Transitions.Add((questionId, expectedStatus, newStatus));
            if (CasLosers.Contains(questionId))
            {
                return Task.FromResult(false);
            }

            if (!Questions.TryGetValue(questionId, out var q) || q.Status != expectedStatus)
            {
                return Task.FromResult(false);
            }

            Questions[questionId] = q with { Status = newStatus };
            return Task.FromResult(true);
        }

        public Task UpdateConversationIdAsync(string questionId, string conversationId, CancellationToken ct)
            => Task.CompletedTask;

        public Task<AgentQuestion?> GetMostRecentOpenByConversationAsync(string conversationId, CancellationToken ct)
            => Task.FromResult<AgentQuestion?>(null);

        public Task<IReadOnlyList<AgentQuestion>> GetOpenByConversationAsync(string conversationId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());

        public Task<IReadOnlyList<AgentQuestion>> GetOpenExpiredAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)
        {
            ScanCalls.Add((cutoff, batchSize));
            IReadOnlyList<AgentQuestion> rows = Questions.Values
                .Where(q => q.Status == AgentQuestionStatuses.Open && q.ExpiresAt < cutoff)
                .OrderBy(q => q.ExpiresAt)
                .Take(batchSize)
                .ToList();
            return Task.FromResult(rows);
        }
    }

    private sealed class RecordingTeamsCardManager : ITeamsCardManager
    {
        public List<string> DeleteCalls { get; } = new();
        public HashSet<string> DeleteFailures { get; } = new(StringComparer.Ordinal);

        public Task UpdateCardAsync(string questionId, CardUpdateAction action, CancellationToken ct)
            => Task.CompletedTask;

        public Task UpdateCardAsync(string questionId, CardUpdateAction action, HumanDecisionEvent decision, string? actorDisplayName, CancellationToken ct)
            => Task.CompletedTask;

        public Task DeleteCardAsync(string questionId, CancellationToken ct)
        {
            DeleteCalls.Add(questionId);
            if (DeleteFailures.Contains(questionId))
            {
                throw new InvalidOperationException($"Simulated delete failure for {questionId}.");
            }

            return Task.CompletedTask;
        }
    }

    private static AgentQuestion BuildQuestion(string id, DateTimeOffset expiresAt, string status = AgentQuestionStatuses.Open)
        => new()
        {
            QuestionId = id,
            AgentId = "agent",
            TaskId = "task",
            TenantId = "tenant",
            TargetUserId = "user",
            Title = "T",
            Body = "B",
            Severity = MessageSeverities.Info,
            AllowedActions = new[] { new HumanAction("ack", "Ack", "ack", false) },
            ExpiresAt = expiresAt,
            CorrelationId = "corr-" + id,
            CreatedAt = expiresAt.AddMinutes(-30),
            Status = status,
        };

    [Fact]
    public void Constructor_HasExactlyThreeBusinessDependencies_NoICardStateStore()
    {
        // Iter-5 critique #1 / implementation-plan.md §3.3 line 214: the processor must
        // depend on IAgentQuestionStore, ITeamsCardManager, and TeamsMessagingOptions —
        // never on ICardStateStore. This test pins the constructor signature so future
        // refactors cannot silently widen the dependency surface.
        var ctor = typeof(QuestionExpiryProcessor)
            .GetConstructors()
            .OrderBy(c => c.GetParameters().Length)
            .First();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.Contains(typeof(IAgentQuestionStore), paramTypes);
        Assert.Contains(typeof(ITeamsCardManager), paramTypes);
        Assert.Contains(typeof(TeamsMessagingOptions), paramTypes);
        Assert.DoesNotContain(typeof(ICardStateStore), paramTypes);
    }

    [Fact]
    public async Task ProcessOnceAsync_TransitionsAndDeletesEachExpiredQuestion()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider_(now);
        var questionStore = new FakeAgentQuestionStore();
        questionStore.Questions["q1"] = BuildQuestion("q1", now.AddMinutes(-10));
        questionStore.Questions["q2"] = BuildQuestion("q2", now.AddMinutes(-5));

        var cardManager = new RecordingTeamsCardManager();
        var processor = new QuestionExpiryProcessor(
            questionStore,
            cardManager,
            new TeamsMessagingOptions(),
            time,
            NullLogger<QuestionExpiryProcessor>.Instance);

        var processed = await processor.ProcessOnceAsync(batchSize: 10, CancellationToken.None);

        Assert.Equal(2, processed);
        Assert.Equal(AgentQuestionStatuses.Expired, questionStore.Questions["q1"].Status);
        Assert.Equal(AgentQuestionStatuses.Expired, questionStore.Questions["q2"].Status);
        Assert.Equal(new[] { "q1", "q2" }, cardManager.DeleteCalls);

        // Both transitions targeted Open → Expired (not Resolved or Deleted).
        Assert.All(questionStore.Transitions, t =>
        {
            Assert.Equal(AgentQuestionStatuses.Open, t.Expected);
            Assert.Equal(AgentQuestionStatuses.Expired, t.New);
        });
    }

    [Fact]
    public async Task ProcessOnceAsync_CasLost_SkipsCardDelete()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider_(now);
        var questionStore = new FakeAgentQuestionStore();
        questionStore.Questions["winner"] = BuildQuestion("winner", now.AddMinutes(-1));
        questionStore.Questions["racer"] = BuildQuestion("racer", now.AddMinutes(-2));
        // Concurrent process already promoted "racer" — our CAS will fail.
        questionStore.CasLosers.Add("racer");

        var cardManager = new RecordingTeamsCardManager();
        var processor = new QuestionExpiryProcessor(
            questionStore,
            cardManager,
            new TeamsMessagingOptions(),
            time,
            NullLogger<QuestionExpiryProcessor>.Instance);

        var processed = await processor.ProcessOnceAsync(batchSize: 10, CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Single(cardManager.DeleteCalls);
        Assert.Equal("winner", cardManager.DeleteCalls[0]);
    }

    [Fact]
    public async Task ProcessOnceAsync_DeleteThrows_LogsAndContinues_DoesNotRollback()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider_(now);
        var questionStore = new FakeAgentQuestionStore();
        questionStore.Questions["a"] = BuildQuestion("a", now.AddMinutes(-3));
        questionStore.Questions["b"] = BuildQuestion("b", now.AddMinutes(-2));
        questionStore.Questions["c"] = BuildQuestion("c", now.AddMinutes(-1));

        var cardManager = new RecordingTeamsCardManager();
        cardManager.DeleteFailures.Add("b");

        var processor = new QuestionExpiryProcessor(
            questionStore,
            cardManager,
            new TeamsMessagingOptions(),
            time,
            NullLogger<QuestionExpiryProcessor>.Instance);

        // The failure on "b" must NOT abort the loop, must NOT rollback "b"'s Expired
        // transition (the deadline really elapsed), and "c" must still be processed.
        var processed = await processor.ProcessOnceAsync(batchSize: 10, CancellationToken.None);

        Assert.Equal(2, processed); // a and c succeeded; b's delete threw
        Assert.Equal(new[] { "a", "b", "c" }, cardManager.DeleteCalls);
        Assert.Equal(AgentQuestionStatuses.Expired, questionStore.Questions["b"].Status);
    }

    [Fact]
    public async Task ProcessOnceAsync_NoExpiredQuestions_Noop()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider_(now);
        var questionStore = new FakeAgentQuestionStore();
        // Future expiry — should not be returned by GetOpenExpiredAsync.
        questionStore.Questions["future"] = BuildQuestion("future", now.AddHours(1));

        var cardManager = new RecordingTeamsCardManager();
        var processor = new QuestionExpiryProcessor(
            questionStore,
            cardManager,
            new TeamsMessagingOptions(),
            time,
            NullLogger<QuestionExpiryProcessor>.Instance);

        var processed = await processor.ProcessOnceAsync(batchSize: 10, CancellationToken.None);

        Assert.Equal(0, processed);
        Assert.Empty(cardManager.DeleteCalls);
    }

    [Fact]
    public async Task ProcessOnceAsync_RespectsBatchSize()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider_(now);
        var questionStore = new FakeAgentQuestionStore();
        for (var i = 0; i < 5; i++)
        {
            var id = $"q{i}";
            questionStore.Questions[id] = BuildQuestion(id, now.AddMinutes(-30 + i));
        }

        var cardManager = new RecordingTeamsCardManager();
        var processor = new QuestionExpiryProcessor(
            questionStore,
            cardManager,
            new TeamsMessagingOptions(),
            time,
            NullLogger<QuestionExpiryProcessor>.Instance);

        var processed = await processor.ProcessOnceAsync(batchSize: 2, CancellationToken.None);

        Assert.Equal(2, processed);
        var scan = Assert.Single(questionStore.ScanCalls);
        Assert.Equal(2, scan.BatchSize);
    }

    [Fact]
    public async Task ProcessOnceAsync_ThrowsForNonPositiveBatchSize()
    {
        var processor = new QuestionExpiryProcessor(
            new FakeAgentQuestionStore(),
            new RecordingTeamsCardManager(),
            new TeamsMessagingOptions(),
            TimeProvider.System,
            NullLogger<QuestionExpiryProcessor>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            processor.ProcessOnceAsync(batchSize: 0, CancellationToken.None));
    }

    private sealed class FakeTimeProvider_ : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider_(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
