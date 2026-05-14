# Iter notes — Stage 1.2 (Messenger Connector Interface) — review iter

## Files touched this iter
- (none — surface inherited from prior iters was reviewed and judged complete)
- `.forge/iter-notes.md` — refreshed for this turn (Forge archives prior content automatically)

## State found at iter start
- HEAD: `cbc70a4 test(stage-1.2): cover NoOpMessageOutbox cancellation and dead-letter logging`
- Worktree has 5 projects on disk: Abstractions, Core, Persistence, +tests. The sln only references the 4 Stage 1.2 projects (Abstractions + Core + their tests) — Persistence files were brought in by the `a344005` merge of `feature/teams` and are intentionally NOT in the sln since they belong to Stage 1.3's separate workstream.
- `dotnet build AgentSwarm.Messaging.sln` → 0 warn / 0 err.
- `dotnet test AgentSwarm.Messaging.sln --no-build` → 103 / 103 pass (82 Abstractions + 21 Core).

## Decisions made this iter
- Rejected the rubber-duck's one finding (have `DefaultDenyAuthorizationService` map canonical commands → `RequiredRole`). Rationale: the brief literally says "minimal deny-all policy"; role-to-command mapping is explicitly Stage 5.1's `RbacAuthorizationService` territory (impl-plan.md §5.1, line 324). Implementing that here would (a) duplicate Stage 5.1 logic in a stub, (b) silently ship a fake RBAC matrix that Stage 2.2 tests might trust, and (c) drift from the literal brief wording. The current stub's `RequiredRole = null` correctly signals "stub has no policy opinion".

## Verified — Stage 1.2's 10 implementation steps & 2 test scenarios
- ✅ `IMessengerConnector` exposes exactly `SendMessageAsync`, `SendQuestionAsync`, `ReceiveAsync` returning `Task<MessengerEvent>` (asserted by `IMessengerConnectorContractTests`).
- ✅ `AgentSwarm.Messaging.Core` targets net8.0; registered in `.sln` with GUID `355D2A37-…`.
- ✅ `IMessageOutbox` has all 4 methods (`EnqueueAsync`/`DequeueAsync`/`AcknowledgeAsync`/`DeadLetterAsync`); `NoOpMessageOutbox` logs dead-letter via `ILogger.LogWarning` and discards.
- ✅ `ICommandDispatcher.DispatchAsync(CommandContext, CancellationToken)` defined in Abstractions.
- ✅ `IInboundEventPublisher.PublishAsync(MessengerEvent, CancellationToken)` defined in Abstractions.
- ✅ `IIdentityResolver.ResolveAsync(string, CancellationToken)` + `IUserAuthorizationService.AuthorizeAsync(string,string,string, CancellationToken)`.
- ✅ `DefaultDenyIdentityResolver` returns `null`; `DefaultDenyAuthorizationService` returns `IsAuthorized=false`. Both honor cancellation.
- ✅ `IAgentQuestionStore` has all 7 methods including `TryUpdateStatusAsync` returning `Task<bool>` (asserted by `Stage12ContractShapeTests.IAgentQuestionStore_DeclaresAllSevenRequiredMethods`).
- ✅ `ConnectorOptions` has `RetryCount=3`, `RetryDelayMs=1000`, `MaxConcurrency=1`, `DeadLetterThreshold=5` (defaults asserted by `ConnectorOptionsDefaultsTests`).
- ✅ Test scenarios: contract completeness ✓; options defaults ✓.

## Dead ends tried this iter
- (none — review-only iter)

## Open questions surfaced this iter
- None new. (No `## LATEST evaluator feedback` section in this iter's prompt, so no specific items to chase.)

## What's still left
- Nothing actionable for Stage 1.2 in this worktree. The next stages (handled by other workstreams) will: register stubs in DI (Stage 2.1), implement `ChannelInboundEventPublisher` (Stage 2.1), wire `TeamsMessengerConnector` (Stage 2.3), real `CommandDispatcher` (Stage 3.2), real `SqlAgentQuestionStore` (Stage 3.3), real `EntraIdentityResolver`/`RbacAuthorizationService` (Stage 5.1), real `SqlMessageOutbox` (Stage 6.1).
