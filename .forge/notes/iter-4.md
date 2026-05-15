# Iter notes — Stage 2.2 (Teams Activity Handler) — iter 4

## Prior feedback resolution

Iter-3 evaluator said: `Still needs improvement: None.` Verdict was demoted from
'pass' to 'iterate' by an OPERATOR RETRY (not a fresh critique). On entering this
iter the worktree was actually red — `dotnet test` showed 8 failing tests in
`AgentSwarm.Messaging.Teams.Tests` that the iter-3 notes claimed were green. The
breakage was self-inflicted by iter-3's commit `369a89b`.

- 1. (NEW THIS ITER, NOT IN EVAL LIST) FIXED — non-canonical audit `EventType`
  introduced by iter-3 broke 8 tests. `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs`
  defined a local `private const string InstallationUpdateEventType = "InstallationUpdate";`
  and `LogInstallAuditAsync` used it. `tech-spec.md` §4.3 fixes the audit
  vocabulary at exactly seven values
  (`CommandReceived, MessageSent, CardActionReceived, SecurityRejection, ProactiveNotification, MessageActionReceived, Error`)
  and `AuditEntry.EventType` setter (in
  `src/AgentSwarm.Messaging.Persistence/AuditEntry.cs:57-72`) rejects
  non-canonical values with `ArgumentException`. Removed the constant + xmldoc
  and switched `LogInstallAuditAsync` (line ~767) to
  `eventType: AuditEventTypes.CommandReceived`. Replaced the deleted xmldoc with
  a code comment near the helper explaining the schema constraint and that
  install rows are disambiguated from human commands via the `Action` column
  (`AppInstalled` / `AppUninstalledFromTeam` / `BotAddedToTeam` /
  `BotRemovedFromTeam`). The Teams test suite already asserted
  `AuditEventTypes.CommandReceived` for install events
  (`tests/AgentSwarm.Messaging.Teams.Tests/TeamsSwarmActivityHandlerTests.cs:314,332,357`),
  so no test edits were needed. Verification:
  ```
  $ grep -rnF 'InstallationUpdateEventType' src/ tests/
  (empty -- symbol fully removed)
  $ grep -rnF '"InstallationUpdate"' src/ tests/
  src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs:766: // values; "InstallationUpdate" is not in that set and the AuditEntry init setter
  ```
  (the lone remaining hit is the explanatory code comment).

- 2. (NEW THIS ITER, NOT IN EVAL LIST) FIXED — iter-3 also rewrote
  `SerializeConversationReference` from
  `Newtonsoft.Json.JsonConvert.SerializeObject(reference)` to
  `JsonSerializer.Serialize(reference, PayloadJsonOptions)` with a
  silent-`"{}"` fallback on `NotSupportedException`. `Microsoft.Bot.Schema.ConversationReference`
  is annotated with Newtonsoft `[JsonProperty(PropertyName="serviceUrl")]`-style
  attributes, `[JsonExtensionData]` property bags, and `JObject` members — STJ
  ignores ALL of those, emits PascalCase wire names, and the silent fallback
  would persist `"{}"` whenever a `JObject` member was present, breaking the
  Stage 4.x proactive-messaging worker's
  `JsonConvert.DeserializeObject<ConversationReference>(...)` round-trip.
  `Microsoft.Bot.Builder` 4.22.7 already pulls Newtonsoft.Json transitively, so
  no extra package reference is needed. Restored the original Newtonsoft.Json
  serializer + the explanatory comment. Added a new regression test
  `OnMessageActivityAsync_PersistsReferenceJson_RoundTripsViaNewtonsoftAndPreservesCanonicalWireNames`
  that asserts (a) `ReferenceJson != "{}"`, (b) the JSON contains the camelCase
  wire names `"serviceUrl"`, `"conversation"`, `"bot"`, (c) it does NOT contain
  PascalCase `"ServiceUrl"`, and (d) round-trips back to a
  `ConversationReference` whose `ServiceUrl`, `Conversation.Id`, and `Bot.Id` are
  populated. Verification:
  ```
  $ grep -nF 'Newtonsoft.Json.JsonConvert.SerializeObject' src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs
  715:        return Newtonsoft.Json.JsonConvert.SerializeObject(reference);
  ```

## Files touched this iter

- `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs` — removed
  `InstallationUpdateEventType` constant + xmldoc; switched `LogInstallAuditAsync`
  to use canonical `AuditEventTypes.CommandReceived`; restored
  Newtonsoft.Json serialization in `SerializeConversationReference` with the
  original explanatory comment.
- `tests/AgentSwarm.Messaging.Teams.Tests/TeamsSwarmActivityHandlerTests.cs` —
  added `OnMessageActivityAsync_PersistsReferenceJson_RoundTripsViaNewtonsoftAndPreservesCanonicalWireNames`
  to lock in the serializer contract (camelCase wire names + Newtonsoft round-trip).

## Decisions made this iter

- **Use `AuditEventTypes.CommandReceived` for install/uninstall lifecycle audit
  rows** rather than extending the canonical EventType set or skipping audit
  logging. Rationale: tech-spec.md §4.3 is explicit ("exactly seven values"),
  changing it would propagate across architecture/audit-completeness metrics in
  sibling docs, and the existing test contract already chose this mapping.
  Operators distinguish install rows from human commands via the `Action`
  column. Logged as future schema debt only — no scope creep here.
- **Revert the serializer change immediately rather than deferring.** The
  rubber-duck flagged it as a real persisted-data contract regression with a
  silent-corruption failure mode (`"{}"` fallback masks the schema mismatch).
  Cost to revert is one line + one regression test; cost to defer is a Stage 4.x
  worker that silently fails proactive messaging.
- **Round-trip test asserts both wire-name shape AND deserialization** to defend
  against a future agent re-introducing STJ (which would still pass a naive
  "deserializes successfully" check via Newtonsoft's case-insensitive default
  but fail the explicit camelCase-vs-PascalCase grep).

## Dead ends tried this iter

- None. Diagnosis was straightforward (test output gave the validator's
  rejection message verbatim with the allowed-values list), the rubber-duck
  validated the two-fix plan up front, and both fixes landed in two clean
  edits.

## Open questions surfaced this iter

- None blocking. (Long-term schema debt: should `AuditEventTypes` get an 8th
  `InstallationUpdate` value? That belongs to a future spec-update workstream,
  not Stage 2.2.)

## What's still left

- Nothing for Stage 2.2 iter 4. Build clean (0 warnings / 0 errors), 117 tests
  pass solution-wide (82 abstractions + 35 Teams; was 116 in iter 3, +1
  serializer round-trip test).
- Stage 2.1 DI wiring + Stage 3.x dispatch/card-handler still pending downstream.
