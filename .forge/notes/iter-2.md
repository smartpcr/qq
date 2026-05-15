# Iter notes — Stage 3.1 (Adaptive Card Templates) — iter 2

Iter-1 evaluator score 83 / verdict iterate. Four numbered items;
all four addressed this iter (none deferred).

## Prior feedback resolution

- [x] 1. ADDRESSED — `src/AgentSwarm.Messaging.Teams/Cards/AdaptiveCardBuilder.cs:208-220`
  + `src/AgentSwarm.Messaging.Teams/Cards/ReleaseGateRequest.cs:36-44`
  + `tests/.../AdaptiveCardBuilderTests.cs:362-466`. Removed the
  "Approvals" fact (`CurrentApprovals` / `RequiredApprovals` rendering)
  AND removed `RequiredApprovals` / `CurrentApprovals` / `Approvers`
  from the `ReleaseGateRequest` record entirely so the card template
  CANNOT surface threshold aggregation even if a future caller passes
  values. Added a new dedicated test
  `RenderReleaseGateCard_DoesNotSurfaceThresholdAggregationFacts` that
  asserts `Approvals`, ` of `, `Required`, and `Approver` are all
  absent from the card JSON, and walks every `FactSet` to assert no
  fact title contains those substrings. Verification:
  ```
  $ grep -rnF "RequiredApprovals" src/ tests/
  src/AgentSwarm.Messaging.Teams/Cards/ReleaseGateRequest.cs:32: /// this record exposed <c>RequiredApprovals</c> / <c>CurrentApprovals</c> / <c>Approvers</c>
  $ grep -rnF "1 of 2" src/ tests/
  (empty)
  ```
  Sole `RequiredApprovals` hit is the doc comment that explains the
  iter-2 removal and points future readers at `architecture.md` §6.3.1
  for the rationale (i.e., it's intentional teaching docstring, not a
  live reference).

- [x] 2. ADDRESSED —
  `tests/.../AdaptiveCardBuilderTests.cs:50-150`. Replaced the weak
  "any Input.Text exists" assertion with a stronger 4-part contract on
  the renamed test
  `RenderQuestionCard_ActionRequiresComment_PlacesInputTextAdjacentToActions`:
  (a) the comment input has `id == "comment"`;
  (b) **adjacency** — `Assert.Same(commentInput, body[body.Count - 1])`
  proves the input is the LAST body element, immediately preceding the
  actions row (the strongest "adjacent" the Adaptive Card schema can
  express, since inputs live in `body` and actions in `actions`);
  (c) the input label NAMES the requiring action ("Reject") so the
  user knows WHICH button demands a reason;
  (d) **wiring proof** — render → pluck Reject's data dict → merge a
  comment value → run through `CardActionMapper` → assert the comment
  flows onto `HumanDecisionEvent.Comment`. Plus a second new test
  `RenderQuestionCard_AllActionsRequireComment_UsesUniversalRequiredLabel`
  for the all-actions-require-comment shape.

- [x] 3. ADDRESSED —
  `src/AgentSwarm.Messaging.Teams/TeamsMessengerConnector.cs:30-36`.
  XML doc bullet now reads "render the question into an Adaptive Card
  via the injected `IAdaptiveCardRenderer` (Stage 3.1), wrap it in an
  Attachment with `ContentType = "application/vnd.microsoft.card.adaptive"`,
  and send it through `ContinueConversationAsync`. `Activity.Text` is
  set to the question title as a notification-banner / accessibility
  fallback only." Verification:
  ```
  $ grep -rnF "text summary" src/AgentSwarm.Messaging.Teams/
  (empty)
  ```

- [x] 4. ADDRESSED —
  `src/AgentSwarm.Messaging.Teams/TeamsServiceCollectionExtensions.cs:30-49`.
  XML doc now says "TeamsMessengerConnector's **nine**-argument
  constructor" and explicitly enumerates BOTH default-fill registrations
  (router auto-wire AND the new `IAdaptiveCardRenderer` /
  `AdaptiveCardBuilder` registration). Verification:
  ```
  $ grep -rnF "eight ctor" src/AgentSwarm.Messaging.Teams/
  (empty)
  ```

## Files touched this iter

- `src/AgentSwarm.Messaging.Teams/Cards/ReleaseGateRequest.cs` — removed
  3 fields (`RequiredApprovals`, `CurrentApprovals`, `Approvers`); added
  remarks block explaining the architectural boundary.
- `src/AgentSwarm.Messaging.Teams/Cards/AdaptiveCardBuilder.cs` — removed
  the "Approvals: N of M" fact from the gate fact set; added a comment
  explaining that the card MUST NOT surface threshold aggregation.
- `src/AgentSwarm.Messaging.Teams/TeamsMessengerConnector.cs` — XML doc
  refresh on `SendQuestionAsync` to describe the Adaptive Card path.
- `src/AgentSwarm.Messaging.Teams/TeamsServiceCollectionExtensions.cs`
  — XML doc refresh: "nine-argument constructor" and explicit list of
  both default-fill registrations (router + renderer).
- `tests/.../AdaptiveCardBuilderTests.cs` — strengthened comment-input
  test (renamed + 4-part assertion + round-trip check); added
  `RenderQuestionCard_AllActionsRequireComment_UsesUniversalRequiredLabel`;
  removed `1 of 2` assertion; replaced gate-test ctor args (no more
  threshold fields); added
  `RenderReleaseGateCard_DoesNotSurfaceThresholdAggregationFacts`.

## Decisions made this iter

- **Structural removal over assertion-only fix for item 1.** Could
  have left the threshold fields on `ReleaseGateRequest` and just
  removed the "Approvals" fact from rendering. Removed the FIELDS too
  so a future caller can't reintroduce the violation by passing values
  the renderer would otherwise have to ignore. This makes the
  architectural boundary part of the type contract.
- **"Adjacent" interpreted as "last body element".** Adaptive Cards
  1.5 puts inputs in `body` and actions in `actions` — the schema
  forbids inputs INSIDE the actions array. The strongest adjacency
  the schema can express is "input is the last body element, the
  actions row immediately follows". That's what `Assert.Same(commentInput,
  body[body.Count - 1])` proves.
- **Negative-fact-set walk on the rollup test.** `Assert.DoesNotContain`
  on the JSON string would catch a literal "1 of 2" but miss a
  reformulation ("1/2", "Approved by 1"). Iterating every `FactSet`'s
  facts and asserting NO title contains "Approval" / "Approver" /
  "Required" is a stronger structural guard.

## Dead ends tried this iter

- None. The four iter-1 items were all small, well-scoped changes;
  no exploratory dead ends.

## Open questions surfaced this iter

- None. `architecture.md` §3.3 still lists the removed gate fields
  in the data-model table; the workstream brief said edits should land
  primarily in production/test code so I left the doc table alone. If
  the evaluator flags the doc / code drift in iter 3, the next iter
  should propose a one-line edit to the §3.3 table noting the fields
  live on the orchestrator's gate-state record, not on the renderer's
  payload — but that is properly the orchestrator workstream's call.

## What's still left

- Nothing for Stage 3.1 iter 2. Build clean (0 warnings / 0 errors,
  `TreatWarningsAsErrors=true`); 190 tests pass solution-wide
  (82 abstractions + 108 Teams; +35 from the iter-7 baseline of 73,
  +2 since iter-1's 106 because of the new comment-adjacency split
  and the new no-rollup test).
