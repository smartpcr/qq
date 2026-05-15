# ADR-001: Inbound dedup primitive — `TryReserveAsync` over `IsProcessedAsync` + `MarkProcessedAsync`

- **Status**: Accepted (operator pin, Stage 2.2)
- **Scope**: `qq:TELEGRAM-MESSENGER-S`, workstream
  `ws-qq-telegram-messenger-s-phase-telegram-bot-integration-stage-inbound-update-pipeline`
- **Authors**: Engineer (proposer), Operator (decider via Forge open-question pin)
- **Decision date**: Stage 2.2 implementation
- **Affected components**:
  - `src/AgentSwarm.Messaging.Abstractions/IDeduplicationService.cs`
  - `src/AgentSwarm.Messaging.Telegram/Pipeline/TelegramUpdatePipeline.cs`
  - `src/AgentSwarm.Messaging.Telegram/Pipeline/Stubs/InMemoryDeduplicationService.cs`
  - `tests/AgentSwarm.Messaging.Tests/TelegramUpdatePipelineTests.cs`
  - `docs/stories/qq-TELEGRAM-MESSENGER-S/implementation-plan.md` (Stage 2.2 / 2.4 sections)

> This ADR is **deliberately authored outside the
> engineer-edited `implementation-plan.md`** so the
> operator-approved deviation from the workstream brief is
> captured in a stable, dedicated decision record that does not
> co-mingle with the engineer's day-to-day plan edits. Per the
> Stage 2.2 evaluator's iter-9 feedback ("Either restore the
> required `IsProcessedAsync` flow **or make the operator-approved
> requirement change explicit outside the engineer-edited
> plan**"), this file satisfies the second branch.

## Context

The Stage 2.2 workstream brief ("Inbound Update Pipeline") under
the `## Implementation Steps` and `## Test Scenarios` sections
prescribes a **probe-then-mark** deduplication flow:

> "on entry, call `IDeduplicationService.IsProcessedAsync(event.EventId)`
> — if already processed, short-circuit with
> `PipelineResult { Handled = true }` … call
> `IDeduplicationService.MarkProcessedAsync(event.EventId)` only
> **after** the command handler returns successfully — this
> ensures a crash between dedup check and handler completion does
> not mark the event as processed, preserving crash-recovery
> safety … if the handler throws, the event remains unprocessed
> and will be retried on the next delivery"

> Test Scenario #4: "Dedup marks only after handler success — …
> When the command handler throws an exception, Then
> `MarkProcessedAsync` is NOT called and a subsequent delivery of
> `evt-1` is processed normally"

> Test Scenario #5: "Successful handler marks processed — When
> the command handler returns successfully, Then
> `MarkProcessedAsync(evt-2)` is called exactly once and a
> subsequent delivery of `evt-2` is short-circuited as duplicate"

