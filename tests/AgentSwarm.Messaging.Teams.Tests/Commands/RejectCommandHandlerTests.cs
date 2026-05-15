using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Commands;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentSwarm.Messaging.Teams.Tests.HandlerFactory;
using static AgentSwarm.Messaging.Teams.Tests.TestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests.Commands;

/// <summary>
/// Stage 3.2 <see cref="RejectCommandHandler"/> tests — mirror of the approve handler
/// coverage covering the same resolution paths but emitting <c>"reject"</c> as the action
/// value (per <c>implementation-plan.md</c> §3.2 step 4).
/// </summary>
public sealed class RejectCommandHandlerTests
{
    private const string TenantId = "tenant-1";
    private const string ConversationId = "conv-1";

    private static AgentQuestion BuildOpenQuestion(
        string questionId,
        bool requiresComment = false,
        IReadOnlyList<HumanAction>? allowedActions = null,
        DateTimeOffset? createdAt = null)
    {
        return new AgentQuestion
        {
            QuestionId = questionId,
            AgentId = "agent-1",
            TaskId = "task-1",
            TenantId = TenantId,
            TargetUserId = "user-internal-1",
            TargetChannelId = null,
            Title = $"Question {questionId}",
            Body = "Body",
            Severity = MessageSeverities.Info,
            AllowedActions = allowedActions ?? new[]
            {
                new HumanAction("approve", "Approve", "approve", requiresComment),
                new HumanAction("reject", "Reject", "reject", requiresComment),
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            ConversationId = ConversationId,
            CorrelationId = "corr-q-1",
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            Status = AgentQuestionStatuses.Open,
        };
    }

    private static (TurnContext Context, InertBotAdapter Adapter) BuildTurnContext()
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Message,
            Id = "activity-1",
            Text = "reject",
            Conversation = new ConversationAccount { Id = ConversationId },
            From = new ChannelAccount(id: "from"),
            Recipient = new ChannelAccount(id: "bot"),
        };
        var adapter = new InertBotAdapter();
        var ctx = new TurnContext(adapter, activity);
        return (ctx, adapter);
    }

    private static CommandContext BuildContext(string commandArguments, ITurnContext turn)
    {
        return new CommandContext
        {
            NormalizedText = string.IsNullOrEmpty(commandArguments) ? "reject" : $"reject {commandArguments}",
            ResolvedIdentity = new UserIdentity("user-internal-1", "aad-obj-1", "Test", "Approver"),
            CorrelationId = "corr-1",
            TurnContext = turn,
            ConversationId = ConversationId,
            ActivityId = "activity-1",
            CommandArguments = commandArguments,
        };
    }

    private static RejectCommandHandler BuildHandler(IAgentQuestionStore store, IInboundEventPublisher publisher)
    {
        return new RejectCommandHandler(
            store,
            publisher,
            new AdaptiveCardBuilder(),
            NullLogger<RejectCommandHandler>.Instance);
    }

    [Fact]
    public void CommandName_IsCanonical()
    {
        var handler = BuildHandler(new InMemoryAgentQuestionStore(), new RecordingInboundEventPublisher());
        Assert.Equal(CommandNames.Reject, handler.CommandName);
    }

    [Fact]
    public async Task Explicit_QuestionId_Rejects_AndEmitsDecisionEvent()
    {
        var store = new InMemoryAgentQuestionStore();
        store.Seed(BuildOpenQuestion("q-456"));
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, adapter) = BuildTurnContext();

        await handler.HandleAsync(BuildContext("q-456", turn), CancellationToken.None);

        Assert.Equal(new[] { "q-456" }, store.GetByIdCalls);
        var transition = Assert.Single(store.StatusTransitionCalls);
        Assert.Equal("q-456", transition.QuestionId);
        Assert.Equal(AgentQuestionStatuses.Open, transition.Expected);
        Assert.Equal(AgentQuestionStatuses.Resolved, transition.New);

        var ev = Assert.IsType<DecisionEvent>(Assert.Single(publisher.Published));
        Assert.Equal("q-456", ev.Payload.QuestionId);
        Assert.Equal("reject", ev.Payload.ActionValue);
        Assert.Single(adapter.Sent);
    }

    [Fact]
    public async Task Bare_SingleOpenQuestion_AutoRejects()
    {
        var store = new InMemoryAgentQuestionStore();
        store.Seed(BuildOpenQuestion("q-only"));
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, _) = BuildTurnContext();

        await handler.HandleAsync(BuildContext(string.Empty, turn), CancellationToken.None);

        Assert.Equal(new[] { ConversationId }, store.GetOpenByConversationCalls);
        var ev = Assert.IsType<DecisionEvent>(Assert.Single(publisher.Published));
        Assert.Equal("reject", ev.Payload.ActionValue);
        Assert.Equal("q-only", ev.Payload.QuestionId);
    }

    [Fact]
    public async Task Bare_ZeroOpenQuestions_Replies_AndDoesNotPublish()
    {
        var store = new InMemoryAgentQuestionStore();
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, adapter) = BuildTurnContext();

        await handler.HandleAsync(BuildContext(string.Empty, turn), CancellationToken.None);

        Assert.Empty(publisher.Published);
        Assert.Single(adapter.Sent);
    }

    [Fact]
    public async Task Bare_MultipleOpenQuestions_RendersDisambiguationCard()
    {
        var store = new InMemoryAgentQuestionStore();
        store.Seed(BuildOpenQuestion("q-A"));
        store.Seed(BuildOpenQuestion("q-B"));
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, adapter) = BuildTurnContext();

        await handler.HandleAsync(BuildContext(string.Empty, turn), CancellationToken.None);

        Assert.Empty(publisher.Published);
        Assert.Empty(store.StatusTransitionCalls);
        var sent = Assert.Single(adapter.Sent);
        var attachment = Assert.Single(sent.Attachments);
        var json = attachment.Content!.ToString()!;
        Assert.Contains("q-A", json, StringComparison.Ordinal);
        Assert.Contains("q-B", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reject_NotInAllowedActions_RepliesError()
    {
        var store = new InMemoryAgentQuestionStore();
        store.Seed(BuildOpenQuestion("q-only-approve", allowedActions: new[]
        {
            new HumanAction("approve", "Approve", "approve", RequiresComment: false),
        }));
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, _) = BuildTurnContext();

        await handler.HandleAsync(BuildContext("q-only-approve", turn), CancellationToken.None);

        Assert.Empty(store.StatusTransitionCalls);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task Reject_RequiresComment_RepliesError()
    {
        var store = new InMemoryAgentQuestionStore();
        store.Seed(BuildOpenQuestion("q-comment", requiresComment: true));
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, _) = BuildTurnContext();

        await handler.HandleAsync(BuildContext("q-comment", turn), CancellationToken.None);

        Assert.Empty(store.StatusTransitionCalls);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task Reject_CasRace_RepliesAlreadyRecorded()
    {
        var store = new InMemoryAgentQuestionStore();
        store.Seed(BuildOpenQuestion("q-r"));
        store.ForceTransitionFailure = true;
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, _) = BuildTurnContext();

        await handler.HandleAsync(BuildContext("q-r", turn), CancellationToken.None);

        Assert.Single(store.StatusTransitionCalls);
        Assert.Empty(publisher.Published);
    }
}
