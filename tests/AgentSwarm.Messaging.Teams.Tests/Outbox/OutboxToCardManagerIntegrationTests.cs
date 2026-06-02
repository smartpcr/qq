using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Outbox;
using AgentSwarm.Messaging.Teams.Extensions;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace AgentSwarm.Messaging.Teams.Tests.Outbox;

/// <summary>
/// End-to-end integration coverage for Stage 6.1 work-item scenarios 6 and 9
/// ("Outbox-delivered card can be updated after delivery" and "Card delete inline
/// retry"). Existing per-component unit suites assert the dispatcher captures
/// <see cref="OutboxEntry.ActivityId"/>/<see cref="OutboxEntry.ConversationId"/>/
/// <see cref="OutboxEntry.ConversationReferenceJson"/> into <see cref="ICardStateStore"/>,
/// and that <see cref="ITeamsCardManager"/> rehydrates the proactive context from a
/// PRE-loaded card-state row — but the two sides are exercised against independent test
/// doubles. These tests close the contract gap by sharing a single
/// <see cref="ICardStateStore"/> across both components and asserting:
/// <list type="number">
///   <item><description>
///     The dispatcher's post-send <see cref="TeamsOutboxDispatcher"/> capture writes
///     exactly the identifiers <see cref="TeamsMessengerConnector.UpdateCardAsync(string, CardUpdateAction, CancellationToken)"/>
///     and <see cref="TeamsMessengerConnector.DeleteCardAsync"/> need (canonical
///     <see cref="TeamsCardState"/> shape).
///   </description></item>
///   <item><description>
///     The connector reads that state, deserialises <see cref="TeamsCardState.ConversationReferenceJson"/>,
///     calls <see cref="CloudAdapter.ContinueConversationAsync"/> with the rehydrated
///     reference, and dispatches <see cref="ITurnContext.UpdateActivityAsync(Activity, CancellationToken)"/>
///     /<see cref="ITurnContext.DeleteActivityAsync(string, CancellationToken)"/> against
///     the SAME <see cref="OutboxEntry.ActivityId"/> the dispatcher captured.
///   </description></item>
/// </list>
/// The hand-shake validated here is the canonical "outbox owns sends; card manager owns
/// post-send mutations" contract called out in <c>architecture.md</c> §4.7.
/// </summary>
public sealed class OutboxToCardManagerIntegrationTests
{
    private const string AppId = "11111111-2222-3333-4444-555555555555";
    private const string TenantId = "tenant-it";
    private const string UserId = "user-it";
    private const string DeliveredActivityId = "act-it-001";

    // The outbox entry's serialized ConversationReferenceJson carries this conversation
    // id. The HybridCloudAdapter intentionally REWRITES the synthesized turn context's
    // Conversation.Id to <see cref="DeliveredConversationId"/> below — so a dispatcher
    // bug that persisted the original entry reference instead of the post-send captured
    // reference would surface as a `saved.ConversationId == OriginalConversationId`
    // mismatch the assertions below reject.
    private const string OriginalConversationId = "19:original-entry@thread.tacv2";
    private const string DeliveredConversationId = "19:delivered-by-adapter@thread.tacv2";

