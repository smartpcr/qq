# Iter 1 — Stage 1.2 Messenger Connector Interface

## Files touched this iter
- `AgentSwarm.Messaging.sln` — added `AgentSwarm.Messaging.Core` (GUID `355D2A37-...`) and `AgentSwarm.Messaging.Core.Tests` (GUID `2C730A3D-...`) project entries + config + nested mapping.
- `src/AgentSwarm.Messaging.Abstractions/IMessengerConnector.cs` — 3-method contract (`SendMessageAsync`, `SendQuestionAsync`, `ReceiveAsync` -> `Task<MessengerEvent>`).
- `src/AgentSwarm.Messaging.Abstractions/{UserIdentity,IIdentityResolver,DefaultDenyIdentityResolver}.cs` — identity contract + deny stub.
- `src/AgentSwarm.Messaging.Abstractions/{AuthorizationResult,IUserAuthorizationService,DefaultDenyAuthorizationService}.cs` — RBAC contract + deny stub.
- `src/AgentSwarm.Messaging.Abstractions/{CommandContext,ICommandDispatcher}.cs` — `TurnContext` typed `object?` (documented why) to keep Abstractions Bot-Framework-free.
- `src/AgentSwarm.Messaging.Abstractions/IInboundEventPublisher.cs` — channel publisher contract.
- `src/AgentSwarm.Messaging.Abstractions/IAgentQuestionStore.cs` — all 7 methods (SaveAsync, GetByIdAsync, TryUpdateStatusAsync, UpdateConversationIdAsync, GetMostRecentOpenByConversationAsync, GetOpenByConversationAsync, GetOpenExpiredAsync).
- `src/AgentSwarm.Messaging.Abstractions/ConnectorOptions.cs` — `RetryCount=3`, `RetryDelayMs=1000`, `MaxConcurrency=1` (safer base default; duck flagged 4 as risky), `DeadLetterThreshold=5`.
- `src/AgentSwarm.Messaging.Core/AgentSwarm.Messaging.Core.csproj` — new project, refs Abstractions + `Microsoft.Extensions.Logging.Abstractions 8.0.0`.
- `src/AgentSwarm.Messaging.Core/{OutboxEntry,OutboxEntryStatuses,OutboxPayloadTypes,IMessageOutbox,NoOpMessageOutbox}.cs` — outbox plumbing; stub logs `LogWarning` on dead-letter.
- `tests/AgentSwarm.Messaging.Abstractions.Tests/{IMessengerConnectorContractTests,ConnectorOptionsDefaultsTests,DefaultDenyStubsTests,Stage12ContractShapeTests}.cs` — covers both required stage scenarios + smoke checks for stubs and contract shapes.
- `tests/AgentSwarm.Messaging.Core.Tests/` — new test project; `NoOpMessageOutboxTests` + `OutboxVocabularyTests`.

## Decisions made this iter
- `CommandContext.TurnContext` is `object?` (matches implementation-plan §3.2 spec; documented in XML doc that Teams concrete code casts to `ITurnContext`). Rejected duck's purer alternative (capability interface in Abstractions) because it would diverge from the implementation-plan contract that downstream stages reference.
- `OutboxEntry` lives in `Core`, not `Abstractions` (matches architecture.md §3.2 "Defined in `AgentSwarm.Messaging.Core`").
- `ConnectorOptions.MaxConcurrency` default set to `1` (duck recommendation) to avoid silent reordering surprises in future outbox engine. Tests only assert `RetryCount`/`RetryDelayMs` defaults so this is safe.
- All stubs include `ct.ThrowIfCancellationRequested()` so cancellation propagates predictably even when no real I/O happens.
- Added test project `AgentSwarm.Messaging.Core.Tests` rather than packing Core tests into Abstractions.Tests — keeps test boundaries aligned with prod assemblies.
- Added `Stage12ContractShapeTests` (interface method-name / parameter-type checks) on top of the two required scenarios to catch silent contract drift in Stage 2.1+.

## Dead ends tried this iter
- (none — first iter)

## Open questions surfaced this iter
- (none — workstream brief was unambiguous after consulting architecture/implementation-plan; duck-flagged `ReceiveAsync` discrepancy between arch.md and impl-plan.md was resolved by the workstream brief which explicitly specifies `Task<MessengerEvent>`)

## What's still left
- Stage 1.2 is feature-complete: build green, 100 tests pass (82 Abstractions + 18 Core).
- Next stages (handled by other workstreams) will: register stubs in DI (Stage 2.1), implement `ChannelInboundEventPublisher` (Stage 2.1), wire `TeamsMessengerConnector` (Stage 2.3), provide real `CommandDispatcher` (Stage 3.2), real `SqlAgentQuestionStore` (Stage 3.3), real `EntraIdentityResolver`/`RbacAuthorizationService` (Stage 5.1), real `SqlMessageOutbox` (Stage 6.1).
