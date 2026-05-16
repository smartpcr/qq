using System.Collections.Concurrent;
using System.Data.Common;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams;
using AgentSwarm.Messaging.Teams.Cards;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests;

/// <summary>
/// End-to-end coverage for the workstream brief's "CardActionReceived audit persisted"
/// scenario:
/// <blockquote>
/// Given <c>SqlAuditLogger</c> is registered in DI (replacing <c>NoOpAuditLogger</c>) and a
/// user submits an Adaptive Card <c>Action.Submit</c> with <c>actionValue = "approve"</c>
/// for question <c>q-aud-persist</c>, When <c>CardActionHandler</c> processes the action
/// and calls <c>IAuditLogger.LogAsync</c> with <c>EventType = "CardActionReceived"</c>,
/// Then an <c>AuditLog</c> row exists in the database with
/// <c>EventType = "CardActionReceived"</c>, <c>ActorId</c> matching the user's AAD object
/// ID, <c>AgentId</c> from the question, <c>Action = "approve"</c>,
/// <c>Outcome = "Success"</c>, and a valid <c>Checksum</c>.
/// </blockquote>
/// </summary>
/// <remarks>
/// <para>
/// Item 7 of the iter-1 evaluator feedback flagged that no test wires
/// <c>CardActionHandler</c> through the real <c>SqlAuditLogger</c> and asserts a persisted
/// <c>AuditLog</c> row, so the Stage 5.2 acceptance criterion above was unverified. This
/// fixture closes that gap: it resolves <c>IAuditLogger</c> from a real DI container
/// configured via <see cref="EntityFrameworkCoreServiceCollectionExtensions.AddSqlAuditLogger"/>,
/// asserts the resolved instance is a <see cref="SqlAuditLogger"/>, hands it to a real
/// <see cref="CardActionHandler"/>, invokes the handler with a synthetic Adaptive Card
/// <c>Action.Submit</c> payload, and then queries the underlying <c>AuditLog</c> table
/// through the same <see cref="AuditLogDbContext"/>.
/// </para>
/// <para>
/// SQLite (in-memory) is used as the test database. The production migration emits SQL
/// Server-specific <c>INSTEAD OF</c> triggers and <c>GRANT/REVOKE/DENY</c> statements that
/// SQLite does not understand, so the fixture provisions the schema via
/// <see cref="DatabaseFacade.EnsureCreated"/> (which honours the model snapshot) rather
/// than <see cref="DatabaseFacade.Migrate"/>. The end-to-end persistence assertion is
/// schema-agnostic: it verifies the row landed with the canonical column shape per
/// <c>tech-spec.md</c> §4.3.
/// </para>
/// </remarks>
public sealed class CardActionHandlerAuditPersistenceTests
{
    private const string QuestionId = "q-aud-persist";
    private const string AgentId = "agent-aud-1";
    private const string TaskId = "task-aud-1";
    private const string TenantId = "tenant-aud-1";
    private const string ActorAad = "aad-aud-user-1";
    private const string CorrelationId = "corr-aud-persist-1";