    [Fact]
    public async Task OutboxDeliveredQuestion_CanBeUpdatedByCardManager_MarkAnswered()
    {
        // ---- Arrange -- shared graph -----------------------------------------------
        var adapter = new HybridCloudAdapter
        {
            FixedActivityId = DeliveredActivityId,
            FixedConversationId = DeliveredConversationId,
        };
        var sharedCardStore = new ProductionLikeCardStateStore();
        var questionStore = new RecordingAgentQuestionStore();
        var outbox = new InMemoryOutbox();
        var renderer = new AdaptiveCardBuilder();
        var options = new TeamsMessagingOptions { MicrosoftAppId = AppId };

        // ---- Phase 1 -- dispatcher delivers the AgentQuestion --------------------
        var dispatcher = new TeamsOutboxDispatcher(
            adapter,
            options,
            outbox,
            sharedCardStore,
            questionStore,
            renderer,
            NullLogger<TeamsOutboxDispatcher>.Instance);

        var question = BuildQuestion("q-it-update");
        var entry = BuildOutboxEntry("e-it-1", question, BuildConversationReferenceJson());

        var dispatchResult = await dispatcher.DispatchAsync(entry, CancellationToken.None);

        Assert.Equal(OutboxDispatchOutcome.Success, dispatchResult.Outcome);
        Assert.NotNull(dispatchResult.Receipt);
        Assert.Equal(DeliveredActivityId, dispatchResult.Receipt!.Value.ActivityId);
        Assert.Equal(DeliveredConversationId, dispatchResult.Receipt!.Value.ConversationId);

        // The dispatcher MUST have populated the shared card-state store; this is the
        // post-send capture step the work-item brief calls out for outbox-delivered
        // AgentQuestion cards.
        var saved = Assert.Single(sharedCardStore.Saved);
        Assert.Equal(question.QuestionId, saved.QuestionId);
        Assert.Equal(DeliveredActivityId, saved.ActivityId);
        // Capture-vs-original proof: the saved row must hold the POST-SEND captured
        // conversation id (set by HybridCloudAdapter on the synthesized turn context),
        // NOT the OriginalConversationId the outbox entry shipped. A regression that
        // persisted the original entry reference would surface as
        // saved.ConversationId == OriginalConversationId, which this assertion rejects.
        Assert.Equal(DeliveredConversationId, saved.ConversationId);
        Assert.NotEqual(OriginalConversationId, saved.ConversationId);
        Assert.False(string.IsNullOrWhiteSpace(saved.ConversationReferenceJson));

        // The serialized ConversationReferenceJson on the saved row must ALSO reflect
        // the captured (delivered) conversation id — proving the dispatcher serialized
        // the FRESH turn-context reference (via Activity.GetConversationReference()),
        // not the original entry reference.
        var savedReference = JsonConvert.DeserializeObject<ConversationReference>(saved.ConversationReferenceJson);
        Assert.NotNull(savedReference);
        Assert.Equal(DeliveredConversationId, savedReference!.Conversation?.Id);

        // Chain proof — the activity id flows: dispatcher receipt → saved row.
        Assert.Equal(saved.ActivityId, dispatchResult.Receipt!.Value.ActivityId);

        // The dispatcher MUST also stamp the conversation id onto the AgentQuestion so
        // CardActionHandler's bare approve/reject resolution path works.
        var convoUpdate = Assert.Single(questionStore.ConversationIdUpdates);
        Assert.Equal(question.QuestionId, convoUpdate.QuestionId);
        Assert.Equal(DeliveredConversationId, convoUpdate.ConversationId);

        // Sanity — only one outbound proactive call should have been made (no
        // duplicate sends from the idempotency layers).
        Assert.Single(adapter.ContinueCalls);
        Assert.Single(adapter.SendActivityCalls);

        // ---- Phase 2 -- card manager updates the outbox-delivered card -----------
        ITeamsCardManager cardManager = new TeamsMessengerConnector(
            adapter,
            options,
            new InertConversationReferenceStore(),
            new InertConversationReferenceRouter(),
            questionStore,
            sharedCardStore,
            renderer,
            new ChannelInboundEventPublisher(),
            NullLogger<TeamsMessengerConnector>.Instance);

        await cardManager.UpdateCardAsync(
            question.QuestionId,
            CardUpdateAction.MarkAnswered,
            CancellationToken.None);

        // The connector MUST have rehydrated the conversation reference from the
        // dispatcher-saved row (proves the dispatcher's capture is sufficient for the
        // inline-retry path). One ContinueConversationAsync each for the dispatcher's
        // send and the connector's update → 2 total.
        Assert.Equal(2, adapter.ContinueCalls.Count);

        // Rehydration-source proof: the SECOND ContinueConversationAsync (the
        // connector's update) MUST have received the reference that was deserialized
        // from the SAVED card-state row, NOT the original entry reference. Asserting
        // Conversation.Id == DeliveredConversationId here proves the connector
        // resolved the reference from the persisted row written in Phase 1, since the
        // entry's original reference carried OriginalConversationId.
        var connectorUpdateReference = adapter.ContinueCalls[1];
        Assert.Equal(DeliveredConversationId, connectorUpdateReference.Conversation?.Id);
        Assert.NotEqual(OriginalConversationId, connectorUpdateReference.Conversation?.Id);

        // The update MUST target the SAME activity id the dispatcher captured —
        // proving the outbox-delivered card identity flowed through unchanged. The
        // assertion is anchored on the SAVED row's ActivityId (which itself came from
        // the dispatcher's receipt), so a constant-coupling regression on
        // DeliveredActivityId would still fail at the saved-row capture above.
        var updated = Assert.Single(adapter.UpdateActivityCalls);
        Assert.Equal(saved.ActivityId, updated.Id);

        // Final card-state status should be Answered.
        var statusUpdate = Assert.Single(sharedCardStore.StatusUpdates);
        Assert.Equal(question.QuestionId, statusUpdate.QuestionId);
        Assert.Equal(TeamsCardStatuses.Answered, statusUpdate.NewStatus);
    }

    [Fact]
    public async Task OutboxDeliveredQuestion_CanBeDeletedByCardManager()
    {
        // ---- Arrange -- shared graph -----------------------------------------------
        var adapter = new HybridCloudAdapter
        {
            FixedActivityId = DeliveredActivityId,
            FixedConversationId = DeliveredConversationId,
        };
        var sharedCardStore = new ProductionLikeCardStateStore();
        var questionStore = new RecordingAgentQuestionStore();
        var outbox = new InMemoryOutbox();
        var renderer = new AdaptiveCardBuilder();
        var options = new TeamsMessagingOptions { MicrosoftAppId = AppId };

        // ---- Phase 1 -- dispatcher delivers the AgentQuestion --------------------
        var dispatcher = new TeamsOutboxDispatcher(
            adapter,
            options,
            outbox,
            sharedCardStore,
            questionStore,
            renderer,
            NullLogger<TeamsOutboxDispatcher>.Instance);

        var question = BuildQuestion("q-it-delete");
        var entry = BuildOutboxEntry("e-it-2", question, BuildConversationReferenceJson());

        var dispatchResult = await dispatcher.DispatchAsync(entry, CancellationToken.None);
        Assert.Equal(OutboxDispatchOutcome.Success, dispatchResult.Outcome);

        // ---- Phase 2 -- card manager deletes the outbox-delivered card -----------
        ITeamsCardManager cardManager = new TeamsMessengerConnector(
            adapter,
            options,
            new InertConversationReferenceStore(),
            new InertConversationReferenceRouter(),
            questionStore,
            sharedCardStore,
            renderer,
            new ChannelInboundEventPublisher(),
            NullLogger<TeamsMessengerConnector>.Instance);

        await cardManager.DeleteCardAsync(question.QuestionId, CancellationToken.None);

        // The connector MUST have rehydrated the conversation reference from the
        // dispatcher-saved row and dispatched a DeleteActivity against the captured
        // activity id.
        Assert.Equal(2, adapter.ContinueCalls.Count);

        // Rehydration-source proof — the connector's delete call MUST have used the
        // POST-SEND captured reference written to the shared store by the dispatcher,
        // not the entry's original reference (which carried OriginalConversationId).
        var connectorDeleteReference = adapter.ContinueCalls[1];
        Assert.Equal(DeliveredConversationId, connectorDeleteReference.Conversation?.Id);
        Assert.NotEqual(OriginalConversationId, connectorDeleteReference.Conversation?.Id);

        // Chain proof — DeleteActivity targets the SAME activity id captured in the
        // shared card-state row by the dispatcher.
        var savedAfterPhase1 = await sharedCardStore.GetByQuestionIdAsync(question.QuestionId, CancellationToken.None);
        Assert.NotNull(savedAfterPhase1);
        var deletedActivityId = Assert.Single(adapter.DeleteActivityCalls);
        Assert.Equal(savedAfterPhase1!.ActivityId, deletedActivityId);

        // Final card-state status should be Expired per the §3.3 contract.
        var statusUpdate = Assert.Single(sharedCardStore.StatusUpdates);
        Assert.Equal(question.QuestionId, statusUpdate.QuestionId);
        Assert.Equal(TeamsCardStatuses.Expired, statusUpdate.NewStatus);
    }

