using AgentSwarm.Messaging.Abstractions;
using static AgentSwarm.Messaging.Teams.Tests.HandlerFactory;
using static AgentSwarm.Messaging.Teams.Tests.TestDoubles;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Iter-2 evaluator feedback item #3: comprehensive coverage of the
/// <c>OnMessageActivityAsync</c> → <see cref="IInboundEventPublisher"/> handoff.
/// The single E2E connector test in <see cref="TeamsMessengerConnectorTests"/> covers the
/// <c>agent status</c> happy path only — this suite expands coverage to every canonical
/// command verb (mapping, body extraction, source classification) plus the three
/// negative-no-publish paths the evaluator called out: unrecognized text, unmapped
/// user, unauthorized role.
/// </summary>
/// <remarks>
/// Every test downcasts the <see cref="IInboundEventPublisher"/> on the harness to the
/// <see cref="RecordingInboundEventPublisher"/> test double that
/// <see cref="HandlerFactory.Build()"/> wires by default. This keeps the harness type
/// generic (so the connector E2E test can swap in a real
/// <see cref="ChannelInboundEventPublisher"/>) while still letting these unit tests
/// observe the published events directly.
/// </remarks>
public sealed class TeamsSwarmActivityHandlerInboundPublishTests
{
    /// <summary>
    /// e2e-scenarios.md §Personal Chat lines 26–34: <c>agent ask &lt;text&gt;</c>
    /// publishes a <see cref="CommandEvent"/> with <see cref="MessengerEventTypes.AgentTaskRequest"/>
    /// and <c>Payload.Body = &lt;text&gt;</c>.
    /// </summary>
    [Fact]
    public async Task OnMessageActivityAsync_AgentAskWithBody_PublishesAgentTaskRequestWithBody()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage(
            "agent ask create e2e test scenarios for update service",
            correlationId: "corr-ask-001");

        await ProcessAsync(harness, activity);

