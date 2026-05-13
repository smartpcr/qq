# Iter notes — Stage 1.3 Connector Interface and Service Contracts (iter 7)

## Prior feedback resolution (evaluator iter-6 list, score 88)

All four flagged items were doc-only contradictions between code/plan
and architecture.md. Code was already correct; this iter aligns the
docs to match.

- **1. ADDRESSED** — `architecture.md` §4.11 (was line 618): changed
  "to be defined in planned `AgentSwarm.Messaging.Abstractions`" to
  "...`AgentSwarm.Messaging.Core`" and added a Layering Note explaining
  why (`AuthorizationResult.Bindings` is `IReadOnlyList<OperatorBinding>`
  and Abstractions may not reference Core). Verification:
  `grep -nF "IUserAuthorizationService.*planned.*Abstractions" docs/` →
  `(empty — stale claim removed)`
- **2. ADDRESSED** — `implementation-plan.md:65` `PendingQuestion`
  property list now includes `TaskId` (with e2e-scenarios anchor) and
  `Severity` (`MessageSeverity`, with severity-routing rationale).
  Closes the divergence with `src/.../PendingQuestion.cs` lines 38–59
  where both fields are `required`. Plan also adds an explicit
  "Stage 3.5 mapper MUST preserve `TaskId` and `Severity` end-to-end"
  guard so the persistence stage cannot silently drop them.
- **3. ADDRESSED** — `architecture.md` §3.1 callback-data constraint
  block (lines 212, 220, 221, 225–228) AND §11 `callback_data` row
  (line 1249) rewritten to describe the actual wire contract:
  `QuestionId`/`ActionId` are **printable ASCII only**, ≤ 30 chars,
  with no `':'` separator and no ASCII control characters; `Label` is
  **64 UTF-8 bytes** (not characters). Includes the byte-vs-char
  divergence math (one non-ASCII code point can encode to 4 UTF-8
  bytes; ASCII rule pins byte count == char count) so the rationale
  survives downstream re-reads. §11 row updated for consistency.
- **4. ADDRESSED** — `implementation-plan.md:70` `IAuditLogger` step
  rewritten to specify **two** methods (`LogAsync` + `LogHumanResponseAsync`),
  the new `HumanResponseAuditEntry` record with all five story-brief-mandated
  fields marked `required`, and the rationale for the type-level
  enforcement. `architecture.md` §3.1 audit section heading renamed to
  "AuditEntry / AuditLogEntry / HumanResponseAuditEntry" with a new
  field table for `HumanResponseAuditEntry`, an updated `IAuditLogger`
  C# block showing both overloads, and an extended `AuditLogEntry`
  persistence table with `QuestionId`/`ActionValue`/`Comment`/`EntryKind`
  discriminator columns so Stage 5.3 can persist both shapes.

## Files touched this iter
- `docs/.../architecture.md`: §3.1 AgentQuestion/HumanAction
  constraint blocks rewritten for ASCII / UTF-8 byte semantics; §3.1
  audit section expanded with `HumanResponseAuditEntry`; §4.11
  IUserAuthorizationService heading fixed to Core + Layering Note;
  §11 callback_data row rewritten.
- `docs/.../implementation-plan.md:65`: `PendingQuestion` properties
  list now includes `TaskId` + `Severity`.
- `docs/.../implementation-plan.md:70`: `IAuditLogger` step now
  describes both overloads + `HumanResponseAuditEntry`.

## Decisions made this iter
- Aligned docs to existing code rather than vice-versa. Changing code
  to match a stale doc would have regressed prior evaluator-passed
  items (`AuthorizationResult.Bindings` layering, `PendingQuestion`
  test pins, audit required-field type guarantee). Doc-side edits are
  the safe, structural fix.
- For item 3 I edited BOTH §3.1 (data-model constraints) AND §11 line
  1249 (architecture decisions row) in the same pass. Without the §11
  update a downstream reader looking up "callback_data format" would
  still see the char-only claim and reintroduce the byte-budget bug.
  Doc-wide blast-radius pass per the Forge "fix one place / break
  another" warning.
- Used the `EntryKind` discriminator column in the persistence table
  rather than two separate tables. A single audit table preserves the
  natural append-only semantics; the discriminator + nullable
  question/action columns let Stage 5.3 round-trip both abstraction
  shapes.

## Dead ends tried this iter
- None.

## Open questions surfaced this iter
- None.

## What's still left (unchanged from iter-5)
- Stage 1.4: `IMessageSender` (+ `SendResult`) in Core; `IAlertService`
  + `IOutboundQueue` in Abstractions (implementation-plan.md lines 96–98).
- Stage 2.x: stub/no-op implementations in the Telegram project per
  implementation-plan line 135.
- Stage 5.3: persistent `AuditLogEntry` entity mapping from `AuditEntry`
  / `HumanResponseAuditEntry` (now spec'd in architecture §3.1).