    /// <summary>
    /// Iter-3 evaluator feedback item 1 — work-item scenario 9 ("Card delete inline
    /// retry"). The Phase-1 dispatcher delivers the card, then Phase-2's
    /// <see cref="TeamsMessengerConnector.DeleteCardAsync"/> sees a transient HTTP 503
    /// on the first <see cref="ITurnContext.DeleteActivityAsync(string, CancellationToken)"/>
    /// and succeeds on the second. Proves the inline retry path (1) classifies the
    /// failure as transient, (2) retries the WHOLE
    /// <see cref="CloudAdapter.ContinueConversationAsync"/> wrap, (3) lands the
    /// terminal <c>Expired</c> status only after the retried delete succeeds.
    /// </summary>
    [Fact]
    public async Task OutboxDeliveredQuestion_DeleteWithTransientFailure_RetriesAndSucceeds()
    {
        var adapter = new TransientThenSuccessDeleteAdapter(failuresBeforeSuccess: 1)
        {
            FixedActivityId = DeliveredActivityId,
            FixedConversationId = DeliveredConversationId,
        };
        var sharedCardStore = new ProductionLikeCardStateStore();
        var questionStore = new RecordingAgentQuestionStore();
        var outbox = new InMemoryOutbox();
        var renderer = new AdaptiveCardBuilder();
        var options = new TeamsMessagingOptions
        {
            MicrosoftAppId = AppId,
            MaxRetryAttempts = 3,
            RetryBaseDelaySeconds = 1, // floored to 1s by ExecuteWithInlineRetryAsync
        };

        var dispatcher = new TeamsOutboxDispatcher(
            adapter, options, outbox, sharedCardStore, questionStore, renderer,
            NullLogger<TeamsOutboxDispatcher>.Instance);
        var question = BuildQuestion("q-it-del-retry");
        var entry = BuildOutboxEntry("e-it-del-retry", question, BuildConversationReferenceJson());

        var dispatchResult = await dispatcher.DispatchAsync(entry, CancellationToken.None);
        Assert.Equal(OutboxDispatchOutcome.Success, dispatchResult.Outcome);

        ITeamsCardManager cardManager = new TeamsMessengerConnector(
            adapter, options,
            new InertConversationReferenceStore(),
            new InertConversationReferenceRouter(),
            questionStore, sharedCardStore, renderer,
            new ChannelInboundEventPublisher(),
            NullLogger<TeamsMessengerConnector>.Instance);

        await cardManager.DeleteCardAsync(question.QuestionId, CancellationToken.None);

        // Inline retry MUST have invoked DeleteActivityAsync exactly twice (1 transient
        // failure + 1 success). The card-state status MUST land at Expired ONLY after
        // the retried delete succeeds — proving UpdateStatusAsync runs after the
        // operation lambda returns from ExecuteWithInlineRetryAsync, not inside the
        // failed attempt's exception path.
        Assert.Equal(2, adapter.DeleteAttempts);
        // Three ContinueConversationAsync calls total: 1 dispatcher send + 2 manager
        // retry attempts (the retry loop wraps the WHOLE ContinueConversationAsync).
        Assert.Equal(3, adapter.ContinueCalls.Count);

        var statusUpdate = Assert.Single(sharedCardStore.StatusUpdates);
        Assert.Equal(question.QuestionId, statusUpdate.QuestionId);
        Assert.Equal(TeamsCardStatuses.Expired, statusUpdate.NewStatus);
    }

