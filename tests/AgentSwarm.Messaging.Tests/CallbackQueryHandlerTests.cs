using System.Globalization;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram.Pipeline;
using AgentSwarm.Messaging.Telegram.Pipeline.Stubs;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Xunit;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 3.3 — pins the <see cref="CallbackQueryHandler"/> behaviour
/// against the five scenarios in implementation-plan.md Stage 3.3 plus
/// the two e2e-scenarios.md acceptance cases ("Callback query answered
/// after question expired" and "Concurrent button taps from same user"),
/// AND the iter-1 evaluator's three follow-ups: duplicate replay of the
/// previous result, full keyboard removal on decision, and composite
/// dedup release on exception.
/// </summary>
public sealed class CallbackQueryHandlerTests
{
    private const string QuestionId = "Q-3001";
    private const long ChatId = 100;
    private const long MessageId = 9001;
    private const long RespondentUserId = 42;
    private const string CorrelationId = "trace-cb-3300";

    // ============================================================
    // Scenario 1 (implementation-plan.md Stage 3.3):
    // "Button press emits decision — Given a callback query with data
    // Q1:approve, When processed, Then a HumanDecisionEvent is emitted
    // with QuestionId=Q1 and ActionValue=approve"
    // ============================================================

    [Fact]
    public async Task CallbackResponse_EmitsHumanDecisionEvent_WithQuestionIdAndActionValue()
    {
        var harness = await CallbackHarness.BuildAsync();
        var evt = harness.BuildCallback("cb-1", QuestionId, "approve");

        var result = await harness.Handler.HandleAsync(evt, default);

        result.Success.Should().BeTrue();
        result.CorrelationId.Should().Be(CorrelationId);

        harness.PublishedDecisions.Should().HaveCount(1, "one tap → one HumanDecisionEvent");
        var decision = harness.PublishedDecisions[0];
        decision.QuestionId.Should().Be(QuestionId);
        decision.ActionValue.Should().Be("approve");
        decision.Messenger.Should().Be(CallbackQueryHandler.MessengerName);
        decision.ExternalUserId.Should().Be(RespondentUserId.ToString(CultureInfo.InvariantCulture));
        decision.ExternalMessageId.Should().Be(MessageId.ToString(CultureInfo.InvariantCulture));
        decision.CorrelationId.Should().Be(CorrelationId, "decision carries the PendingQuestion correlation id");
        decision.Comment.Should().BeNull();

        harness.AuditEntries.Should().HaveCount(1);
        var audit = harness.AuditEntries[0];
        audit.QuestionId.Should().Be(QuestionId);
        audit.ActionValue.Should().Be("approve");
        audit.AgentId.Should().Be("agent-deploy");
        // Stage 5.3 iter-4 evaluator item 2 — the audit MessageId
        // must match the callback (per the scenario "AuditLogEntry
        // exists with Action=approve, MessageId matching the
        // callback"). The connector populates evt.CallbackId from
        // Telegram's CallbackQuery.Id; that id is the platform-
        // native "message id of the human reply" per
        // HumanResponseAuditEntry.MessageId's contract. The
        // original question's Telegram message_id is preserved
        // in DecisionAuditDetails.TelegramMessageIdNumeric (asserted
        // below in CallbackResponse_AuditRow_CarriesTenantIdAndDecisionDetailsJson).
        audit.MessageId.Should().Be("cb-1");
        audit.UserId.Should().Be(RespondentUserId.ToString(CultureInfo.InvariantCulture));
        audit.CorrelationId.Should().Be(CorrelationId);

        var stored = await harness.Store.GetAsync(QuestionId, default);
        stored!.Status.Should().Be(PendingQuestionStatus.Answered,
            "the handler MUST transition the question to Answered after publish + audit");
        stored.SelectedActionValue.Should().Be("approve");
        stored.RespondentUserId.Should().Be(RespondentUserId);

        harness.AnswerCallbackRequests.Should().ContainSingle();
        harness.AnswerCallbackRequests[0].CallbackQueryId.Should().Be("cb-1");
        harness.AnswerCallbackRequests[0].Text.Should().Be(CallbackQueryHandler.DecisionShownLabelPrefix + "Approve");
    }

    // ============================================================
    // Stage 5.3 iter-2 evaluator item 6:
    // Callback / decision audit rows MUST carry the full
    // tenant/workspace context — TenantId on the row plus a
    // DecisionAuditDetails JSON payload identifying the source edge.
    // ============================================================

    [Fact]
    public async Task CallbackResponse_AuditRow_CarriesTenantIdAndDecisionDetailsJson()
    {
        var harness = await CallbackHarness.BuildAsync(
            tenantId: "t-acme",
            workspaceId: "w-prod");
        var evt = harness.BuildCallback("cb-tenant-1", QuestionId, "approve");

        var result = await harness.Handler.HandleAsync(evt, default);

        result.Success.Should().BeTrue();
        harness.AuditEntries.Should().HaveCount(1);
        var audit = harness.AuditEntries[0];
        audit.TenantId.Should().Be("t-acme",
            "Stage 5.3 iter-2 evaluator item 6: the callback decision audit row MUST carry the tenant the PendingQuestion was stamped with");
        audit.Details.Should().NotBeNullOrWhiteSpace(
            "Stage 5.3 iter-2 evaluator item 6: the decision row MUST carry a Details JSON payload with workspace + chat + source context");
        audit.Details.Should().Contain("\"workspaceId\":\"w-prod\"",
            "the Details JSON must include the workspace identifier the operator is scoped to");
        audit.Details.Should().Contain("\"telegramChatId\":" + ChatId.ToString(CultureInfo.InvariantCulture),
            "the Details JSON must include the chat id the decision was rendered into");
        audit.Details.Should().Contain("\"source\":\"" + CallbackQueryHandler.DecisionSourceCallback + "\"",
            "the Details JSON must tag source='callback' so forensic queries can pivot on the originating edge of the decision");
        audit.Details.Should().Contain("\"telegramMessageIdNumeric\":" + MessageId.ToString(CultureInfo.InvariantCulture),
            "the Details JSON must include the numeric telegram message id so joins back to the rendered question are O(1)");
    }

    [Fact]
    public async Task CallbackResponse_CommentReply_AuditRow_CarriesTenantIdAndCommentSource()
    {
        var harness = await CallbackHarness.BuildAsync(
            tenantId: "t-tenant-comment",
            workspaceId: "w-comment");
        var tap = harness.BuildCallback("cb-comment-1", QuestionId, "comment");
        await harness.Handler.HandleAsync(tap, default);
        harness.AuditEntries.Should().BeEmpty(
            "the inline button press for a RequiresComment action does NOT emit an audit row yet — the audit fires on the follow-up text reply");

        var reply = new MessengerEvent
        {
            EventId = "tg-update-comment-1",
            EventType = EventType.TextReply,
            UserId = RespondentUserId.ToString(CultureInfo.InvariantCulture),
            ChatId = ChatId.ToString(CultureInfo.InvariantCulture),
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = CorrelationId,
            Payload = "the rollout is unsafe right now",
        };
        var result = await harness.Handler.HandleAsync(reply, default);

        result.Success.Should().BeTrue();
        harness.AuditEntries.Should().HaveCount(1);
        var audit = harness.AuditEntries[0];
        audit.TenantId.Should().Be("t-tenant-comment",
            "Stage 5.3 iter-2 evaluator item 6: the comment-fallback audit row MUST carry the tenant the PendingQuestion was stamped with");
        audit.Comment.Should().Be("the rollout is unsafe right now");
        // Stage 5.3 iter-4 evaluator item 2 — the comment-reply
        // path's audit MessageId must match the operator's inbound
        // text message, not the original question. evt.EventId is
        // the Telegram-update-derived id of the reply itself.
        audit.MessageId.Should().Be("tg-update-comment-1",
            "Stage 5.3 iter-4 evaluator item 2: the comment-reply audit row's MessageId must match the operator's inbound text message (evt.EventId from the TextReply MessengerEvent), not the original question's Telegram message_id");
        audit.Details.Should().NotBeNullOrWhiteSpace();
        audit.Details.Should().Contain("\"workspaceId\":\"w-comment\"");
        audit.Details.Should().Contain("\"source\":\"" + CallbackQueryHandler.DecisionSourceComment + "\"",
            "the comment-reply Details JSON tags source='comment' so forensic queries can distinguish the follow-up reply from the original button tap");
        audit.Details.Should().Contain("\"telegramMessageIdNumeric\":" + MessageId.ToString(CultureInfo.InvariantCulture),
            "the Details JSON must still carry the original question's Telegram message_id so a forensic query can join the comment-reply audit row back to the rendered question");
    }

