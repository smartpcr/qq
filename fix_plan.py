import os

filepath = r'E:\forge\qq\.worktree\spawn-story-qq-MICROSOFT-TEAMS-MESS-plan\docs\stories\qq-MICROSOFT-TEAMS-MESS\implementation-plan.md'

with open(filepath, 'r', encoding='utf-8') as f:
    lines = f.readlines()

# === FIX 1: Remove 5 contract-only steps from Stage 1.2 ===
# Lines 33, 36, 37, 38, 39 (1-indexed) = 32, 35, 36, 37, 38 (0-indexed)
lines_to_remove = {32, 35, 36, 37, 38}
new_lines = [line for i, line in enumerate(lines) if i not in lines_to_remove]

# === FIX 1 continued: Add file-creation steps to Stage 2.1 ===
target_idx = None
for i, line in enumerate(new_lines):
    if line.startswith('- [ ] Create project `AgentSwarm.Messaging.Teams` with NuGet references'):
        target_idx = i
        break

assert target_idx is not None, "Could not find Stage 2.1 project creation step"

new_step_1 = ('- [ ] Create `IConversationReferenceStore.cs`, `ICardActionHandler.cs`, and '
    '`ITeamsCardLifecycleService.cs` interface files in the `AgentSwarm.Messaging.Teams` project. '
    '`IConversationReferenceStore` defines the full method surface: `SaveOrUpdateAsync`, `GetAsync`, '
    '`GetAllActiveAsync`, `GetByAadObjectIdAsync(string tenantId, string aadObjectId)` (lookup by '
    'Teams-native AAD identity key), `GetByInternalUserIdAsync(string tenantId, string internalUserId)` '
    '(lookup by orchestrator identity key for proactive routing), `GetByChannelIdAsync(string tenantId, '
    'string channelId)`, `MarkInactiveAsync(string tenantId, string aadObjectId)` (user-scoped uninstall), '
    '`MarkInactiveByChannelAsync(string tenantId, string channelId)` (channel-scoped uninstall per '
    '`architecture.md` \u00a74.2 lines 543-544), `IsActiveAsync(string tenantId, string aadObjectId)`, '
    '`DeleteAsync(string tenantId, string aadObjectId)`, `DeleteByChannelAsync(string tenantId, string '
    'channelId)` (administrative cleanup only). The dual-key model (separate `AadObjectId` and '
    '`InternalUserId` fields per `architecture.md` \u00a7TeamsConversationReference lines 383-384) '
    'supports two lookup paths. `ICardActionHandler` defines `HandleAsync(ITurnContext turnContext, '
    'CancellationToken ct)` returning `AdaptiveCardInvokeResponse` (uses Bot Framework types, hence '
    'placed in Teams not Abstractions). `ITeamsCardLifecycleService` defines '
    '`UpdateCardToResolvedAsync(string questionId, string resolvedByUserId, string actionValue, '
    'CancellationToken ct)` and `DeleteCardAsync(string questionId, CancellationToken ct)` \u2014 the '
    'SOLE call path for card update/delete operations.\n')

new_step_2 = ('- [ ] Create `TeamsCardState.cs` record and `ICardStateStore.cs` interface in the '
    '`AgentSwarm.Messaging.Teams` project (co-located to avoid a circular Abstractions \u2192 Teams '
    'assembly dependency since `ICardStateStore.SaveAsync` takes `TeamsCardState` as a parameter). '
    '`TeamsCardState` record has fields: `QuestionId` (string), `ActivityId` (string \u2014 Teams '
    'message ID), `ConversationId` (string), `ConversationReferenceJson` (string \u2014 serialized '
    '`ConversationReference` for background rehydration), `Status` (string \u2014 '
    '`Pending`/`Answered`/`Expired`), `CreatedAt` (DateTimeOffset), `UpdatedAt` (DateTimeOffset). '
    '`ICardStateStore` interface defines: `SaveAsync(TeamsCardState, CancellationToken)`, '
    '`GetByQuestionIdAsync(string questionId, CancellationToken)` returning the full `TeamsCardState` '
    'including `ConversationReferenceJson`, `UpdateStatusAsync(string questionId, string newStatus, '
    'CancellationToken)`. Cross-doc note: `architecture.md` \u00a74.3 line 614 lists `ICardStateStore` '
    'under Abstractions, but this plan co-locates it with `TeamsCardState` in Teams to avoid the '
    'circular dependency.\n')

new_lines.insert(target_idx + 1, new_step_2)
new_lines.insert(target_idx + 1, new_step_1)

# === FIX 2: Split monolithic DI registration into 4 smaller steps ===
di_idx = None
for i, line in enumerate(new_lines):
    if line.startswith('- [ ] Register all interface stubs in DI so that'):
        di_idx = i
        break

assert di_idx is not None, "Could not find DI registration step"

di_step_1 = ('- [ ] Register conversation reference and identity stubs in DI (`Program.cs` in '
    '`AgentSwarm.Messaging.Worker`): `InMemoryConversationReferenceStore` as '
    '`IConversationReferenceStore` \u2014 a `ConcurrentDictionary`-backed in-memory implementation '
    'created in this step (sufficient for local dev and integration tests; replaced by '
    '`SqlConversationReferenceStore` in Stage 4.1); `DefaultDenyIdentityResolver` as '
    '`IIdentityResolver` and `DefaultDenyAuthorizationService` as `IUserAuthorizationService` (both '
    'created in Stage 1.2; replaced by full RBAC implementations in Stage 5.1).\n')