    /// <summary>
    /// Iter-3 evaluator feedback item 2 — assert the dispatcher's POST-SEND
    /// durability ordering: <see cref="IMessageOutbox.RecordSendReceiptAsync"/> MUST
    /// be called BEFORE <see cref="ICardStateStore.SaveAsync"/>. The receipt is the
    /// durable marker that lets a subsequent retry safely skip the BF re-send via
    /// the layer-1 idempotency check; if the order is reversed a cardstate-save
    /// failure would leave the outbox row with no <c>ActivityId</c> and the retry
    /// would produce a duplicate card. Proven with a shared call-order log so the
    /// ordering is observable across both the outbox AND the card-state stores.
    /// </summary>
    [Fact]
    public async Task OutboxDispatcher_RecordSendReceipt_RunsBeforeCardStateSave()
    {
        var callOrder = new List<string>();
        var adapter = new HybridCloudAdapter
        {
            FixedActivityId = DeliveredActivityId,
            FixedConversationId = DeliveredConversationId,
        };
        var sharedCardStore = new ProductionLikeCardStateStore(call => callOrder.Add(call));
        var outbox = new InMemoryOutbox(call => callOrder.Add(call));
        var questionStore = new RecordingAgentQuestionStore();
        var renderer = new AdaptiveCardBuilder();
        var options = new TeamsMessagingOptions { MicrosoftAppId = AppId };

        var dispatcher = new TeamsOutboxDispatcher(
            adapter, options, outbox, sharedCardStore, questionStore, renderer,
            NullLogger<TeamsOutboxDispatcher>.Instance);
        var question = BuildQuestion("q-it-order");
        var entry = BuildOutboxEntry("e-it-order", question, BuildConversationReferenceJson());

        var result = await dispatcher.DispatchAsync(entry, CancellationToken.None);
        Assert.Equal(OutboxDispatchOutcome.Success, result.Outcome);

        // Both calls MUST be present.
        Assert.Contains("outbox.RecordSendReceipt", callOrder);
        Assert.Contains("cardstate.Save", callOrder);

        // Strict ordering: receipt persisted FIRST, cardstate save SECOND.
        var receiptIdx = callOrder.IndexOf("outbox.RecordSendReceipt");
        var saveIdx = callOrder.IndexOf("cardstate.Save");
        Assert.True(receiptIdx < saveIdx,
            $"IMessageOutbox.RecordSendReceiptAsync must run BEFORE ICardStateStore.SaveAsync (receipt index {receiptIdx}, save index {saveIdx}). Call order: [{string.Join(", ", callOrder)}].");

        // The receipt persisted to the outbox MUST carry the SAME identifiers later
        // saved into card-state — proving the dispatcher hands the same captured data
        // to both stores.
        var receipt = Assert.Single(outbox.Receipts);
        Assert.Equal(entry.OutboxEntryId, receipt.EntryId);
        Assert.Equal(DeliveredActivityId, receipt.Receipt.ActivityId);
        Assert.Equal(DeliveredConversationId, receipt.Receipt.ConversationId);

        var saved = Assert.Single(sharedCardStore.Saved);
        Assert.Equal(receipt.Receipt.ActivityId, saved.ActivityId);
        Assert.Equal(receipt.Receipt.ConversationId, saved.ConversationId);
    }

    /// <summary>
    /// Iter-3 evaluator feedback item 3 — work-item core outbox guarantee: the
    /// layer-1 idempotent replay path in <see cref="TeamsOutboxDispatcher.DispatchAsync"/>.
    /// Simulates a prior partial-success attempt where the Bot Framework send
    /// succeeded (<see cref="OutboxEntry.ActivityId"/>/<see cref="OutboxEntry.ConversationId"/>
    /// were stamped onto the row via <see cref="IMessageOutbox.RecordSendReceiptAsync"/>)
    /// but the post-send <see cref="ICardStateStore.SaveAsync"/> failed and the
    /// engine rescheduled the entry. On the next dispatch attempt the dispatcher MUST
    /// NOT re-send the card — it MUST replay ONLY the post-send persistence using the
    /// row's identifiers. Proven by configuring a <see cref="HybridCloudAdapter"/>
    /// that records ALL outbound calls and asserting they remain at zero after the
    /// replay dispatch returns Success.
    ///
    /// Iter-4 evaluator feedback — also asserts the saved
    /// <see cref="TeamsCardState.ConversationReferenceJson"/> matches whatever the
    /// entry carried (which, after the iter-4 production fix, is the DELIVERED
    /// reference persisted by the prior attempt's
    /// <see cref="IMessageOutbox.RecordSendReceiptAsync"/>). Eliminates the
    /// original-vs-delivered drift the iter-3 evaluator flagged.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_Layer1IdempotentReplay_EntryHasActivityId_HydratesCardStateWithoutResending()
    {
        const string priorAttemptActivityId = "act-prior-attempt";
        const string priorAttemptConversationId = "19:prior-attempt@thread.tacv2";

        var adapter = new HybridCloudAdapter
        {
            // Distinct from prior-attempt ids so a buggy "re-send anyway" would
            // surface as cardstate with the wrong (post-resend) identifiers.
            FixedActivityId = "act-WRONG-if-resent",
            FixedConversationId = "19:WRONG-if-resent@thread.tacv2",
        };
        var sharedCardStore = new ProductionLikeCardStateStore();
        var questionStore = new RecordingAgentQuestionStore();
        var outbox = new InMemoryOutbox();
        var renderer = new AdaptiveCardBuilder();
        var options = new TeamsMessagingOptions { MicrosoftAppId = AppId };

        var dispatcher = new TeamsOutboxDispatcher(
            adapter, options, outbox, sharedCardStore, questionStore, renderer,
            NullLogger<TeamsOutboxDispatcher>.Instance);

        var question = BuildQuestion("q-it-replay");
        // Iter-4 fix: the entry's ConversationReferenceJson now carries the
        // DELIVERED reference (simulating the prior attempt's
        // RecordSendReceiptAsync persisted it back onto the row). The replay
        // path MUST faithfully thread this through to PersistPostSendStateAsync
        // so the cardstate row matches what a fresh-send path would have
        // produced.
        var deliveredReferenceJson = BuildDeliveredConversationReferenceJson();
        var entry = BuildOutboxEntry("e-it-replay", question, deliveredReferenceJson) with
        {
            ActivityId = priorAttemptActivityId,
            ConversationId = priorAttemptConversationId,
        };

        var result = await dispatcher.DispatchAsync(entry, CancellationToken.None);

        Assert.Equal(OutboxDispatchOutcome.Success, result.Outcome);

        // CRITICAL no-resend assertions — proves the layer-1 replay path skipped the
        // proactive turn entirely (no ContinueConversationAsync, no SendActivitiesAsync).
        Assert.Empty(adapter.ContinueCalls);
        Assert.Empty(adapter.SendActivityCalls);

        // Card state MUST be hydrated using the PRIOR-ATTEMPT identifiers from the row,
        // not the adapter's fixed-value defaults (which would have surfaced if the
        // dispatcher mistakenly re-sent the card).
        var saved = Assert.Single(sharedCardStore.Saved);
        Assert.Equal(question.QuestionId, saved.QuestionId);
        Assert.Equal(priorAttemptActivityId, saved.ActivityId);
        Assert.Equal(priorAttemptConversationId, saved.ConversationId);

        // Iter-4 fix — the saved row's ConversationReferenceJson MUST equal the
        // entry's reference exactly, proving the replay path is reference-faithful.
        // Because the entry carried the DELIVERED reference, the saved row carries
        // it too — there is NO original-vs-delivered drift between the fresh-send
        // path and the replay path.
        Assert.Equal(deliveredReferenceJson, saved.ConversationReferenceJson);
        var savedReference = JsonConvert.DeserializeObject<ConversationReference>(saved.ConversationReferenceJson!);
        Assert.Equal(DeliveredConversationId, savedReference!.Conversation?.Id);

        // Receipt on the dispatcher result MUST also reflect the prior-attempt
        // identifiers so the engine acknowledges the row with the canonical receipt.
        Assert.NotNull(result.Receipt);
        Assert.Equal(priorAttemptActivityId, result.Receipt!.Value.ActivityId);
        Assert.Equal(priorAttemptConversationId, result.Receipt!.Value.ConversationId);
        // Iter-4 fix — the result receipt also carries the reference so the
        // engine's AcknowledgeAsync persists it onto the row (parity with the
        // fresh-send path). This is what closes the audit-trail asymmetry on the
        // replay path.
        Assert.Equal(deliveredReferenceJson, result.Receipt!.Value.ConversationReferenceJson);

        // The AgentQuestion's conversation id MUST also be stamped during the replay
        // so CardActionHandler's bare approve/reject resolution path works even when
        // the original send completed before any cardstate write.
        var convoUpdate = Assert.Single(questionStore.ConversationIdUpdates);
        Assert.Equal(question.QuestionId, convoUpdate.QuestionId);
        Assert.Equal(priorAttemptConversationId, convoUpdate.ConversationId);
    }

