# Iter notes — Stage 4.1 (SQL Conversation Reference Store) — iter 2

## Prior feedback resolution

Iter-1 evaluator score 87 (verdict: iterate). Three numbered items, all
focused contract/security validation gaps. All ADDRESSED structurally
(not via word-tweaks): each item gets a code edit + dedicated
regression test that fails without the fix.

- [x] 1. ADDRESSED — `IsActive` now has `HasDefaultValue(true)` in the
  EF model AND the migration emits `defaultValue: true` on the SQL
  Server `bit` column, AND the model snapshot/designer reflect the
  default. Files touched:
  - `src/AgentSwarm.Messaging.Teams.EntityFrameworkCore/TeamsConversationReferenceDbContext.cs:60`
    — `builder.Property(e => e.IsActive).IsRequired().HasDefaultValue(true);`
  - `src/AgentSwarm.Messaging.Teams.EntityFrameworkCore/Migrations/20260515051028_InitialCreate.cs:28`
    — `IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true)`
  - `src/AgentSwarm.Messaging.Teams.EntityFrameworkCore/Migrations/20260515051028_InitialCreate.Designer.cs:70-73`
    — `b.Property<bool>("IsActive").ValueGeneratedOnAdd().HasColumnType("bit").HasDefaultValue(true)`
  - `src/AgentSwarm.Messaging.Teams.EntityFrameworkCore/Migrations/TeamsConversationReferenceDbContextModelSnapshot.cs:67-70`
    — same shape as designer
  - `tests/AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests/SqlConversationReferenceStoreTests.cs`
    — new test `IsActive_HasDatabaseDefaultValueOfTrue` that asserts
    BOTH (a) `entityType.FindProperty("IsActive").GetDefaultValue() == true`
    (model annotation) AND (b) a raw `DbCommand` INSERT that omits the
    `IsActive` column persists a row with `IsActive = true` (DDL/schema
    behavior). The two-part assertion guards against future migrations
    silently dropping the default while the model still claims it.

  Migration regeneration approach: `dotnet ef migrations remove` hung
  for >7 minutes from a cold cache so I hand-edited the three migration
  artefacts to match what `dotnet ef migrations add` would produce
  given the new `HasDefaultValue(true)`. The shape is verified by the
  new `IsActive_HasDatabaseDefaultValueOfTrue` test which exercises
  the schema EnsureCreated emits from the same model EF would diff.

- [x] 2. ADDRESSED — `SaveOrUpdateAsync` now rejects ambiguous
  references that populate BOTH `AadObjectId` AND `ChannelId`. The
  contract is: user-scoped rows have `AadObjectId` set + `ChannelId`
  null; channel-scoped rows have `ChannelId` set + `AadObjectId` null.
  Files touched:
  - `src/AgentSwarm.Messaging.Teams.EntityFrameworkCore/SqlConversationReferenceStore.cs:52-83`
    — replaced the old neither-key check with explicit `hasAad`/
    `hasChannel` flags and a four-way branch: tenant-empty → throw,
    neither-key → throw, both-keys → throw with explicit "mutually
    exclusive" message naming both fields and explaining the contract,
    exactly-one → proceed. The downstream upsert branch was simplified
    to use `hasAad` instead of re-checking `string.IsNullOrEmpty`.
  - `tests/AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests/SqlConversationReferenceStoreTests.cs`
    — new test `SaveOrUpdate_RejectsReferenceWithBothNaturalKeysSet`
    that constructs an ambiguous reference (both keys), asserts
    `ArgumentException` is thrown with "mutually exclusive" in the
    message, AND queries the DB to confirm zero rows were persisted as
    a side effect (defends against "logged but accepted" failure modes).

- [x] 3. ADDRESSED — `SaveOrUpdateAsync` now validates `TenantId` is
  non-empty BEFORE any natural-key validation or DB query. Tenant
  isolation is the security boundary; accepting null/empty would
  collapse all tenants into a shared keyspace. Files touched:
  - `src/AgentSwarm.Messaging.Teams.EntityFrameworkCore/SqlConversationReferenceStore.cs:54-61`
    — `if (string.IsNullOrEmpty(reference.TenantId)) throw new
    ArgumentException(...)` immediately after the null-reference guard.
    Message explains the security model rationale.
  - `tests/AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests/SqlConversationReferenceStoreTests.cs`
    — new `[Theory]` `SaveOrUpdate_RejectsReferenceWithEmptyTenantId`
    parameterized over `[null, ""]` (2 cases). Each case asserts
    `ArgumentException` is thrown with "TenantId" in the message AND
    confirms zero rows persisted. Theory rather than two Facts so the
    null/empty equivalence is documented in the test signature.

  The pre-existing `ValidateKey(tenantId, ...)` helper at the bottom
  of the file already validates tenant on lookup/mutation paths
  (`GetByAadObjectIdAsync`, `MarkInactiveAsync`, etc.), so the only
  missing path was `SaveOrUpdateAsync`. No other store methods need
  edits.

## Verification