di_step_2 = ('- [ ] Register command dispatcher and question store stubs in DI: a no-op '
    '`ICommandDispatcher` stub that returns a "commands not yet available" reply (replaced by concrete '
    '`CommandDispatcher` in Stage 3.2); `InMemoryAgentQuestionStore` as `IAgentQuestionStore` \u2014 '
    'backed by a `ConcurrentDictionary<string, AgentQuestion>` that supports `SaveAsync` (stores keyed '
    'by `QuestionId`), `GetByIdAsync` (lookup by key), `TryUpdateStatusAsync` (compare-and-set using '
    '`Interlocked` semantics), `GetMostRecentOpenByConversationAsync` (LINQ scan filtered by '
    '`ConversationId` and `Status = "Open"`, ordered by `CreatedAt` descending), '
    '`UpdateConversationIdAsync` (updates `ConversationId` field), and `GetOpenExpiredAsync` (LINQ scan '
    'filtered by `Status = "Open"` and `ExpiresAt < cutoff`, ordered ascending, limited by '
    '`batchSize`). This in-memory stub is sufficient for Stage 2.3 and Stage 2.2 before the SQL-backed '
    '`SqlAgentQuestionStore` lands in Stage 3.3.\n')

di_step_3 = ('- [ ] Register card-related stubs in DI: `NoOpCardStateStore` as `ICardStateStore` \u2014 '
    'an in-memory stub using `ConcurrentDictionary` (replaced by `SqlCardStateStore` in Stage 3.3); '
    '`NoOpCardActionHandler` as `ICardActionHandler` \u2014 returns a "card actions not yet available" '
    '`AdaptiveCardInvokeResponse` (replaced by `CardActionHandler` in Stage 3.3); '
    '`NoOpCardLifecycleService` as `ITeamsCardLifecycleService` \u2014 logs and no-ops (replaced by '
    '`TeamsCardLifecycleService` in Stage 3.3).\n')

di_step_4 = ('- [ ] Register infrastructure stubs in DI: `InMemoryActivityIdStore` as '
    '`IActivityIdStore` (created earlier in this stage); `ChannelInboundEventPublisher` as '
    '`IInboundEventPublisher` \u2014 a `System.Threading.Channels.Channel<MessengerEvent>`-backed '
    'implementation created in this step (unbounded channel connecting event producers to '
    '`TeamsMessengerConnector.ReceiveAsync`); `NoOpAuditLogger` as `IAuditLogger` (created in Stage '
    '1.3; replaced by `SqlAuditLogger` in Stage 5.2); `NoOpMessageOutbox` as `IMessageOutbox` '
    '(created in Stage 1.2; replaced by `SqlMessageOutbox` in Stage 6.1).\n')

new_lines[di_idx:di_idx+1] = [di_step_1, di_step_2, di_step_3, di_step_4]

# === FIX 3: Split Stage 5.1 TeamsAppPolicyOptions step into 3 smaller steps ===
policy_idx = None
for i, line in enumerate(new_lines):
    if line.startswith('- [ ] Implement Teams admin deployment policy integration'):
        policy_idx = i
        break

assert policy_idx is not None, "Could not find TeamsAppPolicyOptions step"

policy_step_1 = ('- [ ] Create `TeamsAppPolicyOptions` configuration class in '
    '`AgentSwarm.Messaging.Teams.Security` with properties: `RequireAdminConsent` (bool, default '
    '`true`), `AllowedAppCatalogScopes` (list of `"organization"` | `"personal"`), and '
    '`BlockSideloading` (bool, default `true` in production). Bind from configuration in `Program.cs` '
    'and validate at startup that `MicrosoftAppId` has a corresponding Entra ID app registration with '
    'the `TeamsAppInstallation.ReadForUser.All` Graph API permission (required for reading installation '
    'state).\n')

policy_step_2 = ('- [ ] Implement `TeamsAppPolicyHealthCheck : IHealthCheck` in '
    '`AgentSwarm.Messaging.Teams.Security` that calls the Microsoft Graph '
    '`/appCatalogs/teamsApps?$filter=externalId eq \'{MicrosoftAppId}\'` endpoint to verify the bot is '
    'published to the organization catalog (when `AllowedAppCatalogScopes` includes `"organization"`). '
    'Returns `Healthy` when the app is found; returns `Degraded` with detail explaining the app is not '
    'found and admins must publish it via Teams admin center when the app is missing. Logs a warning if '
    'the app is not found. Register as a health check in `Program.cs`.\n')

policy_step_3 = ('- [ ] Create `docs/stories/qq-MICROSOFT-TEAMS-MESS/deployment-checklist.md` '
    'documenting the Entra ID admin consent steps: (1) register the bot app in Entra ID with '
    '`TeamsAppInstallation.ReadForUser.All` and `User.Read` delegated permissions, (2) grant admin '
    'consent in the Azure portal, (3) configure Teams admin center app setup policy to pre-install or '
    'allow the bot for target users/groups, (4) set `RequireAdminConsent = true` in '
    '`TeamsAppPolicyOptions` to enforce that only admin-consented installations are trusted. This '
    'satisfies the story requirement "Integrate with Entra ID / Teams app installation policies" by '
    'providing operator documentation for admin deployment policy configuration.\n')

new_lines[policy_idx:policy_idx+1] = [policy_step_1, policy_step_2, policy_step_3]

with open(filepath, 'w', encoding='utf-8') as f:
    f.writelines(new_lines)

print(f'File written with {len(new_lines)} lines')
with open(filepath, 'r', encoding='utf-8') as f:
    content = f.read()
print(f'File size: {len(content.encode("utf-8"))} bytes')
