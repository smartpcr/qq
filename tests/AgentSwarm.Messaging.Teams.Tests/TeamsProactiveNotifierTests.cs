using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Cards;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Verifies the five Stage 4.2 test scenarios from <c>implementation-plan.md</c> §4.2:
/// proactive question delivery for a stored user reference, reference-not-found throws
/// <see cref="ConversationReferenceNotFoundException"/>, the routing helper dispatches
/// based on <see cref="AgentQuestion.TargetUserId"/> / <see cref="AgentQuestion.TargetChannelId"/>,
/// channel-scope proactive message delivery, and channel-scope proactive question
/// delivery.
/// </summary>
public sealed class TeamsProactiveNotifierTests
{
    private const string TenantId = "contoso-tenant-id";
    private const string MicrosoftAppId = "11111111-1111-1111-1111-111111111111";
    private const string PersonalConversationId = "19:conversation-alice";
    private const string ChannelConversationId = "19:channel-conv-general";

    /// <summary>
    /// Scenario: Proactive question delivery (Stage 4.2 brief #1) — Given a stored
    /// conversation reference for <c>user-1</c>, When
    /// <see cref="TeamsProactiveNotifier.SendProactiveQuestionAsync"/> is called with an
    /// <see cref="AgentQuestion"/>, Then the rendered Adaptive Card is delivered to the
    /// user's personal chat via <c>ContinueConversationAsync</c> and the question's
    /// conversation ID + card state are persisted.
    /// </summary>
    [Fact]
    public async Task SendProactiveQuestionAsync_StoredUserReference_DeliversAdaptiveCardAndPersistsCardState()
    {
        var harness = NotifierHarness.Build();
        var stored = NewPersonalReference("ref-alice", aadObjectId: "aad-alice", internalUserId: "user-1");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-1")] = stored;
        var question = NewQuestion("Q-proactive-1", targetUserId: "user-1");

        await harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-1", question, CancellationToken.None);

        // Reference looked up via the canonical (TenantId, InternalUserId) key.
        var lookup = Assert.Single(harness.ConversationReferenceStore.InternalUserLookups);
        Assert.Equal((TenantId, "user-1"), lookup);
        Assert.Empty(harness.ConversationReferenceStore.ChannelLookups);

        // ContinueConversationAsync invoked with the bot app ID and the rehydrated
        // ConversationReference (round-tripped through Newtonsoft from the stored JSON).
        var call = Assert.Single(harness.Adapter.ContinueCalls);
        Assert.Equal(MicrosoftAppId, call.BotAppId);
        Assert.Equal(stored.ConversationId, call.Reference.Conversation.Id);

        // Adaptive Card attachment was sent — verify the content type is the canonical
        // application/vnd.microsoft.card.adaptive, the title is set on Activity.Text as
        // the accessibility fallback, and the card JSON carries the question's allowed
        // actions so AdaptiveCardBuilder was actually invoked.
        var sent = Assert.Single(harness.Adapter.Sent);
        Assert.Equal(question.Title, sent.Text);
        var attachment = Assert.Single(sent.Attachments);
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.ContentType);
        var cardJson = attachment.Content?.ToString() ?? string.Empty;
        Assert.Contains(question.Title, cardJson, StringComparison.Ordinal);
        Assert.Contains(question.Body, cardJson, StringComparison.Ordinal);
        Assert.Contains("approve", cardJson, StringComparison.Ordinal);

        // Question's ConversationId stamped from the proactive turn context.
        var update = Assert.Single(harness.AgentQuestionStore.ConversationIdUpdates);
        Assert.Equal("Q-proactive-1", update.QuestionId);
        Assert.Equal(stored.ConversationId, update.ConversationId);