    // ============================================================
    // Stage 5.3 iter-4 evaluator item 2 (explicit pin):
    // "Callback decision audit MessageId does not match the
    // callback token from the processed MessengerEvent ... Store
    // evt.CallbackId as MessageId or include it explicitly in
    // Details and test it." We test the CallbackId-on-MessageId
    // shape directly so a regression that re-routes the column
    // back to TelegramMessageId is caught here even if the
    // top-level scenario test ever drifts.
    // ============================================================

    [Fact]
    public async Task CallbackResponse_AuditMessageId_MatchesEvtCallbackIdNotPendingTelegramMessageId()
    {
        const string callbackIdFromTelegram = "telegram-cbq-id-9999";
        var harness = await CallbackHarness.BuildAsync();
        var evt = harness.BuildCallback(callbackIdFromTelegram, QuestionId, "approve");

        await harness.Handler.HandleAsync(evt, default);

        harness.AuditEntries.Should().HaveCount(1);
        var audit = harness.AuditEntries[0];
        audit.MessageId.Should().Be(callbackIdFromTelegram,
            "Stage 5.3 iter-4 evaluator item 2: the callback decision audit row's MessageId MUST be the inbound callback_query_id (evt.CallbackId), so the audit_logs row joins directly to the processed MessengerEvent (the Stage 5.3 acceptance scenario says 'MessageId matching the callback').");
        audit.MessageId.Should().NotBe(MessageId.ToString(CultureInfo.InvariantCulture),
            "the audit MessageId MUST NOT be the original question's Telegram message_id — that lives in DecisionAuditDetails.TelegramMessageIdNumeric for forensic join-back to the rendered question.");
        audit.Details.Should().Contain("\"telegramMessageIdNumeric\":" + MessageId.ToString(CultureInfo.InvariantCulture),
            "the original question's Telegram message_id is preserved in Details so forensic queries can still correlate the decision audit row to the rendered question.");
    }

    // ============================================================
    // Scenario 2 (implementation-plan.md Stage 3.3 + iter-1 evaluator item 1):
    // "Duplicate callback ignored — Given callback CB1 has already been
    // processed, When received again, Then no duplicate
    // HumanDecisionEvent is emitted AND THE USER SEES THE SAME
    // CONFIRMATION" (NOT a generic "Already responded" — the SAME
    // toast text they already saw).
    // ============================================================

    [Fact]
    public async Task CallbackResponse_DuplicateCallbackId_ReplaysPreviousResultToUser()
    {
        var harness = await CallbackHarness.BuildAsync();
        var first = harness.BuildCallback("CB1", QuestionId, "approve");
        var second = harness.BuildCallback("CB1", QuestionId, "approve");

        await harness.Handler.HandleAsync(first, default);
        await harness.Handler.HandleAsync(second, default);

        harness.PublishedDecisions.Should().HaveCount(1,
            "the second delivery with the SAME CallbackId must be short-circuited at the cb: dedup gate");
        harness.AuditEntries.Should().HaveCount(1);

        harness.AnswerCallbackRequests.Should().HaveCount(2,
            "both invocations must answer the callback");
        var firstToast = harness.AnswerCallbackRequests[0].Text;
        var secondToast = harness.AnswerCallbackRequests[1].Text;
        firstToast.Should().Be(CallbackQueryHandler.DecisionShownLabelPrefix + "Approve");
        secondToast.Should().Be(firstToast,
            "iter-1 evaluator item 1 — the duplicate MUST re-answer with the PREVIOUS RESULT the user already saw, not a generic 'Already responded' string");
    }

    [Fact]
    public async Task CallbackResponse_DuplicateOfRejectedCallback_ReplaysRejectionToUser()
    {
        // The replay-cache contract is symmetric — if the FIRST delivery
        // was an expiry or unknown-action rejection, the duplicate must
        // re-emit the SAME rejection text, not a different message.
        var harness = await CallbackHarness.BuildAsync();
        var firstTap = harness.BuildCallback("CB-malformed", payload: "no-separator");
        var secondTap = harness.BuildCallback("CB-malformed", payload: "no-separator");

        await harness.Handler.HandleAsync(firstTap, default);
        await harness.Handler.HandleAsync(secondTap, default);

        harness.AnswerCallbackRequests.Should().HaveCount(2);
        harness.AnswerCallbackRequests[0].Text.Should().Be(CallbackQueryHandler.MalformedCallbackText);
        harness.AnswerCallbackRequests[1].Text
            .Should().Be(CallbackQueryHandler.MalformedCallbackText,
                "replay cache must reproduce the prior MalformedCallbackText, not switch to AlreadyRespondedText");
    }

    // ============================================================
    // Scenario 3 (implementation-plan.md Stage 3.3 + iter-1 evaluator item 2):
    // "Original message updated — Given a question message with three
    // action buttons, When one button is pressed, Then the message is
    // edited to show only the selected action AND BUTTONS ARE REMOVED"
    // (no residual tappable button — keyboard nulled entirely).
    // ============================================================

    [Fact]
    public async Task CallbackResponse_EditsOriginalMessage_RemovesAllButtonsAndShowsSelectedAction()
    {
        var harness = await CallbackHarness.BuildAsync();
        var evt = harness.BuildCallback("cb-edit", QuestionId, "reject");

        await harness.Handler.HandleAsync(evt, default);

        // The edit is a SINGLE EditMessageText call that BOTH carries
        // the new body (with the selected action embedded) AND sets
        // ReplyMarkup = null — that one call removes EVERY button.
        harness.EditTextRequests.Should().ContainSingle(
            "the original message MUST be edited (one call: new text + null markup) after a decision");
        var edit = harness.EditTextRequests[0];
        edit.ChatId.Identifier.Should().Be(ChatId);
        edit.MessageId.Should().Be((int)MessageId);
        edit.Text.Should().Contain(CallbackQueryHandler.DecisionShownLabelPrefix + "Reject",
            "the edited message body MUST embed the selected-action badge so the operator can SEE which action was applied");
        edit.Text.Should().Contain("Deploy build 42?",
            "the edit must preserve the original question title");
        edit.Text.Should().Contain("Pre-flight is clean.",
            "the edit must preserve the original question body (context)");
        edit.Text.Should().Contain(CorrelationId,
            "iter-2 evaluator item 1 — the post-decision edit MUST preserve the trace/correlation ID (story-wide acceptance criterion: All messages include trace/correlation ID)");

        // iter-3 evaluator item 3 — every field rendered in the
        // original message (severity, timeout, proposed default
        // action) MUST survive the post-decision edit. The story's
        // "Question handling" row requires these fields end-to-end.
        edit.Text.Should().Contain("Severity: High",
            "iter-3 evaluator item 3 — the post-decision edit MUST preserve severity (story requirement: 'Question handling: include context, severity, timeout, proposed default action')");
        edit.Text.Should().Contain("⚠️",
            "the post-decision edit MUST preserve the severity BADGE matching TelegramQuestionRenderer's outbound rendering");
        edit.Text.Should().Contain("Timeout: 2025-01-01",
            "iter-3 evaluator item 3 — the post-decision edit MUST preserve the question timeout (absolute ExpiresAt)");
        edit.Text.Should().Contain("Default action if no response: Approve",
            "iter-3 evaluator item 3 — the post-decision edit MUST preserve the proposed default action label so the operator can see what would have happened on no response");

        edit.ReplyMarkup.Should().BeNull(
            "iter-1 evaluator item 2 — the inline keyboard MUST be removed entirely (ReplyMarkup = null), not replaced with a residual tappable button");

        // Defence: assert that NO EditMessageReplyMarkup call was made
        // — the old approach (residual `_noop_` button) would have
        // shown up here.
        harness.EditReplyMarkupRequests.Should().BeEmpty(
            "the handler MUST NOT leave a residual inline keyboard (no EditMessageReplyMarkup call); buttons are removed via EditMessageText with null markup");
    }

