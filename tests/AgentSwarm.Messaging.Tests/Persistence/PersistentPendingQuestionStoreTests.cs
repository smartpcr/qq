using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Tests.Persistence;

/// <summary>
/// Stage 2.2 acceptance tests for <see cref="PersistentPendingQuestionStore"/>.
/// Covers the StoreAsync / GetAsync round-trip, status transitions,
/// RecordSelectionAsync, and the GetExpiredAsync filter contract from
/// architecture.md §4.7.
/// </summary>
public class PersistentPendingQuestionStoreTests : IDisposable
{
    private readonly SqliteContextHarness _harness = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly PersistentPendingQuestionStore _store;

    public PersistentPendingQuestionStoreTests()
    {
        _store = new PersistentPendingQuestionStore(_harness.Factory, _clock);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task StoreAsync_PersistsAllFieldsAndStatusIsPending()
    {
        var envelope = NewEnvelope("Q-100", expiresInMinutes: 15);
        await _store.StoreAsync(envelope, channelId: 12345L, platformMessageId: 67890L, CancellationToken.None);

        var loaded = await _store.GetAsync("Q-100", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.QuestionId.Should().Be("Q-100");
        loaded.ChannelId.Should().Be(12345L);
        loaded.PlatformMessageId.Should().Be(67890L);
        loaded.Status.Should().Be(PendingQuestionStatus.Pending);
        loaded.DefaultActionId.Should().Be("approve");
        loaded.DefaultActionValue.Should().Be("yes");
        loaded.Question.QuestionId.Should().Be("Q-100");
        loaded.Question.AllowedActions.Should().HaveCount(2);
    }

    [Fact]
    public async Task StoreAsync_WithRoutingThreadId_PersistsThreadId()
    {
        var envelope = NewEnvelope("Q-101", expiresInMinutes: 5, threadId: 9988776655UL);
        await _store.StoreAsync(envelope, 100L, 200L, CancellationToken.None);

        var loaded = await _store.GetAsync("Q-101", CancellationToken.None);

        loaded!.ThreadId.Should().Be(unchecked((long)9988776655UL));
    }

    [Fact]
    public async Task GetAsync_UnknownQuestionId_ReturnsNull()
    {
        var loaded = await _store.GetAsync("never-stored", CancellationToken.None);
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task MarkAnsweredAsync_TransitionsToAnswered()
    {
        var envelope = NewEnvelope("Q-200", expiresInMinutes: 30);
        await _store.StoreAsync(envelope, 1L, 2L, CancellationToken.None);

        await _store.MarkAnsweredAsync("Q-200", CancellationToken.None);

        var loaded = await _store.GetAsync("Q-200", CancellationToken.None);
        loaded!.Status.Should().Be(PendingQuestionStatus.Answered);
    }

    [Fact]
    public async Task MarkAwaitingCommentAsync_TransitionsToAwaitingComment()
    {
        var envelope = NewEnvelope("Q-201", expiresInMinutes: 30);
        await _store.StoreAsync(envelope, 1L, 2L, CancellationToken.None);

        await _store.MarkAwaitingCommentAsync("Q-201", CancellationToken.None);

        var loaded = await _store.GetAsync("Q-201", CancellationToken.None);
        loaded!.Status.Should().Be(PendingQuestionStatus.AwaitingComment);
    }

    [Fact]
    public async Task RecordSelectionAsync_PopulatesSelectionAndRespondent()
    {
        var envelope = NewEnvelope("Q-202", expiresInMinutes: 30);
        await _store.StoreAsync(envelope, 1L, 2L, CancellationToken.None);

        await _store.RecordSelectionAsync(
            "Q-202",
            selectedActionId: "reject",
            selectedActionValue: "no",
            respondentUserId: 5566778899L,
            CancellationToken.None);

        var loaded = await _store.GetAsync("Q-202", CancellationToken.None);
        loaded!.SelectedActionId.Should().Be("reject");
        loaded.SelectedActionValue.Should().Be("no");
        loaded.RespondentUserId.Should().Be(5566778899L);
        // Status should still be Pending -- contract says caller composes
        // RecordSelectionAsync with MarkAnsweredAsync or MarkAwaitingCommentAsync.
        loaded.Status.Should().Be(PendingQuestionStatus.Pending);
    }

    [Fact]
    public async Task RecordSelectionAsync_UnknownQuestion_IsNoOp()
    {
        await _store.RecordSelectionAsync(
            "never",
            selectedActionId: "approve",
            selectedActionValue: "yes",
            respondentUserId: 1L,
            CancellationToken.None);

        using var ctx = _harness.NewContext();
        (await ctx.PendingQuestions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetExpiredAsync_ReturnsOnlyDuePendingAndAwaitingCommentRows()
    {
        // Three envelopes: one Pending and overdue, one AwaitingComment and
        // overdue, one Pending but not yet due. Plus one Answered (terminal)
        // overdue row to prove status filtering.
        var overduePending = NewEnvelope("Q-300", expiresInMinutes: -5);
        var overdueAwaitingComment = NewEnvelope("Q-301", expiresInMinutes: -5);
        var notYetDue = NewEnvelope("Q-302", expiresInMinutes: 30);
        var overdueAnswered = NewEnvelope("Q-303", expiresInMinutes: -5);

        await _store.StoreAsync(overduePending, 1, 1, CancellationToken.None);
        await _store.StoreAsync(overdueAwaitingComment, 1, 2, CancellationToken.None);
        await _store.MarkAwaitingCommentAsync("Q-301", CancellationToken.None);
        await _store.StoreAsync(notYetDue, 1, 3, CancellationToken.None);
        await _store.StoreAsync(overdueAnswered, 1, 4, CancellationToken.None);
        await _store.MarkAnsweredAsync("Q-303", CancellationToken.None);

        var expired = await _store.GetExpiredAsync(CancellationToken.None);

        expired.Select(p => p.QuestionId).Should().BeEquivalentTo(new[] { "Q-300", "Q-301" });
    }

    [Fact]
    public async Task StoreAsync_NullEnvelope_Throws()
    {
        var act = async () => await _store.StoreAsync(null!, 1, 2, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task StoreAsync_DuplicateQuestionId_IsNoOpAndKeepsFirstRow()
    {
        // Architecture.md §10.3 Gap B: the original producer path and the
        // QuestionRecoverySweep both call StoreAsync for the same
        // QuestionId after a crash window. The second call must be a
        // benign no-op (the first row's routing data is canonical) rather
        // than throwing -- otherwise the recovery sweep crashes on every
        // tick after the first successful backfill.
        var first = NewEnvelope("Q-DUP-1", expiresInMinutes: 30);
        var second = NewEnvelope("Q-DUP-1", expiresInMinutes: 30);

        await _store.StoreAsync(first, channelId: 1L, platformMessageId: 100L, CancellationToken.None);
        await _store.StoreAsync(second, channelId: 999L, platformMessageId: 200L, CancellationToken.None);

        using var ctx = _harness.NewContext();
        var rows = await ctx.PendingQuestions.ToListAsync();
        rows.Should().ContainSingle();
        rows[0].DiscordChannelId.Should().Be(1UL);
        rows[0].DiscordMessageId.Should().Be(100UL);
    }

    [Fact]
    public async Task StoreAsync_MalformedDiscordThreadIdRouting_ThrowsFormatException()
    {
        // A connector contract violation: DiscordThreadId routing metadata
        // must be an unsigned 64-bit integer. Surfacing the bad payload as
        // FormatException prevents the question from silently posting into
        // the parent channel root instead of the intended thread.
        var question = new AgentQuestion(
            QuestionId: "Q-BAD-1",
            AgentId: "agent-1",
            TaskId: "task-1",
            Title: "title",
            Body: "body",
            Severity: MessageSeverity.High,
            AllowedActions: new[]
            {
                new HumanAction("approve", "Approve", "yes", RequiresComment: false),
            },
            ExpiresAt: _clock.UtcNow + TimeSpan.FromMinutes(10),
            CorrelationId: "trace-bad");

        var routing = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DiscordThreadId"] = "not-a-snowflake",
        };
        var envelope = new AgentQuestionEnvelope(question, ProposedDefaultActionId: "approve", routing);

        var act = async () => await _store.StoreAsync(envelope, 1, 2, CancellationToken.None);
        await act.Should().ThrowAsync<FormatException>()
            .WithMessage("*DiscordThreadId*not-a-snowflake*");
    }

    [Fact]
    public async Task StoreAsync_EmptyDiscordThreadIdRouting_TreatedAsNoThread()
    {
        // Explicit blank value is the connector's way of saying "no
        // thread"; same outcome as the key being absent. Distinct from a
        // malformed value (which throws).
        var question = new AgentQuestion(
            QuestionId: "Q-EMPTY-1",
            AgentId: "agent-1",
            TaskId: "task-1",
            Title: "title",
            Body: "body",
            Severity: MessageSeverity.Normal,
            AllowedActions: new[]
            {
                new HumanAction("approve", "Approve", "yes", RequiresComment: false),
            },
            ExpiresAt: _clock.UtcNow + TimeSpan.FromMinutes(10),
            CorrelationId: "trace-empty");

        var routing = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DiscordThreadId"] = "   ",
        };
        var envelope = new AgentQuestionEnvelope(question, ProposedDefaultActionId: "approve", routing);

        await _store.StoreAsync(envelope, 1, 2, CancellationToken.None);

        var loaded = await _store.GetAsync("Q-EMPTY-1", CancellationToken.None);
        loaded!.ThreadId.Should().BeNull();
    }

    private AgentQuestionEnvelope NewEnvelope(
        string questionId,
        int expiresInMinutes,
        ulong? threadId = null)
    {
        var question = new AgentQuestion(
            QuestionId: questionId,
            AgentId: "agent-1",
            TaskId: "task-1",
            Title: "title",
            Body: "body",
            Severity: MessageSeverity.High,
            AllowedActions: new[]
            {
                new HumanAction("approve", "Approve", "yes", RequiresComment: false),
                new HumanAction("reject", "Reject", "no", RequiresComment: false),
            },
            ExpiresAt: _clock.UtcNow + TimeSpan.FromMinutes(expiresInMinutes),
            CorrelationId: $"trace-{questionId}");

        var routing = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DiscordChannelId"] = "12345",
        };
        if (threadId.HasValue)
        {
            routing["DiscordThreadId"] = threadId.Value.ToString();
        }

        return new AgentQuestionEnvelope(question, ProposedDefaultActionId: "approve", routing);
    }
}