    /// <summary>
    /// Iter-4 evaluator feedback — closes the production gap directly: on a
    /// FRESH-SEND of an AgentQuestion, <see cref="TeamsOutboxDispatcher"/> MUST
    /// hand the DELIVERED <see cref="ConversationReference"/> (captured via
    /// <c>turnContext.Activity.GetConversationReference()</c> after
    /// <c>SendActivityAsync</c>) into <see cref="IMessageOutbox.RecordSendReceiptAsync"/>
    /// — not just <see cref="OutboxEntry.ActivityId"/>/<see cref="OutboxEntry.ConversationId"/>.
    /// This is the durable bridge that makes the layer-1 replay path
    /// reference-symmetric with the fresh-send path: a partial-success retry now
    /// reads the DELIVERED reference back out of
    /// <see cref="OutboxEntry.ConversationReferenceJson"/> (because
    /// <see cref="SqlMessageOutbox.RecordSendReceiptAsync"/> persisted it
    /// in-place on the column), so <see cref="TeamsCardState.ConversationReferenceJson"/>
    /// saved on replay matches what a fresh save would have produced.
    /// </summary>
    [Fact]
    public async Task FreshSend_RecordSendReceiptAsync_ReceivesReceiptCarryingDeliveredConversationReferenceJson()
    {
        var adapter = new HybridCloudAdapter
        {
            FixedActivityId = DeliveredActivityId,
            FixedConversationId = DeliveredConversationId,
        };
        var sharedCardStore = new ProductionLikeCardStateStore();
        var questionStore = new RecordingAgentQuestionStore();
        var outbox = new InMemoryOutbox();
        var renderer = new AdaptiveCardBuilder();
        var options = new TeamsMessagingOptions { MicrosoftAppId = AppId };

        var dispatcher = new TeamsOutboxDispatcher(
            adapter, options, outbox, sharedCardStore, questionStore, renderer,
            NullLogger<TeamsOutboxDispatcher>.Instance);
        var question = BuildQuestion("q-it-fresh-receipt");
        var entry = BuildOutboxEntry("e-it-fresh-receipt", question, BuildConversationReferenceJson());

        var result = await dispatcher.DispatchAsync(entry, CancellationToken.None);
        Assert.Equal(OutboxDispatchOutcome.Success, result.Outcome);

        // The dispatcher MUST have called RecordSendReceiptAsync exactly once.
        var receipt = Assert.Single(outbox.Receipts);
        Assert.Equal(entry.OutboxEntryId, receipt.EntryId);

        // Critical assertion — receipt carries the captured post-send reference,
        // NOT null.
        Assert.False(string.IsNullOrWhiteSpace(receipt.Receipt.ConversationReferenceJson));

        // Deeper assertion — the receipt's reference holds the DELIVERED
        // conversation id (the adapter's FixedConversationId rewrite), proving the
        // dispatcher captured it from turnContext.Activity.GetConversationReference()
        // AFTER SendActivityAsync, not from the original entry reference.
        var capturedReference = JsonConvert.DeserializeObject<ConversationReference>(receipt.Receipt.ConversationReferenceJson!);
        Assert.NotNull(capturedReference);
        Assert.Equal(DeliveredConversationId, capturedReference!.Conversation?.Id);
        Assert.NotEqual(OriginalConversationId, capturedReference.Conversation?.Id);

        // The receipt's ActivityId/ConversationId align with the captured reference.
        Assert.Equal(DeliveredActivityId, receipt.Receipt.ActivityId);
        Assert.Equal(DeliveredConversationId, receipt.Receipt.ConversationId);
    }

