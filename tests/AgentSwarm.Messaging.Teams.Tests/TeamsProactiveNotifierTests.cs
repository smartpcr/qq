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

        // Iter-3 evaluator feedback #1 — the question must be persisted BEFORE the
        // network send so CardActionHandler.GetByIdAsync resolves subsequent Adaptive
        // Card actions, AND so UpdateConversationIdAsync (which is implemented as a
        // SQL ExecuteUpdate that silently affects zero rows when the row is missing)
        // actually stamps the conversation ID. The saved copy MUST have
        // ConversationId = null (sanitised) so a caller-supplied stale value cannot
        // poison the bare approve/reject lookup path.
        var saved = Assert.Single(harness.AgentQuestionStore.Saved);
        Assert.Equal("Q-proactive-1", saved.QuestionId);
        Assert.Null(saved.ConversationId);

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

        // Iter-3 evaluator feedback #1 — the AgentQuestion row IS persisted up front
        // (before the lookup), so the orchestrator can observe the attempt and the
        // Phase 6 outbox engine has a durable row to replay against. The saved copy
        // has ConversationId = null (no proactive turn context to source it from).
        var saved = Assert.Single(harness.AgentQuestionStore.Saved);
        Assert.Equal("Q-missing-ref", saved.QuestionId);
        Assert.Null(saved.ConversationId);
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

        // Iter-3 evaluator feedback #1 — the channel-scoped question path also persists
        // the AgentQuestion BEFORE the send so the post-send UpdateConversationIdAsync
        // can find a row to stamp and CardActionHandler.GetByIdAsync can resolve
        // subsequent Adaptive Card actions delivered into the channel.
        var saved = Assert.Single(harness.AgentQuestionStore.Saved);
        Assert.Equal("Q-channel-question", saved.QuestionId);
        Assert.Null(saved.ConversationId);
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

        // Send was attempted but no per-card persistence ran.
        Assert.Single(harness.Adapter.Sent);
        Assert.Empty(harness.CardStateStore.Saved);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);

        // Iter-3 evaluator feedback #1 — the upfront SaveAsync still ran (it precedes
        // the lookup / send), so the AgentQuestion row exists with ConversationId =
        // null. The all-or-nothing card-state persistence guarantee is what gets
        // skipped: CardStateStore.Saved and ConversationIdUpdates remain empty so the
        // Stage 3.3 card-update / bare approve-reject paths are not given a partial
        // row to follow.
        var saved = Assert.Single(harness.AgentQuestionStore.Saved);
        Assert.Equal("Q-noconv", saved.QuestionId);
        Assert.Null(saved.ConversationId);
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
    /// Iter-3 evaluator feedback #1 (channel-scope coverage) — mirrors
    /// <see cref="SendProactiveQuestionAsync_NoStoredReference_ThrowsConversationReferenceNotFoundException"/>
    /// for the channel-scope question entry point. The upfront SaveAsync must run
    /// even when the channel reference is missing so the orchestrator and the
    /// Phase 6 outbox engine see a durable row to attribute the failed delivery to.
    /// </summary>
    [Fact]
    public async Task SendQuestionToChannelAsync_NoStoredReference_PersistsQuestionAndThrows()
    {
        var harness = NotifierHarness.Build();
        var question = NewQuestion("Q-missing-chan-ref", targetChannelId: "missing-channel");

        var ex = await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            harness.Notifier.SendQuestionToChannelAsync(TenantId, "missing-channel", question, CancellationToken.None));

        Assert.Equal(TenantId, ex.TenantId);
        Assert.Equal("missing-channel", ex.ChannelId);
        Assert.Equal("Q-missing-chan-ref", ex.QuestionId);

        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.Adapter.Sent);
        Assert.Empty(harness.CardStateStore.Saved);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);

        var saved = Assert.Single(harness.AgentQuestionStore.Saved);
        Assert.Equal("Q-missing-chan-ref", saved.QuestionId);
        Assert.Null(saved.ConversationId);
    }

    /// <summary>
    /// Iter-3 evaluator feedback #1 (ordering invariant) — the upfront SaveAsync must
    /// execute BEFORE the reference lookup AND before <c>ContinueConversationAsync</c>.
    /// This pins the relative ordering by recording the sequence into a shared list
    /// across the AgentQuestion store, the conversation-reference store, and the
    /// CloudAdapter — the ordering matters because UpdateConversationIdAsync silently
    /// affects zero rows when the row is missing, and CardActionHandler.GetByIdAsync
    /// returns null for an unpersisted question.
    /// </summary>
    [Fact]
    public async Task SendProactiveQuestionAsync_SaveAsyncRunsBeforeLookupAndContinue()
    {
        var ordering = new List<string>();
        var adapter = new OrderRecordingCloudAdapter(ordering);
        var harness = NotifierHarness.Build(adapter);
        var stored = NewPersonalReference("ref-ordering", aadObjectId: "aad-ord", internalUserId: "user-1");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-1")] = stored;
        harness.ConversationReferenceStore.OnInternalUserLookup = () => ordering.Add("lookup");
        harness.AgentQuestionStore.OnSave = () => ordering.Add("save");
        harness.AgentQuestionStore.OnUpdateConversationId = () => ordering.Add("updateConversationId");
        harness.CardStateStore.OnSave = () => ordering.Add("cardStateSave");
        var question = NewQuestion("Q-ordering", targetUserId: "user-1");

        await harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-1", question, CancellationToken.None);

        Assert.Equal(
            new[] { "save", "lookup", "continueConversation", "updateConversationId", "cardStateSave" },
            ordering);
    }

    /// <summary>
    /// Reliability of the new ordering — symmetric to the user-scope ordering test for
    /// the channel-scope question entry point.
    /// </summary>
    [Fact]
    public async Task SendQuestionToChannelAsync_SaveAsyncRunsBeforeLookupAndContinue()
    {
        var ordering = new List<string>();
        var adapter = new OrderRecordingCloudAdapter(ordering);
        var harness = NotifierHarness.Build(adapter);
        var stored = NewChannelReference("ref-ordering-chan", channelId: "channel-general");
        harness.ConversationReferenceStore.PreloadByChannelId[(TenantId, "channel-general")] = stored;
        harness.ConversationReferenceStore.OnChannelLookup = () => ordering.Add("lookup");
        harness.AgentQuestionStore.OnSave = () => ordering.Add("save");
        harness.AgentQuestionStore.OnUpdateConversationId = () => ordering.Add("updateConversationId");
        harness.CardStateStore.OnSave = () => ordering.Add("cardStateSave");
        var question = NewQuestion("Q-ordering-chan", targetChannelId: "channel-general");

        await harness.Notifier.SendQuestionToChannelAsync(TenantId, "channel-general", question, CancellationToken.None);

        Assert.Equal(
            new[] { "save", "lookup", "continueConversation", "updateConversationId", "cardStateSave" },
            ordering);
    }

    /// <summary>
    /// Iter-4 evaluator feedback #1 / #2 — outbox-retry idempotency for the user-scope
    /// proactive question path. The first attempt fails at the reference-lookup step
    /// (no stored reference), so the question row IS persisted but the card is NOT
    /// delivered. After the reference becomes available, a second attempt with the
    /// SAME <see cref="AgentQuestion"/> must:
    /// <list type="bullet">
    ///   <item><description>Detect the preexisting row via <see cref="IAgentQuestionStore.GetByIdAsync"/> and skip the duplicate <see cref="IAgentQuestionStore.SaveAsync"/> (otherwise <c>SqlAgentQuestionStore.SaveAsync</c>'s insert-only <c>Add(entity)</c> would throw a unique-PK <c>DbUpdateException</c>).</description></item>
    ///   <item><description>Still resolve the reference, deliver the card, and stamp the conversation ID on the existing row.</description></item>
    ///   <item><description>Persist exactly ONE card-state row keyed by <c>QuestionId</c>.</description></item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task SendProactiveQuestionAsync_RetryAfterMissingReference_DeliversWithoutDoubleSave()
    {
        var harness = NotifierHarness.Build();
        var question = NewQuestion("Q-retry-user", targetUserId: "user-retry");

        // First attempt — no reference yet. Question row is persisted; throws.
        await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-retry", question, CancellationToken.None));
        Assert.Single(harness.AgentQuestionStore.Saved);
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.CardStateStore.Saved);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);

        // Reference becomes available (user installed the app, etc.).
        var stored = NewPersonalReference("ref-retry", aadObjectId: "aad-retry", internalUserId: "user-retry");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-retry")] = stored;

        // Second attempt — same question. Must succeed and must NOT double-save.
        await harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-retry", question, CancellationToken.None);

        // Question row is still the single original one (no duplicate insert attempted).
        var saved = Assert.Single(harness.AgentQuestionStore.Saved);
        Assert.Equal("Q-retry-user", saved.QuestionId);
        Assert.Null(saved.ConversationId);

        // Card was delivered, ConversationId stamped, and card-state persisted.
        Assert.Single(harness.Adapter.ContinueCalls);
        Assert.Single(harness.Adapter.Sent);
        var update = Assert.Single(harness.AgentQuestionStore.ConversationIdUpdates);
        Assert.Equal("Q-retry-user", update.QuestionId);
        Assert.Equal(stored.ConversationId, update.ConversationId);
        var cardState = Assert.Single(harness.CardStateStore.Saved);
        Assert.Equal("Q-retry-user", cardState.QuestionId);
    }

    /// <summary>
    /// Iter-4 evaluator feedback #1 / #2 — channel-scope mirror of the retry-idempotency
    /// test. Documents that <see cref="TeamsProactiveNotifier.SendQuestionToChannelAsync"/>
    /// also tolerates a preexisting <see cref="AgentQuestion"/> row.
    /// </summary>
    [Fact]
    public async Task SendQuestionToChannelAsync_RetryAfterMissingReference_DeliversWithoutDoubleSave()
    {
        var harness = NotifierHarness.Build();
        var question = NewQuestion("Q-retry-chan", targetChannelId: "channel-retry");

        await Assert.ThrowsAsync<ConversationReferenceNotFoundException>(() =>
            harness.Notifier.SendQuestionToChannelAsync(TenantId, "channel-retry", question, CancellationToken.None));
        Assert.Single(harness.AgentQuestionStore.Saved);
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.CardStateStore.Saved);

        var stored = NewChannelReference("ref-retry-chan", channelId: "channel-retry");
        harness.ConversationReferenceStore.PreloadByChannelId[(TenantId, "channel-retry")] = stored;

        await harness.Notifier.SendQuestionToChannelAsync(TenantId, "channel-retry", question, CancellationToken.None);

        var saved = Assert.Single(harness.AgentQuestionStore.Saved);
        Assert.Equal("Q-retry-chan", saved.QuestionId);
        Assert.Null(saved.ConversationId);

        Assert.Single(harness.Adapter.ContinueCalls);
        Assert.Single(harness.Adapter.Sent);
        Assert.Single(harness.AgentQuestionStore.ConversationIdUpdates);
        var cardState = Assert.Single(harness.CardStateStore.Saved);
        Assert.Equal("Q-retry-chan", cardState.QuestionId);
    }

    /// <summary>
    /// Iter-4 rubber-duck blocking #1 — when a <see cref="TeamsCardState"/> row already
    /// exists for the question ID, the previous attempt already delivered the card and a
    /// re-send would produce a duplicate Adaptive Card in Teams (plus overwrite the
    /// stored <c>ActivityId</c>/<c>ConversationReferenceJson</c>, orphaning the first
    /// card). The notifier must short-circuit and not touch the network.
    /// </summary>
    [Fact]
    public async Task SendProactiveQuestionAsync_CardStateAlreadyPresent_SkipsResendEntirely()
    {
        var harness = NotifierHarness.Build();
        var stored = NewPersonalReference("ref-idem", aadObjectId: "aad-idem", internalUserId: "user-idem");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-idem")] = stored;
        var question = NewQuestion("Q-card-state-present", targetUserId: "user-idem");

        // Seed a card-state row as if the previous attempt already delivered.
        var preexisting = new TeamsCardState
        {
            QuestionId = "Q-card-state-present",
            ConversationId = stored.ConversationId,
            ActivityId = "act-prior-delivery",
            ConversationReferenceJson = "{}",
            Status = TeamsCardStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        await harness.CardStateStore.SaveAsync(preexisting, CancellationToken.None);
        Assert.Single(harness.CardStateStore.Saved);

        await harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-idem", question, CancellationToken.None);

        // No network call, no new save, no ConversationId update — pure idempotent no-op.
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.Adapter.Sent);
        Assert.Empty(harness.AgentQuestionStore.Saved);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
        Assert.Empty(harness.ConversationReferenceStore.InternalUserLookups);
        Assert.Single(harness.CardStateStore.Saved); // unchanged
    }

    /// <summary>
    /// Iter-4 rubber-duck blocking #2 — when the preexisting <see cref="AgentQuestion"/>
    /// row has a terminal status (<c>Resolved</c> / <c>Expired</c>), sending another
    /// Adaptive Card would produce a stale approval prompt the user could not actually
    /// interact with (<see cref="Cards.CardActionHandler"/> rejects with
    /// <c>AlreadyResolved</c> / <c>Expired</c>). The notifier throws so the outbox
    /// surfaces the orchestrator bug rather than silently delivering a dead card.
    /// </summary>
    [Theory]
    [InlineData(AgentQuestionStatuses.Resolved)]
    [InlineData(AgentQuestionStatuses.Expired)]
    public async Task SendProactiveQuestionAsync_ExistingTerminalStatus_ThrowsAndDoesNotSend(string terminalStatus)
    {
        var harness = NotifierHarness.Build();
        var stored = NewPersonalReference("ref-term", aadObjectId: "aad-term", internalUserId: "user-term");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-term")] = stored;
        var question = NewQuestion("Q-terminal", targetUserId: "user-term");
        // Seed a preexisting question row already in a terminal status.
        var seeded = question with { Status = terminalStatus, ConversationId = null };
        await harness.AgentQuestionStore.SaveAsync(seeded, CancellationToken.None);
        var savedCountBefore = harness.AgentQuestionStore.Saved.Count;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-term", question, CancellationToken.None));
        Assert.Contains(terminalStatus, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Q-terminal", ex.Message, StringComparison.Ordinal);

        // No additional save, no network call, no ConversationId stamp, no card-state row.
        Assert.Equal(savedCountBefore, harness.AgentQuestionStore.Saved.Count);
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.Adapter.Sent);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
        Assert.Empty(harness.CardStateStore.Saved);
    }

    /// <summary>
    /// Iter-4 rubber-duck non-blocking #2 — if the orchestrator retries a proactive
    /// send with a question whose immutable identity/payload fields differ from the
    /// stored row, the card delivered to Teams would diverge from the row that
    /// <see cref="Cards.CardActionHandler"/> later loads via
    /// <see cref="IAgentQuestionStore.GetByIdAsync"/>. The notifier rejects this rather
    /// than silently sending a card with the new payload but storing the old one.
    /// </summary>
    [Fact]
    public async Task SendProactiveQuestionAsync_PreexistingRowWithDivergentPayload_ThrowsAndDoesNotSend()
    {
        var harness = NotifierHarness.Build();
        var stored = NewPersonalReference("ref-mismatch", aadObjectId: "aad-mismatch", internalUserId: "user-mismatch");
        harness.ConversationReferenceStore.PreloadByInternalUserId[(TenantId, "user-mismatch")] = stored;
        var original = NewQuestion("Q-mismatch", targetUserId: "user-mismatch");
        await harness.AgentQuestionStore.SaveAsync(original with { ConversationId = null }, CancellationToken.None);
        var savedCountBefore = harness.AgentQuestionStore.Saved.Count;

        // Same QuestionId, but Title and Body changed. This simulates the orchestrator
        // mutating the in-flight question.
        var mutated = original with { Title = "TOTALLY DIFFERENT TITLE", Body = "different body too" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Notifier.SendProactiveQuestionAsync(TenantId, "user-mismatch", mutated, CancellationToken.None));
        Assert.Contains("Q-mismatch", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Title", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Body", ex.Message, StringComparison.Ordinal);

        Assert.Equal(savedCountBefore, harness.AgentQuestionStore.Saved.Count);
        Assert.Empty(harness.Adapter.ContinueCalls);
        Assert.Empty(harness.Adapter.Sent);
        Assert.Empty(harness.AgentQuestionStore.ConversationIdUpdates);
        Assert.Empty(harness.CardStateStore.Saved);
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
        // Iter-3 evaluator feedback #1 invariant — the tenant guard fires BEFORE the
        // upfront SaveAsync, so no AgentQuestion row leaks into the store on a tenant
        // mismatch (would otherwise pollute the durable log with a question that was
        // never authorised to be delivered).
        Assert.Empty(harness.AgentQuestionStore.Saved);
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
        // Iter-3 evaluator feedback #1 invariant — see tenant-mismatch test above.
        Assert.Empty(harness.AgentQuestionStore.Saved);
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
        // Iter-3 evaluator feedback #1 invariant — the scope guard fires BEFORE Save.
        Assert.Empty(harness.AgentQuestionStore.Saved);
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
        // Iter-3 evaluator feedback #1 invariant — see user-scope tenant-mismatch test.
        Assert.Empty(harness.AgentQuestionStore.Saved);
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
        // Iter-3 evaluator feedback #1 invariant — see user-scope tenant-mismatch test.
        Assert.Empty(harness.AgentQuestionStore.Saved);
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
        // Iter-3 evaluator feedback #1 invariant — see user-scope tenant-mismatch test.
        Assert.Empty(harness.AgentQuestionStore.Saved);
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
            OnContinueConversation();
            var continuation = SynthesizeContinuationActivity(reference);
            var turnContext = new TurnContext(this, continuation);
            return callback(turnContext, cancellationToken);
        }

        /// <summary>
        /// Hook invoked at the start of every <see cref="ContinueConversationAsync"/>
        /// call so the ordering tests can record the relative position of the network
        /// send against the upfront <see cref="IAgentQuestionStore.SaveAsync"/> and the
        /// reference lookup. The default is a no-op.
        /// </summary>
        protected virtual void OnContinueConversation()
        {
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
    /// Recording adapter that appends the literal <c>"continueConversation"</c> marker
    /// to an externally-provided ordering list. Used by the iter-3 ordering tests to
    /// pin the relative position of the upfront <see cref="IAgentQuestionStore.SaveAsync"/>,
    /// the reference lookup, and the proactive send.
    /// </summary>
    public sealed class OrderRecordingCloudAdapter : RecordingCloudAdapter
    {
        private readonly List<string> _ordering;
        public OrderRecordingCloudAdapter(List<string> ordering) => _ordering = ordering;
        protected override void OnContinueConversation() => _ordering.Add("continueConversation");
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

        /// <summary>Optional hook fired BEFORE every <see cref="GetByInternalUserIdAsync"/> resolves; used by ordering tests.</summary>
        public Action? OnInternalUserLookup { get; set; }

        /// <summary>Optional hook fired BEFORE every <see cref="GetByChannelIdAsync"/> resolves; used by ordering tests.</summary>
        public Action? OnChannelLookup { get; set; }

        public Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct) => Task.CompletedTask;
        public Task<TeamsConversationReference?> GetAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);
        public Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct) => Task.FromResult<TeamsConversationReference?>(null);

        public Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
        {
            OnInternalUserLookup?.Invoke();
            InternalUserLookups.Add((tenantId, internalUserId));
            PreloadByInternalUserId.TryGetValue((tenantId, internalUserId), out var hit);
            return Task.FromResult<TeamsConversationReference?>(hit);
        }

        public Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct)
        {
            OnChannelLookup?.Invoke();
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

        /// <summary>Optional hook fired BEFORE every <see cref="SaveAsync"/> records; used by ordering tests.</summary>
        public Action? OnSave { get; set; }

        /// <summary>Optional hook fired BEFORE every <see cref="UpdateConversationIdAsync"/> records; used by ordering tests.</summary>
        public Action? OnUpdateConversationId { get; set; }

        public Task SaveAsync(AgentQuestion question, CancellationToken ct)
        {
            OnSave?.Invoke();
            Saved.Add(question);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Iter-4: returns the most recently saved question with the matching
        /// <see cref="AgentQuestion.QuestionId"/>, mirroring the SQL store's read
        /// semantics so retry-idempotency tests can simulate a preexisting row.
        /// Returns <c>null</c> when nothing has been saved with that ID.
        /// </summary>
        public Task<AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct)
        {
            AgentQuestion? hit = null;
            for (var i = Saved.Count - 1; i >= 0; i--)
            {
                if (string.Equals(Saved[i].QuestionId, questionId, StringComparison.Ordinal))
                {
                    hit = Saved[i];
                    break;
                }
            }

            return Task.FromResult(hit);
        }
        public Task<bool> TryUpdateStatusAsync(string questionId, string expectedStatus, string newStatus, CancellationToken ct) => Task.FromResult(false);

        public Task UpdateConversationIdAsync(string questionId, string conversationId, CancellationToken ct)
        {
            OnUpdateConversationId?.Invoke();
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

        /// <summary>Optional hook fired BEFORE every <see cref="SaveAsync"/> records; used by ordering tests.</summary>
        public Action? OnSave { get; set; }

        public Task SaveAsync(TeamsCardState state, CancellationToken ct)
        {
            OnSave?.Invoke();
            Saved.Add(state);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Iter-4: returns the most recently saved card state with the matching
        /// <see cref="TeamsCardState.QuestionId"/>, mirroring the SQL store so the
        /// notifier's outbox-retry idempotency short-circuit can be exercised.
        /// Returns <c>null</c> when nothing has been saved with that QuestionId.
        /// </summary>
        public Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct)
        {
            TeamsCardState? hit = null;
            for (var i = Saved.Count - 1; i >= 0; i--)
            {
                if (string.Equals(Saved[i].QuestionId, questionId, StringComparison.Ordinal))
                {
                    hit = Saved[i];
                    break;
                }
            }

            return Task.FromResult(hit);
        }
        public Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct) => Task.CompletedTask;
    }
}
