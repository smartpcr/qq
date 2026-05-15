# Iter notes — Stage 2.3 (Teams Messenger Connector) — iter 5

## Prior feedback resolution

Iter-2 evaluator (score 88, verdict iterate) flagged TWO items: a real
production-DI gap (item 1) and a narrative-quality issue (item 2). Both
addressed below.

- [x] 1. FIXED — `src/AgentSwarm.Messaging.Teams/TeamsServiceCollectionExtensions.cs:115-132`.
  Added a default `services.TryAddSingleton<IConversationReferenceRouter>(...)`
  registration to `AddTeamsMessengerConnector` that adapts the host-supplied
  `IConversationReferenceStore` singleton via an `as IConversationReferenceRouter`
  cast. The canonical store implementations (Stage 2.1's
  `InMemoryConversationReferenceStore` and Stage 4.1's
  `SqlConversationReferenceStore`) BOTH implement BOTH interfaces, so the
  cast resolves to the same singleton without an extra type and without
  requiring the host to write a router-only registration. When the
  registered store does NOT implement `IConversationReferenceRouter`, the
  factory throws `InvalidOperationException` with a descriptive message
  that names the offending store type, both interfaces, and the canonical
  fix paths. The `TryAddSingleton` guard means hosts that pre-register an
  explicit router (e.g. for testing) win — the default wiring is a no-op
  in that case. Three new regression tests landed in
  `tests/AgentSwarm.Messaging.Teams.Tests/TeamsServiceCollectionExtensionsTests.cs`:
  - `AddTeamsMessengerConnector_StoreImplementsBothInterfaces_AutoWiresRouterToSameSingleton` —
    success path: store implements both, router and store resolve to the
    same instance, and the keyed `IMessengerConnector` resolves end-to-end
    via the auto-wired router.
  - `AddTeamsMessengerConnector_StoreDoesNotImplementRouter_ResolvingRouterThrowsWithDescriptiveMessage` —
    failure path: the `InvalidOperationException` message contains the
    store's full type name, the `IConversationReferenceRouter` symbol, and
    the `AddTeamsMessengerConnector` helper name (so an operator can grep
    their stack for the fix).
  - `AddTeamsMessengerConnector_ExplicitRouterPreRegistered_AutoWiringIsNoOp` —
    idempotency: an explicit router registration before the helper call
    is preserved by `TryAddSingleton`, and the connector receives that
    instance rather than the cast adapter.

  Two new test doubles landed in the same file —
  `DualInterfaceConversationReferenceStore` (mimics the canonical Stage 2.1
  + 4.1 stores; satisfies BOTH interfaces) and
  `StoreOnlyConversationReferenceStore` (mimics a host-supplied store that
  satisfies only `IConversationReferenceStore`). Both are standalone
  implementations rather than subclasses of the existing
  `ConnectorRecordingConversationReferenceStore` (which is `sealed`); the
  CS0509 compile failure on the first attempt forced this restructure
  before the suite would build.

  Verification (the `IConversationReferenceRouter` symbol the evaluator
  flagged is now bound through the helper):
  ```
  $ grep -nF 'IConversationReferenceRouter' src/AgentSwarm.Messaging.Teams/TeamsServiceCollectionExtensions.cs
  32:/// Also fills in a default <see cref="IConversationReferenceRouter"/> registration that
  84:    /// <see cref="IConversationReferenceRouter"/> registration that adapts the host-supplied
  86:    /// <see cref="IConversationReferenceRouter"/> for the contract documenting that the
  105:        // Default IConversationReferenceRouter wiring — adapt the host-supplied
  112:        // wire a store NOT implementing IConversationReferenceRouter get a clear startup
  115:        services.TryAddSingleton<IConversationReferenceRouter>(sp =>
  118:            return store as IConversationReferenceRouter
  122:                    "IConversationReferenceRouter. TeamsMessengerConnector.SendMessageAsync " +
  129:                    "separate IConversationReferenceRouter implementation BEFORE calling " +
  ```