    /// <summary>
    /// Iter-4 evaluator feedback — proves the dispatcher's Success result
    /// (which the engine threads into <see cref="IMessageOutbox.AcknowledgeAsync"/>)
    /// also carries the delivered <see cref="OutboxDeliveryReceipt.ConversationReferenceJson"/>.
    /// Combined with the SqlMessageOutbox EF tests, this guarantees the canonical
    /// audit row's <see cref="OutboxEntry.ConversationReferenceJson"/> column ends
    /// the lifecycle holding the DELIVERED reference rather than the enqueue-time
    /// snapshot.
    /// </summary>
    [Fact]
    public async Task FreshSend_DispatchResultReceipt_CarriesDeliveredConversationReferenceJson()
    {
        var adapter = new HybridCloudAdapter
        {
            FixedActivityId = DeliveredActivityId,
            FixedConversationId = DeliveredConversationId,
        };
        var sharedCardStore = new ProductionLikeCardStateStore();
        var questionStore = new RecordingAgentQuestionStore();
        var outbox = new InMemoryOutbox();
        var renderer = new AdaptiveCardBuilder();
        var options = new TeamsMessagingOptions { MicrosoftAppId = AppId };

        var dispatcher = new TeamsOutboxDispatcher(
            adapter, options, outbox, sharedCardStore, questionStore, renderer,
            NullLogger<TeamsOutboxDispatcher>.Instance);
        var question = BuildQuestion("q-it-success-receipt");
        var entry = BuildOutboxEntry("e-it-success-receipt", question, BuildConversationReferenceJson());

        var result = await dispatcher.DispatchAsync(entry, CancellationToken.None);

        Assert.Equal(OutboxDispatchOutcome.Success, result.Outcome);
        Assert.NotNull(result.Receipt);
        Assert.False(string.IsNullOrWhiteSpace(result.Receipt!.Value.ConversationReferenceJson));

        var capturedReference = JsonConvert.DeserializeObject<ConversationReference>(result.Receipt.Value.ConversationReferenceJson!);
        Assert.NotNull(capturedReference);
        Assert.Equal(DeliveredConversationId, capturedReference!.Conversation?.Id);

        // The cardstate row's reference and the result receipt's reference must
        // agree — both flow from the same post-send capture in
        // PersistPostSendStateAsync. This is the symmetry guarantee.
        var saved = Assert.Single(sharedCardStore.Saved);
        Assert.Equal(saved.ConversationReferenceJson, result.Receipt.Value.ConversationReferenceJson);
    }

    /// <summary>
    /// Iter-4 evaluator feedback — strict ordering proof: the dispatcher MUST
    /// stamp the delivered reference onto the outbox row (via
    /// <see cref="IMessageOutbox.RecordSendReceiptAsync"/>) BEFORE it persists the
    /// card-state row (via <see cref="ICardStateStore.SaveAsync"/>). This ensures
    /// that if the card-state save fails and the engine reschedules, the
    /// next attempt's Layer-1 replay reads the DELIVERED reference from the row
    /// rather than the stale enqueue-time one.
    /// </summary>
    [Fact]
    public async Task FreshSend_DeliveredReference_PersistedToOutboxRowBeforeCardStateSave()
    {
        var callOrder = new List<string>();
        var adapter = new HybridCloudAdapter
        {
            FixedActivityId = DeliveredActivityId,
            FixedConversationId = DeliveredConversationId,
        };
        var sharedCardStore = new ProductionLikeCardStateStore(call => callOrder.Add(call));
        var outbox = new InMemoryOutbox(call => callOrder.Add(call));
        var questionStore = new RecordingAgentQuestionStore();
        var renderer = new AdaptiveCardBuilder();
        var options = new TeamsMessagingOptions { MicrosoftAppId = AppId };

        var dispatcher = new TeamsOutboxDispatcher(
            adapter, options, outbox, sharedCardStore, questionStore, renderer,
            NullLogger<TeamsOutboxDispatcher>.Instance);
        var question = BuildQuestion("q-it-ref-order");
        var entry = BuildOutboxEntry("e-it-ref-order", question, BuildConversationReferenceJson());

        var result = await dispatcher.DispatchAsync(entry, CancellationToken.None);
        Assert.Equal(OutboxDispatchOutcome.Success, result.Outcome);

        // The RecordSendReceiptAsync call must precede the cardstate Save call AND
        // it must carry the delivered reference — proving the durability ordering
        // and that the row is reference-correct before the cardstate write
        // happens.
        var receiptIdx = callOrder.IndexOf("outbox.RecordSendReceipt");
        var saveIdx = callOrder.IndexOf("cardstate.Save");
        Assert.True(receiptIdx >= 0 && saveIdx >= 0, $"Expected both calls, saw [{string.Join(", ", callOrder)}].");
        Assert.True(receiptIdx < saveIdx,
            $"RecordSendReceiptAsync must precede ICardStateStore.SaveAsync; got receipt {receiptIdx} vs save {saveIdx}.");

        var receipt = Assert.Single(outbox.Receipts);
        Assert.False(string.IsNullOrWhiteSpace(receipt.Receipt.ConversationReferenceJson));
        var capturedReference = JsonConvert.DeserializeObject<ConversationReference>(receipt.Receipt.ConversationReferenceJson!);
        Assert.Equal(DeliveredConversationId, capturedReference!.Conversation?.Id);
    }

    // ----- helpers --------------------------------------------------------------

    private static AgentQuestion BuildQuestion(string id) => new()
    {
        QuestionId = id,
        AgentId = "agent-it",
        TaskId = "task-it",
        TenantId = TenantId,
        TargetUserId = UserId,
        Title = "Approve operation?",
        Body = "End-to-end integration test card.",
        Severity = MessageSeverities.Info,
        AllowedActions = new[]
        {
            new HumanAction("approve", "Approve", "approve", RequiresComment: false),
            new HumanAction("reject", "Reject", "reject", RequiresComment: false),
        },
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        CorrelationId = $"corr-{id}",
    };

