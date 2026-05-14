# Iter notes — Stage 2.2 (Teams Activity Handler)

## Files touched this iter
- `src/AgentSwarm.Messaging.Teams/AgentSwarm.Messaging.Teams.csproj` — NEW. net8.0; PackageRef `Microsoft.Bot.Builder 4.22.7` + `Microsoft.Extensions.Logging.Abstractions 8.0.0`; ProjectRefs Abstractions + Persistence; `TreatWarningsAsErrors=true`, doc gen on, `NoWarn=CS1591`.
- `src/AgentSwarm.Messaging.Teams/TeamsConversationReference.cs` — NEW. Sealed record per architecture §3.2.
- `src/AgentSwarm.Messaging.Teams/IConversationReferenceStore.cs` — NEW. Full 13-method interface (Stage 2.1 canonical home; moved into this stage to unblock compile since 2.1 hasn't landed yet).
- `src/AgentSwarm.Messaging.Teams/ICardActionHandler.cs` — NEW. `HandleAsync(ITurnContext, CT) → Task<AdaptiveCardInvokeResponse>`. Lives here because returns Bot.Builder type.
- `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs` — NEW (~29 KB). 9-arg ctor, `OnTurnAsync`, `OnMessageActivityAsync`, `OnTeamsMembersAddedAsync`, `OnTeamsMembersRemovedAsync`, `OnInstallationUpdateActivityAsync`, `OnAdaptiveCardInvokeAsync`, helpers (mention strip, verb extract, tenant/channel/corr-id pull, audit, persist).
- `AgentSwarm.Messaging.sln` — added Teams project + Teams.Tests project.
- `tests/AgentSwarm.Messaging.Teams.Tests/*` — NEW project (csproj + 5 files): `TestDoubles.cs` (hand-rolled spies, no Moq, matches existing style), `HandlerFactory.cs` (harness builder + activity factories + `InertBotAdapter`), `TeamsSwarmActivityHandlerTests.cs` (12 scenario tests), `CorrelationIdPropagationTests.cs` (3 tests), `TeamsSwarmActivityHandlerConstructorTests.cs` (9 null-arg tests). 24 tests total, all passing.

## Decisions made this iter
- **Scaffold Stage 2.1 surface inline** (Teams project, TeamsConversationReference, IConversationReferenceStore, ICardActionHandler). Stage 2.1 hasn't landed; without these the handler can't compile. When 2.1 lands it should be additive (it'll register the DI bindings + add the HTTP host).
- **Cast `IActivity` → `Activity` via `as Activity`**. `ITurnContext<T>.Activity` returns the interface type, but `RemoveRecipientMention()`, `GetConversationReference()`, and `GetChannelData<TeamsChannelData>()` are extension methods on the concrete `Activity` class. `as` (not hard cast) tolerates exotic test doubles.
- **Two-tier auth** kept strict: install events get tenant-only (already enforced by `TenantValidationMiddleware` from Stage 2.1); command events get tenant + identity + RBAC. Rejection ORDER in `OnMessageActivityAsync`: strip mention → identity (null ⇒ `UnmappedUserRejected` audit + access-denied reply, NO conv-ref save) → verb extract → authorize (false ⇒ `InsufficientRoleRejected` audit, NO conv-ref save) → save conv-ref → dispatch.
- **`@mention` stripping SOLE site**: only `OnMessageActivityAsync` calls `Activity.RemoveRecipientMention()`. `CommandContext.NormalizedText` is the cleaned text — `CommandDispatcher` (Stage 3.2) does not re-strip.
- **CorrelationId**: read from `Activity.Properties["correlationId"]` (with `CorrelationId` / `correlation_id` fallbacks) or new GUID. Stamped on `TurnState[CorrelationIdTurnStateKey]` so downstream middleware/handlers see the same value.
- **Install audit uses `AuditEventTypes.CommandReceived`** (no `InstallEvent` value in the canonical vocab) with descriptive `Action` strings (`AppInstalled`, `AppUninstalled`, `BotAddedToTeam`, …). Rejections use `AuditEventTypes.SecurityRejection` per persistence vocab.
- **Verb extraction helper** (lightweight pre-parse) only used to populate `command` for `AuthorizeAsync`. Full parse stays in `CommandDispatcher` (Stage 3.2). Matches the seven canonical verbs.
- **Tests use `InertBotAdapter`** (custom `BotAdapter` that doesn't mutate the inbound activity). The in-box `TestAdapter` *unconditionally* overwrites `Recipient`, `Conversation`, `ServiceUrl` on the activity — which clobbered the AAD/Bot IDs needed for mention stripping and broke the assertions on ConversationId. Rolling our own avoids that.

## Dead ends tried this iter
- First attempt at tests used `TestAdapter` from `Microsoft.Bot.Builder.Adapters` — 6 tests failed because `TestAdapter.ProcessActivityAsync` overwrites `activity.Recipient = _conversation.Bot` (and Conversation, ServiceUrl) regardless of what the caller supplied. Replaced with a 30-line `InertBotAdapter` that just runs the pipeline.
- Initial helper signatures took `Activity?` but call sites passed `IMessageActivity` / `IConversationUpdateActivity` (8 errors). Fixed by `var activity = turnContext.Activity as Activity;` at the top of each override.
- Tried `<see cref="Activity.RemoveRecipientMention"/>` in XML doc — failed `CS1574` under TreatWarningsAsErrors because the method is on an extension class. Switched to plain `<c>` ref.

## Open questions surfaced this iter
- `Persistence` and `Core` projects exist on disk but aren't in `AgentSwarm.Messaging.sln`. I only added my new projects (Teams + Teams.Tests). Whoever owns Stage 1.3 should sln-add Persistence/Core when their workstream lands.
- Some 13 methods on `IConversationReferenceStore` are technically Stage 2.1's; this stage uses only `SaveOrUpdateAsync`, `MarkInactiveAsync`, `MarkInactiveByChannelAsync`. The other 10 are defined so Stage 2.1 doesn't have to re-define them on top of mine.

## What's still left
- Nothing for Stage 2.2. Build clean (0/0), 106 tests pass (82 abstractions + 24 Teams).
- Stage 2.1 will: register `TeamsSwarmActivityHandler` + 9 dependencies in DI, scaffold the ASP.NET Core host + `CloudAdapter`, register `TenantValidationMiddleware`, register `ChannelInboundEventPublisher`, register default-deny stubs for identity/RBAC/card-handler/agent-question-store, register `NoOpAuditLogger` from Persistence.
- Stage 3.2 will replace the stub `ICommandDispatcher` with the real `CommandDispatcher` that parses verbs out of `CommandContext.NormalizedText`.