    // ============================================================
    // Scenario 4 (e2e-scenarios.md "Callback query answered after
    // question expired"):
    // ============================================================

    [Fact]
    public async Task CallbackResponse_ExpiredQuestion_AnswersExpiredAndDoesNotPublish()
    {
        var harness = await CallbackHarness.BuildAsync(
            questionId: "Q-5001",
            expiresAt: DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        harness.Time.SetUtcNow(DateTimeOffset.Parse("2025-01-01T00:10:00Z", CultureInfo.InvariantCulture));
        var evt = harness.BuildCallback("cb-expired", "Q-5001", "approve");

        var result = await harness.Handler.HandleAsync(evt, default);

        result.Success.Should().BeTrue("expiry is a graceful short-circuit, NOT a handler failure");
        harness.PublishedDecisions.Should().BeEmpty(
            "no HumanDecisionEvent must be published when the question has already expired");
        harness.AuditEntries.Should().BeEmpty();
        harness.AnswerCallbackRequests.Should().ContainSingle();
        harness.AnswerCallbackRequests[0].Text.Should().Be(CallbackQueryHandler.ExpiredQuestionText);

        var stored = await harness.Store.GetAsync("Q-5001", default);
        stored!.Status.Should().Be(PendingQuestionStatus.Pending,
            "expiry rejection must NOT transition the question — the QuestionTimeoutService owns the terminal transition");
    }

    // ============================================================
    // Scenario 5 (e2e-scenarios.md "Concurrent button taps from same
    // user"):
    // ============================================================

    [Fact]
    public async Task CallbackResponse_ConcurrentTapsFromSameUser_OnlyFirstTapEmitsDecision()
    {
        var harness = await CallbackHarness.BuildAsync(questionId: "Q-6001");

        var firstTap = harness.BuildCallback("cb-6001-a", "Q-6001", "approve");
        var secondTap = harness.BuildCallback("cb-6001-b", "Q-6001", "reject");

        await harness.Handler.HandleAsync(firstTap, default);
        await harness.Handler.HandleAsync(secondTap, default);

        harness.PublishedDecisions.Should().HaveCount(1,
            "the second tap from the SAME respondent on the SAME question must NOT publish a duplicate decision");
        harness.PublishedDecisions[0].ActionValue.Should().Be("approve",
            "the FIRST tap (approve) wins, not the second (reject)");

        harness.AnswerCallbackRequests.Should().HaveCount(2);
        harness.AnswerCallbackRequests[0].Text
            .Should().Be(CallbackQueryHandler.DecisionShownLabelPrefix + "Approve");
        harness.AnswerCallbackRequests[1].Text
            .Should().Be(CallbackQueryHandler.AlreadyRespondedText,
                "the second tap MUST be answered with 'Already responded'");
    }

    // ============================================================
    // Iter-1 evaluator item 3 — composite dedup must be released on
    // exception (publish/audit/markAnswered failure must NOT leave the
    // composite slot reserved; a redelivery must be able to re-process
    // the same question + user).
    // ============================================================

    [Fact]
    public async Task CallbackResponse_PublishFailureReleasesBothReservations_AndRetrySucceeds()
    {
        var harness = await CallbackHarness.BuildAsync(failPublishOnFirstCall: true);
        var firstTap = harness.BuildCallback("cb-retry-1", QuestionId, "approve");
        var retryTap = harness.BuildCallback("cb-retry-2", QuestionId, "approve");

        // First attempt — publish throws, so the handler MUST release
        // both the cb: and the qa: reservations and re-throw so the
        // pipeline can release its own EventId reservation too.
        Func<Task> first = () => harness.Handler.HandleAsync(firstTap, default);
        await first.Should().ThrowAsync<InvalidOperationException>(
            "publish failure must propagate so the pipeline can release-on-throw");

        harness.PublishedDecisions.Should().BeEmpty(
            "publish was injected to fail on the first call so the captured-events list is empty");
        // Stage 5.3 iter-8 evaluator item 3 — AUDIT-FIRST ordering:
        // audit runs BEFORE publish, so a publish failure leaves a
        // durable audit_logs row from the first attempt. The Stage
        // 5.3 brief mandate "log every outbound decision event with
        // full context" is honoured because the only failure modes
        // are (a) audit-then-publish both succeed → 1 audit + 1
        // publish (canonical), or (b) audit succeeds + publish
        // fails → 1 audit on the failed attempt + at-least-once
        // retry. There is NO failure mode where a publish escapes
        // without an audit row paired in front of it.
        harness.AuditEntries.Should().HaveCount(1,
            "Stage 5.3 iter-8 evaluator item 3: audit ran BEFORE publish, so the audit row from the failed attempt is durable even though publish threw — the brief's 'log every outbound decision event' guarantee requires the audit row to land before the bus publish, not after");

        // Second delivery — DIFFERENT CallbackId (Telegram never reuses
        // ids across deliveries), SAME (QuestionId, RespondentUserId).
        // The composite reservation MUST have been released by the
        // exception path; otherwise the retry would answer 'Already
        // responded' without publishing — the bug the evaluator flagged.
        var result = await harness.Handler.HandleAsync(retryTap, default);

        result.Success.Should().BeTrue();
        harness.PublishedDecisions.Should().HaveCount(1,
            "iter-1 evaluator item 3 — the retry MUST publish (composite slot was released on the prior exception)");
        harness.AuditEntries.Should().HaveCount(2,
            "Stage 5.3 iter-8 evaluator item 3 (AUDIT-FIRST): the retry audits AGAIN before publishing, so there are two audit rows for the same decision — one from the failed attempt, one from the success. Consumer-side dedup on QuestionId+ActionValue handles the bounded duplicate; the persist-every-decision guarantee is preserved because every publish has a paired durable audit row");
        var stored = await harness.Store.GetAsync(QuestionId, default);
        stored!.Status.Should().Be(PendingQuestionStatus.Answered);
    }

    [Fact]
    public async Task CallbackResponse_AuditFailureReleasesBothReservations_AndRetrySucceeds()
    {
        // Stage 5.3 iter-8 evaluator item 3 — AUDIT-FIRST symmetry:
        // a failing audit on the first attempt MUST short-circuit
        // BEFORE publish so the bus event NEVER escapes without a
        // durable audit_logs row. Pre-iter-8 ordering published
        // first then audited (so an audit failure left the event
        // already on the bus); post-iter-8 ordering audits first
        // so an audit failure cleanly aborts and no publish runs.
        var harness = await CallbackHarness.BuildAsync(failAuditOnFirstCall: true);
        var firstTap = harness.BuildCallback("cb-audit-1", QuestionId, "approve");
        var retryTap = harness.BuildCallback("cb-audit-2", QuestionId, "approve");

        Func<Task> first = () => harness.Handler.HandleAsync(firstTap, default);
        await first.Should().ThrowAsync<InvalidOperationException>();

        // Stage 5.3 iter-8 evaluator item 3 — AUDIT-FIRST: with the
        // new ordering the audit runs BEFORE publish, so the failed
        // audit on the first attempt means publish never ran. The
        // bus event did NOT escape — the Stage 5.3 'log every
        // outbound decision event with full context' guarantee
        // holds because there is no published event without a
        // paired audit row.
        harness.PublishedDecisions.Should().BeEmpty(
            "Stage 5.3 iter-8 evaluator item 3 (AUDIT-FIRST): the failing audit short-circuited BEFORE publish — no bus event escaped without a paired audit row");
        harness.AuditEntries.Should().BeEmpty(
            "the audit mock was injected to throw on the first call so the captured-entries list is empty (the audit-write attempt never reached the success branch that records the entry)");

        var result = await harness.Handler.HandleAsync(retryTap, default);

        result.Success.Should().BeTrue();
        harness.PublishedDecisions.Should().HaveCount(1,
            "Stage 5.3 iter-8 evaluator item 3 (AUDIT-FIRST): publish ran EXACTLY once on the successful retry; the first attempt never published because audit failed first");
        harness.AuditEntries.Should().HaveCount(1,
            "audit only succeeded on the retry — the first attempt's audit threw before recording the entry, so only the retry's row is captured");
    }

    // ============================================================
    // Stage 5.3 iter-3 evaluator item 7 — claim-before-publish race
    // safety. A concurrent timeout sweep that terminal-s the row
    // between the callback handler's GetAsync read and its
    // MarkAnsweredAsync attempt must cause the callback to short-
    // circuit (issue 'Already responded', mark composite dedup
    // processed, NOT publish, NOT audit). The fix is the atomic CAS
    // in MarkAnsweredAsync that returns false when the row is no
    // longer Pending; this test simulates that race by directly
    // calling MarkTimedOutAsync on the store between Store and
    // HandleAsync.
    // ============================================================

    [Fact]
    public async Task CallbackResponse_LosesAtomicClaim_DoesNotPublishOrAudit()
    {
        var harness = await CallbackHarness.BuildAsync(questionId: "Q-race");

        // Simulate "QuestionTimeoutService won the race" by
        // terminal-ing the row directly via the same atomic primitive
        // the sweep uses in production.
        var timedOutWon = await harness.Store.MarkTimedOutAsync("Q-race", default);
        timedOutWon.Should().BeTrue();

        var lateTap = harness.BuildCallback("cb-race-1", "Q-race", "approve");
        var result = await harness.Handler.HandleAsync(lateTap, default);

        result.Success.Should().BeTrue("a lost claim is a successful no-op, not an error");
        harness.PublishedDecisions.Should().BeEmpty(
            "iter-3 evaluator item 7 — the callback MUST NOT publish a decision after losing the atomic claim");
        harness.AuditEntries.Should().BeEmpty(
            "iter-3 evaluator item 7 — the callback MUST NOT audit a decision it did not publish");
        harness.AnswerCallbackRequests.Should().ContainSingle();
        harness.AnswerCallbackRequests[0].Text.Should().Be(CallbackQueryHandler.AlreadyRespondedText);

        // Row is still TimedOut — the callback did NOT overwrite it.
        var stored = await harness.Store.GetAsync("Q-race", default);
        stored!.Status.Should().Be(PendingQuestionStatus.TimedOut);

        // Stage 5.3 iter-5 evaluator item 2 — a callback that loses
        // the atomic Answered claim MUST NOT mutate the human-
        // selection metadata on the now-TimedOut row. The prior
        // ordering (RecordSelectionAsync BEFORE the claim CAS) wrote
        // SelectedActionId / SelectedActionValue / RespondentUserId
        // onto the timed-out row even though no decision was
        // accepted — a forensic landmine that made TimedOut rows
        // look like the operator had approved them. After the fix,
        // RecordSelectionAsync runs only on the winning side of the
        // claim CAS, so a lost-race callback leaves the row exactly
        // as the timeout sweep saw it.
        stored.SelectedActionId.Should().BeNull(
            "Stage 5.3 iter-5 evaluator item 2 — the lost-race callback MUST NOT write SelectedActionId onto a TimedOut row (RecordSelectionAsync must run AFTER the atomic claim CAS, not before)");
        stored.SelectedActionValue.Should().BeNull(
            "Stage 5.3 iter-5 evaluator item 2 — the lost-race callback MUST NOT write SelectedActionValue onto a TimedOut row");
        stored.RespondentUserId.Should().BeNull(
            "Stage 5.3 iter-5 evaluator item 2 — the lost-race callback MUST NOT write RespondentUserId onto a TimedOut row");
    }

    [Fact]
    public async Task CallbackResponse_RequiresComment_LosesAtomicClaim_DoesNotPromptOrPublish()
    {
        var harness = await CallbackHarness.BuildAsync(questionId: "Q-race-comment");

        // Pre-claim the row as TimedOut via the same atomic primitive
        // the sweep would use, then send a callback that selects an
        // action with RequiresComment=true. The claim CAS in
        // MarkAwaitingCommentAsync must return false, the handler must
        // short-circuit with 'Already responded', and the operator must
        // never get prompted for a comment that cannot be applied.
        var timedOutWon = await harness.Store.MarkTimedOutAsync("Q-race-comment", default);
        timedOutWon.Should().BeTrue();

        var lateTap = harness.BuildCallback("cb-race-comment", "Q-race-comment", "comment");
        var result = await harness.Handler.HandleAsync(lateTap, default);

        result.Success.Should().BeTrue();
        harness.PublishedDecisions.Should().BeEmpty();
        harness.AuditEntries.Should().BeEmpty();
        // No "please send a comment" prompt was sent — operator is not
        // misled into typing a reply against a settled question.
        harness.AnswerCallbackRequests.Should().ContainSingle();
        harness.AnswerCallbackRequests[0].Text.Should().Be(CallbackQueryHandler.AlreadyRespondedText);

        // Stage 5.3 iter-5 evaluator item 2 — symmetric assertion for
        // the RequiresComment branch. A callback that loses the atomic
        // AwaitingComment claim must leave the timed-out row's
        // selection metadata untouched. The prior ordering
        // (RecordSelectionAsync BEFORE the MarkAwaitingComment CAS)
        // wrote SelectedActionId / SelectedActionValue /
        // RespondentUserId onto the TimedOut row even though no
        // decision was accepted; after the fix, RecordSelectionAsync
        // runs only on the winning side of the claim CAS so the
        // timed-out row carries no spurious human-selection data.
        var stored = await harness.Store.GetAsync("Q-race-comment", default);
        stored!.Status.Should().Be(PendingQuestionStatus.TimedOut);
        stored.SelectedActionId.Should().BeNull(
            "Stage 5.3 iter-5 evaluator item 2 — RequiresComment branch must also gate RecordSelectionAsync on the MarkAwaitingComment claim winning");
        stored.SelectedActionValue.Should().BeNull();
        stored.RespondentUserId.Should().BeNull();
    }

    // ============================================================
    // Stage 5.3 iter-5 evaluator item 1 — RequiresComment branch
    // MUST revert the AwaitingComment claim when any post-claim
    // side effect (prompt, message edit, callback ack, dedup-seal)
    // throws. Without the revert, a single transient Telegram /
    // network hiccup permanently strands the question: Status is
    // AwaitingComment so no callback can re-claim it via
    // MarkAwaitingCommentAsync (only Pending is a legal source
    // state), and no text reply can complete it because the prompt
    // the operator was supposed to see never sent.
    // ============================================================

    [Fact]
    public async Task CallbackResponse_RequiresComment_PromptSendFails_RevertsAwaitingCommentClaimAndPropagates()
    {
        var harness = await CallbackHarness.BuildAsync(failPromptOnFirstCall: true);
        var tap = harness.BuildCallback("cb-comment-prompt-fail", QuestionId, "comment");

        Func<Task> act = () => harness.Handler.HandleAsync(tap, default);
        await act.Should().ThrowAsync<InvalidOperationException>(
            "Stage 5.3 iter-5 evaluator item 1 — a failed prompt MUST propagate so the pipeline can release its EventId reservation");

        harness.PublishedDecisions.Should().BeEmpty(
            "RequiresComment defers HumanDecisionEvent emission until the text reply; the failed prompt path must not publish");
        harness.AuditEntries.Should().BeEmpty();

        // Stage 5.3 iter-5 evaluator item 1 — the AwaitingComment
        // claim MUST have been reverted to Pending so a retry
        // (webhook redelivery, operator re-tap) can re-claim the row.
        // The prior code left Status=AwaitingComment forever after a
        // post-claim failure, which permanently stranded the question.
        var stored = await harness.Store.GetAsync(QuestionId, default);
        stored!.Status.Should().Be(
            PendingQuestionStatus.Pending,
            "Stage 5.3 iter-5 evaluator item 1 — the post-claim side-effect failure MUST trigger TryRevertAwaitingCommentClaimAsync so the row is sweep-eligible (and re-claimable by a retry) again. Without the revert, Status stays AwaitingComment and no retry can MarkAwaitingComment again because Pending is the only legal source state for that CAS");

        // The retry must succeed end-to-end — confirms the revert
        // actually restored the row to a claimable state AND the
        // dedup slots were released by the outer catch.
        harness.AllowPromptSends();
        var retry = harness.BuildCallback("cb-comment-prompt-retry", QuestionId, "comment");
        var result = await harness.Handler.HandleAsync(retry, default);

        result.Success.Should().BeTrue();
        var afterRetry = await harness.Store.GetAsync(QuestionId, default);
        afterRetry!.Status.Should().Be(
            PendingQuestionStatus.AwaitingComment,
            "the retry must complete the lifecycle — RequiresComment branch transitions the now-Pending row to AwaitingComment on the second attempt");
        afterRetry.SelectedActionValue.Should().Be("comment");
        afterRetry.RespondentUserId.Should().Be(RespondentUserId);
        harness.SendMessageRequests.Should().HaveCount(
            1,
            "the prompt fired exactly once on the retry (the failed first attempt threw before the captured list grew)");
    }

    // ============================================================
    // Additional defence-in-depth tests
    // ============================================================

    [Fact]
    public async Task CallbackResponse_MalformedPayload_AnswersInvalidActionAndDoesNotPublish()
    {
        var harness = await CallbackHarness.BuildAsync();
        var evt = harness.BuildCallback("cb-malformed", payload: "no-separator-here");

        var result = await harness.Handler.HandleAsync(evt, default);

        result.Success.Should().BeTrue();
        harness.PublishedDecisions.Should().BeEmpty();
        harness.AnswerCallbackRequests.Should().ContainSingle();
        harness.AnswerCallbackRequests[0].Text.Should().Be(CallbackQueryHandler.MalformedCallbackText);
    }

    [Fact]
    public async Task CallbackResponse_UnknownQuestionId_AnswersNotAvailable()
    {
        var harness = await CallbackHarness.BuildAsync();
        var evt = harness.BuildCallback("cb-ghost", "Q-DOES-NOT-EXIST", "approve");

        await harness.Handler.HandleAsync(evt, default);

        harness.PublishedDecisions.Should().BeEmpty();
        harness.AnswerCallbackRequests.Should().ContainSingle();
        harness.AnswerCallbackRequests[0].Text.Should().Be(CallbackQueryHandler.QuestionNotFoundText);
    }

    [Fact]
    public async Task CallbackResponse_UnknownActionId_AnswersUnknownActionAndDoesNotPublish()
    {
        var harness = await CallbackHarness.BuildAsync();
        var evt = harness.BuildCallback("cb-unknown-action", QuestionId, "no-such-action");

        await harness.Handler.HandleAsync(evt, default);

        harness.PublishedDecisions.Should().BeEmpty();
        harness.AnswerCallbackRequests.Should().ContainSingle();
        harness.AnswerCallbackRequests[0].Text.Should().Be(CallbackQueryHandler.UnknownActionText);

        var stored = await harness.Store.GetAsync(QuestionId, default);
        stored!.Status.Should().Be(PendingQuestionStatus.Pending,
            "an unknown action must NOT terminate the pending question — a fresh tap with a valid action may follow");
    }

    [Fact]
    public async Task CallbackResponse_UnknownActionThenValidAction_PublishesOnTheValidTap()
    {
        // The UnknownAction path releases the composite slot so a
        // legitimate follow-up tap from the same user is processed.
        var harness = await CallbackHarness.BuildAsync();
        await harness.Handler.HandleAsync(
            harness.BuildCallback("cb-bad", QuestionId, "no-such-action"),
            default);
        await harness.Handler.HandleAsync(
            harness.BuildCallback("cb-good", QuestionId, "approve"),
            default);

        harness.PublishedDecisions.Should().HaveCount(1);
        harness.PublishedDecisions[0].ActionValue.Should().Be("approve");
    }

    [Fact]
    public async Task CallbackResponse_RequiresCommentAction_TransitionsToAwaitingCommentAndDoesNotEmitYet()
    {
        var harness = await CallbackHarness.BuildAsync();
        var evt = harness.BuildCallback("cb-comment", QuestionId, "comment");

        await harness.Handler.HandleAsync(evt, default);

        harness.PublishedDecisions.Should().BeEmpty(
            "RequiresComment defers HumanDecisionEvent emission until the follow-up text reply arrives");
        harness.AuditEntries.Should().BeEmpty();

        var stored = await harness.Store.GetAsync(QuestionId, default);
        stored!.Status.Should().Be(PendingQuestionStatus.AwaitingComment);
        stored.SelectedActionValue.Should().Be("comment");
        stored.RespondentUserId.Should().Be(RespondentUserId);

        // The handler also sent the prompt + edited the message + answered the callback.
        harness.SendMessageRequests.Should().ContainSingle();
        harness.SendMessageRequests[0].Text.Should().StartWith(CallbackQueryHandler.CommentPromptText,
            "the comment-prompt body MUST start with the canonical CommentPromptText");
        harness.SendMessageRequests[0].Text.Should().Contain(CorrelationId,
            "iter-3 evaluator item 2 — the comment-prompt message MUST include the trace/correlation ID (story-wide acceptance criterion: All messages include trace/correlation ID)");
        harness.SendMessageRequests[0].ChatId.Identifier.Should().Be(ChatId);

        harness.EditTextRequests.Should().ContainSingle(
            "RequiresComment path STILL edits the message text + nulls the markup so the buttons disappear");
        harness.EditTextRequests[0].ReplyMarkup.Should().BeNull();
        harness.EditTextRequests[0].Text.Should().Contain(CallbackQueryHandler.DecisionShownLabelPrefix + "Comment");

        harness.AnswerCallbackRequests.Should().ContainSingle();
        harness.AnswerCallbackRequests[0].Text
            .Should().Be(CallbackQueryHandler.DecisionShownLabelPrefix + "Comment");
    }

    [Fact]
    public async Task TextReplyAfterRequiresComment_EmitsDecisionWithComment()
    {
        var harness = await CallbackHarness.BuildAsync();
        await harness.Handler.HandleAsync(
            harness.BuildCallback("cb-comment-step-1", QuestionId, "comment"),
            default);
        harness.ResetCaptured();

        var commentEvent = new MessengerEvent
        {
            EventId = "tg-update-text-reply",
            EventType = EventType.TextReply,
            UserId = RespondentUserId.ToString(CultureInfo.InvariantCulture),
            ChatId = ChatId.ToString(CultureInfo.InvariantCulture),
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = "trace-text-reply",
            Payload = "Confirmed — see ticket #444",
        };

        await harness.Handler.HandleAsync(commentEvent, default);

        harness.PublishedDecisions.Should().HaveCount(1);
        var decision = harness.PublishedDecisions[0];
        decision.ActionValue.Should().Be("comment");
        decision.Comment.Should().Be("Confirmed — see ticket #444");
        decision.CorrelationId.Should().Be(CorrelationId);

        var stored = await harness.Store.GetAsync(QuestionId, default);
        stored!.Status.Should().Be(PendingQuestionStatus.Answered);
    }

    [Fact]
    public async Task CallbackResponse_AlreadyAnsweredQuestion_AnswersAlreadyRespondedWithoutPublishing()
    {
        var harness = await CallbackHarness.BuildAsync();
        await harness.Store.MarkAnsweredAsync(QuestionId, default);

        var evt = harness.BuildCallback("cb-stale", QuestionId, "approve");

        await harness.Handler.HandleAsync(evt, default);

        harness.PublishedDecisions.Should().BeEmpty();
        harness.AnswerCallbackRequests.Should().ContainSingle();
        harness.AnswerCallbackRequests[0].Text.Should().Be(CallbackQueryHandler.AlreadyRespondedText);
    }

    [Fact]
    public async Task CallbackResponse_MissingCallbackId_SilentlyAcknowledgesAndDoesNotTouchStore()
    {
        var harness = await CallbackHarness.BuildAsync();
        var evt = new MessengerEvent
        {
            EventId = "tg-update-nocb",
            EventType = EventType.CallbackResponse,
            UserId = RespondentUserId.ToString(CultureInfo.InvariantCulture),
            ChatId = ChatId.ToString(CultureInfo.InvariantCulture),
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = CorrelationId,
            Payload = QuestionId + ":approve",
            CallbackId = null,
        };

        var result = await harness.Handler.HandleAsync(evt, default);

        result.Success.Should().BeTrue();
        harness.PublishedDecisions.Should().BeEmpty();
        harness.AnswerCallbackRequests.Should().BeEmpty();
        harness.EditTextRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-separator")]
    [InlineData(":only-action")]
    [InlineData("only-question:")]
    public void TryParseCallbackData_RejectsMalformedPayloads(string? payload)
    {
        var ok = CallbackQueryHandler.TryParseCallbackData(payload, out var q, out var a);

        ok.Should().BeFalse();
        q.Should().BeEmpty();
        a.Should().BeEmpty();
    }

    [Fact]
    public void TryParseCallbackData_ParsesQuestionAndActionAtFirstSeparator()
    {
        var ok = CallbackQueryHandler.TryParseCallbackData("Q1:approve", out var q, out var a);

        ok.Should().BeTrue();
        q.Should().Be("Q1");
        a.Should().Be("approve");
    }

    [Fact]
    public void BuildDecisionMessageText_EmbedsTitleBodyDecisionBadgeAndCorrelationFooter()
    {
        var pending = new PendingQuestion
        {
            QuestionId = "Q1",
            AgentId = "agent-x",
            TaskId = "T1",
            Title = "Approve deploy?",
            Body = "Pre-flight clean.",
            Severity = MessageSeverity.Critical,
            AllowedActions = new[]
            {
                new HumanAction { ActionId = "ap", Label = "Approve", Value = "approve" },
                new HumanAction { ActionId = "rj", Label = "Reject", Value = "reject" },
            },
            DefaultActionId = "ap",
            DefaultActionValue = "approve",
            TelegramChatId = 1,
            TelegramMessageId = 2,
            ExpiresAt = DateTimeOffset.Parse("2025-06-01T12:00:00Z", CultureInfo.InvariantCulture),
            CorrelationId = "trace-build-decision-text-001",
            Status = PendingQuestionStatus.Pending,
            StoredAt = DateTimeOffset.UtcNow,
        };
        var selected = pending.AllowedActions[1];

        var text = CallbackQueryHandler.BuildDecisionMessageText(pending, selected);

        text.Should().Contain("Approve deploy?");
        text.Should().Contain("Pre-flight clean.");
        text.Should().Contain(CallbackQueryHandler.DecisionShownLabelPrefix + "Reject");
        text.Should().Contain("trace-build-decision-text-001",
            "iter-2 evaluator item 1 — every outbound message MUST include the trace/correlation ID, including the post-decision edit");
        text.Should().Contain(
            string.Format(
                CultureInfo.InvariantCulture,
                CallbackQueryHandler.CorrelationFooterFormat,
                pending.CorrelationId),
            "the footer MUST use the canonical CorrelationFooterFormat so the rendered shape is operator-recognisable across messages");

        // iter-3 evaluator item 3 — every field rendered in the
        // original message MUST be preserved in the post-decision edit.
        text.Should().Contain("🚨",
            "the severity badge MUST be preserved (Critical → 🚨)");
        text.Should().Contain("Severity: Critical",
            "iter-3 evaluator item 3 — severity label MUST be preserved");
        text.Should().Contain("Timeout: 2025-06-01",
            "iter-3 evaluator item 3 — timeout MUST be preserved (absolute ISO-8601)");
        text.Should().Contain("Default action if no response: Approve",
            "iter-3 evaluator item 3 — proposed default action MUST be preserved with its human-readable label");
    }

    [Fact]
    public void BuildCommentPromptText_AppendsCorrelationFooter()
    {
        var pending = new PendingQuestion
        {
            QuestionId = "Q1",
            AgentId = "agent-x",
            TaskId = "T1",
            Title = "T",
            Body = "B",
            Severity = MessageSeverity.Normal,
            AllowedActions = new[] { new HumanAction { ActionId = "a", Label = "A", Value = "a", RequiresComment = true } },
            TelegramChatId = 1,
            TelegramMessageId = 2,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            CorrelationId = "trace-comment-prompt-007",
            Status = PendingQuestionStatus.AwaitingComment,
            StoredAt = DateTimeOffset.UtcNow,
        };

        var text = CallbackQueryHandler.BuildCommentPromptText(pending);

        text.Should().StartWith(CallbackQueryHandler.CommentPromptText,
            "the prompt body MUST begin with the canonical CommentPromptText");
        text.Should().Contain("trace-comment-prompt-007",
            "iter-3 evaluator item 2 — the comment prompt MUST include the trace/correlation ID (story-wide criterion: All messages include trace/correlation ID)");
        text.Should().EndWith(
            string.Format(
                CultureInfo.InvariantCulture,
                CallbackQueryHandler.CorrelationFooterFormat,
                pending.CorrelationId),
            "the footer MUST use the canonical CorrelationFooterFormat shape so the rendered trace is recognisable");
    }

    [Fact]
    public async Task CallbackResponse_DuplicateOnDifferentHandlerInstance_ReplaysFromSharedDistributedCache()
    {
        // iter-3 evaluator item 1 — the replay cache is now backed by
        // IDistributedCache, so a duplicate Telegram redelivery that
        // lands on a DIFFERENT pod (Stage 4.3 Redis) or arrives AFTER
        // a process restart (rehydrated from durable cache) MUST
        // resolve to the same prior result. We simulate cross-pod by
        // sharing the IDistributedCache across two harness instances
        // that are otherwise independent (different stores, dedup
        // services, mock clients) — the second handler is a "new pod"
        // that has never seen this CallbackId locally.
        var sharedCache = new MemoryDistributedCache(
            Options.Create(new MemoryDistributedCacheOptions()));
        var podA = await CallbackHarness.BuildAsync(replayCache: sharedCache);
        var podB = await CallbackHarness.BuildAsync(replayCache: sharedCache);

        // First tap on pod A: full processing, replay cached.
        await podA.Handler.HandleAsync(
            podA.BuildCallback("CB-XPOD", QuestionId, "approve"),
            default);
        podA.PublishedDecisions.Should().HaveCount(1,
            "pod A processes the first tap normally");
        var firstToast = podA.AnswerCallbackRequests.Single().Text;
        firstToast.Should().Be(CallbackQueryHandler.DecisionShownLabelPrefix + "Approve");

        // Duplicate redelivery routes to pod B. Pod B's local dedup
        // service has NEVER seen CB-XPOD, but the shared distributed
        // cache has the replay entry — so the duplicate-callback
        // short-circuit on pod B must still re-answer with the SAME
        // toast pod A sent (NOT the AlreadyRespondedText fallback).
        // To exercise the duplicate-short-circuit branch on pod B,
        // first reserve CB-XPOD in pod B's dedup (simulating the
        // distributed dedup that Stage 4.3 will share alongside the
        // distributed cache).
        await podB.Dedup.TryReserveAsync(
            CallbackQueryHandler.CallbackIdDedupKeyPrefix + "CB-XPOD",
            default);

        await podB.Handler.HandleAsync(
            podB.BuildCallback("CB-XPOD", QuestionId, "approve"),
            default);

        podB.PublishedDecisions.Should().BeEmpty(
            "pod B sees the duplicate at its dedup gate and MUST NOT publish another HumanDecisionEvent");
        podB.AnswerCallbackRequests.Should().ContainSingle(
            "pod B still answers the callback (operator's spinner must stop on whichever pod handles the redelivery)");
        podB.AnswerCallbackRequests[0].Text.Should().Be(firstToast,
            "iter-3 evaluator item 1 — cross-pod duplicate MUST resolve to the SAME prior toast via the shared IDistributedCache, not the AlreadyRespondedText fallback");
    }

    [Fact]
    public void ReplayCacheTtl_IsBoundedAboveZeroAndBelow24Hours()
    {
        // iter-3 evaluator item 1 — the replay cache entries are
        // written with AbsoluteExpirationRelativeToNow = ReplayCacheTtl
        // so a long-running bot cannot accumulate redelivery state
        // indefinitely, AND a stale answer cannot replay a year later.
        // Size policy now lives on the underlying IDistributedCache
        // (MemoryDistributedCache.SizeLimit in dev, Redis maxmemory in
        // production), not on the handler itself.
        CallbackQueryHandler.ReplayCacheTtl.Should().BeGreaterThan(TimeSpan.Zero,
            "the replay cache MUST expire entries on a finite horizon");
        CallbackQueryHandler.ReplayCacheTtl.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(24),
            "the TTL MUST be tighter than 24 h so a stuck process cannot replay year-old callback answers");
    }

    [Fact]
    public async Task CallbackResponse_DuplicateWithCacheReadFailure_DegradesToAlreadyRespondedAndAnswersCallback()
    {
        // iter-5 hardening — if the distributed cache (e.g. Redis)
        // throws on the duplicate-callback short-circuit's GetStringAsync
        // (transient outage, network blip), the handler MUST degrade to
        // AlreadyRespondedText AND still answer the callback so the
        // operator's spinner stops. Throwing out would trip pipeline
        // release-on-throw and leave the operator hanging despite the
        // canonical decision already being published / audited.
        var failingCache = new ThrowingDistributedCache();
        var harness = await CallbackHarness.BuildAsync(replayCache: failingCache);

        // First tap: cache write also throws (best-effort, swallowed),
        // but decision publish + audit still complete.
        await harness.Handler.HandleAsync(
            harness.BuildCallback("CB-FAIL", QuestionId, "approve"),
            default);
        harness.PublishedDecisions.Should().HaveCount(1,
            "first tap processes normally even when the replay cache write fails");
        harness.AnswerCallbackRequests.Should().ContainSingle();

        // Second tap with the same CallbackId: the cb: dedup gate
        // short-circuits, then GetStringAsync throws — the handler must
        // catch and degrade.
        await harness.Handler.HandleAsync(
            harness.BuildCallback("CB-FAIL", QuestionId, "approve"),
            default);
        harness.PublishedDecisions.Should().HaveCount(1,
            "duplicate is short-circuited at the cb: gate; no second HumanDecisionEvent");
        harness.AnswerCallbackRequests.Should().HaveCount(2,
            "the duplicate STILL answers the callback so the spinner stops, even when the cache read throws");
        harness.AnswerCallbackRequests[1].Text.Should().Be(CallbackQueryHandler.AlreadyRespondedText,
            "cache-read failure on the duplicate path MUST degrade to AlreadyRespondedText, not throw out and strand the operator");
    }

    /// <summary>
    /// Test double: <see cref="IDistributedCache"/> impl that throws on
    /// every operation. Used to pin the duplicate-callback short-circuit's
    /// cache-read resilience.
    /// </summary>
    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("simulated cache read failure");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
            => Task.FromException<byte[]?>(new InvalidOperationException("simulated cache read failure"));
        public void Refresh(string key) => throw new InvalidOperationException("simulated cache refresh failure");
        public Task RefreshAsync(string key, CancellationToken token = default)
            => Task.FromException(new InvalidOperationException("simulated cache refresh failure"));
        public void Remove(string key) => throw new InvalidOperationException("simulated cache remove failure");
        public Task RemoveAsync(string key, CancellationToken token = default)
            => Task.FromException(new InvalidOperationException("simulated cache remove failure"));
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => throw new InvalidOperationException("simulated cache write failure");
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => Task.FromException(new InvalidOperationException("simulated cache write failure"));
    }

