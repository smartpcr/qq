# Iter notes — Stage 2.2 (Teams Activity Handler) — iter 2

## Prior feedback resolution
- [x] 1. ADDRESSED — `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs:332-385` + helper `MarkTeamChannelsInactiveAsync` at line 776. `OnTeamsMembersRemovedAsync` now classifies team scope via `TeamInfo.Id ?? TeamsChannelData.Team.Id` then enumerates `IConversationReferenceStore.GetActiveChannelsByTeamIdAsync(tenantId, teamId)` and calls `MarkInactiveByChannelAsync` once per channel. New test `OnTeamsMembersRemovedAsync_TeamScope_MarksEachChannelInactive` seeds 3 channels and asserts all 3 are marked.
- [x] 2. ADDRESSED — `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs:425-451`. `OnInstallationUpdateActivityAsync` remove path now classifies team-scope via `ExtractTeamId(activity) || ExtractChannelId(activity)` (NOT via "aadObjectId-absent"), so a team-level `remove` carrying the installer's AAD ID correctly fans out per-channel instead of marking the user inactive. Audit action becomes `AppUninstalledFromTeam`. New test `OnInstallationUpdateActivityAsync_Remove_TeamScope_MarksEachChannelInactiveAndAudits` covers it.
- [x] 3. ADDRESSED — `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs:642-685` (`PersistConversationReferenceAsync`). When `channelId` is present: `AadObjectId = null`, `InternalUserId = null`, `TeamId = ExtractTeamId(activity)`. Added new `TeamId` field to `TeamsConversationReference` to support the team-scope channel enumeration in item 1. New test `OnMessageActivityAsync_ChannelMessage_SavesReferenceWithNullAadObjectIdAndPopulatedTeamId`.
- [x] 4. ADDRESSED — `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs:233-241` + helper `LogCommandReceivedAsync` at line 816. After successful identity+RBAC and before persisting the conv-ref / dispatching, the handler now emits `AuditEntry { EventType = CommandReceived, Outcome = Success, Action = "CommandReceived" }` with a payload carrying the canonical verb + normalized text. New test `OnMessageActivityAsync_AuthorizedCommand_EmitsCommandReceivedAuditBeforeDispatch`.
- [x] 5. ADDRESSED — `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs:854-911` (`BuildAccessDeniedCardActivity`). Both rejection paths now send an `Activity` with an `Attachment { ContentType = "application/vnd.microsoft.card.adaptive", Content = AdaptiveCard }`. Card carries Title + Reason + RequiredRole (when present). Plain-text fallback retained in `Activity.Text` so non-AC-capable channels still see a message. New tests assert `attachment.ContentType == "application/vnd.microsoft.card.adaptive"` for both unmapped and unauthorized cases.
- [x] 6. ADDRESSED — New tests added: `OnTeamsMembersRemovedAsync_TeamScope_MarksEachChannelInactive` and `OnTeamsMembersRemovedAsync_TeamScope_AuditPayloadCarriesTeamIdAndChannelCount` for team `MembersRemoved`; `OnInstallationUpdateActivityAsync_Remove_TeamScope_MarksEachChannelInactiveAndAudits` for team `installationUpdate.remove`. Total Teams tests: 29 (up from 24); 111 tests pass solution-wide.

## Files touched this iter
- `src/AgentSwarm.Messaging.Teams/TeamsConversationReference.cs` — added `TeamId` field (null for personal scope).
- `src/AgentSwarm.Messaging.Teams/IConversationReferenceStore.cs` — added `GetActiveChannelsByTeamIdAsync(tenantId, teamId, ct)` to enumerate channels in a team for team-scope uninstall fan-out.
- `src/AgentSwarm.Messaging.Teams/TeamsSwarmActivityHandler.cs` — multiple structural changes:
  - `PersistConversationReferenceAsync`: channel-scope refs now have `AadObjectId = null` + populated `TeamId`; personal-scope refs unchanged.
  - `OnTeamsMembersRemovedAsync`: scope classification via `TeamInfo.Id ?? TeamsChannelData.Team.Id`; team-scope path calls new `MarkTeamChannelsInactiveAsync` to fan out per-channel.
  - `OnInstallationUpdateActivityAsync`: remove path classifies via `ExtractTeamId ?? ExtractChannelId` (NOT by AAD-presence) so team uninstalls don't get misclassified as personal.
  - NEW helpers: `ExtractTeamId`, `IsTeamScope`, `MarkTeamChannelsInactiveAsync`, `LogCommandReceivedAsync`, `BuildAccessDeniedCardActivity`.
  - `LogInstallAuditAsync` signature: added `extraPayload: IReadOnlyDictionary<string, object?>?` so install audits can attach `teamId` and `channelsMarkedInactive`.
  - `OnMessageActivityAsync`: after auth succeeds, emit `CommandReceived` audit BEFORE persist+dispatch; rejection paths replaced plain-text `SendActivityAsync(string)` with `SendActivityAsync(IMessageActivity)` carrying an Adaptive Card attachment.