    private static OutboxEntry BuildOutboxEntry(string outboxId, AgentQuestion question, string referenceJson) => new()
    {
        OutboxEntryId = outboxId,
        CorrelationId = question.CorrelationId,
        Destination = $"teams://{TenantId}/user/{UserId}",
        DestinationType = OutboxDestinationTypes.Personal,
        DestinationId = UserId,
        PayloadType = OutboxPayloadTypes.AgentQuestion,
        PayloadJson = System.Text.Json.JsonSerializer.Serialize(
            new TeamsOutboxPayloadEnvelope { Question = question },
            TeamsOutboxPayloadEnvelope.JsonOptions),
        ConversationReferenceJson = referenceJson,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static string BuildConversationReferenceJson()
    {
        // Deliberately use OriginalConversationId here — the HybridCloudAdapter REWRITES
        // the synthesized turn context's Conversation.Id to DeliveredConversationId so
        // the dispatcher's post-send capture path can be distinguished from a buggy
        // "persist the original entry reference" implementation.
        var reference = new ConversationReference
        {
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/teams/",
            Conversation = new ConversationAccount(id: OriginalConversationId, tenantId: TenantId),
            User = new ChannelAccount(id: $"29:{UserId}", aadObjectId: UserId, name: "Integration Test User"),
            Bot = new ChannelAccount(id: $"28:{AppId}"),
        };
        return JsonConvert.SerializeObject(reference);
    }

    /// <summary>
    /// Build a serialized <see cref="ConversationReference"/> that mirrors what the
    /// dispatcher would have captured from <c>turnContext.Activity.GetConversationReference()</c>
    /// AFTER <see cref="HybridCloudAdapter"/> rewrote the synthesized turn context's
    /// <see cref="ConversationAccount.Id"/> to <see cref="DeliveredConversationId"/>.
    /// Used to seed <see cref="OutboxEntry.ConversationReferenceJson"/> on a Layer-1
    /// idempotent replay test, simulating that a prior attempt's
    /// <see cref="IMessageOutbox.RecordSendReceiptAsync"/> already persisted the
    /// DELIVERED reference back onto the row (per the Stage 6.1 iter-4 production
    /// fix). A regression that did NOT persist the delivered reference would surface
    /// here as <c>saved.ConversationReferenceJson</c> still containing
    /// <see cref="OriginalConversationId"/>.
    /// </summary>
    private static string BuildDeliveredConversationReferenceJson()
    {
        var reference = new ConversationReference
        {
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/teams/",
            Conversation = new ConversationAccount(id: DeliveredConversationId, tenantId: TenantId),
            User = new ChannelAccount(id: $"29:{UserId}", aadObjectId: UserId, name: "Integration Test User"),
            Bot = new ChannelAccount(id: $"28:{AppId}"),
        };
        return JsonConvert.SerializeObject(reference);
    }

    // ----- test doubles ---------------------------------------------------------

    /// <summary>
    /// Hybrid adapter that records BOTH the dispatcher's send path
    /// (<see cref="SendActivitiesAsync"/>) and the card manager's update/delete path
    /// (<see cref="UpdateActivityAsync"/>/<see cref="DeleteActivityAsync"/>). Synthesises a
    /// continuation turn whose <see cref="ITurnContext.Activity"/> carries the fixed
    /// <see cref="FixedConversationId"/> so the dispatcher captures a stable
    /// <see cref="OutboxEntry.ConversationId"/>.
    /// </summary>
    private class HybridCloudAdapter : CloudAdapter
    {
        public string FixedActivityId { get; init; } = "act-default";
        public string FixedConversationId { get; init; } = "conv-default";

        public List<ConversationReference> ContinueCalls { get; } = new();
        public List<Activity> SendActivityCalls { get; } = new();
        public List<Activity> UpdateActivityCalls { get; } = new();
        public List<string> DeleteActivityCalls { get; } = new();

        public override Task ContinueConversationAsync(
            string botAppId,
            ConversationReference reference,
            BotCallbackHandler callback,
            CancellationToken cancellationToken)
        {
            ContinueCalls.Add(reference);
            var continuation = (Activity)reference.GetContinuationActivity();
            continuation.Conversation = new ConversationAccount(id: FixedConversationId, tenantId: reference.Conversation?.TenantId);
            var turnContext = new TurnContext(this, continuation);
            return callback(turnContext, cancellationToken);
        }

        public override Task<ResourceResponse[]> SendActivitiesAsync(
            ITurnContext turnContext,
            Activity[] activities,
            CancellationToken cancellationToken)
        {
            SendActivityCalls.AddRange(activities);
            var responses = activities.Select(_ => new ResourceResponse(FixedActivityId)).ToArray();
            return Task.FromResult(responses);
        }

        public override Task<ResourceResponse> UpdateActivityAsync(
            ITurnContext turnContext,
            Activity activity,
            CancellationToken cancellationToken)
        {
            UpdateActivityCalls.Add(activity);
            return Task.FromResult(new ResourceResponse(activity.Id ?? FixedActivityId));
        }

        public override Task DeleteActivityAsync(
            ITurnContext turnContext,
            ConversationReference reference,
            CancellationToken cancellationToken)
        {
            DeleteActivityCalls.Add(reference.ActivityId!);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Hybrid adapter variant that fails the FIRST <see cref="DeleteActivityAsync"/>
    /// calls with a transient HTTP 503 (<see cref="ErrorResponseException"/> per the
    /// canonical Teams whitelist) before succeeding. Used to drive the inline-retry
    /// path in <see cref="TeamsMessengerConnector.DeleteCardAsync"/> from an
    /// integration test that ALSO exercises the dispatcher's prior delivery.
    /// </summary>
    private sealed class TransientThenSuccessDeleteAdapter : HybridCloudAdapter
    {
        private readonly int _failuresBeforeSuccess;

        public TransientThenSuccessDeleteAdapter(int failuresBeforeSuccess)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public int DeleteAttempts { get; private set; }

        public override Task DeleteActivityAsync(
            ITurnContext turnContext,
            ConversationReference reference,
            CancellationToken cancellationToken)
        {
            DeleteAttempts++;
            if (DeleteAttempts <= _failuresBeforeSuccess)
            {
                throw new Microsoft.Bot.Schema.ErrorResponseException("HTTP 503")
                {
                    Response = new Microsoft.Rest.HttpResponseMessageWrapper(
                        new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable),
                        string.Empty),
                };
            }

            DeleteActivityCalls.Add(reference.ActivityId!);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Card-state store that behaves like the production
    /// <c>SqlCardStateStore</c> in the dimension the integration test cares about:
    /// <see cref="SaveAsync"/> persists by <see cref="TeamsCardState.QuestionId"/> and
    /// <see cref="GetByQuestionIdAsync"/> returns the most-recently-saved row for that
    /// id. <see cref="UpdateStatusAsync"/> records the call AND mutates the stored row
    /// so a follow-up <see cref="GetByQuestionIdAsync"/> sees the new status.
    /// </summary>
    private sealed class ProductionLikeCardStateStore : ICardStateStore
    {
        private readonly Dictionary<string, TeamsCardState> _byQuestionId = new(StringComparer.Ordinal);
        private readonly Action<string>? _recordCall;

        public List<TeamsCardState> Saved { get; } = new();
        public List<(string QuestionId, string NewStatus)> StatusUpdates { get; } = new();

        public ProductionLikeCardStateStore() : this(null) { }

        public ProductionLikeCardStateStore(Action<string>? recordCall)
        {
            _recordCall = recordCall;
        }

        public Task SaveAsync(TeamsCardState state, CancellationToken ct)
        {
            _recordCall?.Invoke("cardstate.Save");
            Saved.Add(state);
            _byQuestionId[state.QuestionId] = state;
            return Task.CompletedTask;
        }

        public Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct)
        {
            _byQuestionId.TryGetValue(questionId, out var hit);
            return Task.FromResult<TeamsCardState?>(hit);
        }

        public Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct)
        {
            StatusUpdates.Add((questionId, newStatus));
            if (_byQuestionId.TryGetValue(questionId, out var current))
            {
                _byQuestionId[questionId] = current with { Status = newStatus };
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryOutbox : IMessageOutbox
    {
        private readonly Action<string>? _recordCall;

        public List<(string EntryId, OutboxDeliveryReceipt Receipt)> Receipts { get; } = new();
        public List<(string EntryId, OutboxDeliveryReceipt Receipt)> Acks { get; } = new();

        public InMemoryOutbox() : this(null) { }

        public InMemoryOutbox(Action<string>? recordCall)
        {
            _recordCall = recordCall;
        }

        public Task EnqueueAsync(OutboxEntry entry, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int batchSize, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());

        public Task AcknowledgeAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
        {
            Acks.Add((outboxEntryId, receipt));
            return Task.CompletedTask;
        }

        public Task RecordSendReceiptAsync(string outboxEntryId, OutboxDeliveryReceipt receipt, CancellationToken ct)
        {
            _recordCall?.Invoke("outbox.RecordSendReceipt");
            Receipts.Add((outboxEntryId, receipt));
            return Task.CompletedTask;
        }

        public Task RescheduleAsync(string outboxEntryId, DateTimeOffset nextRetryAt, string error, CancellationToken ct) => Task.CompletedTask;
        public Task DeadLetterAsync(string outboxEntryId, string error, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingAgentQuestionStore : IAgentQuestionStore
    {
        public List<AgentQuestion> Saved { get; } = new();
        public List<(string QuestionId, string ConversationId)> ConversationIdUpdates { get; } = new();

        public Task SaveAsync(AgentQuestion question, CancellationToken ct)
        {
            Saved.Add(question);
            return Task.CompletedTask;
        }

        public Task<AgentQuestion?> GetByIdAsync(string questionId, CancellationToken ct)
            => Task.FromResult<AgentQuestion?>(Saved.FirstOrDefault(q => string.Equals(q.QuestionId, questionId, StringComparison.Ordinal)));

        public Task<bool> TryUpdateStatusAsync(string questionId, string expectedStatus, string newStatus, CancellationToken ct)
            => Task.FromResult(true);

        public Task UpdateConversationIdAsync(string questionId, string conversationId, CancellationToken ct)
        {
            ConversationIdUpdates.Add((questionId, conversationId));
            return Task.CompletedTask;
        }

        public Task<AgentQuestion?> GetMostRecentOpenByConversationAsync(string conversationId, CancellationToken ct)
            => Task.FromResult<AgentQuestion?>(null);

        public Task<IReadOnlyList<AgentQuestion>> GetOpenByConversationAsync(string conversationId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());

        public Task<IReadOnlyList<AgentQuestion>> GetOpenExpiredAsync(DateTimeOffset cutoff, int batchSize, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentQuestion>>(Array.Empty<AgentQuestion>());
    }

    private sealed class InertConversationReferenceStore : IConversationReferenceStore
    {
        public Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct) => Task.CompletedTask;
        public Task<TeamsConversationReference?> GetAsync(string tenantId, string aadObjectId, CancellationToken ct)
            => Task.FromResult<TeamsConversationReference?>(null);
        public Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct)
            => Task.FromResult<TeamsConversationReference?>(null);
        public Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
            => Task.FromResult<TeamsConversationReference?>(null);
        public Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct)
            => Task.FromResult<TeamsConversationReference?>(null);
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

    private sealed class InertConversationReferenceRouter : IConversationReferenceRouter
    {
        public Task<TeamsConversationReference?> GetByConversationIdAsync(string conversationId, CancellationToken ct)
            => Task.FromResult<TeamsConversationReference?>(null);
    }
}
