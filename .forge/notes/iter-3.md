# Iter notes — Stage 1.3 Connector Interface and Service Contracts (iter 3)

## Prior feedback resolution
- **1. ADDRESSED** — Added `ISwarmCommandBus.PublishHumanDecisionAsync(HumanDecisionEvent, ct)` as the strongly-typed publish entry point that closes the "approval/rejection buttons → strongly typed agent events" acceptance criterion. `ICallbackHandler` XML doc now explicitly states the handler builds `HumanDecisionEvent` and publishes via this method. Two new contract tests pin both (1) the mock-recorded publish and (2) reflection-checked method-signature presence on the interface.
- **2. ADDRESSED** — Plan and downstream tests now require `TryReserveAsync`. Edits: `implementation-plan.md:74` (Stage 1.3 dedup interface definition lists `TryReserveAsync` as primary, legacy methods marked supplementary); `implementation-plan.md:132` (Stage 2.2 dedup step rewritten to use `TryReserveAsync` as the atomic claim); `implementation-plan.md:146-147` (Stage 2.2 test scenarios rewritten as "atomic awards exactly one concurrent caller" and "reservation persists across handler outcome"); `architecture.md:563-571` (interface block updated with three methods and behavioral commentary). Verification:
  ```
  $ grep -rnF "IsProcessedAsync(event.EventId)" docs/
  (empty -- racy check-then-act pattern removed from all plan steps)
  ```
- **3. ADDRESSED** — `implementation-plan.md:68-69` now explicitly states `IUserAuthorizationService` and `AuthorizationResult` are created in **Core**, with the layering rationale inline ("co-located with `OperatorBinding` since the returned `AuthorizationResult.Bindings` is `IReadOnlyList<OperatorBinding>` and Abstractions does not reference Core"). Verification:
  ```
  $ grep -rnF "IUserAuthorizationService interface in Abstractions" docs/
  (empty)
  $ grep -rnF "AuthorizationResult record with properties" docs/
  (empty -- replaced)
  ```
- **4. ADDRESSED** — Telegram callback-data limits are now enforced at the type level via `init`-accessor validation: `AgentQuestion.QuestionId ≤ 30`, `AgentQuestion.AllowedActions` uniqueness of `ActionId`, `HumanAction.ActionId ≤ 30`, `HumanAction.Label ≤ 64`. Public `MaxQuestionIdLength`, `MaxActionIdLength`, `MaxLabelLength` constants expose the limits to consumers. 14 new tests cover boundary (=limit OK), over-limit (rejected), null/empty (rejected), duplicate ActionId (rejected), and end-to-end "QuestionId:ActionId ≤ 64 bytes" round-trip.
- **5. ADDRESSED** — Deleted three empty `Placeholder.cs` files (Abstractions/Core/Telegram). Each contained only `namespace X;` with no types so deletion is safe. Verification:
  ```
  $ grep -rnF "Placeholder" src/
  (empty)
  ```

## Files touched this iter
- `src/AgentSwarm.Messaging.Abstractions/ISwarmCommandBus.cs`: added `PublishHumanDecisionAsync`.
- `src/AgentSwarm.Messaging.Abstractions/ICallbackHandler.cs`: doc now explains handler → ISwarmCommandBus.PublishHumanDecisionAsync flow.
- `src/AgentSwarm.Messaging.Abstractions/AgentQuestion.cs`: validation on `QuestionId` length and `AllowedActions` uniqueness via private backing field + `init`; `MaxQuestionIdLength` constant.
- `src/AgentSwarm.Messaging.Abstractions/HumanAction.cs`: validation on `ActionId` ≤ 30 and `Label` ≤ 64 via private backing field + `init`; `MaxActionIdLength` / `MaxLabelLength` constants.
- `src/AgentSwarm.Messaging.Abstractions/Placeholder.cs`: DELETED.
- `src/AgentSwarm.Messaging.Core/Placeholder.cs`: DELETED.
- `src/AgentSwarm.Messaging.Telegram/Placeholder.cs`: DELETED.
- `docs/stories/qq-TELEGRAM-MESSENGER-S/implementation-plan.md`: lines 68-69 → Core; line 74 → TryReserveAsync as primary; line 132 → atomic claim in Stage 2.2; lines 146-147 → atomic test scenarios.
- `docs/stories/qq-TELEGRAM-MESSENGER-S/architecture.md`: §4.9 `IDeduplicationService` block updated with three-method interface + behavioral commentary on which methods satisfy which guarantee.
- `tests/AgentSwarm.Messaging.Tests/ConnectorContractTests.cs`: +16 tests (HumanDecisionEvent publish (2), AgentQuestion / HumanAction validation guards (14)). 79 total tests pass (was 64).

## Decisions made this iter
- Validation pattern: private backing field + custom `init` accessor (same as `MessengerMessage.CorrelationId`). Throws at construction time; no need for a separate `Validate()` method or factory. Constants `MaxQuestionIdLength` etc. are public so downstream code can size-check user input before construction.
- `PublishHumanDecisionAsync` placed on `ISwarmCommandBus` (alongside `PublishCommandAsync`) rather than introduced as a new `IHumanDecisionPublisher` interface — keeps the connector → orchestrator port surface unified and matches the existing `PublishCommandAsync` pattern.
- Plan/architecture were updated in-place rather than via Open Question because the deviations (location of `IUserAuthorizationService`, dedup interface shape) are forced by the layering constraint and resolved deterministically in code; the docs needed to follow the code, not the other way around.

## Dead ends tried this iter
- First-attempt test `AgentQuestion_AllowedActions_RejectsNull` used the `BuildQuestion` helper with a `null` argument, but the helper's `??` operator substituted a default. Fix: inline a direct `new AgentQuestion { AllowedActions = null! }` so the property setter actually receives `null`.

## Open questions surfaced this iter
- None.

## What's still left
- Stage 1.4: `IMessageSender` (+ `SendResult`) in Core; `IAlertService` + `IOutboundQueue` in Abstractions.
- Stage 2.x: stub/no-op implementations in the Telegram project per implementation-plan line 135.
- Stage 5.3: persistent `AuditLogEntry` entity mapping from `AuditEntry` / `HumanResponseAuditEntry`.
