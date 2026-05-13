# Iter notes — Stage 1.3 Connector Interface and Service Contracts (iter 2)

## Prior feedback resolution
- **1. ADDRESSED** — Open questions from iter 1 are now resolved in code, no longer raised: (a) `OperatorBinding` placement landed in Core (item #2); (b) target-files list (`Program.cs`, `appsettings.json`, `docker-compose.yml`, `AgentSwarm.Messaging.IntegrationTests`) is correctly out of Stage 1.3 scope per implementation-plan stage 2.1/2.4/6.x, no action needed.
- **2. ADDRESSED** — `OperatorBinding` + `ChatType` + `OperatorRegistration` + `IOperatorRegistry` are now in `src/AgentSwarm.Messaging.Core/`; `AuthorizationResult.Bindings` and `IUserAuthorizationService` moved to Core as well so the Bindings list lives where its element type lives. Confirmed via `grep -rnF "OperatorBinding" src/AgentSwarm.Messaging.Abstractions/` — only XML-doc cross-references remain (now changed to `<c>OperatorBinding</c>` so they don't depend on Core being visible).
- **3. ADDRESSED** — Added `HumanResponseAuditEntry` record alongside the general-purpose `AuditEntry`. The five story-mandated fields (MessageId, UserId, AgentId, Timestamp, CorrelationId) are all marked `required` and enforced at compile time. `IAuditLogger.LogHumanResponseAsync(HumanResponseAuditEntry, ct)` is the type-safe entry point for human approval/rejection audits. Five `[Theory]` cases in `ConnectorContractTests` reflect on the type and assert `RequiredMemberAttribute` is present on every field.
- **4. ADDRESSED** — `IDeduplicationService.TryReserveAsync` is the atomic primitive: the XML doc mandates the implementation use a single check-and-set (Redis `When.NotExists`, conditional INSERT, or `Interlocked.CompareExchange`). Legacy `IsProcessedAsync`/`MarkProcessedAsync` retained for cache probe + write-through (the canonical durable atomic gate is `IInboundUpdateStore.PersistAsync` per architecture §4.8, defined in Stage 2.4) but documented as non-atomic. Added concurrency contract test that fires 100 parallel `TryReserveAsync` calls against the same eventId and asserts exactly one wins.
- **5. ADDRESSED** — `PendingQuestion` now carries `TaskId` and `Severity` as `required` fields. Reflection-driven `[Theory]` asserts both (plus QuestionId/AgentId/Title/Body/AllowedActions/ExpiresAt/CorrelationId) are marked `required`. New `PendingQuestion_PreservesEveryQuestionContextField` test round-trips an envelope's fields into the stored record.
- **6. ADDRESSED** — Behavioral contract tests added beyond Moq-mock surface checks: (a) supported command vocabulary via `[Theory]` over all 9 story-brief commands plus null/unknown rejection plus exact-count assertion; (b) audit required-field enforcement via reflection on `RequiredMemberAttribute`; (c) atomic dedup semantics via 100-way concurrent `TryReserveAsync` test; (d) pending-question context preservation via reflection + round-trip test. 64 tests total now pass (was 34).

## Files touched this iter
- `src/AgentSwarm.Messaging.Abstractions/IAuditLogger.cs`: split into `AuditEntry` (general) + `HumanResponseAuditEntry` (5 required story fields); `IAuditLogger` now has `LogAsync` + `LogHumanResponseAsync`.
- `src/AgentSwarm.Messaging.Abstractions/IDeduplicationService.cs`: added atomic `TryReserveAsync`; legacy methods retained but documented as non-atomic.
- `src/AgentSwarm.Messaging.Abstractions/PendingQuestion.cs`: added required `TaskId` and `Severity`.
- `src/AgentSwarm.Messaging.Abstractions/IPendingQuestionStore.cs`: doc updated to require denormalizing `TaskId` + `Severity` at `StoreAsync` time.
- `src/AgentSwarm.Messaging.Abstractions/AuthorizedOperator.cs`, `SwarmCommand.cs`, `SwarmStatusQuery.cs`: replaced `<see cref="OperatorBinding"/>` with `<c>OperatorBinding</c>` so doc cross-refs don't depend on Core (Abstractions can't reference Core).
- `src/AgentSwarm.Messaging.Abstractions/TelegramCommands.cs`: constants for all 9 story-brief commands + `IsKnown` helper + `All` collection.
- `src/AgentSwarm.Messaging.Core/OperatorBinding.cs`, `ChatType.cs`, `OperatorRegistration.cs`, `IOperatorRegistry.cs`: now in Core (was Abstractions in iter 1).
- `src/AgentSwarm.Messaging.Core/AuthorizationResult.cs`, `IUserAuthorizationService.cs`: moved to Core to follow the Bindings element-type location.
- `tests/AgentSwarm.Messaging.Tests/ConnectorContractTests.cs`: 30 new behavioral contract tests (vocabulary, audit-required, atomic dedup, pending-question context); fixed two `PendingQuestion` constructions for new required fields.

## Decisions made this iter
- Kept the dual-method `IDeduplicationService` shape rather than removing the legacy methods. The architecture (§4.9) defines exactly the two-method shape; the canonical atomic gate is in `IInboundUpdateStore` (§4.8). Adding `TryReserveAsync` as a forward-compatible third method satisfies the evaluator's acceptance-criterion concern without breaking the architecture; legacy methods carry XML-doc warnings against check-then-act use.
- Two record families for audit (`AuditEntry` general, `HumanResponseAuditEntry` story-mandated) rather than one record with five fields conditionally required. Conditional required isn't expressible in C#; splitting types enforces the story rule at the type signature.
- Used reflection-based contract tests (over `RequiredMemberAttribute`) so the assertions break the build if any future change silently drops a `required` modifier — catches the regression class the evaluator flagged at item #3 and #5.

## Dead ends tried this iter
- None (the prior-iter notes claimed `OperatorBinding` was in Abstractions, but the current source had already been refactored into Core before this session started — I verified via grep and proceeded).

## Open questions surfaced this iter
- None new. The iter-1 questions are settled (see resolution checklist).

## What's still left
- Stage 1.4: `IMessageSender` (+ `SendResult`) in Core; `IAlertService` + `IOutboundQueue` in Abstractions.
- Stage 2.x: stub/no-op implementations in the Telegram project per implementation-plan line 135.
- Stage 5.3: concrete `AuditLogEntry` persistence entity mapping from `AuditEntry` / `HumanResponseAuditEntry`.