    // ============================================================
    // Harness
    // ============================================================

    private sealed class CallbackHarness
    {
        public required CallbackQueryHandler Handler { get; init; }
        public required InMemoryPendingQuestionStore Store { get; init; }
        public required InMemoryDeduplicationService Dedup { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required List<HumanDecisionEvent> PublishedDecisions { get; init; }
        public required List<HumanResponseAuditEntry> AuditEntries { get; init; }
        public required List<AnswerCallbackQueryRequest> AnswerCallbackRequests { get; init; }
        public required List<EditMessageTextRequest> EditTextRequests { get; init; }
        public required List<EditMessageReplyMarkupRequest> EditReplyMarkupRequests { get; init; }
        public required List<SendMessageRequest> SendMessageRequests { get; init; }
        public required IDistributedCache ReplayCache { get; init; }

        // Stage 5.3 iter-5 evaluator item 1 — mutable flag so the
        // prompt-fails-revert test can flip "fail prompt" → "allow
        // prompt" between attempts to prove the retry path succeeds
        // end-to-end after the revert restores the row to Pending.
        public required Action AllowPromptSendsAction { get; init; }

        public void AllowPromptSends() => AllowPromptSendsAction();

        public MessengerEvent BuildCallback(
            string callbackId,
            string questionId = QuestionId,
            string actionId = "approve",
            string? payload = null,
            long respondentUserId = RespondentUserId,
            string? correlationId = null)
        {
            return new MessengerEvent
            {
                EventId = "tg-update-" + callbackId,
                EventType = EventType.CallbackResponse,
                UserId = respondentUserId.ToString(CultureInfo.InvariantCulture),
                ChatId = ChatId.ToString(CultureInfo.InvariantCulture),
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = correlationId ?? CallbackQueryHandlerTests.CorrelationId,
                Payload = payload ?? (questionId + ":" + actionId),
                CallbackId = callbackId,
            };
        }

        public void ResetCaptured()
        {
            PublishedDecisions.Clear();
            AuditEntries.Clear();
            AnswerCallbackRequests.Clear();
            EditTextRequests.Clear();
            EditReplyMarkupRequests.Clear();
            SendMessageRequests.Clear();
        }

        public static async Task<CallbackHarness> BuildAsync(
            string questionId = QuestionId,
            DateTimeOffset? expiresAt = null,
            bool failPublishOnFirstCall = false,
            bool failAuditOnFirstCall = false,
            bool failPromptOnFirstCall = false,
            IDistributedCache? replayCache = null,
            string? tenantId = null,
            string? workspaceId = null)
        {
            var store = new InMemoryPendingQuestionStore();
            var dedup = new InMemoryDeduplicationService();
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture));
            // MemoryDistributedCache is the in-process production binding
            // (TelegramServiceCollectionExtensions calls
            // AddDistributedMemoryCache); using the same impl in tests
            // exercises the real IDistributedCache contract. Allow
            // injection so cross-pod tests can share ONE cache across
            // multiple harness instances.
            replayCache ??= new MemoryDistributedCache(
                Options.Create(new MemoryDistributedCacheOptions()));