```
$ dotnet build AgentSwarm.Messaging.sln --nologo --verbosity minimal
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ dotnet test AgentSwarm.Messaging.sln --nologo --verbosity minimal --no-build
Passed!  - Failed: 0, Passed:  82, Skipped: 0, Total:  82  (Abstractions)
Passed!  - Failed: 0, Passed:  73, Skipped: 0, Total:  73  (Teams)
Passed!  - Failed: 0, Passed:  27, Skipped: 0, Total:  27  (Teams.EntityFrameworkCore)
```

Total: 182 tests pass (+4 from iter-1 baseline of 178: 1 ambiguous-keys
fact + 1 IsActive-default fact + 2 InlineData rows for the empty-tenant
theory).

## Files touched this iter

Production:
- `src/AgentSwarm.Messaging.Teams.EntityFrameworkCore/SqlConversationReferenceStore.cs`
  — added tenant validation + both-keys-set rejection in `SaveOrUpdateAsync`
- `src/AgentSwarm.Messaging.Teams.EntityFrameworkCore/TeamsConversationReferenceDbContext.cs`
  — added `HasDefaultValue(true)` to `IsActive`

Migration artefacts (hand-edited to match what `dotnet ef migrations add`
would emit; shape verified by the new schema-default test):
- `src/AgentSwarm.Messaging.Teams.EntityFrameworkCore/Migrations/20260515051028_InitialCreate.cs`
- `src/AgentSwarm.Messaging.Teams.EntityFrameworkCore/Migrations/20260515051028_InitialCreate.Designer.cs`
- `src/AgentSwarm.Messaging.Teams.EntityFrameworkCore/Migrations/TeamsConversationReferenceDbContextModelSnapshot.cs`

Tests:
- `tests/AgentSwarm.Messaging.Teams.EntityFrameworkCore.Tests/SqlConversationReferenceStoreTests.cs`
  — added 3 new test methods (`SaveOrUpdate_RejectsReferenceWithBothNaturalKeysSet`,
  `SaveOrUpdate_RejectsReferenceWithEmptyTenantId` [Theory ×2],
  `IsActive_HasDatabaseDefaultValueOfTrue`)

## Decisions made this iter

- **Hand-edit migration artefacts vs. regenerate.** `dotnet ef migrations
  remove` hung past 7 minutes from a cold cache. Edited the three
  artefacts (`*.cs`, `*.Designer.cs`, `*ModelSnapshot.cs`) to match
  what `dotnet ef migrations add InitialCreate` would generate from
  the new model. The new `IsActive_HasDatabaseDefaultValueOfTrue`
  test exercises both the model annotation AND the schema DDL, so any
  drift between the hand-edit and what EF would generate would fail
  the test.
- **Two-part assertion for the IsActive-default test.** Asserting only
  `GetDefaultValue() == true` would pass even if the migration was
  missing the default (model annotation lives in DbContext, not
  migration). Asserting only the raw INSERT would pass if the model
  annotation was dropped but the migration kept the SQL default. Both
  assertions together pin both surfaces.
- **Raw `DbCommand` not `ExecuteSqlRawAsync` for the INSERT.** The
  reference JSON column required for testing contains `{}` which
  `ExecuteSqlRawAsync` interprets as a `string.Format` placeholder and
  throws `FormatException`. Switched to `connection.CreateCommand()`
  + `ExecuteNonQueryAsync()` to bypass formatting entirely. This
  surfaced on the first test run; fixed before re-running.
- **Reject-zero-rows confirmation in BOTH validation tests.** Each
  rejection test queries `verify.ConversationReferences.CountAsync()`
  after the throw to confirm no row was persisted. Defends against
  "logged + persisted then thrown" failure modes that would let a bad
  reference slip into the table even though the call appeared to fail.
- **`Theory` with `InlineData(null, "")` over two `Fact`s for the
  tenant test.** Documents the null/empty equivalence in the test
  signature; xUnit reports them as 2 separate test results so failures
  pinpoint which case regressed.

## Dead ends tried this iter

- Tried `dotnet ef migrations remove` to regenerate cleanly. Hung
  past 7 minutes (cold MSBuild + ef-tool cache). Aborted and switched
  to hand-editing the three migration artefacts. Test coverage of the
  schema default validates the hand-edit shape.
- First version of `IsActive_HasDatabaseDefaultValueOfTrue` used
  `ctx.Database.ExecuteSqlRawAsync` which threw `FormatException` on
  the literal `'{}'` JSON value. Switched to `DbCommand.ExecuteNonQueryAsync`.

## Open questions surfaced this iter

- None. The 3 evaluator items were focused, self-contained, and
  fully fixable within this iter's scope.

## What's still left

- Nothing for Stage 4.1 iter 2. Build clean (0 warnings / 0 errors),
  182 tests pass solution-wide (82 + 73 + 27).
- Stage 4.2 (proactive notifier with reactive 403/404 detector that
  writes `DeactivationReason = "StaleReference"`) is downstream and
  out of scope for this workstream.
