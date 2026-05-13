# Iter notes — Stage 1.3 Connector Interface and Service Contracts (iter 4)

## Prior feedback resolution

- **1. ADDRESSED** — Replaced character-count-only validation with a wire-aware split:
  - `AgentQuestion.QuestionId` and `HumanAction.ActionId`: now also enforce **ASCII-only** via `CallbackDataValidation.IsAsciiOnly`, in addition to the `≤ 30` char limit. ASCII guarantees UTF-8 byte count == char count, so a 30-char id is provably ≤ 30 bytes on the wire (half the Telegram 64-byte callback_data budget). Error message includes the literal `"ASCII"` and the offending code point + index.
  - `HumanAction.Label`: validation is **UTF-8 byte-oriented** (`Encoding.UTF8.GetByteCount(value) ≤ MaxLabelByteLength = 64`), error message contains the literal `"UTF-8 byte"`. Char-count constant `MaxLabelLength = 64` is retained for compat with char-oriented callers but the authoritative wire constraint is `MaxLabelByteLength`.
  - Edits: `src/AgentSwarm.Messaging.Abstractions/AgentQuestion.cs` (init accessor + `MaxQuestionIdLength` constant + ASCII rule + AllowedActions null/dup check), `src/AgentSwarm.Messaging.Abstractions/HumanAction.cs` (init accessors + `MaxActionIdLength` / `MaxLabelLength` / **new `MaxLabelByteLength`** constants + ASCII rule for ActionId + UTF-8 byte rule for Label). The helper `CallbackDataValidation` (already added in iter 3) is now actually wired through both records.
  - Verification:
    ```
    $ dotnet test tests\AgentSwarm.Messaging.Tests
    Passed!  Total: 92, Passed: 92  -- includes the iter-3-authored byte-vs-char divergence tests:
      AgentQuestion_QuestionId_RejectsNonAsciiEvenWhenCharCountWithinLimit
      AgentQuestion_QuestionId_RejectsAnyNonAsciiCodePoint (4 inline cases)
      HumanAction_ActionId_RejectsNonAsciiEvenWhenCharCountWithinLimit
      HumanAction_ActionId_RejectsAnyNonAsciiCodePoint (3 inline cases)
      HumanAction_Label_AcceptsMultiByteUnderByteBudget
      HumanAction_Label_RejectsMultiByteOverByteBudgetEvenWhenCharCountFits
      HumanAction_Label_AcceptsBoundaryByteCount
      CallbackData_NonAsciiPathIsBlockedAtConstructionNotAtSerialization
    ```

- **2. ADDRESSED** — `implementation-plan.md:89` rewritten. Old wording was "Given all interfaces are defined in Abstractions"; new wording names the actual split: Abstractions hosts the transport-facing ports (`IMessengerConnector`, `ICommandRouter`, `ICommandParser`, `ICallbackHandler`, `IPendingQuestionStore`, `IDeduplicationService`, `IAuditLogger`, `ITelegramUpdatePipeline`, `ISwarmCommandBus`), Core hosts the Core-typed services that return / accept `OperatorBinding` (`IUserAuthorizationService`, `AuthorizationResult`, `IOperatorRegistry`, `OperatorRegistration`, `ITaskOversightRepository`), with the layering rationale inline. Verification:
  ```
  $ grep -rnF "all interfaces are defined in Abstractions" docs/
  (empty -- stale claim removed)
  ```

- **Bonus (not flagged this iter but iter-3 claimed it was done)** — actually deleted `src/AgentSwarm.Messaging.Abstractions/Placeholder.cs`, `src/AgentSwarm.Messaging.Core/Placeholder.cs`, `src/AgentSwarm.Messaging.Telegram/Placeholder.cs`. Iter 3's grep was empty because the files only contained `namespace X;` (no token "Placeholder" in body), but the files themselves were still on disk and tracked. Confirmed gone via `Get-ChildItem` glob `**/Placeholder.cs` → no matches; `git status --short` now shows three `D` entries.

## Why iter-3 work appeared "lost" at the start of iter-4
- The iter-3 agent edited tracked files (`AgentQuestion.cs`, `HumanAction.cs`) and deleted tracked files (the three `Placeholder.cs`) but Forge's between-iter checkout reverted those tracked changes back to HEAD because they were never committed. Only the *untracked* iter-3 artifacts (the new interface files, the test file, `CallbackDataValidation.cs`) survived. The fresh build therefore failed with 16 CS0117 errors against the un-restored `MaxQuestionIdLength` / `MaxActionIdLength` / `MaxLabelLength` / `MaxLabelByteLength` constants. Iter-4 re-applied the validation AND added the new `MaxLabelByteLength`+ASCII rule that the iter-3 reviewer asked for.

## Files touched this iter
- `src/AgentSwarm.Messaging.Abstractions/AgentQuestion.cs`: ASCII enforcement for `QuestionId`; init-accessor with backing field; AllowedActions null / duplicate guards.
- `src/AgentSwarm.Messaging.Abstractions/HumanAction.cs`: ASCII enforcement for `ActionId`; UTF-8 byte-count enforcement for `Label`; added `MaxLabelByteLength` constant alongside `MaxLabelLength`.
- `src/AgentSwarm.Messaging.Abstractions/Placeholder.cs`: DELETED (was tracked in HEAD).
- `src/AgentSwarm.Messaging.Core/Placeholder.cs`: DELETED.
- `src/AgentSwarm.Messaging.Telegram/Placeholder.cs`: DELETED.
- `docs/stories/qq-TELEGRAM-MESSENGER-S/implementation-plan.md`: line 89 test scenario rewritten to reflect the Abstractions/Core split.

## Decisions made this iter
- Kept `MaxLabelLength` (char count) alongside the new `MaxLabelByteLength` (byte count) rather than removing the char constant. Reason: iter-3 test code uses `MaxLabelLength` to drive both the "boundary length OK" and "over-limit rejected" tests against ASCII labels; removing it would break those tests' compilation. Treating it as an informational char-count alias (numerically equal to `MaxLabelByteLength = 64`) lets both shapes of test coexist while the actual gate enforces UTF-8 bytes. Documented in xmldoc.
- ASCII rule on `QuestionId` / `ActionId` instead of byte-count rule: a pure byte-count rule would silently let a 7-char Chinese id (21 UTF-8 bytes) pass the 30-byte budget, but then the `:` separator + a 30-char ASCII action id could blow the 64-byte total. ASCII gives a deterministic, per-half budget and produces a clearer diagnostic. The byte-vs-char distinction is explicitly tested by `AgentQuestion_QuestionId_RejectsNonAsciiEvenWhenCharCountWithinLimit`.
- Error messages chosen to contain the literal strings `"ASCII"` (QuestionId/ActionId) and `"UTF-8 byte"` (Label) because the iter-3-authored tests already assert `ex.Message.Contains(...)` on those exact substrings.

## Dead ends tried this iter
- Initially considered enforcing the byte budget on `QuestionId` / `ActionId` by computing UTF-8 byte count and capping at 30. Discarded — see the second decision above; ASCII is the cleaner contract for the wire-budget split.

## Open questions surfaced this iter
- None.

## What's still left
- Stage 1.4: `IMessageSender` (+ `SendResult`) in Core; `IAlertService` + `IOutboundQueue` in Abstractions.
- Stage 2.x: stub/no-op implementations in the Telegram project per implementation-plan line 135.
- Stage 5.3: persistent `AuditLogEntry` entity mapping from `AuditEntry` / `HumanResponseAuditEntry`.
