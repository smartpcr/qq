# Iter notes — Stage 2.2 (Teams Activity Handler) — iter 3

## Prior feedback resolution
- [x] 1. ADDRESSED — `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs:816-863` (`LogCommandReceivedAsync`) + new helper `ExtractCommandBody` at line 864. The command-audit record now matches the e2e contract in `docs/stories/qq-MICROSOFT-TEAMS-MESS/e2e-scenarios.md:809-823`:
  - `Action` is now the **canonical command verb** (`"agent ask"`, `"approve"`, etc.) — was the literal string `"CommandReceived"`.
  - `PayloadJson` is now `{"body":"<remainder>"}` — was `{"command":"<verb>","normalizedText":"<full>"}`.
  - Body extraction is case-insensitive at the verb position and preserves the original casing + interior whitespace of the remainder (verified by the `multi   space body` theory row).
  - Updated existing test `OnMessageActivityAsync_AuthorizedCommand_EmitsCommandReceivedAuditBeforeDispatch` to assert the exact `audit.Action == "agent ask"` and `audit.PayloadJson == "{\"body\":\"create e2e test scenarios for update service\"}"` strings from e2e:820. Added new `[Theory]` `OnMessageActivityAsync_AuthorizedCommand_AuditActionAndBodyMatchCanonicalSchema` with 5 InlineData rows covering all canonical-verb shapes (`agent status` / `approve` / `approve <id>` / `pause` with trailing whitespace / `agent ask` with multi-space body).
  - Verification (literal `grep -rnF` of the pre-edit symbols):
    ```
    $ grep -rnF 'action: "CommandReceived"' src/ tests/
    (empty)
    $ grep -rnF 'command = commandVerb' src/ tests/
    (empty)
    $ grep -rnF 'Assert.Equal("CommandReceived", audit.Action)' src/ tests/
    (empty)
    ```

## Files touched this iter
- `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs` — rewrote `LogCommandReceivedAsync` payload shape + Action; added `ExtractCommandBody` helper (preserves body casing + interior whitespace via single `TrimStart` of the leading whitespace following the verb).
- `tests/AgentSwarm.Messaging.Teams.Tests/TeamsSwarmActivityHandlerTests.cs` — tightened existing audit-shape test to assert the exact e2e contract; added new theory covering 5 canonical-verb shapes (no body, single-token body, trailing whitespace, multi-space body).

## Decisions made this iter
- **Body extraction preserves interior whitespace**: only the single space between verb and body is stripped (via index skip), then `TrimStart` strips any additional leading whitespace from the body region. This means `"agent ask  multi   space body"` → body `"multi   space body"` (interior triple-space preserved). Rationale: the body is intended to be opaque to the audit logger; compliance reviewers will see the user-entered text verbatim. The verb-boundary requirement is just "verb followed by whitespace OR end-of-text".
- **`Action` field uses the canonical lowercase verb** returned by `ExtractCommandVerb` (which already lowercases via `ToLower(InvariantCulture)`). This matches the e2e table verbatim (`agent ask`, `approve`, etc.) and aligns with `tech-spec.md` §4.3's `Action` field doc which gives `agent ask`, `approve`, etc. as canonical examples.
- **Did NOT change install / rejection audit Actions** (`AppInstalled`, `BotAddedToTeam`, `UnmappedUserRejected`, `InsufficientRoleRejected`, etc.). The evaluator's item-1 critique scoped strictly to the inbound-command audit shape; install/rejection events are lifecycle/security records and the e2e contract doesn't constrain their `Action` strings beyond the `EventType` discriminator.
- **Test split into one targeted Fact + one Theory**: keeps the literal e2e:820 string assertion visible at a glance, while the theory exercises edge cases (empty body, trailing whitespace, interior whitespace) that would otherwise hide in a single mega-test.

## Dead ends tried this iter
- None — the fix was structural (rewrite the payload-build + Action assignment in one method), the test update was a direct contract assertion, and the build/test cycle was clean on the first attempt.

## Open questions surfaced this iter
- None. The `Messenger` column in the e2e:813-823 table is NOT a property of the canonical `AuditEntry` record (Stage 1.3 schema). The evaluator did not flag it. Treating it as out-of-scope for Stage 2.2 — if a future workstream needs that column, it lands on the `AuditEntry` record itself, not in the Teams handler.

## What's still left
- Nothing for Stage 2.2 iter 3. Build clean (0 warnings / 0 errors), 116 tests pass solution-wide (82 abstractions + 34 Teams; was 29 Teams in iter 2, +1 Fact + 5 Theory rows = +5).
- Stage 2.1 DI wiring + Stage 3.x dispatch/card-handler still pending downstream.