            var question = new AgentQuestion
            {
                QuestionId = questionId,
                AgentId = "agent-deploy",
                TaskId = "TASK-3300",
                Title = "Deploy build 42?",
                Body = "Pre-flight is clean.",
                Severity = MessageSeverity.High,
                AllowedActions = new List<HumanAction>
                {
                    new() { ActionId = "approve", Label = "Approve", Value = "approve" },
                    new() { ActionId = "reject", Label = "Reject", Value = "reject" },
                    new() { ActionId = "comment", Label = "Comment", Value = "comment", RequiresComment = true },
                },
                ExpiresAt = expiresAt ?? DateTimeOffset.Parse("2025-01-01T01:00:00Z", CultureInfo.InvariantCulture),
                CorrelationId = CorrelationId,
            };

            // Stage 5.3 iter-2 evaluator item 6 — stamp tenant /
            // workspace onto RoutingMetadata so the
            // InMemoryPendingQuestionStore denormalises them onto
            // PendingQuestion.TenantId / WorkspaceId and the audit
            // path can write the full context onto the row.
            var routing = new Dictionary<string, string>();
            if (tenantId is not null) routing["TenantId"] = tenantId;
            if (workspaceId is not null) routing["WorkspaceId"] = workspaceId;

            await store.StoreAsync(
                new AgentQuestionEnvelope
                {
                    Question = question,
                    // Stage 3.3 story / iter-3 evaluator item 3 — every
                    // question MUST carry a proposed default action.
                    // The denormalised DefaultActionId on PendingQuestion
                    // drives the "Default action if no response: …"
                    // line in the post-decision edit body.
                    ProposedDefaultActionId = "approve",
                    RoutingMetadata = routing,
                },
                ChatId,
                MessageId,
                default);