- [x] 2. ADDRESSED — narrative correction. The iter-2 / iter-3 / iter-4
  iter-notes claimed `.forge/iter-notes.md` was edited as part of the
  changed-file list, but `.forge/` is gitignored and not part of the
  operator-supplied ground-truth diff. The Working-Notes-REQUIRED instruction
  in the agent prompt tells me to write `.forge/iter-notes.md` as the
  agent-to-future-self artifact; that file is NEVER part of the change
  accounting because it never lands in commits. This iter's `Files touched
  this iter` block (below) lists ONLY files that ARE in the worktree's
  ground-truth diff — `.forge/iter-notes.md` is intentionally NOT listed
  there even though it is being written, because Forge excludes the
  `.forge/` dir from the worktree's git index by design.

## Files touched this iter

- `src/AgentSwarm.Messaging.Teams/TeamsServiceCollectionExtensions.cs` —
  added the default `TryAddSingleton<IConversationReferenceRouter>` factory
  that adapts the host-supplied `IConversationReferenceStore` via an `as`
  cast, with a descriptive `InvalidOperationException` fallback when the
  store does not also implement the router contract; expanded the class +
  method xmldoc to document the auto-wiring behaviour and the host-override
  escape hatch (item 1).
- `tests/AgentSwarm.Messaging.Teams.Tests/TeamsServiceCollectionExtensionsTests.cs` —
  added three regression tests (success path, failure path, explicit-override
  no-op) plus two standalone test doubles
  (`DualInterfaceConversationReferenceStore` /
  `StoreOnlyConversationReferenceStore`) and a parameterized
  `BuildServicesWithoutSeparateRouter<TStore>` helper that intentionally
  omits the explicit router registration so the cast-adapter code path is
  the one under test (item 1).

## Decisions made this iter

- **Cast-adapter rather than a separate `ConversationReferenceStoreRouterAdapter`
  class.** The evaluator gave two options: "register the real store under
  the companion interface OR avoid the extra contract". I picked option 1
  (register the store under both contracts via a factory cast) because:
  (a) the canonical store implementations already satisfy both interfaces
  per `IConversationReferenceRouter.cs:26-29` (Stage 2.1 + Stage 4.1 design
  intent); (b) shipping a bridge class would add a third type that
  duplicates the store's state in a wrapper-of-wrapper pattern; (c) the
  cast in a factory delegate is the idiomatic .NET DI pattern for
  "adapter when both interfaces are on the same class". The fallback throw
  fires at FIRST resolution of `IConversationReferenceRouter` (eager during
  connector resolution if no other consumer triggers it earlier), not at
  first send — this is the closest .NET DI gets to compile-time validation
  without `ValidateOnBuild`.
- **`TryAddSingleton` for the new registration** matches the iter-2 fix's
  idempotency pattern. Tests that pre-register an explicit router (e.g.
  the existing `BuildServices()` helper that wires the
  `RecordingConversationReferenceRouter` test double) keep working because
  the explicit registration wins. Verified by the new
  `AddTeamsMessengerConnector_ExplicitRouterPreRegistered_AutoWiringIsNoOp`
  test.
- **Standalone test doubles, not subclasses** — the existing
  `ConnectorRecordingConversationReferenceStore` is `sealed` (CS0509 on
  the first build attempt). Two standalone classes mirror the same method
  surface, and the duplication is intentional: the Dual / Store-Only
  distinction is the EXACT contract being tested. Neither test double
  needs to record calls — they only need to expose a specific interface
  surface so the cast in `AddTeamsMessengerConnector` resolves
  predictably.
- **Narrative correction (item 2) is notes-only** — the production code is
  already correct (the `.forge/` dir was always gitignored and never
  intended to land in commits); the iter-2 evaluator's complaint was that
  earlier iter notes claimed it was in the changed-file list. This iter's
  notes correctly describe `.forge/iter-notes.md` as the
  agent-to-future-self artifact rather than a changed source file.

## Dead ends tried this iter

- First attempt at the test doubles tried to subclass
  `ConnectorRecordingConversationReferenceStore` and add the router
  interface — failed CS0509 because the base is `sealed`. Resolved by
  rewriting both doubles as standalone `IConversationReferenceStore`
  implementations.

## Open questions surfaced this iter

- None.

## What's still left

- Nothing for Stage 2.3 iter 5. Build clean (0 warnings / 0 errors), 155
  tests pass solution-wide (82 abstractions + 73 Teams; iter 4 had
  82 + 70 = 152, this iter adds +3 router-wiring regression tests).
- Stage 3.x command-handler dispatch + `AdaptiveCardBuilder` still pending
  downstream.
