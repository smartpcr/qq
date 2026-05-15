# Iter notes — Stage 3.1 (Adaptive Card Templates) — iter 1

This is the first iter of a fresh workstream branched from `feature/teams`
(post Stage 2.3). All 7 implementation steps from `implementation-plan.md`
§3.1 are addressed. No prior evaluator feedback to triage.

## Files touched this iter

### New (production)
- `src/AgentSwarm.Messaging.Teams/Cards/IAdaptiveCardRenderer.cs` — contract per architecture §4.6 (5 Render* → Attachment).
- `src/AgentSwarm.Messaging.Teams/Cards/AdaptiveCardBuilder.cs` — concrete renderer (AdaptiveCards 3.1, schema 1.5 pinned per tech-spec §5.1 R-3).
- `src/AgentSwarm.Messaging.Teams/Cards/AgentStatusSummary.cs`, `IncidentSummary.cs`, `ReleaseGateRequest.cs` — payload records (architecture §3.3) extended with `QuestionId` for per-recipient round-trip correlation per §6.3.1.
- `src/AgentSwarm.Messaging.Teams/Cards/CardActionPayload.cs` — typed Action.Submit data view + `CardActionDataKeys` constants (`questionId`, `actionId`, `actionValue`, `correlationId`, `comment`).
- `src/AgentSwarm.Messaging.Teams/Cards/CardActionMapper.cs` — `Map` produces `HumanDecisionEvent`; `ReadPayload` exposes the typed payload (incl. `ActionId`) for Stage 3.3.

### New (tests)
- `tests/.../AdaptiveCardBuilderTests.cs` — 16 tests (3 explicit plan scenarios + every Render* + schema-version pin + shared-input invariant + null guards).
- `tests/.../CardActionMapperTests.cs` — 13 tests (round-trip + every required-key error path + dictionary input shape + render-then-map e2e + ActionId presence assertion).

### Modified
- `src/AgentSwarm.Messaging.Teams/AgentSwarm.Messaging.Teams.csproj` — added `<PackageReference Include="AdaptiveCards" Version="3.1.0" />` per tech-spec.
- `src/AgentSwarm.Messaging.Teams/TeamsMessengerConnector.cs` — injected `IAdaptiveCardRenderer` (ctor now 9 params); `SendQuestionAsync` builds `MessageFactory.Attachment(_cardRenderer.RenderQuestionCard(question))` with `Activity.Text = question.Title` as notification-banner fallback. Removed text-summary helper. This satisfies acceptance criteria for "proactive Adaptive Card questions" and §3.1 step 7.
- `src/AgentSwarm.Messaging.Teams/TeamsServiceCollectionExtensions.cs` — `TryAddSingleton<IAdaptiveCardRenderer, AdaptiveCardBuilder>()` honoring idempotency contract from prior iters.
- `tests/.../TeamsMessengerConnectorTests.cs` — happy-path now asserts attachment shape (ContentType + JObject); ConnectorHarness wires renderer; Constructor_NullDependencies test now has 9 throw assertions.
- `tests/.../TeamsServiceCollectionExtensionsTests.cs` — idempotency counts new descriptor; +2 tests for renderer registration + explicit-pre-registration honor.

## Decisions made this iter

- **Architecture-aligned `ActionId` in payload (rubber-duck finding).** Architecture §2.10 line 181 explicitly says cards embed `QuestionId, ActionId, CorrelationId`. Initial draft only carried `ActionValue`. Fixed by adding `CardActionDataKeys.ActionId`, propagating through builder + payload + mapper. `Map()` enforces presence at gateway boundary even though `HumanDecisionEvent` itself only carries `ActionValue` — Stage 3.3's `CardActionHandler` will use `ActionId` for unambiguous button resolution.
- **Per-approver `QuestionId` on `ReleaseGateRequest` and `IncidentSummary` (rubber-duck finding).** Initial draft used `GateId`/`IncidentId` as the synthetic questionId, but per architecture §6.3.1 each approver / acknowledger gets their own `AgentQuestion` record. Added `QuestionId` field to both records so the orchestrator passes the per-recipient question ID to render. Cards still surface `GateId`/`IncidentId` as metadata in the body fact set. This is a minor extension to the §3.3 table that's necessary for §6.3 step-4 correctness.
- **Single shared `Input.Text` (not per-action).** Bot Framework merges the typed value into every action's Data dict on submit, so a single shared input is the correct design. Mapper normalises empty/whitespace to `null` on `HumanDecisionEvent.Comment`. Comment label is now context-aware: "Comment (required)" when all actions need it, otherwise "Comment (required for: <names>)" — addresses rubber-duck UX nit.
- **`Activity.Text = question.Title`** kept as notification-banner / accessibility fallback (mobile lock screens, low-bandwidth clients). Drops the previous multiline body+actions summary because the canonical card now carries that information; rubber-duck confirmed not a regression.
- **Schema version 1.5 pinned** as static `AdaptiveCardBuilder.SchemaVersion` and verified by an explicit test (`RenderQuestionCard_PinsAdaptiveCardSchemaVersion_To_1_5`).

## Dead ends tried this iter

- Initially had `BuildSubmitAction` carry only `actionValue`; rubber-duck flagged the missing `ActionId` against architecture §2.10. Backtracked and threaded `actionId` through every Render* call site (`HumanAction.ActionId` for question buttons; `gate.<verb>` / `incident.<verb>` synthetic IDs for the 5 fixed-template buttons).

## Open questions surfaced this iter

- None this iter. The reflection-on-NuGet API surface, the architecture §2.10/§4.6 contract, and the §6.3.1 multi-approver model all aligned cleanly after the rubber-duck pass.

## What's still left

- Nothing for Stage 3.1. Build clean (0 warnings / 0 errors, `TreatWarningsAsErrors=true`), 188 tests pass solution-wide (82 abstractions + 106 Teams; +33 from the iter-7 baseline of 73 — see Stage 2.3 prior notes).
- Stage 3.2 (`TeamsCommandRouter`) + Stage 3.3 (`CardActionHandler`) consume this stage's surface (`IAdaptiveCardRenderer.Render*`, `CardActionMapper.Map` / `ReadPayload`, `CardActionPayload.ActionId`) — handled by their own workstreams.
- Stage 4.2 (`TeamsProactiveNotifier`) also depends on `IAdaptiveCardRenderer` — wiring is part of that stage per the Phase 4 dependency chain.