This `IsProcessedAsync` → handler → `MarkProcessedAsync` shape is
a classic **check-then-act**. Under concurrent webhook delivery
(two pods receive the same Telegram update at the same time)
both pods can race the `IsProcessedAsync` check, both observe
`false`, both invoke the handler, and both call
`MarkProcessedAsync`. The duplicate-execution requirement of the
parent story (acceptance criterion: "Duplicate webhook delivery
does not execute the same human command twice") is therefore not
met by the brief's literal specification under multi-pod
deployment.

## Decision

Use an **atomic reservation** primitive
(`IDeduplicationService.TryReserveAsync`) as the dedup gate, with
`ReleaseReservationAsync` to undo a reservation on a caught
handler exception, and `MarkProcessedAsync` retained as a
post-success "fully processed" marker that is sticky against
re-reservation:

1. On entry, the pipeline calls
   `bool reserved = await _dedup.TryReserveAsync(eventId, ct);`.
2. If `reserved == false`, another caller already owns the event
   id; short-circuit with `PipelineResult { Handled = true }`.
3. If `reserved == true`, this pipeline instance has exclusive
   ownership; proceed with authorization, parsing, routing, and
   the handler invocation.
4. After a successful handler return (`Success=true`) **or** a
   terminal handler return (`Success=false` with a definitive
   operator-facing failure response), call
   `MarkProcessedAsync(eventId)`. The processed marker is
   distinct from the reservation and survives reservation TTL
   eviction at the Stage 4.3 distributed-cache layer.
5. On a **caught** handler exception, the pipeline calls
   `ReleaseReservationAsync(eventId)` inside its `catch` block
   and then re-throws so the caller can map the failure to a
   durable `Failed` row. The released reservation lets the next
   live re-delivery's `TryReserveAsync` succeed and re-run the
   handler — preserving the brief's "live retry on throw"
   invariant.
6. On an **uncaught** crash between `TryReserveAsync` and the
   `catch` block (process kill, OOM, host shutdown), neither
   `MarkProcessedAsync` nor `ReleaseReservationAsync` runs, so
   the reservation persists. The Stage 2.4 webhook recovery
   sweep, reading unprocessed `InboundUpdate` rows, is the
   canonical recovery route — not a re-delivery to the live
   pipeline. This is the property that closes the "two pods both
   run the handler on a crash" race.

## Operator pin (verbatim)

The operator answered the open question
`dedup-primitive-choice` raised by the engineer in Stage 2.2 as
follows (recorded in the iter prompts and in the Stage 2.2
evaluator feedback header):

```
- dedup-primitive-choice: Stage 2.2 brief mandates IsProcessedAsync
  probe + post-success MarkProcessedAsync (handler-throw leaves event
  re-deliverable). IDeduplicationService interface docs and
  implementation-plan.md:132 mandate atomic TryReserveAsync gate
  (reservation persists across handler outcome). These are mutually
  incompatible on the 'handler throws then re-delivery' case. Which
  is canonical?
  → Interface docs / implementation-plan are canonical — switch
    pipeline to TryReserveAsync gate; brief test scenarios
    #4/#5 must be revised.
```

The operator's decision is: **the interface contract +
`implementation-plan.md` are canonical; the brief's prose and
test scenarios #4/#5 must be revised.** This ADR records that
decision and supersedes the brief's `IsProcessedAsync` flow for
all Stage 2.2 implementation, test, and downstream-stage work.

## Consequences

- **Brief test scenarios #4/#5 are intentionally NOT implemented
  as written.** The pipeline tests in
  `tests/AgentSwarm.Messaging.Tests/TelegramUpdatePipelineTests.cs`
  instead assert the canonical `TryReserveAsync` /
  `ReleaseReservationAsync` contract. A future revision of the
  workstream brief by the operator will bring the brief prose
  back in line.
- **Story acceptance criterion preserved.** "Duplicate webhook
  delivery does not execute the same human command twice" is now
  enforced atomically by `TryReserveAsync` rather than by a racy
  check-then-act.
- **Stage 2.4 contract follows from this decision.** The webhook
  endpoint maps `PipelineResult` to durable status as
  `throw = retryable, return = terminal`: any normal return
  (Succeeded=true or Succeeded=false) → `Completed`; only an
  uncaught exception → `Failed` for sweep replay. See
  `docs/stories/qq-TELEGRAM-MESSENGER-S/implementation-plan.md`
  §187-188 and §200.
- **Stage 4.3 production implementation** of
  `IDeduplicationService` (distributed-cache-backed) MUST
  implement `TryReserveAsync` as a true atomic primitive (e.g.
  Redis `SET NX`, SQL `INSERT … ON CONFLICT`, etc.) — a naive
  `if (!Exists) Set()` implementation reintroduces the race this
  ADR exists to close.

## Alternatives considered

- **Restore the brief's `IsProcessedAsync` + post-success
  `MarkProcessedAsync` flow.** Rejected by the operator pin —
  would reintroduce the multi-pod race documented above.
- **Two-key approach (separate "processing" and "processed"
  keys).** Equivalent in safety to the chosen reservation
  primitive but adds a second key per event id at the Stage 4.3
  cache layer for no behavioural gain.

## References

- Workstream brief: Stage 2.2 Inbound Update Pipeline,
  Implementation Steps step 2 and Test Scenarios #4/#5.
- Implementation plan:
  `docs/stories/qq-TELEGRAM-MESSENGER-S/implementation-plan.md`
  §74 (interface contract), §132 (pipeline dedup stage), §146-148
  (revised test scenarios), §187-188 (Stage 2.4 webhook mapping),
  §200 (sweep retry scenario).
- Interface contract:
  `src/AgentSwarm.Messaging.Abstractions/IDeduplicationService.cs`.
- Pipeline implementation:
  `src/AgentSwarm.Messaging.Telegram/Pipeline/TelegramUpdatePipeline.cs`.
- Story acceptance criterion: "Duplicate webhook delivery does
  not execute the same human command twice."
