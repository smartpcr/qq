# Iteration notes (overwritten each iter; Forge archives the prior copy)

## Files touched this iter
- `src/AgentSwarm.Messaging.Telegram/QuestionTimeoutService.cs` — moved `using Telegram.Bot;` (and 3 siblings) ABOVE the file-scoped `namespace AgentSwarm.Messaging.Telegram;` declaration; placing them inside the namespace block had caused C# to resolve `Telegram` as `AgentSwarm.Messaging.Telegram.Telegram` (no such symbol) → 6 build errors. Same fix-pattern used by `CallbackQueryHandler.cs`.
- `src/AgentSwarm.Messaging.Persistence/PersistentPendingQuestionStore.cs` — promoted `internal sealed` → `public sealed` so the unit-test project can construct it directly (mirrors `PersistentAuditLogger` / `PersistentOperatorRegistry`; the `internal` siblings are only the ones tested via IntegrationTests).
- `src/AgentSwarm.Messaging.Telegram/TelegramServiceCollectionExtensions.cs` (line 126) — changed `services.AddSingleton<IPendingQuestionStore, InMemoryPendingQuestionStore>()` to `TryAddSingleton` so it does NOT override the `services.Replace<PersistentPendingQuestionStore>()` from `AddMessagingPersistence`. The other replaceable abstractions (IAuditLogger, IOperatorRegistry, IOutboundDeadLetterStore, IOutboundMessageIdIndex, ITaskOversightRepository) already used `TryAddSingleton` — this was an oversight in the prior iter.
- `src/AgentSwarm.Messaging.Worker/appsettings.json` — added `"Telegram:QuestionTimeout": { "PollInterval": "00:00:30" }`.
- `src/AgentSwarm.Messaging.Worker/appsettings.Development.json` — `"00:00:10"` (faster local feedback).
- `tests/AgentSwarm.Messaging.Tests/PersistentPendingQuestionStoreTests.cs` (NEW, 10 tests) — round-trips against in-memory SQLite + real `MessagingDbContext` schema. Asserts: denormalised DefaultActionValue, null default → null cols, idempotent upsert by QuestionId, composite (chat,msg) lookup, all three Mark* transitions (Answered, AwaitingComment, idempotent TimedOut), RecordSelection persistence, GetAwaitingCommentAsync oldest-first ordering, GetExpiredAsync filter (Pending+expired, AwaitingComment+expired included; Answered+expired excluded; Pending+future excluded).
- `tests/AgentSwarm.Messaging.Tests/QuestionTimeoutServiceTests.cs` (NEW, 5 tests) — drives `SweepOnceAsync` against seeded `InMemoryPendingQuestionStore`. Covers: timeout-with-default ("Skip" → ActionValue="skip_v", edit body "⏰ Timed out — default action applied: Skip"), timeout-without-default (ActionValue="__timeout__", body "⏰ Timed out — no default action"), Telegram edit failure does NOT roll back the decision and STILL marks TimedOut, no-expired-rows no-op, per-row failure isolation.
- `tests/AgentSwarm.Messaging.Tests/TelegramMessageSenderTests.cs` — added `SendQuestionAsync_PersistsPendingQuestionRecordAfterSuccessfulSend` (new test for the "Question stored on send" scenario).

## Decisions made this iter
- **`using Telegram.Bot;` placement**: moved OUTSIDE the namespace block (matching CallbackQueryHandler) rather than wrapping with `global::` prefix; the bulk-pattern keeps the file readable.
- **DI ordering fix**: changed `AddSingleton` → `TryAddSingleton` for `IPendingQuestionStore` in `AddTelegram`. Confirmed by reading `ServiceCollectionExtensions.cs` XML remarks "Recommended composition-root call order" — the canonical Worker order is `AddMessagingPersistence` THEN `AddTelegram`, which only works when `AddTelegram` uses `TryAddSingleton` for replaceable abstractions.
- **InMemory stub uses real wall-clock**: discovered when first test seed (`BaseTime + offset` in 2026) was rejected as not-expired. Fixed seed to use `DateTimeOffset.UtcNow + offset`. Production `PersistentPendingQuestionStore` does honor `TimeProvider` (verified line 234 of store), so this is purely a stub limitation; production timeout sweeps will respect injected time.

## Dead ends tried this iter
- (none — the namespace fix worked first try)

## Open questions surfaced this iter
- **Workstream brief vs implementation-plan.md tension** (carried forward): brief says `TelegramMessageSender.SendQuestionAsync` calls `IPendingQuestionStore.StoreAsync` directly after successful Telegram send; implementation-plan.md line 360 says defer to Stage 4.1 `OutboundQueueProcessor` post-send hook. Followed the brief — `PersistPendingQuestionAsync` is invoked from the sender. Stage 4.1 implementation will need to decide whether to move the call or keep it where it is.

## What's still left
- (none — DONE-eligible)
