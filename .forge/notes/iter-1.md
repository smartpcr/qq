# Iter notes — Stage 2.3 (Teams Messenger Connector) — iter 5

## What this iter delivered (Stage 2.3 from scratch)
First iter on Stage 2.3 — prior iters worked Stage 2.2. Stage 2.1
(`TeamsMessagingOptions`, `ICardStateStore`, `TeamsCardState`,
`ChannelInboundEventPublisher`) had not landed; required for 2.3 to compile so
the **interface + canonical record types** are created here. DI stubs
(`NoOpCardStateStore`, `Program.cs`, middleware) remain Stage 2.1 work.

## Files touched this iter
**Production (new):**
- `src/AgentSwarm.Messaging.Abstractions/IInboundEventReader.cs` — read-only
  abstraction (`Task<MessengerEvent> ReceiveAsync(CancellationToken)`); lets
  connector depend on a small read interface instead of the publisher contract.
- `src/AgentSwarm.Messaging.Teams/TeamsMessagingOptions.cs` — Stage 2.1 config
  (impl-plan §2.1 step 5); setting `MaxRetryAttempts` recomputes base
  `RetryCount = MaxRetryAttempts - 1` (5 total → 4 retries — fixes off-by-one
  rubber-duck flagged).
- `src/AgentSwarm.Messaging.Teams/TeamsCardStatuses.cs` — `Pending`/`Answered`/
  `Expired`/`Deleted` constants + `IsValid`.
- `src/AgentSwarm.Messaging.Teams/TeamsCardState.cs` — fields per impl-plan §2.1
  step 3 (`QuestionId` is natural PK; no surrogate `Id` — rubber-duck guidance).
- `src/AgentSwarm.Messaging.Teams/ICardStateStore.cs` — three methods per
  architecture §4.3 (`SaveAsync`, `GetByQuestionIdAsync`, `UpdateStatusAsync`).
- `src/AgentSwarm.Messaging.Teams/ChannelInboundEventPublisher.cs` — `Channel<>`-
  backed; satisfies BOTH `IInboundEventPublisher` (write) and
  `IInboundEventReader` (read).
- `src/AgentSwarm.Messaging.Teams/TeamsMessengerConnector.cs` — main 2.3
  deliverable. Ctor takes 7 deps (CloudAdapter, options, conv-ref store,
  question store, card-state store, inbound reader, logger). `SendQuestionAsync`
  implements §2.3 three-step pattern: (1) `SaveAsync` with null ConversationId,
  (2) resolve ref by `TargetUserId` via `GetByInternalUserIdAsync` or
  `TargetChannelId` via `GetByChannelIdAsync` then `ContinueConversationAsync`,
  (3) `UpdateConversationIdAsync` + `ICardStateStore.SaveAsync` with the
  activity ID and a **fresh** `ConversationReference` captured from the proactive
  turn context (saving the stored ReferenceJson would give Stage 3.3 stale
  rehydration data — rubber-duck blocker).
- `src/AgentSwarm.Messaging.Teams/TeamsServiceCollectionExtensions.cs` — DI
  helpers: `AddInProcessInboundEventChannel` (one publisher singleton under both
  interfaces) + `AddTeamsMessengerConnector` (.NET 8 keyed
  `IMessengerConnector` under `"teams"`, same instance as
  `TeamsMessengerConnector` singleton).

**Production (modified):**
- `src/AgentSwarm.Messaging.Teams/AgentSwarm.Messaging.Teams.csproj` — added
  `Microsoft.Bot.Builder.Integration.AspNet.Core` 4.22.7 (CloudAdapter, per
  impl-plan §2.1 line 74) + `Microsoft.Extensions.DependencyInjection.Abstractions`
  8.0.0 (keyed services).
- `src/AgentSwarm.Messaging.Teams/IConversationReferenceStore.cs` — added
  `GetByConversationIdAsync(string conversationId, CancellationToken)`. Necessary
  because `MessengerMessage` (Stage 1.1) carries no `TenantId`, so the BF
  `ConversationId` is the only routing key reachable from `SendMessageAsync`.
  Documented as Stage 1 contract deficiency (not normal store growth).

**Tests (new):**
- `tests/AgentSwarm.Messaging.Teams.Tests/ChannelInboundEventPublisherTests.cs` —
  7 tests: round-trip, await-publish ordering, cancellation, null-event guard,
  null-channel guard, concurrent publishers, bounded backpressure.
