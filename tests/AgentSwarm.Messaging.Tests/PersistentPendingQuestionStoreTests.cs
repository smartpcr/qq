// -----------------------------------------------------------------------
// <copyright file="PersistentPendingQuestionStoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.5 — round-trip tests for <see cref="PersistentPendingQuestionStore"/>
/// against an in-memory SQLite connection using the real
/// <see cref="MessagingDbContext"/> schema, so the
/// <see cref="PendingQuestionRecordConfiguration"/>-defined indexes,
/// the <see cref="DateTimeOffset"/> value converter, and the
/// AgentQuestion JSON serializer all run end-to-end.
/// </summary>
public sealed class PersistentPendingQuestionStoreTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private PersistentPendingQuestionStore _store = null!;
    private FakeTimeProvider _time = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<MessagingDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        await using (var scope = _provider.CreateAsyncScope())
        await using (var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        _store = new PersistentPendingQuestionStore(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _time,
            NullLogger<PersistentPendingQuestionStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static AgentQuestion BuildQuestion(string id = "q-1", string corr = "trace-1") =>
        new()
        {
            QuestionId = id,
            AgentId = "agent-deployer",
            TaskId = "task-7",
            Title = "Deploy Solution12?",
            Body = "Pre-flight clean. Stage now?",
            Severity = MessageSeverity.High,
            AllowedActions = new[]
            {
                new HumanAction { ActionId = "approve", Label = "Approve", Value = "approve_v" },
                new HumanAction { ActionId = "skip", Label = "Skip", Value = "skip_v" },
                new HumanAction { ActionId = "comment", Label = "Comment", Value = "comment_v", RequiresComment = true },
            },
            ExpiresAt = new DateTimeOffset(2026, 6, 1, 12, 15, 0, TimeSpan.Zero),
            CorrelationId = corr,
        };

    [Fact]
    public async Task StoreAsync_PersistsRowAndDenormalisesDefaultActionValue()
    {
        var envelope = new AgentQuestionEnvelope
        {
            Question = BuildQuestion(),
            ProposedDefaultActionId = "skip",
        };

        await _store.StoreAsync(envelope, telegramChatId: 42, telegramMessageId: 1001, default);

        var roundTrip = await _store.GetAsync("q-1", default);
        roundTrip.Should().NotBeNull();
        roundTrip!.QuestionId.Should().Be("q-1");
        roundTrip.AgentId.Should().Be("agent-deployer");
        roundTrip.TaskId.Should().Be("task-7");
        roundTrip.Title.Should().Be("Deploy Solution12?");
        roundTrip.Severity.Should().Be(MessageSeverity.High);
        roundTrip.AllowedActions.Should().HaveCount(3);
        roundTrip.TelegramChatId.Should().Be(42);
        roundTrip.TelegramMessageId.Should().Be(1001);
        roundTrip.DefaultActionId.Should().Be("skip");
        roundTrip.DefaultActionValue.Should().Be("skip_v",
            "the store must denormalise the matching HumanAction.Value at persist time so the callback / RequiresComment text-reply path has a durable fallback when the IDistributedCache entry is evicted (timeout itself reads DefaultActionId, NOT DefaultActionValue, per architecture.md §10.3)");
        roundTrip.Status.Should().Be(PendingQuestionStatus.Pending);
        roundTrip.CorrelationId.Should().Be("trace-1");
        roundTrip.StoredAt.Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public async Task StoreAsync_NoDefaultActionId_PersistsNullValue()
    {
        var envelope = new AgentQuestionEnvelope { Question = BuildQuestion() };

        await _store.StoreAsync(envelope, 42, 1002, default);

        var row = await _store.GetAsync("q-1", default);
        row!.DefaultActionId.Should().BeNull();
        row.DefaultActionValue.Should().BeNull();
    }

    [Fact]
    public async Task StoreAsync_RetryWithSameQuestionId_IsIdempotentUpsert()
    {
        var envelope = new AgentQuestionEnvelope { Question = BuildQuestion() };

        await _store.StoreAsync(envelope, 42, 1003, default);
        // Simulate a retry of an already-acknowledged Telegram send.
        await _store.StoreAsync(envelope, 42, 1003, default);

        await using var scope = _provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        (await ctx.PendingQuestions.CountAsync()).Should().Be(1,
            "the upsert path must keep exactly one row per QuestionId across retries");
    }

    [Fact]
    public async Task GetByTelegramMessageAsync_FindsRowByCompositeKey()
    {
        await _store.StoreAsync(
            new AgentQuestionEnvelope { Question = BuildQuestion("q-A") },
            telegramChatId: 100,
            telegramMessageId: 5001,
            default);
        await _store.StoreAsync(
            new AgentQuestionEnvelope { Question = BuildQuestion("q-B") },
            telegramChatId: 200,
            telegramMessageId: 5001,
            default);

        var row = await _store.GetByTelegramMessageAsync(200, 5001, default);
        row!.QuestionId.Should().Be("q-B",
            "the lookup must be composite (ChatId + MessageId); a colliding message id in a different chat must not alias");
    }

    [Fact]
    public async Task MarkAnsweredAsync_TransitionsToAnswered()
    {
        await _store.StoreAsync(new AgentQuestionEnvelope { Question = BuildQuestion() }, 42, 1, default);
        await _store.MarkAnsweredAsync("q-1", default);
        var row = await _store.GetAsync("q-1", default);
        row!.Status.Should().Be(PendingQuestionStatus.Answered);
    }

    [Fact]
    public async Task MarkAwaitingCommentAsync_TransitionsToAwaitingComment()
    {
        await _store.StoreAsync(new AgentQuestionEnvelope { Question = BuildQuestion() }, 42, 1, default);
        await _store.MarkAwaitingCommentAsync("q-1", default);
        var row = await _store.GetAsync("q-1", default);
        row!.Status.Should().Be(PendingQuestionStatus.AwaitingComment);
    }

    [Fact]
    public async Task MarkTimedOutAsync_IsAtomicClaimAndIdempotent()
    {
        // Per iter-2 evaluator item 5 the signature changed from
        // Task → Task<bool> to support cross-process atomic claim:
        //   true  = THIS caller transitioned the row Pending/AwaitingComment → TimedOut
        //   false = the row was already in a terminal state, OR did not exist
        // Implementation uses EF Core 8 ExecuteUpdateAsync to emit a
        // single conditional UPDATE — see PersistentPendingQuestionStore.
        await _store.StoreAsync(new AgentQuestionEnvelope { Question = BuildQuestion() }, 42, 1, default);

        var first = await _store.MarkTimedOutAsync("q-1", default);
        first.Should().BeTrue("the first caller against a Pending row must win the atomic claim");

        var second = await _store.MarkTimedOutAsync("q-1", default);
        second.Should().BeFalse("a second caller against an already-TimedOut row must lose the claim — this is the cross-process safety guarantee the QuestionTimeoutService relies on to avoid double-publish");

        var missing = await _store.MarkTimedOutAsync("does-not-exist", default);
        missing.Should().BeFalse("a claim against a non-existent row must return false, not throw");

        var row = await _store.GetAsync("q-1", default);
        row!.Status.Should().Be(PendingQuestionStatus.TimedOut);
    }

    [Fact]
    public async Task MarkTimedOutAsync_FromAwaitingComment_AlsoWinsTheClaim()
    {
        // AwaitingComment is the OTHER non-terminal status — the
        // atomic claim must include it (the ExecuteUpdateAsync WHERE
        // clause filters on Status IN (Pending, AwaitingComment))
        // because a question awaiting a comment can also time out.
        await _store.StoreAsync(new AgentQuestionEnvelope { Question = BuildQuestion() }, 42, 1, default);
        await _store.MarkAwaitingCommentAsync("q-1", default);

        var claimed = await _store.MarkTimedOutAsync("q-1", default);
        claimed.Should().BeTrue("AwaitingComment → TimedOut is a valid transition; the WHERE clause must accept either non-terminal state");

        var row = await _store.GetAsync("q-1", default);
        row!.Status.Should().Be(PendingQuestionStatus.TimedOut);
    }

    [Fact]
    public async Task MarkTimedOutAsync_FromAnswered_LosesTheClaim()
    {
        // Answered is terminal — a question that has been answered
        // by the operator must NOT be timed out from underneath.
        await _store.StoreAsync(new AgentQuestionEnvelope { Question = BuildQuestion() }, 42, 1, default);
        await _store.MarkAnsweredAsync("q-1", default);

        var claimed = await _store.MarkTimedOutAsync("q-1", default);
        claimed.Should().BeFalse("Answered is terminal; the timeout claim must not flip an already-answered row");

        var row = await _store.GetAsync("q-1", default);
        row!.Status.Should().Be(PendingQuestionStatus.Answered,
            "the row must remain Answered — the operator's decision wins over a late sweep");
    }

    [Fact]
    public async Task TryRevertTimedOutClaimAsync_FromTimedOut_WinsAndRestoresPriorStatus()
    {
        // Iter-3 evaluator item 1 fix — the new compensating action
        // for the at-least-once timeout-delivery contract. When the
        // QuestionTimeoutService's publish fails after the atomic
        // claim, it calls TryRevertTimedOutClaimAsync(priorStatus) to
        // re-arm the row for the next sweep.
        await _store.StoreAsync(new AgentQuestionEnvelope { Question = BuildQuestion() }, 42, 1, default);
        (await _store.MarkTimedOutAsync("q-1", default)).Should().BeTrue();

        var reverted = await _store.TryRevertTimedOutClaimAsync("q-1", PendingQuestionStatus.Pending, default);
        reverted.Should().BeTrue("the row was TimedOut; the conditional UPDATE matches and the revert wins");

        var row = await _store.GetAsync("q-1", default);
        row!.Status.Should().Be(PendingQuestionStatus.Pending,
            "the revert must put the row back to its pre-claim status so the next GetExpiredAsync re-finds it");
    }

    [Fact]
    public async Task TryRevertTimedOutClaimAsync_OnPendingRow_LosesTheRevert()
    {
        // The conditional UPDATE filters on Status == TimedOut, so a
        // revert against any non-TimedOut row must return false
        // without mutating state — guards against a stale revert that
        // would race with an unrelated callback.
        await _store.StoreAsync(new AgentQuestionEnvelope { Question = BuildQuestion() }, 42, 1, default);

        var reverted = await _store.TryRevertTimedOutClaimAsync("q-1", PendingQuestionStatus.Pending, default);
        reverted.Should().BeFalse("the row is Pending, not TimedOut; the conditional UPDATE matches zero rows");

        var row = await _store.GetAsync("q-1", default);
        row!.Status.Should().Be(PendingQuestionStatus.Pending,
            "state must be untouched on a lost revert");
    }

    [Fact]
    public async Task TryRevertTimedOutClaimAsync_OnAnsweredRow_LosesTheRevert()
    {
        // Defends the operator's decision against a late mis-revert
        // — Answered is terminal so the revert must NOT undo it.
        await _store.StoreAsync(new AgentQuestionEnvelope { Question = BuildQuestion() }, 42, 1, default);
        await _store.MarkAnsweredAsync("q-1", default);

        var reverted = await _store.TryRevertTimedOutClaimAsync("q-1", PendingQuestionStatus.Pending, default);
        reverted.Should().BeFalse("Answered is not TimedOut; the conditional UPDATE matches zero rows and the operator's decision is preserved");

        var row = await _store.GetAsync("q-1", default);
        row!.Status.Should().Be(PendingQuestionStatus.Answered);
    }

    [Fact]
    public async Task TryRevertTimedOutClaimAsync_InvalidRevertTo_ThrowsArgumentException()
    {
        // The interface contract restricts revertTo to non-terminal
        // pre-claim values; a revert to TimedOut would be a no-op
        // self-loop and a revert to Answered would silently flip
        // semantics — both must throw.
        await _store.StoreAsync(new AgentQuestionEnvelope { Question = BuildQuestion() }, 42, 1, default);
        await _store.MarkTimedOutAsync("q-1", default);

        var act1 = async () => await _store.TryRevertTimedOutClaimAsync("q-1", PendingQuestionStatus.TimedOut, default);
        await act1.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var act2 = async () => await _store.TryRevertTimedOutClaimAsync("q-1", PendingQuestionStatus.Answered, default);
        await act2.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task StoreAsync_DbUpdateException_NonDuplicateFailure_PropagatesToCaller()
    {
        // Iter-3 evaluator item 2 + iter-4 evaluator item 4 — the
        // narrowed catch in StoreAsync's INSERT branch must rethrow
        // when the DbUpdateException is NOT a duplicate-key race
        // (i.e. the re-query inside the catch returns null). A
        // dedicated SaveChangesInterceptor injects a synthetic
        // DbUpdateException at the SAVE boundary so the failure path
        // runs against the real production code (no SaveChanges ever
        // commits → the re-query inside the catch finds nothing →
        // the exception must propagate so TelegramMessageSender can
        // wrap it in PendingQuestionPersistenceException).
        var injector = new ThrowOnSaveInterceptor("q-real-failure");
        await using var failingConnection = new SqliteConnection("Filename=:memory:");
        await failingConnection.OpenAsync();
        var failingServices = new ServiceCollection();
        failingServices.AddDbContext<MessagingDbContext>(o => o
            .UseSqlite(failingConnection)
            .AddInterceptors(injector));
        await using var failingProvider = failingServices.BuildServiceProvider();
        await using (var scope = failingProvider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            await ctx.Database.EnsureCreatedAsync();
        }
        var failingStore = new PersistentPendingQuestionStore(
            failingProvider.GetRequiredService<IServiceScopeFactory>(),
            _time,
            NullLogger<PersistentPendingQuestionStore>.Instance);

        var envelope = new AgentQuestionEnvelope { Question = BuildQuestion("q-real-failure") };

        var act = async () => await failingStore.StoreAsync(envelope, 42, 5001, default);

        // The synthetic DbUpdateException must surface — NOT be
        // swallowed as a benign duplicate — because the re-query
        // returns null (no row was ever committed).
        await act.Should().ThrowAsync<DbUpdateException>()
            .WithMessage("*synthetic non-duplicate failure*");

        // Sanity check: the row truly is not in the table. This is
        // what makes the failure non-recoverable at the store
        // boundary and forces the caller to wrap.
        var roundTrip = await failingStore.GetAsync("q-real-failure", default);
        roundTrip.Should().BeNull(
            "the synthetic failure prevented any commit; the re-query inside StoreAsync's catch sees the same and must rethrow");
    }

    [Fact]
    public async Task StoreAsync_DbUpdateException_DuplicateKeyRace_RecoversViaReQueryAndUpdate()
    {
        // The complementary path: a DbUpdateException ON THE INSERT
        // BRANCH where the re-query DOES find a row must NOT throw
        // (the durable contract is satisfied — another writer
        // committed first). We simulate the race by inserting the
        // row directly via a separate scope BEFORE the store's
        // SaveChanges runs.
        var injector = new RacingInserter("q-raced", _time.GetUtcNow());
        await using var racedConnection = new SqliteConnection("Filename=:memory:");
        await racedConnection.OpenAsync();
        var racedServices = new ServiceCollection();
        racedServices.AddDbContext<MessagingDbContext>(o => o
            .UseSqlite(racedConnection)
            .AddInterceptors(injector));
        await using var racedProvider = racedServices.BuildServiceProvider();
        await using (var scope = racedProvider.CreateAsyncScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            await ctx.Database.EnsureCreatedAsync();
        }
        injector.ScopeFactory = racedProvider.GetRequiredService<IServiceScopeFactory>();

        var racedStore = new PersistentPendingQuestionStore(
            racedProvider.GetRequiredService<IServiceScopeFactory>(),
            _time,
            NullLogger<PersistentPendingQuestionStore>.Instance);

        var envelope = new AgentQuestionEnvelope { Question = BuildQuestion("q-raced") };

        var act = async () => await racedStore.StoreAsync(envelope, 42, 7777, default);

        await act.Should().NotThrowAsync(
            "the duplicate-key race must be recovered silently via re-query + UPDATE — only NON-duplicate failures rethrow");

        var row = await racedStore.GetAsync("q-raced", default);
        row.Should().NotBeNull();
        row!.TelegramMessageId.Should().Be(7777,
            "the catch's re-query + UPDATE branch must have refreshed the winner with our incoming envelope");
    }

    private sealed class ThrowOnSaveInterceptor : SaveChangesInterceptor
    {
        private readonly string _failOnQuestionId;

        public ThrowOnSaveInterceptor(string failOnQuestionId)
        {
            _failOnQuestionId = failOnQuestionId;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var ctx = eventData.Context;
            if (ctx is not null)
            {
                foreach (var entry in ctx.ChangeTracker.Entries<PendingQuestionRecord>())
                {
                    if (entry.State == EntityState.Added &&
                        entry.Entity.QuestionId == _failOnQuestionId)
                    {
                        throw new DbUpdateException(
                            "synthetic non-duplicate failure (e.g. CHECK / FK / permission)",
                            new InvalidOperationException("injected by ThrowOnSaveInterceptor"));
                    }
                }
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class RacingInserter : SaveChangesInterceptor
    {
        private readonly string _raceOnQuestionId;
        private readonly DateTimeOffset _now;
        private bool _hasInjected;

        public RacingInserter(string raceOnQuestionId, DateTimeOffset now)
        {
            _raceOnQuestionId = raceOnQuestionId;
            _now = now;
        }

        public IServiceScopeFactory? ScopeFactory { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var ctx = eventData.Context;
            if (!_hasInjected && ctx is not null && ScopeFactory is not null)
            {
                foreach (var entry in ctx.ChangeTracker.Entries<PendingQuestionRecord>())
                {
                    if (entry.State == EntityState.Added &&
                        entry.Entity.QuestionId == _raceOnQuestionId)
                    {
                        _hasInjected = true;

                        // Insert the racing row via a separate scope
                        // BEFORE our SaveChanges runs. SQLite's PK
                        // constraint will then trigger a real
                        // DbUpdateException on our SaveChanges below.
                        using (var raceScope = ScopeFactory.CreateScope())
                        {
                            var raceCtx = raceScope.ServiceProvider
                                .GetRequiredService<MessagingDbContext>();
                            raceCtx.PendingQuestions.Add(new PendingQuestionRecord
                            {
                                QuestionId = entry.Entity.QuestionId,
                                AgentQuestionJson = "{}",
                                TelegramChatId = 99,
                                TelegramMessageId = 1,
                                StoredAt = _now,
                                ExpiresAt = _now.AddMinutes(15),
                                Status = PendingQuestionStatus.Pending,
                                CorrelationId = "race-winner",
                            });
                            await raceCtx.SaveChangesAsync(cancellationToken)
                                .ConfigureAwait(false);
                        }

                        break;
                    }
                }
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task RecordSelectionAsync_PersistsSelectedActionAndRespondent()
    {
        await _store.StoreAsync(new AgentQuestionEnvelope { Question = BuildQuestion() }, 42, 1, default);
        await _store.RecordSelectionAsync("q-1", "approve", "approve_v", 555, default);
        var row = await _store.GetAsync("q-1", default);
        row!.SelectedActionId.Should().Be("approve");
        row.SelectedActionValue.Should().Be("approve_v");
        row.RespondentUserId.Should().Be(555);
    }

    [Fact]
    public async Task GetAwaitingCommentAsync_ReturnsOldestForChatUserStatus()
    {
        // Two awaiting-comment rows for the same (chat, user); the
        // store must return the OLDEST by StoredAt for deterministic
        // tie-breaking (architecture.md §3.1).
        var t0 = _time.GetUtcNow();
        await _store.StoreAsync(
            new AgentQuestionEnvelope { Question = BuildQuestion("q-old", "trace-old") },
            42, 1001, default);
        await _store.RecordSelectionAsync("q-old", "comment", "comment_v", 777, default);
        await _store.MarkAwaitingCommentAsync("q-old", default);

        _time.Advance(TimeSpan.FromSeconds(5));

        await _store.StoreAsync(
            new AgentQuestionEnvelope { Question = BuildQuestion("q-new", "trace-new") },
            42, 1002, default);
        await _store.RecordSelectionAsync("q-new", "comment", "comment_v", 777, default);
        await _store.MarkAwaitingCommentAsync("q-new", default);

        var row = await _store.GetAwaitingCommentAsync(42, 777, default);
        row!.QuestionId.Should().Be("q-old",
            "GetAwaitingCommentAsync must order by StoredAt asc and return the oldest match");
    }

    [Fact]
    public async Task GetExpiredAsync_ReturnsPendingAndAwaitingCommentRowsWithPastExpiry()
    {
        // Three rows: one Pending+expired, one AwaitingComment+expired,
        // one Pending+future, one Answered+expired (terminal; not eligible).
        var future = BuildQuestion("q-future", "trace-fut") with
        {
            ExpiresAt = _time.GetUtcNow().AddHours(1),
        };
        var past1 = BuildQuestion("q-past1", "trace-p1") with
        {
            ExpiresAt = _time.GetUtcNow().AddMinutes(-1),
        };
        var past2 = BuildQuestion("q-past2", "trace-p2") with
        {
            ExpiresAt = _time.GetUtcNow().AddMinutes(-5),
        };
        var past3 = BuildQuestion("q-past3", "trace-p3") with
        {
            ExpiresAt = _time.GetUtcNow().AddMinutes(-3),
        };

        await _store.StoreAsync(new AgentQuestionEnvelope { Question = future }, 1, 1, default);
        await _store.StoreAsync(new AgentQuestionEnvelope { Question = past1 }, 1, 2, default);
        await _store.StoreAsync(new AgentQuestionEnvelope { Question = past2 }, 1, 3, default);
        await _store.StoreAsync(new AgentQuestionEnvelope { Question = past3 }, 1, 4, default);
        await _store.MarkAwaitingCommentAsync("q-past2", default);
        await _store.MarkAnsweredAsync("q-past3", default);

        var expired = await _store.GetExpiredAsync(default);
        expired.Select(p => p.QuestionId).Should().BeEquivalentTo(new[] { "q-past1", "q-past2" },
            "Pending+past and AwaitingComment+past are eligible; Answered+past is terminal and skipped; Pending+future is not expired yet");
    }
}