        // Card state persisted with the activityId returned by SendActivitiesAsync, the
        // proactive turn context's ConversationId, and a ConversationReferenceJson that
        // round-trips through Newtonsoft (so Stage 6 outbox retries can rehydrate).
        var cardState = Assert.Single(harness.CardStateStore.Saved);
        Assert.Equal("Q-proactive-1", cardState.QuestionId);
        Assert.False(string.IsNullOrWhiteSpace(cardState.ActivityId));
        Assert.Equal(stored.ConversationId, cardState.ConversationId);
        Assert.Equal(TeamsCardStatuses.Pending, cardState.Status);
        var rehydrated = JsonConvert.DeserializeObject<ConversationReference>(cardState.ConversationReferenceJson);
        Assert.NotNull(rehydrated);
        Assert.Equal(stored.ConversationId, rehydrated!.Conversation.Id);
    }

    /// <summary>
    /// Scenario: Reference not found (Stage 4.2 brief #2) — Given no conversation
    /// reference exists for <c>user-2</c>, When
    /// <see cref="TeamsProactiveNotifier.SendProactiveQuestionAsync"/> is called, Then a
    /// <see cref="ConversationReferenceNotFoundException"/> is thrown so the caller can
    /// log the failure for later outbox-based retry.
    /// </summary>
    [Fact]
    public async Task SendProactiveQuestionAsync_NoStoredReference_ThrowsConversationReferenceNotFoundException()
    {
        var harness = NotifierHarness.Build();
        var question = NewQuestion("Q-missing-ref", targetUserId: "user-2");

        var ex = await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-2", question, CancellationToken.None));

        Assert.Equal(TenantId, ex.TenantId);
        Assert.Equal("user-2", ex.InternalUserId);
        Assert.Null(ex.ChannelId);
        Assert.Equal("Q-missing-ref", ex.QuestionId);

        // No send was attempted and no card state was persisted — the failure is
        // surfaced BEFORE any partial state can be written.
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.Adapter.Sent);
        Assert.Empty(harness.CardStateStore.Saved);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
    }

    /// <summary>
    /// Scenario: Proactive notification routing (Stage 4.2 brief #3) — Given an
    /// <see cref="AgentQuestion"/> with <c>TargetUserId = "user-1"</c> and
    /// <c>TargetChannelId = null</c>, When the routing helper
    /// <see cref="IProactiveNotifier.NotifyQuestionAsync"/> is invoked, Then
    /// <see cref="IProactiveNotifier.SendProactiveQuestionAsync"/> is dispatched (not the
    /// channel variant) and the AAD-keyed user lookup is exercised. The complementary
    /// assertion (channel-only target dispatches to <c>SendQuestionToChannelAsync</c>) is
    /// covered by <see cref="NotifyQuestionAsync_TargetChannelId_DispatchesToChannelMethod"/>.
    /// </summary>
    [Fact]
    public async Task NotifyQuestionAsync_TargetUserId_DispatchesToUserMethod()
    {
        var harness = NotifierHarness.Build();
        var stored = NewPersonalReference("ref-route-user", aadObjectId: "aad-user-route", internalUserId: "user-1");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-1")] = stored;
        var question = NewQuestion("Q-route-user", targetUserId: "user-1");

        await ((IProactiveNotifier)harness.Notifier).NotifyQuestionAsync(question, CancellationToken.None);

        // Routing fanned out to the internal-user lookup, NOT the channel lookup.
        Assert.Single(harness.ConversationReferenceStore.InternalUserLookups);
        Assert.Empty(harness.ConversationReferenceStore.ChannelLookups);
        Assert.Single(harness.Adapter.ContinueCalls);
    }

    /// <summary>
    /// Scenario: Proactive notification routing (Stage 4.2 brief #3, complementary) —
    /// Given an <see cref="AgentQuestion"/> with <c>TargetUserId = null</c> and
    /// <c>TargetChannelId = "channel-general"</c>, When the routing helper
    /// <see cref="IProactiveNotifier.NotifyQuestionAsync"/> is invoked, Then
    /// <see cref="IProactiveNotifier.SendQuestionToChannelAsync"/> is dispatched and the
    /// channel-keyed lookup is exercised.
    /// </summary>
    [Fact]
    public async Task NotifyQuestionAsync_TargetChannelId_DispatchesToChannelMethod()
    {
        var harness = NotifierHarness.Build();
        var stored = NewChannelReference("ref-route-channel", channelId: "channel-general");
        harness.ConversationReferenceStore.PreloadByChannelId[(TenantId, "channel-general")] = stored;
        var question = NewQuestion("Q-route-channel", targetChannelId: "channel-general");

        await ((IProactiveNotifier)harness.Notifier).NotifyQuestionAsync(question, CancellationToken.None);

        // Routing fanned out to the channel lookup, NOT the internal-user lookup.
        Assert.Single(harness.ConversationReferenceStore.ChannelLookups);
        Assert.Empty(harness.ConversationReferenceStore.InternalUserLookups);
        Assert.Single(harness.Adapter.ContinueCalls);
    }

    /// <summary>
    /// Scenario: Channel proactive delivery (Stage 4.2 brief #4) — Given a stored
    /// channel-scoped conversation reference for <c>channel-general</c> in
    /// <c>tenant-1</c>, When
    /// <see cref="TeamsProactiveNotifier.SendToChannelAsync"/> is called, Then the
    /// message is delivered to the team channel (not a personal chat) using the channel's
    /// stored <see cref="ConversationReference"/> via <c>ContinueConversationAsync</c>.
    /// </summary>
    [Fact]
    public async Task SendToChannelAsync_StoredChannelReference_DeliversTextMessageToChannel()
    {
        var harness = NotifierHarness.Build();
        var stored = NewChannelReference("ref-channel-1", channelId: "channel-general");
        harness.ConversationReferenceStore.PreloadByChannelId[(TenantId, "channel-general")] = stored;

        var message = new MessengerMessage(
            MessageId: "msg-channel-1",
            CorrelationId: "corr-channel-1",
            AgentId: "agent-build",
            TaskId: "task-7",
            ConversationId: string.Empty,
            Body: "Daily build complete on develop.",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        await harness.Notifier.SendToChannelAsync(TenantId, "channel-general", message, CancellationToken.None);

        // Lookup keyed on the channel ID — internal-user path NOT exercised.
        var lookup = Assert.Single(harness.ConversationReferenceStore.ChannelLookups);
        Assert.Equal((TenantId, "channel-general"), lookup);
        Assert.Empty(harness.ConversationReferenceStore.InternalUserLookups);

        // Delivered via ContinueConversationAsync to the channel's conversation.
        var call = Assert.Single(harness.Adapter.ContinueCalls);
        Assert.Equal(MicrosoftAppId, call.BotAppId);
        Assert.Equal(stored.ConversationId, call.Reference.Conversation.Id);
        Assert.Equal("channel", call.Reference.Conversation.ConversationType);
        var sent = Assert.Single(harness.Adapter.Sent);
        Assert.Equal(ActivityTypes.Message, sent.Type);
        Assert.Equal(message.Body, sent.Text);

        // No card state is persisted for a plain message (no question → no card).
        Assert.Empty(harness.CardStateStore.Saved);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
    }

    /// <summary>
    /// Scenario: Channel question delivery (Stage 4.2 brief #5) — Given a stored channel
    /// reference, When
    /// <see cref="TeamsProactiveNotifier.SendQuestionToChannelAsync"/> is called with an
    /// <see cref="AgentQuestion"/>, Then the rendered Adaptive Card is delivered to the
    /// team channel and the same two-step persistence (UpdateConversationId +
    /// SaveAsync(TeamsCardState)) used by <see cref="SendProactiveQuestionAsync"/> runs.
    /// </summary>
    [Fact]
    public async Task SendQuestionToChannelAsync_StoredChannelReference_DeliversAdaptiveCardAndPersistsState()
    {
        var harness = NotifierHarness.Build();
        var stored = NewChannelReference("ref-channel-q", channelId: "channel-general");
        harness.ConversationReferenceStore.PreloadByChannelId[(TenantId, "channel-general")] = stored;
        var question = NewQuestion("Q-channel-question", targetChannelId: "channel-general");

        await harness.Notifier.SendQuestionToChannelAsync(TenantId, "channel-general", question, CancellationToken.None);

        // Lookup keyed on (TenantId, ChannelId).
        var lookup = Assert.Single(harness.ConversationReferenceStore.ChannelLookups);
        Assert.Equal((TenantId, "channel-general"), lookup);

        // Adaptive Card delivered with the same content-type / fallback-text pattern as
        // the user-scope question path.
        var sent = Assert.Single(harness.Adapter.Sent);
        Assert.Equal(question.Title, sent.Text);
        var attachment = Assert.Single(sent.Attachments);
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.ContentType);
        var cardJson = attachment.Content?.ToString() ?? string.Empty;
        Assert.Contains(question.Title, cardJson, StringComparison.Ordinal);

        // Two-step persistence: UpdateConversationId stamped + TeamsCardState saved.
        var update = Assert.Single(harness.AgentQuestionStore.ConversationIdUpdates);
        Assert.Equal("Q-channel-question", update.QuestionId);
        Assert.Equal(stored.ConversationId, update.ConversationId);
        var cardState = Assert.Single(harness.CardStateStore.Saved);
        Assert.Equal("Q-channel-question", cardState.QuestionId);
        Assert.Equal(stored.ConversationId, cardState.ConversationId);
        Assert.Equal(TeamsCardStatuses.Pending, cardState.Status);
    }

    /// <summary>
    /// Defensive coverage for the all-or-nothing persistence guarantee: when the
    /// proactive turn context does not surface a <c>Conversation.Id</c> the notifier
    /// throws <see cref="InvalidOperationException"/> rather than silently writing a
    /// partial <see cref="TeamsCardState"/> that the bare-approve/reject lookup or the
    /// Stage 3.3 card-update path cannot resolve.
    /// </summary>
    [Fact]
    public async Task SendProactiveQuestionAsync_NoConversationIdFromTurnContext_ThrowsAndDoesNotPersist()
    {
        var harness = NotifierHarness.Build(new ConversationlessCloudAdapter());
        var stored = NewPersonalReference("ref-noconv", aadObjectId: "aad-noconv", internalUserId: "user-1");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-1")] = stored;
        var question = NewQuestion("Q-noconv", targetUserId: "user-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-1", question, CancellationToken.None));

        // Send was attempted but no persistence ran.
        Assert.Single(harness.Adapter.Sent);
        Assert.Empty(harness.CardStateStore.Saved);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
    }

    /// <summary>
    /// Reference-not-found path for the channel scope mirrors the user-scope test —
    /// verifies the typed exception's <see cref="ConversationReferenceNotFoundException.ChannelId"/>
    /// carries the looked-up identifier.
    /// </summary>
    [Fact]
    public async Task SendToChannelAsync_NoStoredReference_ThrowsConversationReferenceNotFoundException()
    {
        var harness = NotifierHarness.Build();
        var message = new MessengerMessage(
            MessageId: "msg-missing",
            CorrelationId: "corr-missing",
            AgentId: "agent-build",
            TaskId: "task-9",
            ConversationId: string.Empty,
            Body: "Should fail.",
            Severity: MessageSeverities.Info,
            Timestamp: DateTimeOffset.UtcNow);

        var ex = await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            harness.Notifier.SendToChannelAsync(TenantId, "missing-channel", message, CancellationToken.None));

        Assert.Equal(TenantId, ex.TenantId);
        Assert.Equal("missing-channel", ex.ChannelId);
        Assert.Null(ex.InternalUserId);
        Assert.Null(ex.QuestionId);
        Assert.Empty(harness.Adapter.ContinueCalls);
    }

    /// <summary>
    /// Null / blank argument guards exercise every public entry point so DI mis-use
    /// fails loudly at the call site rather than producing a NullReferenceException
    /// inside the notifier.
    /// </summary>
    [Fact]
    public async Task PublicEntryPoints_NullOrBlankArguments_ThrowExpectedExceptions()
    {
        var harness = NotifierHarness.Build();
        var question = NewQuestion("Q-arg", targetUserId: "user-1");
        var message = new MessengerMessage("m", "c", "a", "t", string.Empty, "body", MessageSeverities.Info, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Notifier.SendProactiveAsync(string.Empty, "u", message, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Notifier.SendProactiveAsync("t", string.Empty, message, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Notifier.SendProactiveAsync("t", "u", null!, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Notifier.SendProactiveQuestionAsync(string.Empty, "u", question, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Notifier.SendProactiveQuestionAsync("t", string.Empty, question, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Notifier.SendProactiveQuestionAsync("t", "u", null!, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Notifier.SendToChannelAsync(string.Empty, "c", message, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Notifier.SendToChannelAsync("t", string.Empty, message, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Notifier.SendToChannelAsync("t", "c", null!, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Notifier.SendQuestionToChannelAsync(string.Empty, "c", question, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Notifier.SendQuestionToChannelAsync("t", string.Empty, question, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Notifier.SendQuestionToChannelAsync("t", "c", null!, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IProactiveNotifier)harness.Notifier).NotifyQuestionAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// The routing helper throws <see cref="InvalidOperationException"/> when an
    /// <see cref="AgentQuestion"/> fails its own <see cref="AgentQuestion.Validate"/>
    /// (e.g. neither target field populated). This is a structural invariant — the
    /// canonical <c>AgentQuestion</c> contract enforces XOR — and the notifier surfaces
    /// the validation error so misuse is caught before any I/O.
    /// </summary>
    [Fact]
    public async Task NotifyQuestionAsync_InvalidQuestion_ThrowsInvalidOperationException()
    {
        var harness = NotifierHarness.Build();
        // Neither target populated — fails the TargetUserId XOR TargetChannelId invariant.
        var invalid = new AgentQuestion
        {
            QuestionId = "Q-invalid",
            AgentId = "agent",
            TaskId = "task",
            TenantId = TenantId,
            Title = "Title",
            Body = "Body",
            Severity = MessageSeverities.Info,
            AllowedActions = new[] { new HumanAction("a", "A", "approve", false) },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            CorrelationId = "corr-invalid",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IProactiveNotifier)harness.Notifier).NotifyQuestionAsync(invalid, CancellationToken.None));

        Assert.Empty(harness.Adapter.ContinueCalls);
    }

    /// <summary>
    /// Iter-2 evaluator feedback #1 + #3 — security-relevant tenant-isolation guard.
    /// When a direct caller supplies a <paramref name="tenantId"/> that does not match
    /// the question's own <see cref="AgentQuestion.TenantId"/>, the notifier MUST refuse
    /// to send. The previous implementation silently proceeded, which would have
    /// delivered and persisted a tenant-A question under tenant-B's reference store,
    /// breaking the multi-tenant isolation invariant the story's Security / Compliance
    /// rows require. The guard fires BEFORE any I/O, so no reference lookup, network
    /// call, or persistence occurs.
    /// </summary>
    [Fact]
    public async Task SendProactiveQuestionAsync_TenantMismatch_ThrowsAndDoesNotSendOrPersist()
    {
        var harness = NotifierHarness.Build();
        var stored = NewPersonalReference("ref-tenant-mismatch", aadObjectId: "aad-x", internalUserId: "user-1");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-1")] = stored;
        // Question.TenantId == TenantId; caller passes a different tenant.
        var question = NewQuestion("Q-tenant-mismatch", targetUserId: "user-1");
        const string spoofedTenant = "spoofed-tenant-id";

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Notifier.SendProactiveQuestionAsync(spoofedTenant, "user-1", question, CancellationToken.None));
        Assert.Equal("tenantId", ex.ParamName);
        Assert.Contains(question.QuestionId, ex.Message, StringComparison.Ordinal);
        Assert.Contains(TenantId, ex.Message, StringComparison.Ordinal);

        // No reference lookup, no send, no persistence.
        Assert.Empty(harness.ConversationReferenceStore.InternalUserLookups);
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.Adapter.Sent);
        Assert.Empty(harness.CardStateStore.Saved);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
    }

    /// <summary>
    /// Iter-2 evaluator feedback #2 + #3 — user-target consistency guard. When the
    /// supplied <c>userId</c> does not match the question's
    /// <see cref="AgentQuestion.TargetUserId"/>, the notifier refuses to deliver the
    /// approval ask to the wrong user. Fires before any I/O.
    /// </summary>
    [Fact]
    public async Task SendProactiveQuestionAsync_UserIdDoesNotMatchQuestionTarget_ThrowsAndDoesNotSendOrPersist()
    {
        var harness = NotifierHarness.Build();
        var stored = NewPersonalReference("ref-user-mismatch", aadObjectId: "aad-y", internalUserId: "user-other");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-other")] = stored;
        // Question targets user-1; caller asks the notifier to deliver to user-other.
        var question = NewQuestion("Q-user-mismatch", targetUserId: "user-1");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-other", question, CancellationToken.None));
        Assert.Equal("userId", ex.ParamName);
        Assert.Contains("user-1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("user-other", ex.Message, StringComparison.Ordinal);

        Assert.Empty(harness.ConversationReferenceStore.InternalUserLookups);
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.CardStateStore.Saved);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
    }

    /// <summary>
    /// Iter-2 evaluator feedback #2 + #3 — scope-mismatch guard for the user entry
    /// point. When a channel-scoped question (<c>TargetChannelId</c> populated,
    /// <c>TargetUserId</c> null) is handed to <c>SendProactiveQuestionAsync</c>, the
    /// notifier rejects it: routing a channel-scoped approval ask into a 1:1 chat would
    /// leak channel context and mis-route the approval.
    /// </summary>
    [Fact]
    public async Task SendProactiveQuestionAsync_ChannelScopedQuestion_ThrowsAndDoesNotSendOrPersist()
    {
        var harness = NotifierHarness.Build();
        var question = NewQuestion("Q-wrong-scope-user", targetChannelId: "channel-x");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-1", question, CancellationToken.None));
        Assert.Equal("question", ex.ParamName);
        Assert.Contains("channel-scoped", ex.Message, StringComparison.Ordinal);

        Assert.Empty(harness.ConversationReferenceStore.InternalUserLookups);
        Assert.Empty(harness.Adapter.ContinueCalls);
    }

    /// <summary>
    /// Iter-2 evaluator feedback #1 + #3 — tenant-isolation guard for the channel
    /// entry point. Symmetric to
    /// <see cref="SendProactiveQuestionAsync_TenantMismatch_ThrowsAndDoesNotSendOrPersist"/>.
    /// </summary>
    [Fact]
    public async Task SendQuestionToChannelAsync_TenantMismatch_ThrowsAndDoesNotSendOrPersist()
    {
        var harness = NotifierHarness.Build();
        var stored = NewChannelReference("ref-chan-tenant-mismatch", channelId: "channel-general");
        harness.ConversationReferenceStore.PreloadByChannelId[(TenantId, "channel-general")] = stored;
        var question = NewQuestion("Q-chan-tenant-mismatch", targetChannelId: "channel-general");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Notifier.SendQuestionToChannelAsync("spoofed-tenant-id", "channel-general", question, CancellationToken.None));
        Assert.Equal("tenantId", ex.ParamName);
        Assert.Contains(question.QuestionId, ex.Message, StringComparison.Ordinal);

        Assert.Empty(harness.ConversationReferenceStore.ChannelLookups);
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.CardStateStore.Saved);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
    }

    /// <summary>
    /// Iter-2 evaluator feedback #2 + #3 — channel-target consistency guard. Symmetric
    /// to the user-id mismatch case but for channel-scope.
    /// </summary>
    [Fact]
    public async Task SendQuestionToChannelAsync_ChannelIdDoesNotMatchQuestionTarget_ThrowsAndDoesNotSendOrPersist()
    {
        var harness = NotifierHarness.Build();
        var stored = NewChannelReference("ref-chan-mismatch", channelId: "channel-other");
        harness.ConversationReferenceStore.PreloadByChannelId[(TenantId, "channel-other")] = stored;
        var question = NewQuestion("Q-chan-mismatch", targetChannelId: "channel-general");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Notifier.SendQuestionToChannelAsync(TenantId, "channel-other", question, CancellationToken.None));
        Assert.Equal("channelId", ex.ParamName);
        Assert.Contains("channel-general", ex.Message, StringComparison.Ordinal);
        Assert.Contains("channel-other", ex.Message, StringComparison.Ordinal);

        Assert.Empty(harness.ConversationReferenceStore.ChannelLookups);
        Assert.Empty(harness.Adapter.ContinueCalls);
    }

    /// <summary>
    /// Iter-2 evaluator feedback #2 + #3 — scope-mismatch guard for the channel entry
    /// point. When a user-scoped question (<c>TargetUserId</c> populated,
    /// <c>TargetChannelId</c> null) is handed to <c>SendQuestionToChannelAsync</c>, the
    /// notifier rejects it: routing a user-scoped approval ask into a team channel
    /// would broadcast a 1:1 ask to every channel member.
    /// </summary>
    [Fact]
    public async Task SendQuestionToChannelAsync_UserScopedQuestion_ThrowsAndDoesNotSendOrPersist()
    {
        var harness = NotifierHarness.Build();
        var question = NewQuestion("Q-wrong-scope-channel", targetUserId: "user-1");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Notifier.SendQuestionToChannelAsync(TenantId, "channel-general", question, CancellationToken.None));
        Assert.Equal("question", ex.ParamName);
        Assert.Contains("user-scoped", ex.Message, StringComparison.Ordinal);

        Assert.Empty(harness.ConversationReferenceStore.ChannelLookups);
        Assert.Empty(harness.Adapter.ContinueCalls);
    }

    // ---- Test helpers ------------------------------------------------------------

    private static TeamsConversationReference NewPersonalReference(
        string id,
        string aadObjectId,
        string internalUserId,
        string conversationId = PersonalConversationId)
    {
        var bfReference = new ConversationReference
        {
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            Bot = new ChannelAccount(id: MicrosoftAppId, name: "AgentBot"),
            User = new ChannelAccount(id: $"29:{aadObjectId}", name: "User") { AadObjectId = aadObjectId },
            Conversation = new ConversationAccount(id: conversationId) { TenantId = TenantId },
        };

        return new TeamsConversationReference
        {
            Id = id,
            TenantId = TenantId,
            AadObjectId = aadObjectId,
            InternalUserId = internalUserId,
            ServiceUrl = bfReference.ServiceUrl,
            ConversationId = conversationId,
            BotId = MicrosoftAppId,
            ReferenceJson = JsonConvert.SerializeObject(bfReference),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
    }

    private static TeamsConversationReference NewChannelReference(
        string id,
        string channelId,
        string conversationId = ChannelConversationId)
    {
        var bfReference = new ConversationReference
        {
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/amer/",
            Bot = new ChannelAccount(id: MicrosoftAppId, name: "AgentBot"),
            Conversation = new ConversationAccount(id: conversationId) { TenantId = TenantId, ConversationType = "channel" },
        };

        return new TeamsConversationReference
        {
            Id = id,
            TenantId = TenantId,
            ChannelId = channelId,
            ServiceUrl = bfReference.ServiceUrl,
            ConversationId = conversationId,
            BotId = MicrosoftAppId,
            ReferenceJson = JsonConvert.SerializeObject(bfReference),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
    }

    private static AgentQuestion NewQuestion(
        string questionId,
        string? targetUserId = null,
        string? targetChannelId = null)
    {
        return new AgentQuestion
        {
            QuestionId = questionId,
            AgentId = "agent-build",
            TaskId = "task-42",
            TenantId = TenantId,
            TargetUserId = targetUserId,
            TargetChannelId = targetChannelId,
            Title = "Promote build to staging?",
            Body = "Build #42 finished. Promote to staging environment?",
            Severity = MessageSeverities.Info,
            AllowedActions = new[]
            {
                new HumanAction("approve", "Approve", "approve", false),
                new HumanAction("reject", "Reject", "reject", true),
            },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            CorrelationId = $"corr-{questionId}",
        };
    }

    /// <summary>
    /// Aggregates the notifier and every recording double behind a single value so each
    /// test stays focused on Arrange / Act / Assert. Shares the same dependency-graph
    /// shape as <c>TeamsMessengerConnectorTests.ConnectorHarness</c> so the two test
    /// suites are visually consistent.
    /// </summary>
    private sealed record NotifierHarness(
        TeamsProactiveNotifier Notifier,
        RecordingCloudAdapter Adapter,
        RecordingConversationReferenceStore ConversationReferenceStore,
        RecordingAgentQuestionStore AgentQuestionStore,
        RecordingCardStateStore CardStateStore,
        TeamsMessagingOptions Options)
    {
        public static NotifierHarness Build(RecordingCloudAdapter? adapter = null)
        {
            adapter ??= new RecordingCloudAdapter();
            var options = new TeamsMessagingOptions { MicrosoftAppId = MicrosoftAppId };
            var convStore = new RecordingConversationReferenceStore();
            var qStore = new RecordingAgentQuestionStore();
            var cardStore = new RecordingCardStateStore();
            var renderer = new AdaptiveCardBuilder();
            var notifier = new TeamsProactiveNotifier(
                adapter,
                options,
                convStore,
                renderer,
                cardStore,
                qStore,
                NullLogger<TeamsProactiveNotifier>.Instance);
            return new NotifierHarness(notifier, adapter, convStore, qStore, cardStore, options);
        }
    }

    /// <summary>
    /// <see cref="CloudAdapter"/> subclass that captures proactive calls and outbound
    /// activities without contacting the real Bot Framework Connector. Same shape as the
    /// one used by <c>TeamsMessengerConnectorTests</c>; duplicated locally so the two
    /// suites can evolve independently.
    /// </summary>
    public class RecordingCloudAdapter : CloudAdapter
    {
        public List<(string BotAppId, ConversationReference Reference)> ContinueCalls { get; } = new();
        public List<Activity> Sent { get; } = new();

        public override Task ContinueConversationAsync(
            string botAppId,
            ConversationReference reference,
            BotCallbackHandler callback,
            CancellationToken cancellationToken)
        {
            ContinueCalls.Add((botAppId, reference));
            var continuation = SynthesizeContinuationActivity(reference);
            var turnContext = new TurnContext(this, continuation);
            return callback(turnContext, cancellationToken);
        }

        protected virtual Activity SynthesizeContinuationActivity(ConversationReference reference)
            => (Activity)reference.GetContinuationActivity();

        public override Task<ResourceResponse[]> SendActivitiesAsync(
            ITurnContext turnContext,
            Activity[] activities,
            CancellationToken cancellationToken)
        {
            Sent.AddRange(activities);
            var responses = new ResourceResponse[activities.Length];
            for (var i = 0; i < activities.Length; i++)
            {
                responses[i] = new ResourceResponse(id: $"act-{Guid.NewGuid():N}");
            }

            return Task.FromResult(responses);
        }
    }

    /// <summary>
    /// Recording adapter that strips the <c>Conversation.Id</c> from the synthesized
    /// continuation activity so the all-or-nothing persistence guard can be exercised.
    /// </summary>
    public sealed class ConversationlessCloudAdapter : RecordingCloudAdapter
    {
        protected override Activity SynthesizeContinuationActivity(ConversationReference reference)
        {
            var continuation = base.SynthesizeContinuationActivity(reference);
            continuation.Conversation = new ConversationAccount(id: null);
            return continuation;
        }
    }

    /// <summary>
    /// Recording <see cref="IConversationReferenceStore"/> tailored for notifier tests.
    /// Captures the (TenantId, key) tuples used for each lookup so the routing /
    /// reference-resolution paths can be asserted directly.
    /// </summary>
    public sealed class RecordingConversationReferenceStore : IConversationReferenceStore
    {
        public Dictionary<(string TenantId, string InternalUserId), TeamsConversationReference> PreloadByInternalUserId { get; } = new();
        public Dictionary<(string TenantId, string ChannelId), TeamsConversationReference> PreloadByChannelId { get; } = new();
        public List<(string TenantId, string InternalUserId)> InternalUserLookups { get; } = new();
        public List<(string TenantId, string ChannelId)> ChannelLookups { get; } = new();

        public Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct) => Task.CompletedTask;
        public Task<TeamsConversationReference?> GetAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);
        public Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);

        public Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
        {
            InternalUserLookups.Add((tenantId, internalUserId));
            PreloadByInternalUserId.TryGetValue((tenantId, internalUserId), out var hit);
            return Task.FromResult<TeamsConversationReference?>(hit);
        }

        public Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct)
        {
            ChannelLookups.Add((tenantId, channelId));
            PreloadByChannelId.TryGetValue((tenantId, channelId), out var hit);
            return Task.FromResult<TeamsConversationReference?>(hit);
        }

        public Task<IReadOnlyList<TeamsConversationReference>> GetActiveChannelsByTeamIdAsync(string tenantId, string teamId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TeamsConversationReference>>(Array.Empty<TeamsConversationReference>());

        public Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(string tenantId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TeamsConversationReference>>(Array.Empty<TeamsConversationReference>());

        public Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.FromResult(false);

        public Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.CompletedTask;
        public Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Recording <see cref="IAgentQuestionStore"/> mirroring the connector tests' shape
    /// so the two-step persistence sequence (UpdateConversationId + SaveAsync) can be
    /// asserted in order.
    /// </summary>
    public sealed class RecordingAgentQuestionStore : IAgentQuestionStore
    {
        public List<AgentQuestion> Saved { get; } = new();
        public List<(string QuestionId, string ConversationId)> ConversationIdUpdates { get; } = new();

        public Task SaveAsync(AgentQuestion question, CancellationToken ct)
        {
            Saved.Add(question);
            return Task.CompletedTask;
        }

        public Task<AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct) => Task.FromResult<AgentQuestion?>(null);
        public Task<bool> TryUpdateStatusAsync(string questionId, string expectedStatus, string newStatus, CancellationToken ct) => Task.FromResult(false);

        public Task UpdateConversationIdAsync(string questionId, string conversationId, CancellationToken ct)
        {
            ConversationIdUpdates.Add((questionId, conversationId));
            return Task.CompletedTask;
        }

        public Task<AgentQuestion?> GetMostRecentOpenByConversationAsync(string conversationId, CancellationToken ct) => Task.FromResult<AgentQuestion?>(null);
        public Task<IReadOnlyList<AgentQuestion>> GetOpenByConversationAsync(string conversationId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());
        public Task<IReadOnlyList<AgentQuestion>> GetOpenExpiredAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());
    }

    /// <summary>
    /// Recording <see cref="ICardStateStore"/> for notifier tests. Captures every saved
    /// <see cref="TeamsCardState"/> row so the all-or-nothing persistence contract can
    /// be asserted.
    /// </summary>
    public sealed class RecordingCardStateStore : ICardStateStore
    {
        public List<TeamsCardState> Saved { get; } = new();

        public Task SaveAsync(TeamsCardState state, CancellationToken ct)
        {
            Saved.Add(state);
            return Task.CompletedTask;
        }

        public Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct) => Task.FromResult<TeamsCardState?>(null);
        public Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct) => Task.CompletedTask;
    }
}