- `tests/AgentSwarm.Messaging.Teams.Tests/TeamsMessengerConnectorTests.cs` — 11
  tests covering all 3 brief scenarios (SendMessage happy + missing ref,
  ReceiveAsync wired through real publisher) plus SendQuestion happy
  (asserts question saved, send invoked, ConversationId update, card-state row
  with valid `ConversationReferenceJson` that round-trips through Newtonsoft to
  `ConversationReference`), channel-target via `GetByChannelIdAsync`, missing
  ref still saves question but skips card state, invalid question throws
  pre-persist, null-arg guards, full ctor null-arg matrix.
- `tests/AgentSwarm.Messaging.Teams.Tests/TeamsServiceCollectionExtensionsTests.cs`
  — 5 tests: same-instance binding for publisher/reader, keyed resolution.

**Tests (modified):**
- `tests/AgentSwarm.Messaging.Teams.Tests/TestDoubles.cs` — added
  `PreloadByConversationId` map + `ConversationLookups` recorder; implemented
  the new `GetByConversationIdAsync` method on
  `RecordingConversationReferenceStore` so the existing 35 Stage 2.2 tests stay
  green.

## Decisions made this iter
- **`CloudAdapter` (concrete) not `BotAdapter` (abstract)** — rubber-duck
  blocker. Pulling in `Microsoft.Bot.Builder.Integration.AspNet.Core` matches
  impl-plan §2.1 line 74 exactly. `CloudAdapter`'s
  `ContinueConversationAsync(string, ConversationReference, BotCallbackHandler,
  CancellationToken)` is virtual → test adapter subclasses + overrides.
- **`MaxRetryAttempts` → `RetryCount = value - 1`** — base `ConnectorOptions`
  defines RetryCount as "additional attempts after first" (total = RetryCount+1).
  5 total Teams attempts → `RetryCount = 4`.
- **Fresh `ConversationReference` captured from proactive turn context** for
  `TeamsCardState.ConversationReferenceJson` (via
  `turnContext.Activity.GetConversationReference()`) — saving the stored
  `ReferenceJson` would cache stale `serviceUrl` / conversation thread for the
  Stage 3.3 update/delete path. Falls back to stored ref's JSON only if BF
  callback doesn't yield one.
- **`IInboundEventReader` instead of injecting concrete publisher** — keeps
  connector decoupled, prevents accidental publish-from-connector, gives tests
  a tiny stub surface. Same singleton implements both interfaces in DI.
- **Single `TeamsMessengerConnector` singleton, exposed as keyed
  `IMessengerConnector("teams")` via factory delegate** — rubber-duck guidance;
  Stage 3.3 will layer `ITeamsCardManager` onto the same instance.
- **`GetByConversationIdAsync` added to `IConversationReferenceStore`** — XML
  doc explicitly notes this compensates for the `MessengerMessage` model gap
  (no `TenantId` field) so a future Stage 1 revision (add tenant to
  `MessengerMessage` or destination URI parser) can retire it.
- **Plain-text question summary** (`{Title}\n\n{Body}\n\nReply with: {actions}`)
  — Adaptive Card lands in Stage 3.1; brief explicitly says "render the question
  as a simple text summary" until then. Action verbs are lowercased to match the
  canonical command vocabulary.

## Dead ends tried this iter
- `<see cref="RetryCount"/>` xmldoc in `TeamsMessagingOptions` failed CS1574
  (inherited base members aren't directly resolvable at that scope) — switched
  to fully qualified `<see cref="ConnectorOptions.RetryCount"/>`.
- `<see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection)"/>`
  also failed CS1574 (DI.Abstractions XML docs not resolvable in our context) —
  rewrote as plain prose.
- First `ParsedCommand` test stub used a 2-arg ctor — actual record has three
  positional members (`CommandType, Payload, CorrelationId`).

## Open questions surfaced this iter
- None blocking. Long-term: should `MessengerMessage` carry `TenantId` + a
  routing-target hint so `IConversationReferenceStore.GetByConversationIdAsync`
  can be retired? Belongs to a Stage 1 revision workstream, not 2.3.

## What's still left
- Nothing for Stage 2.3 itself. Solution build clean (0 warn / 0 err); tests:
  82 Abstractions + 58 Teams = 140 pass on the .sln (was 117 — +23 for 2.3).
- Pre-existing failures NOT caused by this iter: 7 failures in
  `AgentSwarm.Messaging.Teams.Manifest.Tests` (manifest.json maxLength + missing
  `scripts/package-teams-app.ps1`). Stage 2.4 scope; not in the .sln so the
  `dotnet test AgentSwarm.Messaging.sln` gate is green.
- Stage 2.1 still pending: `Program.cs`, middleware classes, in-memory store
  registrations, `NoOpCardStateStore`/`NoOpCardActionHandler`/`NoOpCardManager`.
  Stage 3.x dispatch + card handler + Stage 4.x proactive worker also pending.
