# Iter notes — Stage 1.3 (Persistence Abstractions) — iter 2

## Files touched this iter
- `src/AgentSwarm.Messaging.Persistence/AuditEntry.cs` — added init-setter validation against canonical vocabularies for `EventType`/`ActorType`/`Outcome` (private backing fields), and replaced pipe-delimited canonical encoding with length-prefixed binary (32-bit LE byte length + UTF-8 bytes; `-1` sentinel for null), prefixed with `CanonicalEncodingVersion = 1`.
- `src/AgentSwarm.Messaging.Persistence/NoOpAuditLogger.cs` — stripped the argument-null guard and `ThrowIfCancellationRequested` call. Now a true no-op returning the singleton `Task.CompletedTask`.
- `tests/AgentSwarm.Messaging.Persistence.Tests/NoOpAuditLoggerTests.cs` — replaced the two "throws on null / cancelled" assertions with "returns CompletedTask on null / cancelled / both" assertions; added `Assert.Same(Task.CompletedTask, task)` reference-identity test.
- `tests/AgentSwarm.Messaging.Persistence.Tests/AuditEntryChecksumTests.cs` — replaced the pipe-joined `ComputeChecksum_MatchesReferenceSha256` reference with a length-prefixed reference; added `ComputeChecksum_DelimiterCollision_DistinguishedAcrossFieldBoundary` (Theory: 3 distinct shifts) and `ComputeChecksum_NullableFieldCollision_DistinguishedFromSentinelContent`.
- `tests/AgentSwarm.Messaging.Persistence.Tests/AuditEntryVocabularyValidationTests.cs` — NEW. Verifies invalid `EventType` / `ActorType` / `Outcome` (including null, empty, wrong case, and domain-event values that collide by name) throw `ArgumentException` from both constructor and `with` expression; verifies all valid combinations construct successfully.

## Decisions made this iter
- Validation via private backing field + init accessor (rather than a separate `Validate()` method) so callers cannot construct or derive an invalid `AuditEntry`. `with` expressions go through the same init setter so derivation is also gated. Records preserve value-equality semantics because the synthesized `Equals` reads through the property getter.
- Length-prefixed binary canonical encoding rather than deterministic JSON. Reason: dependency-free (no `System.Text.Json` surface area to lock down), trivially auditable, and `BinaryWriter` + 32-bit length prefixes are textbook collision-resistant. Added a `CanonicalEncodingVersion = 1` prefix so a future schema change produces a clearly different digest.
- Kept the original "null vs empty" guarantee under the new encoding via `-1` length sentinel (verified by `ComputeChecksum_NullableFieldCollision_DistinguishedFromSentinelContent`).
- Did NOT add validation on `CorrelationId`/`TenantId`/`ActorId`/`Action`/`PayloadJson`/`Checksum` (non-empty / shape). Evaluator scoped feedback to the three closed-vocabulary fields; keeping focus avoids scope creep that could regress other items.

## Dead ends tried this iter
- Briefly considered marking the new backing fields `readonly` — C# does allow `readonly` field assignment inside init accessors, but the declaration order can cause "use before assigned" diagnostics under nullable-aware analysis when the property type is non-nullable string. Left them as plain private fields with a `string.Empty` default; the init setter is still the only assignment path externally.

## Open questions surfaced this iter
- None — the evaluator's three items were specific and actionable.

## What's still left
- Nothing for Stage 1.3. Verified `dotnet build` exit 0 (0 warnings) and `dotnet test AgentSwarm.Messaging.sln` 113/113 pass (62 persistence + 51 abstractions). Stage 2.1 will register `NoOpAuditLogger` as the default `IAuditLogger` in DI; Stage 5.2 swaps in `SqlAuditLogger` whose validation surface can now safely assume `AuditEntry`'s init-time guarantees.
