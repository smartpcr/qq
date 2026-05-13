# Iter notes — Stage 1.3 (Persistence Abstractions)

## Files touched this iter
- `src/AgentSwarm.Messaging.Persistence/AgentSwarm.Messaging.Persistence.csproj` — new .NET 8 project, refs Abstractions, same warning/doc settings as the sibling project.
- `src/AgentSwarm.Messaging.Persistence/AuditEventTypes.cs` — seven canonical audit `EventType` constants + `All` / `IsValid`.
- `src/AgentSwarm.Messaging.Persistence/AuditActorTypes.cs` — `User` / `Agent` constants.
- `src/AgentSwarm.Messaging.Persistence/AuditOutcomes.cs` — `Success` / `Rejected` / `Failed` / `DeadLettered`.
- `src/AgentSwarm.Messaging.Persistence/MessageDirections.cs` — `Inbound` / `Outbound`.
- `src/AgentSwarm.Messaging.Persistence/PersistedMessage.cs` — envelope record returned by `GetByCorrelationIdAsync`.
- `src/AgentSwarm.Messaging.Persistence/AuditEntry.cs` — immutable record with all 12 canonical fields + `Checksum`; `ComputeChecksum` helper (SHA-256 over `|`-delimited canonical fields).
- `src/AgentSwarm.Messaging.Persistence/IAuditLogger.cs` — `LogAsync(AuditEntry, CancellationToken)`.
- `src/AgentSwarm.Messaging.Persistence/NoOpAuditLogger.cs` — completed-task stub, null-guarded and cancellation-aware.
- `src/AgentSwarm.Messaging.Persistence/IMessageStore.cs` — `SaveInboundAsync(MessengerEvent,...)`, `SaveOutboundAsync(MessengerMessage,...)`, `GetByCorrelationIdAsync(string, CT) → Task<IReadOnlyList<PersistedMessage>>`.
- `tests/AgentSwarm.Messaging.Persistence.Tests/*.cs` — five test classes (immutability via reflection, checksum determinism + sensitivity, vocabulary, interface contracts, no-op behavior).
- `AgentSwarm.Messaging.sln` — added both new projects via `dotnet sln add`.

## Decisions made this iter
- `GetByCorrelationIdAsync` returns `Task<IReadOnlyList<PersistedMessage>>`. The plan only said "list of messages" without mixing inbound (`MessengerEvent` polymorphic) and outbound (`MessengerMessage`) shapes into one return type. A thin envelope avoids that mismatch and maps 1:1 to a future SQL row.
- `SaveInboundAsync` takes `MessengerEvent` (base record, already polymorphic via `EventType`); `SaveOutboundAsync` takes `MessengerMessage`. Both are canonical Abstractions records — no new persistence-only domain types.
- `AuditEntry.Checksum` is a required `init` field; computation is delegated to a static `ComputeChecksum(...)` helper so the record stays a pure value (no implicit hashing during `with` expressions) and the SQL implementation can recompute/verify on read.
- Canonical-field serialization for checksum: pipe-delimited UTF-8 with ISO-8601 round-trip timestamp; nulls rendered as `"\0"` so null vs empty differ (verified by test).
- Kept `TreatWarningsAsErrors=true` + `GenerateDocumentationFile=true` to match the sibling Abstractions project's conventions (the existing project sets `CS1591` to ignore, suppressing missing-doc warnings on test-helper-only members).

## Dead ends tried this iter
- Initial draft of `AuditEntryChecksumTests` had an unused local function `Vary` that tripped `CS8321` under TreatWarningsAsErrors — removed.

## Open questions surfaced this iter
- None blocking. Implementation-plan paragraph for Stage 1.3 said "list of messages" without specifying type — chose `PersistedMessage` envelope; can be revisited in Stage 6 when the SQL `MessageStore` is concretized.

## What's still left
- Nothing for Stage 1.3. Stage 2.1 will register `NoOpAuditLogger` as the default `IAuditLogger` in DI; Stage 5.2 swaps in `SqlAuditLogger`.
- Verified `dotnet build` exit 0 and `dotnet test` 35/35 passing in the new project, 51/51 in the existing one.
