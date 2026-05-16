# Iter 1 — Stage 3.3 Callback Query Handler

## Files touched this iter
- `src/AgentSwarm.Messaging.Abstractions/MessengerEvent.cs` — added nullable `CallbackId` (Telegram CallbackQuery.Id, transport-agnostic).
- `src/AgentSwarm.Messaging.Telegram/TelegramUpdateMapper.cs` — polling mapper now sets `CallbackId = callback.Id`.
- `src/AgentSwarm.Messaging.Telegram/Webhook/TelegramUpdateMapper.cs` — webhook mapper now sets `CallbackId = cb.Id`.
- `src/AgentSwarm.Messaging.Telegram/Pipeline/CallbackQueryHandler.cs` — NEW. Real handler (~600 lines, heavily commented). Replaces StubCallbackHandler.
- `src/AgentSwarm.Messaging.Telegram/TelegramServiceCollectionExtensions.cs` (line 163) — DI swap stub → CallbackQueryHandler.
- `tests/AgentSwarm.Messaging.Tests/TelegramPipelineRegistrationTests.cs` (line 34) — pinned ICallbackHandler → CallbackQueryHandler.
- `tests/AgentSwarm.Messaging.Tests/CallbackQueryHandlerTests.cs` — NEW. 13 tests covering all 5 brief scenarios + RequiresComment + TextReply + parse + missing-id + already-answered.

## Decisions made this iter
- **Transport-agnostic `CallbackId` on MessengerEvent** (not a new ICallbackHandler overload). Discord interaction_id / Slack trigger_id plug into the same field; keeps the abstraction free of Telegram leakage.
- **Two-layer in-handler dedup**: `cb:{callbackId}` (per CallbackQuery.Id, plan requirement) + `qa:{questionId}:{userId}` (per e2e "concurrent taps from same user"). Pipeline-level EventId dedup is intentionally orthogonal — composite key catches different-update_id taps the pipeline gate cannot.
- **Durable status backstop** — even if both dedup TTLs evict, `pending.Status != Pending` short-circuits with "Already responded". Three lines of defence as the plan-doc demands.
- **Edit-message strategy** — single residual button labelled `✅ {Label}` with sentinel callback_data `_noop_`. Re-tap is parsed as malformed (no `:`) and answered "Invalid action" without side effect.
- **RequiresComment defers HumanDecisionEvent** — MarkAwaitingComment + prompt + edit + answer; the TextReply branch (Stage 3.5 owns full flow) here suffices to wire end-to-end so the dedup gates also seal on the AwaitingComment transition.
- **Direct `ITelegramBotClient.SendRequest<TResponse>` calls** (not IOutboundQueue) for AnswerCallback + EditReplyMarkup + comment-prompt SendMessage — operator-facing reply must beat Telegram's 30s callback-answer window; the queue is async/durable for agent messages only.
- **Swallow AnswerCallback / EditMessage errors** with WARN log — side-effects (publish + audit + MarkAnswered) have already landed; throwing would trigger pipeline release-on-throw and the composite gate would absorb a redelivery but the noise is real.

## Dead ends tried this iter
- Initially removed `using AgentSwarm.Messaging.Telegram.Pipeline.Stubs;` from `TelegramPipelineRegistrationTests.cs` after the StubCallbackHandler swap — broke build because `InMemoryDeduplicationService`, `InMemoryPendingQuestionStore`, `InMemoryPendingDisambiguationStore` are still asserted by other `[InlineData]` rows. Restored the using.

## Open questions surfaced this iter
None — brief was unambiguous. The handler's RequiresComment + TextReply branches are minimal but functional; Stage 3.5 may want to expand them.

## What's still left
- Nothing for this workstream. Build + unit + integration tests green (698 passed, 1 skipped, 0 failed).
- Future: Stage 3.5 may want to add a comprehensive RequiresComment integration test driving CallbackResponse → TextReply through the full pipeline (not just the handler).
