using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Commands;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using static AgentSwarm.Messaging.Teams.Tests.HandlerFactory;
using static AgentSwarm.Messaging.Teams.Tests.TestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests.Commands;

/// <summary>
/// Stage 3.2 <see cref="CommandDispatcher"/> behavioural tests. Each scenario maps directly
/// to a test scenario in <c>implementation-plan.md</c> §3.2 — ask routing, unknown-input
/// publishing, mention-stripped pass-through, boundary-safe longest-prefix matching, and
/// duplicate-handler-name rejection at construction time.
/// </summary>
public sealed class CommandDispatcherTests
{
    private static CommandContext BuildContext(
        string normalizedText,
        ITurnContext? turnContext = null,
        string? correlationId = null,
        string? conversationId = null,
        string? activityId = null,
        UserIdentity? identity = null)
    {
        return new CommandContext
        {
            NormalizedText = normalizedText,
            ResolvedIdentity = identity ?? new UserIdentity(
                InternalUserId: "user-internal-1",
                AadObjectId: "aad-obj-1",
                DisplayName: "Test User",
                Role: "Operator"),
            CorrelationId = correlationId ?? "corr-1",
            TurnContext = turnContext,
            ConversationId = conversationId ?? "conv-1",
            ActivityId = activityId ?? "activity-1",
        };
    }

