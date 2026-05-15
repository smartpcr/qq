# Iter notes — Stage 2.3 (Teams Messenger Connector) — iter 2

## Prior feedback resolution

Iter-1 evaluator (score 85, verdict iterate) listed six items in `What still
needs work`. ALL SIX FIXED — no items deferred. Each fix below is structural
(new code, new contracts, new tests) rather than a word-tweak; verification
greps follow.

- [x] 1. FIXED — `src/AgentSwarm.Messaging.Teams/TeamsMessengerConnector.cs:172`.
  `SendQuestionAsync` now persists a sanitized copy
  (`var sanitizedQuestion = question with { ConversationId = null };`) instead
  of the caller-supplied `AgentQuestion` so a stale/non-null `ConversationId`
  on input cannot poison `IAgentQuestionStore.SaveAsync`. The actual proactive
  ConversationId is later stamped via `UpdateConversationIdAsync` in step 3.
  New regression test
  `SendQuestionAsync_QuestionWithStaleConversationId_PersistsSanitizedCopyWithNullConversationId`
  in `tests/AgentSwarm.Messaging.Teams.Tests/TeamsMessengerConnectorTests.cs`
  feeds in `ConversationId = "19:STALE-DO-NOT-PERSIST"` and asserts
  `Saved[0].ConversationId is null`.

- [x] 2. FIXED — `src/AgentSwarm.Messaging.Teams/TeamsMessengerConnector.cs:238-245`.
  Replaced the previous `LogWarning + return` path with a hard
  `throw new InvalidOperationException(...)` when
  `turnContext.Activity?.Conversation?.Id` is null/whitespace. The throw runs
  BEFORE either `UpdateConversationIdAsync` or `CardStateStore.SaveAsync` so
  partial persistence is impossible (the dangerous failure mode the evaluator
  flagged: card delivered + question saved without ConversationId stamped =
  bare-approve/bare-reject lookup permanently broken without any operator
  signal). New regression test
  `SendQuestionAsync_MissingConversationIdInProactiveCallback_ThrowsAndSkipsAllStep3Persistence`
  uses a custom `ConversationlessCloudAdapter` that overrides
  `SynthesizeContinuationActivity` to set `Conversation = new ConversationAccount(id: null)`,
  and asserts the throw + empty `ConversationIdUpdates` + empty
  `CardStateStore.Saved`.

- [x] 3. FIXED — `src/AgentSwarm.Messaging.Teams/TeamsMessengerConnector.cs:247-254`.
  Same pattern as item 2: replaced `LogWarning + return` with
  `throw new InvalidOperationException(...)` when
  `resourceResponse?.Id` (the Bot Framework activity ID) is missing. Also
  before either persistence call. New regression test
  `SendQuestionAsync_MissingActivityIdInResourceResponse_ThrowsAndSkipsAllStep3Persistence`
  sets `RecordingCloudAdapter.SendResponseFactory = _ => new ResourceResponse(id: string.Empty)`
  and asserts the throw + empty `ConversationIdUpdates` + empty
  `CardStateStore.Saved`. Both items 2 and 3 share an "all-or-nothing step 3"
  invariant that the new tests lock down independently.

- [x] 4. FIXED — `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs:260-285`
  + `tests/AgentSwarm.Messaging.Teams.Tests/TeamsMessengerConnectorTests.cs`
  (`ReceiveAsync_AfterHandlerProcessesAgentStatusActivity_ReturnsCommandEventForCanonicalVerb`).
  The Stage 2.2 handler used to silence the analyzer with
  `_ = _inboundEventPublisher;` because no producer existed yet — now the
  handler actually publishes a `CommandEvent` onto the inbound channel after
  successful `DispatchAsync`. The new end-to-end test wires a real
  `ChannelInboundEventPublisher` instance into BOTH the handler (via the new
  `HandlerFactory.Build(IInboundEventPublisher)` overload) AND the connector
  (as `IInboundEventReader`), feeds an `agent status` activity through
  `((IBot)handler).OnTurnAsync`, and asserts the connector's `ReceiveAsync`
  observes a `CommandEvent` with `Payload.CommandType == "agent status"`,
  `EventType == MessengerEventTypes.Command`, the propagated correlation ID,
  and the canonical `MessengerEventSources.PersonalChat`. No more shortcut
  publish via the recording stub. Verification:
  ```
  $ grep -rnF '_ = _inboundEventPublisher;' src/ tests/
  (empty — symbol fully removed from the codebase)
  ```

