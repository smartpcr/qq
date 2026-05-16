# Iter 2 — Stage 3.3 evaluator follow-up (3 items addressed)

## Files touched this iter
- `src/AgentSwarm.Messaging.Telegram/Pipeline/CallbackQueryHandler.cs` — rewrote: added `_replayAnswers` ConcurrentDictionary, `AnswerAndRememberAsync` helper, restructured try/catch to track composite reservation in outer scope, swapped `EditMessageReplyMarkupRequest` (residual `_noop_` button) → `EditMessageTextRequest` with `ReplyMarkup = null` + `BuildDecisionMessageText` (embeds title/body/decision badge).
- `tests/AgentSwarm.Messaging.Tests/CallbackQueryHandlerTests.cs` — rewrote: added 5 new tests covering all 3 evaluator items.

## Prior feedback resolution (iter-1 → iter-2)

- [x] 1. FIXED — `CallbackQueryHandler.cs` duplicate-CallbackId path — now consults `_replayAnswers[evt.CallbackId]` and re-answers with the EXACT prior toast text (per plan "re-answer with the previous result"). Fallback to `AlreadyRespondedText` only when the cache evicted. Covered by `CallbackResponse_DuplicateCallbackId_ReplaysPreviousResultToUser` (asserts `secondToast == firstToast`) and `CallbackResponse_DuplicateOfRejectedCallback_ReplaysRejectionToUser`.
- [x] 2. FIXED — `CallbackQueryHandler.cs` `EditMessageShowDecisionAsync` — switched from `EditMessageReplyMarkupRequest` with a residual `_noop_` button to a SINGLE `EditMessageTextRequest` that carries `BuildDecisionMessageText(...)` (Title + Body + `✅ {Label}` footer) AND `ReplyMarkup = null` (Telegram removes keyboard entirely on null). One call, zero residual tappable buttons. Covered by `CallbackResponse_EditsOriginalMessage_RemovesAllButtonsAndShowsSelectedAction`.
- [x] 3. FIXED — `CallbackQueryHandler.cs` `HandleCallbackAsync` — restructured so the composite reservation is captured via an `Action<string> setCompositeKey` callback into a `string? compositeDedupKey` declared in the OUTER scope. The single try/catch releases BOTH `callbackDedupKey` AND `compositeDedupKey` (when set) on exception via the new `SafeReleaseAsync` helper. Covered by `CallbackResponse_PublishFailureReleasesBothReservations_AndRetrySucceeds` and `CallbackResponse_AuditFailureReleasesBothReservations_AndRetrySucceeds`.

## Decisions made this iter
- **`_replayAnswers` as in-process `ConcurrentDictionary<string, string>`** instead of new abstraction — keeps DI graph stable; Stage 4.3 distributed cache will collocate the replay store with the dedup store.
- **`AnswerAndRememberAsync` at EVERY terminal answer point** (Malformed, QuestionNotFound, Expired, AlreadyResponded, UnknownAction, decision success, RequiresComment ack). The duplicate-replay path uses plain `AnswerCallbackAsync` so it does not re-write the cache.
- **`EditMessageTextRequest` with `ParseMode = ParseMode.None`** — plain text edit. Original sender uses MarkdownV2; re-escaping every operator-supplied char for the decision footer is out of scope. Plain text is unambiguous.
- **`SafeReleaseAsync` helper** swallows release errors with WARN — the dedup service's sticky-processed guard makes release-after-MarkProcessed a no-op; hard release failures should be visible but should NOT block the throw bubbling.

## Dead ends tried this iter
- Initial `ParseMode = null` compile error — `ParseMode` is a non-nullable enum in Telegram.Bot 22.10.0. Fixed by using `ParseMode.None` and adding `using Telegram.Bot.Types.Enums;`.

## Open questions surfaced this iter
None.

## What's still left
- Nothing. Build + unit + integration tests green (689 unit + 14 integration = 703 passed, 1 skipped, 0 failed).
