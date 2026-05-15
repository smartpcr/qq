# Iter notes — Stage 2.3 (Teams Messenger Connector) — iter 4

## Prior feedback resolution

Iter-3 evaluator (score 89, verdict iterate) flagged ONE remaining item — a
narrative/verification issue, not a code defect. Items 1, 2, 3 from iter 2
were all marked verified-FIXED. Items 1, 2, 3, 4, 5, 6 from iter 1 stayed
verified-FIXED.

- [x] 1. ADDRESSED — narrative correction. Iter-3's `[x] FIXED` for the
  verb→event-type mapping listed only the two `MessengerEventTypes.Command`
  hits inside `TeamsSwarmActivityHandler.cs` (the xmldoc reference at L303
  and the switch fallback arm at L315). The evaluator ran the SAME literal
  grep across `src/` AND `tests/` and got the full nine-hit set, six of
  which I did not explicitly acknowledge. None are bugs — every hit is
  either an xmldoc cross-reference, a fallback-arm of the new mapper, or a
  test that EXERCISES the fact that `agent status` / `approve` / `reject`
  legitimately map to `MessengerEventTypes.Command` per architecture.md
  §3.1. This iter is a narrative-only fix: paste the FULL grep output
  verbatim and annotate each hit. NO code change because the production
  fix from iter 3 is already correct.

  Verification (FULL output, scope = `src/` + `tests/`, pattern is the
  exact pre-edit symbol the evaluator used):
  ```
  $ grep -rnF "MessengerEventTypes.Command" src/ tests/
  src/AgentSwarm.Messaging.Abstractions/MessengerEvent.cs:70: /// varies based on the parsed command — see <see cref="MessengerEventTypes.CommandEventTypes"/>.
  src/AgentSwarm.Messaging.Abstractions/MessengerEvent.cs:76: /// <see cref="MessengerEventTypes.CommandEventTypes"/>; otherwise an
  src/AgentSwarm.Messaging.Abstractions/MessengerEvent.cs:87: $"Allowed values: [{string.Join(", ", MessengerEventTypes.CommandEventTypes)}].",
  src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs:303: /// already have rejected) fall back to <see cref="MessengerEventTypes.Command"/> so
  src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs:315: _ => MessengerEventTypes.Command,
  tests/AgentSwarm.Messaging.Abstractions.Tests/MessengerEventTests.cs:53: [InlineData(MessengerEventTypes.Command)]
  tests/AgentSwarm.Messaging.Teams.Tests/ChannelInboundEventPublisherTests.cs:114: return new CommandEvent(MessengerEventTypes.Command)
  tests/AgentSwarm.Messaging.Teams.Tests/TeamsMessengerConnectorTests.cs:131: Assert.Equal(MessengerEventTypes.Command, commandEvent.EventType);
  tests/AgentSwarm.Messaging.Teams.Tests/TeamsSwarmActivityHandlerTests.cs:449: /// than collapsing every command into <see cref="AgentSwarm.Messaging.Abstractions.MessengerEventTypes.Command"/>.
  ```

  Per-hit annotation (each is intentional and documented):

  - `src/AgentSwarm.Messaging.Abstractions/MessengerEvent.cs:70,76,87` —
    Three references to `MessengerEventTypes.CommandEventTypes` (the
    constant LIST containing the discriminator subset valid for
    `CommandEvent`). NOT the same symbol as `MessengerEventTypes.Command`
    — the literal `grep -F` matches the prefix. These three hits are the
    base record's xmldoc + the validator's allowed-values error message
    that the `CommandEvent` constructor throws when an invalid
    discriminator is supplied. Both are pre-existing Stage 1.2 contract
    code; no change required.
  - `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs:303` —
    xmldoc line on `MapVerbToEventType` documenting that unknown verbs
    fall back to `Command`. This IS the iter-3 mapping helper.
  - `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs:315` —
    The wildcard arm of the iter-3 switch expression. Maps
    `agent status` / `approve` / `reject` (the architecture.md §3.1
    "Command" row) and any unknown verb to `Command` per the spec. This
    IS the iter-3 mapping fix and is correct per architecture.md §3.1
    line 352 ("`CommandEvent` | `Command` | `ParsedCommand` | User sends
    `agent status`, `approve`, or `reject`").
  - `tests/AgentSwarm.Messaging.Abstractions.Tests/MessengerEventTests.cs:53` —
    Pre-existing Stage 1.2 contract test (`[InlineData(...)]`) verifying
    the `CommandEvent` constructor accepts every value in
    `MessengerEventTypes.CommandEventTypes` including `Command`. Not
    Stage 2.3 code; not modified this workstream.
  - `tests/AgentSwarm.Messaging.Teams.Tests/ChannelInboundEventPublisherTests.cs:114` —
    Test fixture in iter-1's `ChannelInboundEventPublisher` test suite
    that synthesizes a `CommandEvent` to push through the channel. The
    discriminator value is irrelevant to the publisher contract (the
    publisher is event-type-agnostic); using `Command` is just one valid
    choice from `MessengerEventTypes.CommandEventTypes`.
  - `tests/AgentSwarm.Messaging.Teams.Tests/TeamsMessengerConnectorTests.cs:131` —
    The end-to-end `agent status` ReceiveAsync test from iter-2
    (item-4 fix). Asserts the connector observes
    `commandEvent.EventType == MessengerEventTypes.Command` because
    `agent status` IS the `Command` row of the architecture.md §3.1
    table. This assertion is the regression that LOCKS IN the
    `agent status → Command` half of the iter-3 mapping.
  - `tests/AgentSwarm.Messaging.Teams.Tests/TeamsSwarmActivityHandlerTests.cs:449` —
    xmldoc on the iter-3 verb-mapping `[Theory]`; the
    `<see cref="MessengerEventTypes.Command"/>` reference inside a
    documentation comment that explains the regression's scope.

  None of the nine hits represent a regression of iter-3's
  verb→event-type mapping. Items 2, 3, 4 of the iter-3 evaluator (the
  three previously-FIXED iter-2 items it re-verified) remain green; this
  iter-4 entry only widens the iter-3 grep verification to match the
  evaluator's reproduction.

## Files touched this iter

- `.forge/iter-notes.md` — narrative-only update to widen the iter-3 grep
  verification; no production-code or test edits.

## Decisions made this iter

- **Pure narrative fix, not a code change.** The evaluator explicitly noted
  "the remaining blocker is a review-protocol/narrative verification
  problem, not a core connector behavior defect" and that "the implementation
  is effectively complete for Stage 2.3". Adding more code would be
  scope-creep; the right move is to widen the grep claim until it matches
  the evaluator's reproduction verbatim. This is the structural fix the
  feedback template asks for after one round of "the same item flipped
  back to `[ ]`" — instead of repeating the iter-3 word-tweak (paste two
  lines), I now paste ALL nine lines and annotate each.
- **Per-hit annotation in a single block.** A future agent (or reviewer)
  reading this can confirm each grep hit against the spec without needing
  to re-derive the mapping. The annotation is the auditable artifact that
  proves none of the nine hits is an accidental regression.
- **Not deferred.** The fix is a notes edit only; nothing about it requires
  operator input or a sibling-doc change.

## Dead ends tried this iter

- None. The fix was a single-file notes edit; build/test re-verified to
  confirm the iter-3 production code is unchanged and still green
  (0 warnings / 0 errors, 152 tests pass: 82 abstractions + 70 Teams).

## Open questions surfaced this iter

- None.

## What's still left

- Nothing for Stage 2.3 iter 4. The connector implementation, all 6 iter-1
  findings, the 3 iter-2 findings, the iter-3 verb-mapping fix, and now
  the iter-3 grep-narrative fix are all addressed. Build clean
  (0 warnings / 0 errors), 152 tests pass solution-wide
  (82 abstractions + 70 Teams).
- Stage 3.x command-handler dispatch + `AdaptiveCardBuilder` still pending
  downstream.