- [x] 5. FIXED — removed `GetByConversationIdAsync` from the canonical
  `IConversationReferenceStore` (it was added in iter 1 and is NOT part of the
  contract enumerated by `implementation-plan.md` line 75 / `architecture.md`
  §4.2). Created a NEW companion interface
  `src/AgentSwarm.Messaging.Teams/IConversationReferenceRouter.cs` with a
  single `GetByConversationIdAsync(string, CancellationToken)` method.
  `TeamsMessengerConnector` ctor now takes BOTH interfaces (8 ctor args, was
  7); `SendMessageAsync` resolves via the router. The companion-interface
  approach lets real stores (Stage 2.1 in-memory + Stage 4.1 SQL) implement
  both interfaces and register one singleton under both service types without
  widening the canonical contract. Stage 1 follow-up (adding `TenantId` to
  `MessengerMessage` so a natural-key lookup is possible) is documented in the
  router xmldoc as the long-term retirement path. Test doubles split:
  `RecordingConversationReferenceRouter` added to `TestDoubles.cs` (Stage 2.2
  handler suite); `ConnectorRecordingConversationReferenceStore` (inside
  `TeamsMessengerConnectorTests`) and `RecordingConversationReferenceRouter`
  cover Stage 2.3. Constructor null-arg matrix expanded from 7 to 8 cases.
  Verification:
  ```
  $ grep -nF 'GetByConversationIdAsync' src/AgentSwarm.Messaging.Teams/IConversationReferenceStore.cs
  (empty — method fully removed from the canonical store interface)
  ```

- [x] 6. FIXED — `src/AgentSwarm.Messaging.Teams/TeamsServiceCollectionExtensions.cs`.
  Added `using Microsoft.Extensions.DependencyInjection.Extensions;` and
  switched ALL FIVE registrations from `AddSingleton` / `AddKeyedSingleton`
  to `TryAddSingleton` / `TryAddKeyedSingleton`, so the documented
  idempotency claim is now actually enforced. Updated xmldoc to describe true
  idempotency. Two new regression tests added to
  `TeamsServiceCollectionExtensionsTests.cs`:
  `AddInProcessInboundEventChannel_CalledTwice_LeavesSingletonDescriptorsAtOnePerServiceType`
  and `AddTeamsMessengerConnector_CalledTwice_LeavesSingletonDescriptorsAtOnePerServiceType`,
  asserting the descriptor count for each service type (concrete +
  `IInboundEventPublisher` + `IInboundEventReader` + keyed
  `IMessengerConnector`) stays at exactly 1 across two invocations.
  `BuildServices()` was also extended to register the new
  `IConversationReferenceRouter` so the connector can resolve. Verification:
  ```
  $ grep -nF 'services.AddSingleton' src/AgentSwarm.Messaging.Teams/TeamsServiceCollectionExtensions.cs
  (empty — every registration now uses TryAdd*)
  ```

## Files touched this iter

- `src/AgentSwarm.Messaging.Teams/TeamsMessengerConnector.cs` — sanitized
  question save (item 1); fail-loud throws for missing Conversation.Id and
  Activity.Id with all-or-nothing step-3 persistence (items 2+3); ctor takes
  the new `IConversationReferenceRouter` at position 4 of 8;
  `SendMessageAsync` now resolves via the router (item 5). Class xmldoc
  rewritten to describe the three-step persistence contract verbatim from the
  §2.3 brief.
- `src/AgentSwarm.Messaging.Teams/IConversationReferenceStore.cs` — removed
  `GetByConversationIdAsync` method + xmldoc to realign with
  `implementation-plan.md` §2.1 line 75 (item 5).
- `src/AgentSwarm.Messaging.Teams/IConversationReferenceRouter.cs` (NEW) —
  narrow companion contract with `GetByConversationIdAsync`; documents that
  real stores implement both interfaces and that the contract gap will close
  when Stage 1 adds `MessengerMessage.TenantId` (item 5).
- `src/AgentSwarm.Messaging.Teams/TeamsServiceCollectionExtensions.cs` —
  switched five registrations to `TryAdd*`; updated xmldoc to claim true
  idempotency now that it's actually true (item 6).
- `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs` — removed the
  iter-1 `_ = _inboundEventPublisher;` analyzer-silencer; added a (6) Publish
  CommandEvent block after `DispatchAsync` that constructs a `CommandEvent`
  with `MessengerEventTypes.Command`, the correlation ID, the parsed verb +
  body, and `MessengerEventSources.PersonalChat` / `TeamChannel` resolved via
  a new private `ResolveEventSource(Activity?)` helper (item 4).
- `tests/AgentSwarm.Messaging.Teams.Tests/TeamsMessengerConnectorTests.cs` —
  full rewrite: 13 tests (was 8). Adds the three regression tests from items
  1, 2, and 3 plus the end-to-end `agent status` ReceiveAsync test from item
  4. Replaces the inline conversation-reference store double to drop the
  removed `GetByConversationIdAsync` method. Constructor null-arg matrix
  expanded from 7 to 8 cases.
