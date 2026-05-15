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
/// Stage 3.2 <see cref="ApproveCommandHandler"/> tests covering the resolution paths from
/// <c>implementation-plan.md</c> §3.2 step 4 — explicit ID, bare-single auto-resolve,
/// bare-zero "no open questions", bare-multi disambiguation, action-not-in-AllowedActions,
/// RequiresComment refusal, and the first-writer-wins CAS race.
/// </summary>
public sealed class ApproveCommandHandlerTests
{
    private const string TenantId = "tenant-1";
    private const string ConversationId = "conv-1";

    private static AgentQuestion BuildOpenQuestion(
        string questionId,
        string conversationId = ConversationId,
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
            ConversationId = conversationId,
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
            Text = "approve",
            Conversation = new ConversationAccount { Id = ConversationId },
            From = new ChannelAccount(id: "from"),
            Recipient = new ChannelAccount(id: "bot"),
        };
        var adapter = new InertBotAdapter();
        var ctx = new TurnContext(adapter, activity);
        return (ctx, adapter);
    }

    private static CommandContext BuildContext(string commandArguments, ITurnContext? turn = null)
    {
        return new CommandContext
        {
            NormalizedText = string.IsNullOrEmpty(commandArguments) ? "approve" : $"approve {commandArguments}",
            ResolvedIdentity = new UserIdentity("user-internal-1", "aad-obj-1", "Test", "Approver"),
            CorrelationId = "corr-1",
            TurnContext = turn,
            ConversationId = ConversationId,
            ActivityId = "activity-1",
            CommandArguments = commandArguments,
        };
    }

    private static ApproveCommandHandler BuildHandler(
        IAgentQuestionStore store,
        IInboundEventPublisher publisher)
    {
        return new ApproveCommandHandler(
            store,
            publisher,
            new AdaptiveCardBuilder(),
            NullLogger<ApproveCommandHandler>.Instance);
    }

    [Fact]
    public void CommandName_IsCanonical()
    {
        var handler = BuildHandler(new InMemoryAgentQuestionStore(), new RecordingInboundEventPublisher());
        Assert.Equal(CommandNames.Approve, handler.CommandName);
    }

    [Fact]
    public async Task Explicit_QuestionId_Approves_AndEmitsDecisionEvent()
    {
        var store = new InMemoryAgentQuestionStore();
        store.Seed(BuildOpenQuestion("q-123"));
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, adapter) = BuildTurnContext();

        await handler.HandleAsync(BuildContext("q-123", turn), CancellationToken.None);

        Assert.Equal(new[] { "q-123" }, store.GetByIdCalls);
        Assert.Empty(store.GetOpenByConversationCalls);
        var transition = Assert.Single(store.StatusTransitionCalls);
        Assert.Equal("q-123", transition.QuestionId);
        Assert.Equal(AgentQuestionStatuses.Open, transition.Expected);
        Assert.Equal(AgentQuestionStatuses.Resolved, transition.New);

        var ev = Assert.IsType<DecisionEvent>(Assert.Single(publisher.Published));
        Assert.Equal(MessengerEventTypes.Decision, ev.EventType);
        Assert.Equal("q-123", ev.Payload.QuestionId);
        Assert.Equal("approve", ev.Payload.ActionValue);
        Assert.Null(ev.Payload.Comment);
        Assert.Equal("Teams", ev.Messenger);
        Assert.Equal("aad-obj-1", ev.ExternalUserId);

        // Confirmation card was sent.
        Assert.Single(adapter.Sent);
    }

    [Fact]
    public async Task Explicit_UnknownQuestionId_RepliesErrorAndDoesNotPublish()
    {
        var store = new InMemoryAgentQuestionStore();
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, adapter) = BuildTurnContext();

        await handler.HandleAsync(BuildContext("q-missing", turn), CancellationToken.None);

        Assert.Equal(new[] { "q-missing" }, store.GetByIdCalls);
        Assert.Empty(store.StatusTransitionCalls);
        Assert.Empty(publisher.Published);
        Assert.Single(adapter.Sent);
    }

    [Fact]
    public async Task Bare_SingleOpenQuestion_AutoResolves()
    {
        var store = new InMemoryAgentQuestionStore();
        store.Seed(BuildOpenQuestion("q-789"));
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, _) = BuildTurnContext();

        await handler.HandleAsync(BuildContext(string.Empty, turn), CancellationToken.None);

        Assert.Empty(store.GetByIdCalls);
        Assert.Equal(new[] { ConversationId }, store.GetOpenByConversationCalls);
        var transition = Assert.Single(store.StatusTransitionCalls);
        Assert.Equal("q-789", transition.QuestionId);

        var ev = Assert.IsType<DecisionEvent>(Assert.Single(publisher.Published));
        Assert.Equal("q-789", ev.Payload.QuestionId);
        Assert.Equal("approve", ev.Payload.ActionValue);
    }

    [Fact]
    public async Task Bare_ZeroOpenQuestions_RepliesNoOpenQuestions_AndDoesNotPublish()
    {
        var store = new InMemoryAgentQuestionStore();
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, adapter) = BuildTurnContext();

        await handler.HandleAsync(BuildContext(string.Empty, turn), CancellationToken.None);

        Assert.Equal(new[] { ConversationId }, store.GetOpenByConversationCalls);
        Assert.Empty(store.StatusTransitionCalls);
        Assert.Empty(publisher.Published);
        // Error card sent back.
        Assert.Single(adapter.Sent);
    }

    [Fact]
    public async Task Bare_MultipleOpenQuestions_RendersDisambiguationCard_AndDoesNotResolve()
    {
        var store = new InMemoryAgentQuestionStore();
        store.Seed(BuildOpenQuestion("q-100", createdAt: DateTimeOffset.UtcNow.AddMinutes(-10)));
        store.Seed(BuildOpenQuestion("q-101", createdAt: DateTimeOffset.UtcNow.AddMinutes(-1)));
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, adapter) = BuildTurnContext();

        await handler.HandleAsync(BuildContext(string.Empty, turn), CancellationToken.None);

        Assert.Equal(new[] { ConversationId }, store.GetOpenByConversationCalls);
        Assert.Empty(store.StatusTransitionCalls);
        Assert.Empty(publisher.Published);

        var sent = Assert.Single(adapter.Sent);
        var attachment = Assert.Single(sent.Attachments);
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.ContentType);

        var json = attachment.Content!.ToString()!;
        Assert.Contains("q-100", json, StringComparison.Ordinal);
        Assert.Contains("q-101", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approve_NotInAllowedActions_RepliesError_AndDoesNotResolve()
    {
        var store = new InMemoryAgentQuestionStore();
        store.Seed(BuildOpenQuestion("q-only-reject", allowedActions: new[]
        {
            new HumanAction("reject", "Reject", "reject", RequiresComment: false),
        }));
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, adapter) = BuildTurnContext();

        await handler.HandleAsync(BuildContext("q-only-reject", turn), CancellationToken.None);

        Assert.Empty(store.StatusTransitionCalls);
        Assert.Empty(publisher.Published);
        Assert.Single(adapter.Sent);
    }

    [Fact]
    public async Task Approve_ActionRequiresComment_RepliesError_AndDoesNotResolve()
    {
        var store = new InMemoryAgentQuestionStore();
        store.Seed(BuildOpenQuestion("q-needs-comment", requiresComment: true));
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, adapter) = BuildTurnContext();

        await handler.HandleAsync(BuildContext("q-needs-comment", turn), CancellationToken.None);

        Assert.Empty(store.StatusTransitionCalls);
        Assert.Empty(publisher.Published);
        Assert.Single(adapter.Sent);
    }

    [Fact]
    public async Task Approve_CasRace_RepliesDecisionAlreadyRecorded_AndDoesNotPublish()
    {
        var store = new InMemoryAgentQuestionStore();
        store.Seed(BuildOpenQuestion("q-race"));
        store.ForceTransitionFailure = true;
        var publisher = new RecordingInboundEventPublisher();
        var handler = BuildHandler(store, publisher);
        var (turn, adapter) = BuildTurnContext();

        await handler.HandleAsync(BuildContext("q-race", turn), CancellationToken.None);

        Assert.Single(store.StatusTransitionCalls);
        Assert.Empty(publisher.Published);
        Assert.Single(adapter.Sent);
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var store = new InMemoryAgentQuestionStore();
        var publisher = new RecordingInboundEventPublisher();
        var renderer = new AdaptiveCardBuilder();

        Assert.Throws<ArgumentNullException>(() => new ApproveCommandHandler(
            null!, publisher, renderer, NullLogger<ApproveCommandHandler>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ApproveCommandHandler(
            store, null!, renderer, NullLogger<ApproveCommandHandler>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ApproveCommandHandler(
            store, publisher, null!, NullLogger<ApproveCommandHandler>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ApproveCommandHandler(
            store, publisher, renderer, null!));
    }
}
