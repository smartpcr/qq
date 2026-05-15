# Iter notes — Stage 4.1 (Conversation Reference Store Implementation) — iter 1 (this workstream's first iter)

## What landed this iter

First substantive iter on Stage 4.1. Prior iter notes in the prompt
preamble are from the Stage 2.3 workstream (Teams Messenger Connector)
and document a separate, completed branch — they do NOT apply to this
workstream. Treat them as historical context only.

## Files touched this iter

- `src/AgentSwarm.Messaging.Teams/IConversationReferenceStore.cs` —
  changed `GetAsync(string referenceId, ct)` → `GetAsync(string tenantId,
  string aadObjectId, ct)` per Stage 4.1 brief and test scenarios.
  Updated XML docs to document the active-status-agnostic contract.
- `tests/AgentSwarm.Messaging.Teams.Tests/TestDoubles.cs`,
  `tests/AgentSwarm.Messaging.Teams.Tests/TeamsMessengerConnectorTests.cs`,
  `tests/AgentSwarm.Messaging.Teams.Tests/TeamsServiceCollectionExtensionsTests.cs` —
  updated 4 test-double `GetAsync` signatures to match the new contract.
- `src/AgentSwarm.Messaging.Teams.EntityFrameworkCore/` (NEW project) —
  csproj + `ConversationReferenceEntity`, `ConversationReferenceDeactivationReasons`,
  `TeamsConversationReferenceDbContext` (provider-agnostic, 5 indexes
  declared inc. 4 filtered), `TeamsConversationReferenceDbContextDesignTimeFactory`,
  `SqlConversationReferenceStore` (implements both `IConversationReferenceStore`
  AND `IConversationReferenceRouter`), `EntityFrameworkCoreServiceCollectionExtensions`
  (`AddSqlConversationReferenceStore` helper that wires both interfaces from one
  singleton + adds `IDbContextFactory<>`).
- `src/AgentSwarm.Messaging.Teams.EntityFrameworkCore/Migrations/20260515051028_InitialCreate.cs`
  + `Designer.cs` + `TeamsConversationReferenceDbContextModelSnapshot.cs` — generated
  via `dotnet ef migrations add InitialCreate` against the SqlServer provider.
- `tests/AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests/` (NEW project) —
  csproj + `StoreFixture` (SQLite in-memory + IDbContextFactory<>),
  `FakeTimeProvider`, `TeamsConversationReferenceFactory`,
  `SqlConversationReferenceStoreTests` (22 facts covering all 16 brief scenarios
  plus 6 edge cases: re-activate after uninstall, preserve resolved InternalUserId,
  team-channel enumeration, active-only filter on getter, router contract,
  invalid-key reject, no-op on missing row),
  `EntityFrameworkCoreServiceCollectionExtensionsTests` (DI shape + idempotency).
- `AgentSwarm.Messaging.sln` — added the two new projects.
- `.config/dotnet-tools.json` — local manifest pinning dotnet-ef 8.0.10
  (matches EF Core 8.0.10 runtime); makes migration regeneration reproducible.

## Decisions made this iter

- **Changed `IConversationReferenceStore.GetAsync` signature.** Stage 2.1 declared
  `GetAsync(string referenceId, ct)` (by GUID PK, matches `architecture.md` §4.2);
  Stage 4.1 brief explicitly requires `GetAsync(string tenantId, string aadObjectId, ct)`
  with the `IsActive`-agnostic semantics needed by the test scenario "GetAsync returns
  reference regardless of active status". No production callers used the old single-arg
  form — only 4 test doubles. Brief is the more recent and explicit signal; honored it.
  Architecture.md is now slightly stale on this point (out of scope to amend here).
- **New project `AgentSwarm.Messaging.Teams.EntityFrameworkCore`** rather than putting
  EF code in `AgentSwarm.Messaging.Persistence` (where `architecture.md` §7 maps it).
  The architecture mapping conflicts with the dependency direction: `Persistence`
  doesn't reference `Teams`, but `IConversationReferenceStore` lives in `Teams`.
  Inverting the dependency would couple Persistence to a connector. Adjacent
  `*.EntityFrameworkCore` package follows .NET community convention
  (`Microsoft.Extensions.Caching.SqlServer` etc.).
- **`IDbContextFactory<T>` instead of injected `DbContext`** — singletons can't share a
  `DbContext` (not thread-safe) and the connector is registered as a singleton.
  Factory pattern is the canonical EF Core fix and matches `AddDbContextFactory`.
- **`TimeProvider` injection** — deterministic timestamps in `MarkInactive*` tests
  without `Thread.Sleep` or stopwatch hacks.
- **Filtered-index `HasFilter` strings use double-quoted column names** —
  portable across SQL Server (with `QUOTED_IDENTIFIER ON`, the default) and SQLite.
- **`SqlServer` package marked `<PrivateAssets>all</PrivateAssets>`** — consumers of
  the EFCore project don't get SQL Server transitively; design-time factory
  still compiles against it.
- **Preserved-InternalUserId branch in `SaveOrUpdateAsync`** — when an inbound
  reference omits `InternalUserId` (e.g., second message arrives before the
  identity resolver writes back), the upsert MUST NOT clobber a previously-resolved
  value. Documented inline; covered by `SaveOrUpdate_PreservesPreviouslyResolvedInternalUserId`.
- **`SqlConversationReferenceStore` also implements `IConversationReferenceRouter`** —
  satisfies the auto-wire cast in `TeamsServiceCollectionExtensions.AddTeamsMessengerConnector`,
  matching the contract in `IConversationReferenceRouter.cs:27-30` ("the canonical
  store implementations SHOULD implement BOTH interfaces").

## Dead ends tried this iter

- Initial csproj used `Microsoft.Extensions.DependencyInjection.Abstractions 8.0.0`;
  EF Core 8.0.10 transitively pulled 8.0.2, NU1605 downgrade error with
  `TreatWarningsAsErrors`. Bumped explicit ref to 8.0.2.
- Initial DbContext XML doc used `<see cref="HasFilter"/>` — CS1574 because
  the symbol isn't directly in scope at the comment site. Switched to `<c>HasFilter</c>`.

## Open questions surfaced this iter

- None requiring operator input. The interface signature change is documented
  above and is the correct interpretation of the workstream brief.

## What's still left

- Nothing for Stage 4.1. Build clean (0 warnings / 0 errors), 178 tests pass
  solution-wide (82 Abstractions + 73 Teams + 23 EntityFrameworkCore = 178; the
  23 in the new EFCore test assembly cover every Stage 4.1 acceptance scenario).
- Downstream Stage 4.2 (`TeamsProactiveNotifier`) and Stage 5.1
  (`InstallationStateGate`) can now resolve `IConversationReferenceStore` from
  the SQL backing store via `AddSqlConversationReferenceStore`.