- `tests/AgentSwarm.Messaging.Teams.Tests/TeamsServiceCollectionExtensionsTests.cs` —
  added the new `IConversationReferenceRouter` registration to `BuildServices`;
  added two idempotency-regression tests (item 6).
- `tests/AgentSwarm.Messaging.Teams.Tests/TestDoubles.cs` — removed dead
  `PreloadByConversationId` / `ConversationLookups` / `GetByConversationIdAsync`
  members from `RecordingConversationReferenceStore`; added new
  `RecordingConversationReferenceRouter` test double (item 5).
- `tests/AgentSwarm.Messaging.Teams.Tests/HandlerFactory.cs` — added the
  `Build(IInboundEventPublisher)` overload so item-4's end-to-end test can
  share a single `ChannelInboundEventPublisher` instance between handler and
  connector. The original parameterless `Build()` still returns a recording
  stub for Stage 2.2 tests that don't care about the channel.

## Decisions made this iter

- **Companion interface, not store extension (item 5).** The evaluator gave
  two options: widen the planning contract OR avoid widening the store. I
  chose the second because (a) `implementation-plan.md` §2.1 line 75 fixes
  the exact method surface, (b) widening it would force Stage 4.1's
  `SqlConversationReferenceStore` to add a non-natural-key index on
  `(ConversationId)` to make the lookup efficient, and (c) the underlying
  contract gap is in `MessengerMessage` (no `TenantId` field), which is a
  Stage 1 concern. Splitting the companion interface keeps the store
  contract honest while letting the connector compose cleanly. Real stores
  will implement both — the xmldoc on `IConversationReferenceRouter` makes
  this expectation explicit so future reviewers don't lose context.
- **Fail loudly, not silently, on missing delivery IDs (items 2+3).** The
  evaluator said "fail loudly OR be otherwise surfaced and tested". I picked
  fail-loudly because (a) the partial-persistence states the silent path
  produces (saved question without ConversationId, OR saved card without
  ActivityId) are both unrecoverable from the orchestrator's perspective —
  there's no later signal it can hook into; (b) throwing surfaces the
  failure at the seam where the orchestrator already needs to handle
  `IMessengerConnector` errors (transient Bot Framework failures, etc.); and
  (c) the all-or-nothing invariant is the simplest contract for downstream
  consumers to reason about. The trade-off is that question-step-1 has
  already persisted the row when the throw fires; the test coverage now
  asserts this explicitly so a future agent can't accidentally regress to a
  half-persisted state by adding a "rollback step 1 on failure" without
  understanding why the current shape was chosen.
- **Sanitize via `with` rather than ctor cloning (item 1).** `AgentQuestion`
  is a `sealed record` with `init`-only setters, so
  `var sanitizedQuestion = question with { ConversationId = null };` is the
  idiomatic fix and produces the same instance shape Stage 3.3's SQL store
  will encounter. Test asserts on `Saved[0].ConversationId == null` directly,
  not on identity equality, so future record-shape changes won't false-fail.
- **End-to-end ReceiveAsync test wires a real handler (item 4).** The
  evaluator's complaint was that the iter-1 test "manually publishes" rather
  than "proves processing an `agent status` activity yields a received
  command event with the expected status command shape". The new test now
  drives `((IBot)handler).OnTurnAsync` with a real `Activity` so identity
  resolution → authorization → DispatchAsync → publish all run for real, and
  asserts the `agent status` canonical-verb shape arrives at the connector
  side via the same channel instance. Required adding a one-arg
  `Build(IInboundEventPublisher)` overload to `HandlerFactory` (existing
  `Build()` is preserved for Stage 2.2 tests).

## Dead ends tried this iter

- None. Each item had a single obvious fix; the rubber-duck step from prior
  iters was internalized into the design (companion interface for item 5,
  fail-loudly with all-or-nothing semantics for items 2+3) before any code
  was written.

## Open questions surfaced this iter

- Stage 1 contract gap: `MessengerMessage` has no `TenantId` field, so
  `SendMessageAsync` can only route by Bot Framework `ConversationId` (hence
  the companion router interface). When Stage 1 adds `TenantId` (or a
  destination-URI parser), the router can be retired and the canonical store
  contract still won't need a `GetByConversationIdAsync` overload. Documented
  in the router's xmldoc as the long-term path; not blocking for Stage 2.3.

## What's still left

- Nothing for Stage 2.3 iter 2. Build clean (0 warnings / 0 errors), 145
  tests pass solution-wide (82 abstractions + 63 Teams; was 140 in iter 1,
  +5 new tests for items 1, 2, 3, 4, and 6).
- Stage 3.x command-handler dispatch + `AdaptiveCardBuilder` still pending
  downstream.