- `tests/AgentSwarm.Messaging.Teams.Tests/TestDoubles.cs` — added `TeamChannels` keyed dictionary + `TeamChannelLookups` list to `RecordingConversationReferenceStore`; implemented `GetActiveChannelsByTeamIdAsync`.
- `tests/AgentSwarm.Messaging.Teams.Tests/HandlerFactory.cs` — `Harness` now exposes the `InertBotAdapter` instance so tests can assert `Sent` activities (Adaptive Card attachment). New activity factories `NewMembersRemovedTeamActivity`, `NewTeamInstallationUpdateActivity`, `NewChannelMessageActivity`. Added overload `ProcessAsync(Harness, Activity)` that reuses the harness adapter.
- `tests/AgentSwarm.Messaging.Teams.Tests/TeamsSwarmActivityHandlerTests.cs` — switched all tests to the `ProcessAsync(harness, …)` overload; updated rejection tests to assert Adaptive Card content-type; added 5 new tests for items 1-6 above.
- `tests/AgentSwarm.Messaging.Teams.Tests/CorrelationIdPropagationTests.cs` — switched to `ProcessAsync(harness, …)` overload.

## Decisions made this iter
- **Two-call shape for team-scope uninstall**: `GetActiveChannelsByTeamIdAsync` first, then loop `MarkInactiveByChannelAsync` — matches the impl-plan wording ("for each channel in the team") and keeps the bulk method out of the store interface so SQL implementations can rely on the existing per-channel update path.
- **Audit signal carries channels-marked count**: install/uninstall audit payloads now include `channelsMarkedInactive` so operators reviewing the audit trail can spot fan-out anomalies (e.g., a team-uninstall that found zero stored channels — likely indicates the bot was never used in the team).
- **`channelsMarked` fallback**: when `teamId` is unknown (channel data omits team), `MarkTeamChannelsInactiveAsync` falls back to the activity's own `channelId` so the request is not silently lost. Logged but not flagged as an error.
- **Channel-scope `InternalUserId` also nulled**: paired with the AAD-ID nulling because a channel reference doesn't belong to a single user; storing the installer's `InternalUserId` would mis-route proactive sends.
- **Audit payload uses ordinal-comparer Dictionary<string, object?>** so JSON serialization order is stable for checksum determinism (downstream `SqlAuditLogger` in Stage 5.2 will checksum-verify on read).

## Dead ends tried this iter
- Considered adding `MarkInactiveByTeamIdAsync(tenantId, teamId)` to the store interface as a bulk operation. Rejected: the implementation-plan wording explicitly says "call MarkInactiveByChannelAsync for each channel"; a bulk method would diverge from that contract and remove the natural fan-out audit signal.
- Tried using `Microsoft.Bot.Schema.Teams.NotificationInfo` or other typed AC objects to build the card — overkill for an access-denied card. Anonymous-object body + `Attachment.Content` is sufficient and avoids adding `Microsoft.AdaptiveCards` to the package graph.

## Open questions surfaced this iter
- None blocking. The 6 evaluator items map cleanly to structural changes.

## What's still left
- Nothing for Stage 2.2 iter 2. Build clean (0/0), 111 tests pass (82 abstractions + 29 Teams).
- Stage 2.1 DI wiring + Stage 3.x dispatch/card-handler still pending.