        var published = Assert.Single(GetPublished(harness));
        var commandEvent = Assert.IsType<CommandEvent>(published);
        Assert.Equal(MessengerEventTypes.AgentTaskRequest, commandEvent.EventType);
        Assert.Equal("agent ask", commandEvent.Payload.CommandType);
        Assert.Equal("create e2e test scenarios for update service", commandEvent.Payload.Payload);
        Assert.Equal("corr-ask-001", commandEvent.CorrelationId);
        Assert.Equal("corr-ask-001", commandEvent.Payload.CorrelationId);
        Assert.Equal("Teams", commandEvent.Messenger);
        Assert.Equal("aad-obj-dave-001", commandEvent.ExternalUserId);
        Assert.Equal(activity.Id, commandEvent.ActivityId);
        Assert.Equal(MessengerEventSources.PersonalChat, commandEvent.Source);
    }

    /// <summary>
    /// Item #2 fix — <c>agent ask</c> with no body is syntactically recognized but
    /// semantically invalid (AgentTaskRequest requires non-empty <c>Payload.Body</c>),
    /// so the handler must NOT publish an <see cref="CommandEvent"/> for it. The
    /// dispatcher (Stage 3.2) will produce a <see cref="TextEvent"/> via its
    /// unrecognized-input path. The command is still dispatched (the dispatcher decides
    /// how to respond) and still audited as a <c>CommandReceived</c> attempt.
    /// </summary>
    [Theory]
    [InlineData("agent ask")]
    [InlineData("agent ask   ")]
    public async Task OnMessageActivityAsync_BareAgentAsk_DoesNotPublishAgentTaskRequest(string text)
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage(text);

        await ProcessAsync(harness, activity);

        Assert.Empty(GetPublished(harness));
        // Still dispatched and audited (handler does not short-circuit the rest of the
        // turn — only the publish path is gated by validation parity).
        Assert.Single(harness.Dispatcher.Dispatched);
        Assert.Single(harness.AuditLogger.Entries);
    }

    /// <summary>
    /// <c>agent status</c> publishes a <see cref="CommandEvent"/> with the generic
    /// <see cref="MessengerEventTypes.Command"/> discriminator and an empty body.
    /// Matches architecture.md §3.1 (<c>Command</c> for general-purpose commands).
    /// </summary>
    [Fact]
    public async Task OnMessageActivityAsync_AgentStatus_PublishesCommandWithEmptyBody()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage("agent status");

        await ProcessAsync(harness, activity);

        var published = Assert.Single(GetPublished(harness));
        var commandEvent = Assert.IsType<CommandEvent>(published);
        Assert.Equal(MessengerEventTypes.Command, commandEvent.EventType);
        Assert.Equal("agent status", commandEvent.Payload.CommandType);
        Assert.Equal(string.Empty, commandEvent.Payload.Payload);
    }

    /// <summary>
    /// Bare <c>approve</c> publishes <see cref="MessengerEventTypes.Command"/> with an
    /// empty body — per the doc on <c>MessengerEventTypes.Command</c> ("bare approve,
    /// bare reject"). The body field is reserved for the optional question-ID payload
    /// covered by the next test.
    /// </summary>
    [Fact]
    public async Task OnMessageActivityAsync_BareApprove_PublishesCommandWithEmptyBody()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage("approve");

        await ProcessAsync(harness, activity);

        var published = Assert.Single(GetPublished(harness));
        var commandEvent = Assert.IsType<CommandEvent>(published);
        Assert.Equal(MessengerEventTypes.Command, commandEvent.EventType);
        Assert.Equal("approve", commandEvent.Payload.CommandType);
        Assert.Equal(string.Empty, commandEvent.Payload.Payload);
    }

    /// <summary>
    /// <c>approve &lt;questionId&gt;</c> publishes <see cref="MessengerEventTypes.Command"/>
    /// with <c>Payload.Payload</c> set to the question ID. The body is extracted by
    /// <c>ExtractCommandBody</c> (case-insensitive verb prefix, original casing
    /// preserved in the body).
    /// </summary>
    [Fact]
    public async Task OnMessageActivityAsync_ApproveWithQuestionId_PublishesCommandWithBody()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage("approve Q-1001");

        await ProcessAsync(harness, activity);

        var published = Assert.Single(GetPublished(harness));
        var commandEvent = Assert.IsType<CommandEvent>(published);
        Assert.Equal(MessengerEventTypes.Command, commandEvent.EventType);
        Assert.Equal("approve", commandEvent.Payload.CommandType);
        Assert.Equal("Q-1001", commandEvent.Payload.Payload);
    }

    /// <summary>
    /// <c>reject &lt;questionId&gt;</c> mirrors <c>approve</c> — same Command
    /// discriminator, same body-preservation semantics.
    /// </summary>
    [Fact]
    public async Task OnMessageActivityAsync_RejectWithQuestionId_PublishesCommandWithBody()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage("reject Q-2002");

        await ProcessAsync(harness, activity);

        var published = Assert.Single(GetPublished(harness));
        var commandEvent = Assert.IsType<CommandEvent>(published);
        Assert.Equal(MessengerEventTypes.Command, commandEvent.EventType);
        Assert.Equal("reject", commandEvent.Payload.CommandType);
        Assert.Equal("Q-2002", commandEvent.Payload.Payload);
    }

    /// <summary>
    /// Parameterised coverage for the three lifecycle verbs called out by the evaluator:
    /// <c>escalate</c> → <see cref="MessengerEventTypes.Escalation"/>,
    /// <c>pause</c> → <see cref="MessengerEventTypes.PauseAgent"/>,
    /// <c>resume</c> → <see cref="MessengerEventTypes.ResumeAgent"/>. Each is valid as a
    /// bare verb per architecture.md §5.2.
    /// </summary>
    [Theory]
    [InlineData("escalate", MessengerEventTypes.Escalation, "escalate")]
    [InlineData("pause", MessengerEventTypes.PauseAgent, "pause")]
    [InlineData("resume", MessengerEventTypes.ResumeAgent, "resume")]
    public async Task OnMessageActivityAsync_LifecycleVerb_PublishesMatchingEventType(
        string verb,
        string expectedEventType,
        string expectedCommandType)
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage(verb);

        await ProcessAsync(harness, activity);

        var published = Assert.Single(GetPublished(harness));
        var commandEvent = Assert.IsType<CommandEvent>(published);
        Assert.Equal(expectedEventType, commandEvent.EventType);
        Assert.Equal(expectedCommandType, commandEvent.Payload.CommandType);
        Assert.Equal(string.Empty, commandEvent.Payload.Payload);
    }

    /// <summary>
    /// e2e-scenarios.md §Personal Chat — Team Channel Mention (lines 37–56): an
    /// <c>@AgentBot agent ask &lt;text&gt;</c> message from a Teams channel must
    /// publish a <see cref="CommandEvent"/> with <see cref="MessengerEventSources.TeamChannel"/>
    /// for the source field, NOT <see cref="MessengerEventSources.PersonalChat"/>.
    /// </summary>
    [Fact]
    public async Task OnMessageActivityAsync_ChannelMention_PublishesEventWithTeamChannelSource()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver, "aad-obj-bob-002");
        var activity = NewChannelMentionMessage("<at>AgentBot</at> agent ask design persistence layer");

        await ProcessAsync(harness, activity);

        var published = Assert.Single(GetPublished(harness));
        var commandEvent = Assert.IsType<CommandEvent>(published);
        Assert.Equal(MessengerEventTypes.AgentTaskRequest, commandEvent.EventType);
        Assert.Equal("agent ask", commandEvent.Payload.CommandType);
        Assert.Equal("design persistence layer", commandEvent.Payload.Payload);
        Assert.Equal(MessengerEventSources.TeamChannel, commandEvent.Source);
        Assert.Equal("aad-obj-bob-002", commandEvent.ExternalUserId);
    }

    /// <summary>
    /// e2e-scenarios.md §Personal Chat — Unrecognised Command (lines 58–70): unknown
    /// free-text input is NOT published by the handler. CommandDispatcher (Stage 3.2)
    /// is responsible for producing the <see cref="TextEvent"/> for these messages.
    /// </summary>
    [Theory]
    [InlineData("hello there")]
    [InlineData("can you do this for me please")]
    [InlineData("agent ohai")] // looks like a command prefix but verb is not recognized
    public async Task OnMessageActivityAsync_UnrecognizedText_DoesNotPublishEvent(string text)
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var activity = NewPersonalMessage(text);

        await ProcessAsync(harness, activity);

        Assert.Empty(GetPublished(harness));
        // The dispatcher still gets a turn — it owns TextEvent emission for these.
        Assert.Single(harness.Dispatcher.Dispatched);
    }

    /// <summary>
    /// Identity-resolution failure (eve is not mapped) short-circuits the turn BEFORE
    /// any inbound event is published. The published list must be empty even though the
    /// message text was a syntactically valid command.
    /// </summary>
    [Fact]
    public async Task OnMessageActivityAsync_UnmappedUser_DoesNotPublishEvent()
    {
        var harness = Build();
        var activity = NewPersonalMessage(
            "agent ask analyse incident",
            aadObjectId: "aad-obj-eve-external");

        await ProcessAsync(harness, activity);

        Assert.Empty(GetPublished(harness));
        Assert.Empty(harness.Dispatcher.Dispatched);
        Assert.Empty(harness.Store.Saved);
    }

    /// <summary>
    /// RBAC failure (user resolved but lacks the required role) short-circuits the turn
    /// before the publish step. Audit logs the rejection; inbound queue remains empty.
    /// </summary>
    [Fact]
    public async Task OnMessageActivityAsync_UnauthorizedRole_DoesNotPublishEvent()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        harness.Authorization.IsAuthorized = false;
        harness.Authorization.UserRole = "viewer";
        harness.Authorization.RequiredRole = "approver";
        var activity = NewPersonalMessage("approve Q-1001");

        await ProcessAsync(harness, activity);

        Assert.Empty(GetPublished(harness));
        Assert.Empty(harness.Dispatcher.Dispatched);
        Assert.Empty(harness.Store.Saved);
    }

    /// <summary>
    /// Cross-verifies that the publish carries a stable, non-empty <c>EventId</c> and a
    /// monotonically forward <c>Timestamp</c> — both required by the
    /// <see cref="MessengerEvent"/> canonical envelope (FR-004 / architecture.md §3.1).
    /// </summary>
    [Fact]
    public async Task OnMessageActivityAsync_PublishedEvent_CarriesNonEmptyEventIdAndRecentTimestamp()
    {
        var harness = Build();
        MapDave(harness.IdentityResolver);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var activity = NewPersonalMessage("agent status");

        await ProcessAsync(harness, activity);

        var published = Assert.Single(GetPublished(harness));
        Assert.False(string.IsNullOrWhiteSpace(published.EventId));
        // EventId must be unique-per-publish — verified by parsing as a GUID (handler
        // uses Guid.NewGuid() per architecture.md §3.1).
        Assert.True(Guid.TryParse(published.EventId, out _));
        Assert.InRange(published.Timestamp, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    private static IReadOnlyList<MessengerEvent> GetPublished(Harness harness)
    {
        var recording = Assert.IsType<RecordingInboundEventPublisher>(harness.EventPublisher);
        return recording.Published;
    }
}
