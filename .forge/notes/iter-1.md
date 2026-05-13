# Iter notes — Stage 1.3 Connector Interface and Service Contracts

## Files touched this iter
- `src/AgentSwarm.Messaging.Abstractions/`: created `IMessengerConnector`, `ICommandRouter`, `ICommandParser`+`ParsedCommand`, `CommandResult`, `ICallbackHandler`, `IDeduplicationService`, `IPendingQuestionStore`+`PendingQuestion`+`PendingQuestionStatus`, `IUserAuthorizationService`+`AuthorizationResult`+`AuthorizedOperator`, `IAuditLogger`+`AuditEntry`, `ITelegramUpdatePipeline`+`PipelineResult`, `ISwarmCommandBus`+`SwarmCommand`+`SwarmStatusQuery`+`SwarmAgentsQuery`+`SwarmStatusSummary`+`AgentInfo`+`SwarmEvent`+subtypes, `OperatorBinding`+`ChatType`.
- `src/AgentSwarm.Messaging.Core/`: created `IOperatorRegistry`, `OperatorRegistration`, `ITaskOversightRepository`.
- `tests/AgentSwarm.Messaging.Tests/ConnectorContractTests.cs`: 21 new mockability + DTO-default tests; all 34 tests pass.

## Decisions made this iter
- **Placed `OperatorBinding` + `ChatType` in `Abstractions` (deviates from spec "in Core")**. Reason: `AuthorizationResult.Bindings : IReadOnlyList<OperatorBinding>` lives in Abstractions per spec; Abstractions cannot reference Core (Core→Abstractions is the existing dep direction). Rubber-duck agreed this is the right resolution. Documented in `OperatorBinding.cs` XML doc.
- **Made `SwarmCommand.OperatorId` a `Guid`** (plan didn't pin the type). Aligns with `AuthorizedOperator.OperatorId : Guid` resolved by the inbound pipeline before publishing.
- `SwarmCommandType` modeled as a constants class (not enum) so the orchestrator can evolve the vocabulary.
- Added `SwarmStatusSummary` and `AgentInfo` records (plan omits them; architecture §4.6 names them as return types of QueryStatusAsync / QueryAgentsAsync).
- `PendingQuestionStatus` enum (Pending / AwaitingComment / Answered / TimedOut) per architecture §3.1 and §4.7.

## Dead ends tried this iter
- None.

## Open questions surfaced this iter
- Plan/architecture say `OperatorBinding` lives in `Core`. Code now puts it in `Abstractions`. Suggest the spec be updated; otherwise a future stage may "fix" it back and reintroduce the layering loop.
- Target-files block lists `appsettings.json`, `Program.cs`, `docker-compose.yml`, and an `AgentSwarm.Messaging.IntegrationTests.csproj`. None of these belong to Stage 1.3's contract-only scope (they belong to 2.x/2.4/6.x). Did NOT scaffold them.

## What's still left for next iter
- Stage 1.4: `IMessageSender` (+ `SendResult`) in Core; `IAlertService` + `IOutboundQueue` in Abstractions.
- Stage 2.x will provide stubs/no-op implementations in the Telegram project for `ICommandParser`, `ICommandRouter`, `ICallbackHandler`, `IDeduplicationService`, `IPendingQuestionStore` (per plan line 135).