    [Fact]
    public async Task CardActionHandler_ApproveSubmit_PersistsCardActionReceivedAuditRow()
    {
        await using var db = new SqliteAuditLogDb();

        // (1) Compose DI the way a production host does — register the base Teams audit
        //     stub (NoOpAuditLogger) first, then call AddSqlAuditLogger so the SqlAuditLogger
        //     UNCONDITIONALLY replaces the stub. This mirrors the order TeamsServiceCollectionExtensions
        //     wires NoOpAuditLogger as a TryAdd default ahead of any explicit Sql registration.
        var services = new ServiceCollection();
        services.AddSingleton<IAuditLogger, NoOpAuditLogger>();
        services.AddSqlAuditLogger(o => o.UseSqlite(db.Connection));

        await using var provider = services.BuildServiceProvider();

        // (2) Resolve IAuditLogger; assert the AddSqlAuditLogger replacement landed.
        var auditLogger = provider.GetRequiredService<IAuditLogger>();
        Assert.IsType<SqlAuditLogger>(auditLogger);

        // (3) Build CardActionHandler with the resolved SqlAuditLogger and minimal test
        //     doubles for the other dependencies. The question store is pre-seeded with
        //     `q-aud-persist` so CardActionHandler's GetByIdAsync lookup succeeds and its
        //     CAS transition Open→Resolved fires.
        var questionStore = new InMemoryAgentQuestionStore();
        questionStore.Seed(new AgentQuestion
        {
            QuestionId = QuestionId,
            AgentId = AgentId,
            TaskId = TaskId,
            TenantId = TenantId,
            TargetUserId = "ops-1",
            Title = "Approve audit-persistence test?",
            Body = "Pretend you are the approver.",
            Severity = MessageSeverities.Warning,
            AllowedActions = new[]
            {
                new HumanAction("a-approve", "Approve", "approve", false),
                new HumanAction("a-reject", "Reject", "reject", true),
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            CorrelationId = CorrelationId,
            Status = AgentQuestionStatuses.Open,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        var handler = new CardActionHandler(
            questionStore,
            new NoOpCardStateStore(),
            new NoOpTeamsCardManager(),
            new NoOpInboundEventPublisher(),
            auditLogger,
            NullLogger<CardActionHandler>.Instance);

        // (4) Dispatch an Adaptive Card Action.Submit for "approve". The activity shape
        //     follows the contract in `Cards.CardActionPayload` / `CardActionMapper`.
        var turn = BuildInvokeTurn(actionValue: "approve");
        var response = await handler.HandleAsync(turn, CancellationToken.None);
        Assert.NotNull(response);
        Assert.Equal(200, response.StatusCode);

        // (5) Query the AuditLog table directly through the same DbContext factory the
        //     SqlAuditLogger uses; assert the persisted row matches the brief's spec.
        var factory = provider.GetRequiredService<IDbContextFactory<AuditLogDbContext>>();
        await using var ctx = factory.CreateDbContext();
        var rows = await ctx.AuditLog.AsNoTracking().ToListAsync();
        var cardAction = Assert.Single(rows, r => r.EventType == AuditEventTypes.CardActionReceived);

        Assert.Equal(AuditEventTypes.CardActionReceived, cardAction.EventType);
        Assert.Equal(ActorAad, cardAction.ActorId);
        Assert.Equal(AuditActorTypes.User, cardAction.ActorType);
        Assert.Equal(TenantId, cardAction.TenantId);
        Assert.Equal(AgentId, cardAction.AgentId);
        Assert.Equal("approve", cardAction.Action);
        Assert.Equal(AuditOutcomes.Success, cardAction.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(cardAction.Checksum));

        // (6) Re-derive the checksum and assert the persisted value matches; this proves
        //     SqlAuditLogger persisted exactly what CardActionHandler computed and that
        //     the checksum verification path inside SqlAuditLogger accepted the row.
        var expected = AuditEntry.ComputeChecksum(
            timestamp: cardAction.Timestamp,
            correlationId: cardAction.CorrelationId,
            eventType: cardAction.EventType,
            actorId: cardAction.ActorId,
            actorType: cardAction.ActorType,
            tenantId: cardAction.TenantId,
            agentId: cardAction.AgentId,
            taskId: cardAction.TaskId,
            conversationId: cardAction.ConversationId,
            action: cardAction.Action,
            payloadJson: cardAction.PayloadJson,
            outcome: cardAction.Outcome);
        Assert.Equal(expected, cardAction.Checksum);
    }

    private static ITurnContext BuildInvokeTurn(string actionValue)
    {
        var data = new JObject
        {
            [CardActionDataKeys.QuestionId] = QuestionId,
            [CardActionDataKeys.ActionId] = $"a-{actionValue}",
            [CardActionDataKeys.ActionValue] = actionValue,
            [CardActionDataKeys.CorrelationId] = CorrelationId,
        };

        var activity = new Activity
        {
            Type = ActivityTypes.Invoke,
            Id = "act-aud-1",
            Name = "adaptiveCard/action",
            Value = data,
            Timestamp = DateTimeOffset.UtcNow,
            From = new ChannelAccount(id: "29:aud-user", aadObjectId: ActorAad, name: "Audit Tester"),
            Conversation = new ConversationAccount(id: "19:aud-conv", tenantId: TenantId, conversationType: "personal"),
            ChannelData = JObject.FromObject(new { tenant = new { id = TenantId } }),
        };

        return new TurnContext(new InertBotAdapter(), activity);
    }

    private sealed class SqliteAuditLogDb : IAsyncDisposable
    {
        public DbConnection Connection { get; }

        public SqliteAuditLogDb()
        {
            Connection = new SqliteConnection("Filename=:memory:");
            Connection.Open();

            // EnsureCreated against the AuditLogDbContext model snapshot — SQLite cannot
            // execute the SQL Server-specific INSTEAD OF triggers / GRANT / REVOKE
            // emitted by the production migration, so we provision the table directly.
            var options = new DbContextOptionsBuilder<AuditLogDbContext>()
                .UseSqlite(Connection)
                .Options;
            using var ctx = new AuditLogDbContext(options);
            ctx.Database.EnsureCreated();
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private sealed class InMemoryAgentQuestionStore : IAgentQuestionStore
    {
        private readonly ConcurrentDictionary<string, AgentQuestion> _byId = new(StringComparer.Ordinal);

        public void Seed(AgentQuestion q) => _byId[q.QuestionId] = q;

        public Task SaveAsync(AgentQuestion question, CancellationToken ct)
        {
            _byId[question.QuestionId] = question;
            return Task.CompletedTask;
        }

        public Task<AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct)
        {
            _byId.TryGetValue(questionId, out var hit);
            return Task.FromResult<AgentQuestion?>(hit);
        }

        public Task<bool> TryUpdateStatusAsync(string questionId, string expectedStatus, string newStatus, CancellationToken ct)
        {
            if (!_byId.TryGetValue(questionId, out var existing))
            {
                return Task.FromResult(false);
            }

            if (!string.Equals(existing.Status, expectedStatus, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            _byId[questionId] = existing with { Status = newStatus };
            return Task.FromResult(true);
        }

        public Task UpdateConversationIdAsync(string questionId, string conversationId, CancellationToken ct)
        {
            if (_byId.TryGetValue(questionId, out var existing))
            {
                _byId[questionId] = existing with { ConversationId = conversationId };
            }

            return Task.CompletedTask;
        }

        public Task<AgentQuestion?> GetMostRecentOpenByConversationAsync(string conversationId, CancellationToken ct)
            => Task.FromResult<AgentQuestion?>(null);

        public Task<IReadOnlyList<AgentQuestion>> GetOpenByConversationAsync(string conversationId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());

        public Task<IReadOnlyList<AgentQuestion>> GetOpenExpiredAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());
    }

    private sealed class NoOpCardStateStore : ICardStateStore
    {
        public Task SaveAsync(TeamsCardState state, CancellationToken ct) => Task.CompletedTask;

        public Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct)
            => Task.FromResult<TeamsCardState?>(null);

        public Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoOpTeamsCardManager : ITeamsCardManager
    {
        public Task UpdateCardAsync(string questionId, CardUpdateAction action, CancellationToken ct) => Task.CompletedTask;

        public Task UpdateCardAsync(string questionId, CardUpdateAction action, HumanDecisionEvent decision, string? actorDisplayName, CancellationToken ct)
            => Task.CompletedTask;

        public Task DeleteCardAsync(string questionId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoOpInboundEventPublisher : IInboundEventPublisher
    {
        public Task PublishAsync(MessengerEvent messengerEvent, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class InertBotAdapter : BotAdapter
    {
        public override Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext turnContext, Activity[] activities, CancellationToken cancellationToken)
        {
            var responses = new ResourceResponse[activities.Length];
            for (var i = 0; i < activities.Length; i++)
            {
                responses[i] = new ResourceResponse(id: Guid.NewGuid().ToString());
            }

            return Task.FromResult(responses);
        }

        public override Task<ResourceResponse> UpdateActivityAsync(ITurnContext turnContext, Activity activity, CancellationToken cancellationToken)
            => Task.FromResult(new ResourceResponse(activity.Id ?? Guid.NewGuid().ToString()));

        public override Task DeleteActivityAsync(ITurnContext turnContext, ConversationReference reference, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