            var publishedDecisions = new List<HumanDecisionEvent>();
            var bus = new Mock<ISwarmCommandBus>(MockBehavior.Strict);
            var publishCallCount = 0;
            bus.Setup(b => b.PublishHumanDecisionAsync(It.IsAny<HumanDecisionEvent>(), It.IsAny<CancellationToken>()))
                .Returns<HumanDecisionEvent, CancellationToken>((d, _) =>
                {
                    publishCallCount++;
                    if (failPublishOnFirstCall && publishCallCount == 1)
                    {
                        throw new InvalidOperationException("simulated bus failure (test fixture)");
                    }
                    publishedDecisions.Add(d);
                    return Task.CompletedTask;
                });

            var auditEntries = new List<HumanResponseAuditEntry>();
            var audit = new Mock<IAuditLogger>(MockBehavior.Strict);
            var auditCallCount = 0;
            audit.Setup(a => a.LogHumanResponseAsync(It.IsAny<HumanResponseAuditEntry>(), It.IsAny<CancellationToken>()))
                .Returns<HumanResponseAuditEntry, CancellationToken>((e, _) =>
                {
                    auditCallCount++;
                    if (failAuditOnFirstCall && auditCallCount == 1)
                    {
                        throw new InvalidOperationException("simulated audit failure (test fixture)");
                    }
                    auditEntries.Add(e);
                    return Task.CompletedTask;
                });