    private static (TurnContext Context, InertBotAdapter Adapter) BuildTurnContext(string text)
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Message,
            Id = "activity-1",
            Text = text,
            Conversation = new ConversationAccount { Id = "conv-1" },
            From = new ChannelAccount(id: "from", name: "From"),
            Recipient = new ChannelAccount(id: "bot", name: "Bot"),
        };
        var adapter = new InertBotAdapter();
        var ctx = new TurnContext(adapter, activity);
        return (ctx, adapter);
    }

    private sealed class RecordingCommandHandler : ICommandHandler
    {
        public string CommandName { get; }
        public List<CommandContext> Invocations { get; } = new();

        public RecordingCommandHandler(string commandName)
        {
            CommandName = commandName;
        }

        public Task HandleAsync(CommandContext context, CancellationToken ct)
        {
            Invocations.Add(context);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DispatchAsync_RoutesAgentAsk_ToAskHandler_WithArgumentsStripped()
    {
        var ask = new RecordingCommandHandler(CommandNames.AgentAsk);
        var status = new RecordingCommandHandler(CommandNames.AgentStatus);
        var publisher = new RecordingInboundEventPublisher();
        var dispatcher = new CommandDispatcher(
            new ICommandHandler[] { ask, status },
            publisher,
            NullLogger<CommandDispatcher>.Instance);

        var context = BuildContext("agent ask create e2e test scenarios for update service");

        await dispatcher.DispatchAsync(context, CancellationToken.None);

        Assert.Single(ask.Invocations);
        Assert.Empty(status.Invocations);
        Assert.Equal("create e2e test scenarios for update service", ask.Invocations[0].CommandArguments);
        // The original NormalizedText must be preserved on the routed context so handlers
        // can see what the user actually typed.
        Assert.Equal("agent ask create e2e test scenarios for update service", ask.Invocations[0].NormalizedText);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task DispatchAsync_RoutesAgentStatus_ToStatusHandler_WithEmptyArguments()
    {
        var ask = new RecordingCommandHandler(CommandNames.AgentAsk);
        var status = new RecordingCommandHandler(CommandNames.AgentStatus);
        var publisher = new RecordingInboundEventPublisher();
        var dispatcher = new CommandDispatcher(
            new ICommandHandler[] { ask, status },
            publisher,
            NullLogger<CommandDispatcher>.Instance);

        var context = BuildContext("agent status");

        await dispatcher.DispatchAsync(context, CancellationToken.None);

        Assert.Empty(ask.Invocations);
        Assert.Single(status.Invocations);
        Assert.Equal(string.Empty, status.Invocations[0].CommandArguments);
    }

    [Fact]
    public async Task DispatchAsync_LongestPrefixWins_AgentAskBeatsHypotheticalAskAlias()
    {
        // "agent ask" must beat a single-word "ask" handler even when both are registered —
        // the dispatcher orders by descending length to honour the multi-word verbs in §2.5.
        var ask = new RecordingCommandHandler(CommandNames.AgentAsk);
        var aliasAsk = new RecordingCommandHandler("ask");
        var dispatcher = new CommandDispatcher(
            new ICommandHandler[] { aliasAsk, ask },
            new RecordingInboundEventPublisher(),
            NullLogger<CommandDispatcher>.Instance);

        await dispatcher.DispatchAsync(BuildContext("agent ask do thing"), CancellationToken.None);

        Assert.Single(ask.Invocations);
        Assert.Empty(aliasAsk.Invocations);
    }

    [Fact]
    public async Task DispatchAsync_IsCaseInsensitive_OnCommandKeyword()
    {
        var status = new RecordingCommandHandler(CommandNames.AgentStatus);
        var dispatcher = new CommandDispatcher(
            new ICommandHandler[] { status },
            new RecordingInboundEventPublisher(),
            NullLogger<CommandDispatcher>.Instance);

        await dispatcher.DispatchAsync(BuildContext("AGENT STATUS"), CancellationToken.None);

        Assert.Single(status.Invocations);
    }

    [Fact]
    public async Task DispatchAsync_BoundarySafe_ApproveXDoesNotMatchApprove()
    {
        var approve = new RecordingCommandHandler(CommandNames.Approve);
        var publisher = new RecordingInboundEventPublisher();
        var dispatcher = new CommandDispatcher(
            new ICommandHandler[] { approve },
            publisher,
            NullLogger<CommandDispatcher>.Instance);

        var (turn, adapter) = BuildTurnContext("approveX");
        await dispatcher.DispatchAsync(BuildContext("approveX", turn), CancellationToken.None);

        Assert.Empty(approve.Invocations);
        var textEvent = Assert.IsType<TextEvent>(Assert.Single(publisher.Published));
        Assert.Equal("approveX", textEvent.Payload);
        // The help card is sent as the bot reply for unknown text.
        Assert.Single(adapter.Sent);
    }

    [Fact]
    public async Task DispatchAsync_UnknownCommand_PublishesTextEvent_AndSendsHelpCard()
    {
        var ask = new RecordingCommandHandler(CommandNames.AgentAsk);
        var publisher = new RecordingInboundEventPublisher();
        var dispatcher = new CommandDispatcher(
            new ICommandHandler[] { ask },
            publisher,
            NullLogger<CommandDispatcher>.Instance);

        var (turn, adapter) = BuildTurnContext("hello there");
        await dispatcher.DispatchAsync(
            BuildContext("hello there", turn, correlationId: "corr-help"),
            CancellationToken.None);

        Assert.Empty(ask.Invocations);

        var ev = Assert.IsType<TextEvent>(Assert.Single(publisher.Published));
        Assert.Equal("hello there", ev.Payload);
        Assert.Equal("Teams", ev.Messenger);
        Assert.Equal(MessengerEventTypes.Text, ev.EventType);
        Assert.Equal("corr-help", ev.CorrelationId);
        Assert.Equal("aad-obj-1", ev.ExternalUserId);
        Assert.Equal("activity-1", ev.ActivityId);

        var sent = Assert.Single(adapter.Sent);
        var attachment = Assert.Single(sent.Attachments);
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.ContentType);
    }

    [Fact]
    public async Task DispatchAsync_AlreadyMentionStrippedText_RoutesToStatus()
    {
        // The dispatcher must NOT do mention stripping itself — it receives pre-cleaned text
        // from OnMessageActivityAsync. This test asserts the cleaned text routes correctly.
        var status = new RecordingCommandHandler(CommandNames.AgentStatus);
        var dispatcher = new CommandDispatcher(
            new ICommandHandler[] { status },
            new RecordingInboundEventPublisher(),
            NullLogger<CommandDispatcher>.Instance);

        await dispatcher.DispatchAsync(BuildContext("agent status"), CancellationToken.None);

        Assert.Single(status.Invocations);
        // The dispatcher must NEVER mutate NormalizedText itself — even though the dispatcher
        // sets CommandArguments, the original NormalizedText is preserved.
        Assert.Equal("agent status", status.Invocations[0].NormalizedText);
    }

    [Fact]
    public async Task DispatchAsync_EmptyAndWhitespaceText_PublishTextEventAndHelpCard()
    {
        var publisher = new RecordingInboundEventPublisher();
        var dispatcher = new CommandDispatcher(
            new[] { new RecordingCommandHandler(CommandNames.AgentAsk) },
            publisher,
            NullLogger<CommandDispatcher>.Instance);

        var (turn, adapter) = BuildTurnContext(" ");
        await dispatcher.DispatchAsync(BuildContext("   ", turn), CancellationToken.None);

        var ev = Assert.IsType<TextEvent>(Assert.Single(publisher.Published));
        Assert.Equal(string.Empty, ev.Payload);
        Assert.Single(adapter.Sent);
    }

    [Fact]
    public void Constructor_RejectsDuplicateHandlerNames()
    {
        var first = new RecordingCommandHandler(CommandNames.Approve);
        var second = new RecordingCommandHandler(CommandNames.Approve);

        var ex = Assert.Throws<ArgumentException>(() => new CommandDispatcher(
            new ICommandHandler[] { first, second },
            new RecordingInboundEventPublisher(),
            NullLogger<CommandDispatcher>.Instance));

        Assert.Contains(CommandNames.Approve, ex.Message);
    }

    [Fact]
    public void Constructor_RejectsEmptyCommandName()
    {
        Assert.Throws<ArgumentException>(() => new CommandDispatcher(
            new ICommandHandler[] { new RecordingCommandHandler(string.Empty) },
            new RecordingInboundEventPublisher(),
            NullLogger<CommandDispatcher>.Instance));
    }

    [Fact]
    public void Constructor_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new CommandDispatcher(
            null!,
            new RecordingInboundEventPublisher(),
            NullLogger<CommandDispatcher>.Instance));

        Assert.Throws<ArgumentNullException>(() => new CommandDispatcher(
            Array.Empty<ICommandHandler>(),
            null!,
            NullLogger<CommandDispatcher>.Instance));

        Assert.Throws<ArgumentNullException>(() => new CommandDispatcher(
            Array.Empty<ICommandHandler>(),
            new RecordingInboundEventPublisher(),
            null!));
    }

    [Fact]
    public async Task DispatchAsync_NullContext_Throws()
    {
        var dispatcher = new CommandDispatcher(
            Array.Empty<ICommandHandler>(),
            new RecordingInboundEventPublisher(),
            NullLogger<CommandDispatcher>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => dispatcher.DispatchAsync(null!, CancellationToken.None));
    }
}
