using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Commands;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentSwarm.Messaging.Teams.Tests.HandlerFactory;

namespace AgentSwarm.Messaging.Teams.Tests.Commands;

/// <summary>
/// Tests for the simple command handlers
/// (<see cref="AskCommandHandler"/>, <see cref="StatusCommandHandler"/>,
/// <see cref="EscalateCommandHandler"/>, <see cref="PauseCommandHandler"/>,
/// <see cref="ResumeCommandHandler"/>). Each handler is responsible for publishing its own
/// canonical <see cref="CommandEvent"/> (per <c>implementation-plan.md</c> §3.2 — the
/// dispatcher and its handlers own all inbound event publication so the activity handler
/// does not double-emit) and for sending an Adaptive Card reply on the turn context.
/// </summary>
public sealed class SimpleCommandHandlerTests
{
    private static (TurnContext Context, InertBotAdapter Adapter) BuildTurnContext()
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Message,
            Id = "activity-1",
            Text = "agent ask hello",
            Conversation = new ConversationAccount { Id = "conv-1" },
            From = new ChannelAccount(id: "from"),
            Recipient = new ChannelAccount(id: "bot"),
        };
        var adapter = new InertBotAdapter();
        var ctx = new TurnContext(adapter, activity);
        return (ctx, adapter);
    }

    private static CommandContext BuildContext(string normalizedText, string args, ITurnContext? turn)
    {
        return new CommandContext
        {
            NormalizedText = normalizedText,
            ResolvedIdentity = new UserIdentity("user-internal-1", "aad-obj-1", "Test", "Operator"),
            CorrelationId = "corr-1",
            TurnContext = turn,
            ConversationId = "conv-1",
            ActivityId = "activity-1",
            CommandArguments = args,
        };
    }

    [Fact]
    public async Task AskCommandHandler_PublishesAgentTaskRequestAndSendsAckCard()
    {
        var publisher = new TestDoubles.RecordingInboundEventPublisher();
        var handler = new AskCommandHandler(publisher, NullLogger<AskCommandHandler>.Instance);
        Assert.Equal(CommandNames.AgentAsk, handler.CommandName);

        var (turn, adapter) = BuildTurnContext();
        await handler.HandleAsync(
            BuildContext("agent ask build the thing", "build the thing", turn),
            CancellationToken.None);

        var published = Assert.Single(publisher.Published);
        var commandEvent = Assert.IsType<CommandEvent>(published);
        Assert.Equal(MessengerEventTypes.AgentTaskRequest, commandEvent.EventType);
        var parsed = Assert.IsType<ParsedCommand>(commandEvent.Payload);
        Assert.Equal(CommandNames.AgentAsk, parsed.CommandType);
        Assert.Equal("build the thing", parsed.Payload);
        Assert.Equal("corr-1", commandEvent.CorrelationId);

        var sent = Assert.Single(adapter.Sent);
        var attachment = Assert.Single(sent.Attachments);
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.ContentType);
        var json = attachment.Content!.ToString()!;
        Assert.Contains("build the thing", json, StringComparison.Ordinal);
        Assert.Contains("corr-1", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusCommandHandler_QueriesProvider_PublishesCommandEvent_AndRendersStatusCard()
    {
        var publisher = new TestDoubles.RecordingInboundEventPublisher();
        var provider = new RecordingStatusProvider
        {
            Agents = new List<AgentStatusSummary>
            {
                new("agent-1", "task-1", "Build Agent", "Working", 1, DateTimeOffset.UtcNow, 50, "step 2/5", "corr-1"),
                new("agent-2", null, "Deploy Agent", "Paused", 0, DateTimeOffset.UtcNow, null, "awaiting approval", "corr-1"),
            },
        };
        var renderer = new AdaptiveCardBuilder();
        var handler = new StatusCommandHandler(
            provider,
            renderer,
            publisher,
            NullLogger<StatusCommandHandler>.Instance);
        Assert.Equal(CommandNames.AgentStatus, handler.CommandName);

        var (turn, adapter) = BuildTurnContext();
        await handler.HandleAsync(
            BuildContext("agent status", string.Empty, turn),
            CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal("user-internal-1", provider.LastResolvedIdentity?.InternalUserId);

        var published = Assert.Single(publisher.Published);
        var commandEvent = Assert.IsType<CommandEvent>(published);
        Assert.Equal(MessengerEventTypes.Command, commandEvent.EventType);

        var sent = Assert.Single(adapter.Sent);
        Assert.Equal(2, sent.Attachments.Count);
    }

    [Fact]
    public async Task StatusCommandHandler_WithEmptyProvider_RendersEmptyStatusCard()
    {
        var publisher = new TestDoubles.RecordingInboundEventPublisher();
        var provider = new RecordingStatusProvider { Agents = new List<AgentStatusSummary>() };
        var handler = new StatusCommandHandler(
            provider,
            new AdaptiveCardBuilder(),
            publisher,
            NullLogger<StatusCommandHandler>.Instance);

        var (turn, adapter) = BuildTurnContext();
        await handler.HandleAsync(
            BuildContext("agent status", string.Empty, turn),
            CancellationToken.None);

        Assert.Single(publisher.Published);
        var sent = Assert.Single(adapter.Sent);
        Assert.Single(sent.Attachments);
        var json = sent.Attachments[0].Content!.ToString()!;
        Assert.Contains("No active agents", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StatusCommandHandler_WhenProviderThrows_RendersErrorCard_AfterPublishingCommandEvent()
    {
        var publisher = new TestDoubles.RecordingInboundEventPublisher();
        var provider = new ThrowingStatusProvider();
        var handler = new StatusCommandHandler(
            provider,
            new AdaptiveCardBuilder(),
            publisher,
            NullLogger<StatusCommandHandler>.Instance);

        var (turn, adapter) = BuildTurnContext();
        await handler.HandleAsync(
            BuildContext("agent status", string.Empty, turn),
            CancellationToken.None);

        Assert.Single(publisher.Published);
        var sent = Assert.Single(adapter.Sent);
        Assert.Single(sent.Attachments);
    }

    [Fact]
    public async Task EscalateCommandHandler_PublishesEscalationAndSendsAckCard()
    {
        var publisher = new TestDoubles.RecordingInboundEventPublisher();
        var handler = new EscalateCommandHandler(publisher, NullLogger<EscalateCommandHandler>.Instance);
        Assert.Equal(CommandNames.Escalate, handler.CommandName);

        var (turn, adapter) = BuildTurnContext();
        await handler.HandleAsync(
            BuildContext("escalate p1 outage", "p1 outage", turn),
            CancellationToken.None);

        var commandEvent = Assert.IsType<CommandEvent>(Assert.Single(publisher.Published));
        Assert.Equal(MessengerEventTypes.Escalation, commandEvent.EventType);
        Assert.Single(adapter.Sent);
    }

    [Fact]
    public async Task PauseCommandHandler_PublishesPauseAgentAndSendsAckCard()
    {
        var publisher = new TestDoubles.RecordingInboundEventPublisher();
        var handler = new PauseCommandHandler(publisher, NullLogger<PauseCommandHandler>.Instance);
        Assert.Equal(CommandNames.Pause, handler.CommandName);

        var (turn, adapter) = BuildTurnContext();
        await handler.HandleAsync(
            BuildContext("pause", string.Empty, turn),
            CancellationToken.None);

        var commandEvent = Assert.IsType<CommandEvent>(Assert.Single(publisher.Published));
        Assert.Equal(MessengerEventTypes.PauseAgent, commandEvent.EventType);
        Assert.Single(adapter.Sent);
    }

    [Fact]
    public async Task ResumeCommandHandler_PublishesResumeAgentAndSendsAckCard()
    {
        var publisher = new TestDoubles.RecordingInboundEventPublisher();
        var handler = new ResumeCommandHandler(publisher, NullLogger<ResumeCommandHandler>.Instance);
        Assert.Equal(CommandNames.Resume, handler.CommandName);

        var (turn, adapter) = BuildTurnContext();
        await handler.HandleAsync(
            BuildContext("resume", string.Empty, turn),
            CancellationToken.None);

        var commandEvent = Assert.IsType<CommandEvent>(Assert.Single(publisher.Published));
        Assert.Equal(MessengerEventTypes.ResumeAgent, commandEvent.EventType);
        Assert.Single(adapter.Sent);
    }

    [Fact]
    public async Task SimpleHandlers_WithNullTurnContext_StillPublishCommandEventAndDoNotThrow()
    {
        // The dispatcher is callable outside the Bot Framework activity-handler path
        // (per impl-plan §3.2 — dispatcher is self-sufficient). When invoked without a
        // TurnContext, the handlers must still publish their CommandEvent — the reply
        // card is the only thing that needs a turn context.
        var publisher = new TestDoubles.RecordingInboundEventPublisher();
        var ask = new AskCommandHandler(publisher, NullLogger<AskCommandHandler>.Instance);
        var status = new StatusCommandHandler(
            new RecordingStatusProvider { Agents = new List<AgentStatusSummary>() },
            new AdaptiveCardBuilder(),
            publisher,
            NullLogger<StatusCommandHandler>.Instance);
        var escalate = new EscalateCommandHandler(publisher, NullLogger<EscalateCommandHandler>.Instance);
        var pause = new PauseCommandHandler(publisher, NullLogger<PauseCommandHandler>.Instance);
        var resume = new ResumeCommandHandler(publisher, NullLogger<ResumeCommandHandler>.Instance);

        var context = BuildContext("agent ask anything", "anything", turn: null);

        await ask.HandleAsync(context, CancellationToken.None);
        await status.HandleAsync(context, CancellationToken.None);
        await escalate.HandleAsync(context, CancellationToken.None);
        await pause.HandleAsync(context, CancellationToken.None);
        await resume.HandleAsync(context, CancellationToken.None);

        Assert.Equal(5, publisher.Published.Count);
    }

    private sealed class RecordingStatusProvider : IAgentSwarmStatusProvider
    {
        public IReadOnlyList<AgentStatusSummary> Agents { get; set; } = Array.Empty<AgentStatusSummary>();
        public int CallCount { get; private set; }
        public UserIdentity? LastResolvedIdentity { get; private set; }

        public Task<IReadOnlyList<AgentStatusSummary>> GetStatusAsync(
            UserIdentity resolvedIdentity,
            string tenantId,
            string correlationId,
            CancellationToken ct)
        {
            CallCount++;
            LastResolvedIdentity = resolvedIdentity;
            return Task.FromResult(Agents);
        }
    }

    private sealed class ThrowingStatusProvider : IAgentSwarmStatusProvider
    {
        public Task<IReadOnlyList<AgentStatusSummary>> GetStatusAsync(
            UserIdentity resolvedIdentity,
            string tenantId,
            string correlationId,
            CancellationToken ct)
            => throw new InvalidOperationException("orchestrator down");
    }
}