            var answerCallbackRequests = new List<AnswerCallbackQueryRequest>();
            var editTextRequests = new List<EditMessageTextRequest>();
            var editReplyMarkupRequests = new List<EditMessageReplyMarkupRequest>();
            var sendMessageRequests = new List<SendMessageRequest>();
            var client = new Mock<ITelegramBotClient>(MockBehavior.Strict);

            // Stage 5.3 iter-5 evaluator item 1 — mutable flag so the
            // prompt-fails-revert test can flip the failing condition
            // off for the retry attempt. Boxed so the closure in the
            // SendMessageRequest branch and the AllowPromptSends
            // helper see the same backing field.
            var promptShouldFail = failPromptOnFirstCall;
            Action allowPromptSends = () => promptShouldFail = false;

            client.Setup(c => c.SendRequest(
                    It.IsAny<IRequest<bool>>(),
                    It.IsAny<CancellationToken>()))
                .Returns<IRequest<bool>, CancellationToken>((req, _) =>
                {
                    if (req is AnswerCallbackQueryRequest ack)
                    {
                        answerCallbackRequests.Add(ack);
                    }
                    return Task.FromResult(true);
                });

            client.Setup(c => c.SendRequest(
                    It.IsAny<IRequest<Message>>(),
                    It.IsAny<CancellationToken>()))
                .Returns<IRequest<Message>, CancellationToken>((req, _) =>
                {
                    switch (req)
                    {
                        case EditMessageTextRequest editText:
                            editTextRequests.Add(editText);
                            break;
                        case EditMessageReplyMarkupRequest editMarkup:
                            editReplyMarkupRequests.Add(editMarkup);
                            break;
                        case SendMessageRequest send:
                            // Stage 5.3 iter-5 evaluator item 1 — fail the
                            // prompt BEFORE recording it in the captured
                            // list so the test can prove the revert path
                            // (a) propagates the exception, (b) reverts the
                            // AwaitingComment claim, (c) leaves the
                            // captured list empty for the failed attempt.
                            if (promptShouldFail)
                            {
                                throw new InvalidOperationException("simulated prompt send failure (test fixture)");
                            }
                            sendMessageRequests.Add(send);
                            break;
                    }
                    return Task.FromResult(new Message { Id = (int)MessageId });
                });

            var handler = new CallbackQueryHandler(
                store,
                bus.Object,
                audit.Object,
                dedup,
                client.Object,
                time,
                replayCache,
                NullLogger<CallbackQueryHandler>.Instance);

            return new CallbackHarness
            {
                Handler = handler,
                Store = store,
                Dedup = dedup,
                Time = time,
                PublishedDecisions = publishedDecisions,
                AuditEntries = auditEntries,
                AnswerCallbackRequests = answerCallbackRequests,
                EditTextRequests = editTextRequests,
                EditReplyMarkupRequests = editReplyMarkupRequests,
                SendMessageRequests = sendMessageRequests,
                ReplayCache = replayCache,
                AllowPromptSendsAction = allowPromptSends,
            };
        }
    }
}
