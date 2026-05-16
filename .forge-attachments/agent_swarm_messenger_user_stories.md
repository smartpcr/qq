# Agent-Swarm Messenger — User Stories (Microsoft Teams)

> User stories for story `qq:MICROSOFT-TEAMS-MESS` ("Microsoft Teams Messenger
> Support"), focused on the **Stage 5.1: Tenant and Identity Validation**
> slice that this branch delivers. Each story is written in
> operator / agent-runtime / approver voice and traces directly to a row in
> the story brief's *Requirements* table or one of its *Acceptance Criteria*.
> The acceptance criteria are the requirements that the implementation must
> satisfy; the **Implementation** anchors at the end of each story point at
> the production source files that any reviewer can re-grep to confirm
> compliance.
>
> _Scope._ Adaptive-card composition, command dispatch, conversation-reference
> persistence, the Phase 6 outbox dispatch loop, and the P95 card-delivery
> performance SLA are owned by sibling stages — they appear in §3 with the
> stage that owns them so a reviewer can confirm Stage 5.1 has not silently
> taken on out-of-stage responsibility.

## 1. Roles and scope

| Role | Notes |
|------|-------|
| **Enterprise operator** | Configures `RbacOptions`, `TeamsAppPolicyOptions`, and `AllowedTenantIds`; owns deployment via the Entra admin centre + Teams admin centre. See `docs/stories/qq-MICROSOFT-TEAMS-MESS/deployment-checklist.md`. |
| **Approver** | Receives blocking Adaptive Cards; can approve / reject / escalate. RBAC role mapped via `RbacOptions.RoleCommands`. |
| **Viewer** | Can issue `agent status` only. Other commands return an `InsufficientRoleRejected` audit + polite rejection card. |
| **Unmapped user** | A Teams account whose `Activity.From.AadObjectId` does not match any internal user. `EntraIdentityResolver` returns null; activity handler emits HTTP 200 + access-denial Adaptive Card (per `tech-spec.md` §4.2 two-tier rejection model) and writes `UnmappedUserRejected` audit. |
| **Unauthorised tenant** | A Teams account whose token tenant does not match `AllowedTenantIds`. `TenantValidationMiddleware` returns HTTP 403 before the request reaches `CloudAdapter`; no bot response, no Adaptive Card. |

This document covers the **security and identity slice** delivered by Stage
5.1. Adaptive-card composition, command dispatch, conversation-reference
persistence, the outbox engine, and the P95 performance SLA are owned by other
stages and are referenced for context only — see §6 for cross-stage anchors.

---

## 2. In-scope user stories (Stage 5.1)

### US-01 — Tenant allow-list enforcement

> _As an enterprise operator, I want incoming Bot Framework HTTP requests from
> tenants outside the configured allow-list to be rejected at the HTTP layer,
> so that hostile or mis-routed traffic never reaches the activity pipeline._

**Acceptance criteria** _(implementation contract)_:
1. `TenantValidationMiddleware` runs in the ASP.NET Core pipeline **before**
   `CloudAdapter` (`TeamsSecurityApplicationBuilderExtensions.UseTeamsSecurity`).
2. The middleware reads the inbound activity **HTTP body** via
   `TenantValidationMiddleware.ExtractTenantIdAsync` — which inspects
   `channelData.tenant.id` first and then falls back to
   `conversation.tenantId` — and short-circuits with **HTTP 403 Forbidden**
   (`StatusCodes.Status403Forbidden`) when the extracted tenant ID is absent
   or not in `AllowedTenantIds`. The HTTP layer intentionally does **not**
   inspect the JWT bearer's `tid` claim — that defense-in-depth check is
   owned by `EntraTenantAwareClaimsValidator` (US-07) and runs later in the
   Bot Framework SDK token-validation pipeline. The middleware writes **no
   reply activity** and no Adaptive Card — there is no conversation context
   at the HTTP layer.
3. Exactly **one** audit record is written per blocked request:
   `IAuditLogger.LogAsync` with `EventType = "SecurityRejection"`,
   `Outcome = AuditOutcomes.Rejected`, `Action = "UnauthorizedTenantRejected"`.
   The Stage 2.1 `ILogger.LogWarning` call is removed by this stage and is not
   re-emitted alongside the audit record.
4. If `IAuditLogger.LogAsync` itself throws (audit sink unavailable), the
   middleware throws `UnauthorizedTenantAuditException` so the host's exception
   handler can return 5xx rather than silently swallowing the audit failure.

| Anchor | File / class |
|--------|--------------|
| Brief AC | "Unauthorized tenant/user is rejected." |
| Brief Requirements | _Security_ row: "Enforce tenant ID, user identity, Teams app installation, and RBAC." |
| Implementation | `src/AgentSwarm.Messaging.Teams/Security/TenantValidationMiddleware.cs` — `ExtractTenantIdAsync` body parse at lines 97–103 and 122–163; rejection action string at line 183; audit-failure escalation throws `UnauthorizedTenantAuditException` at line 240; the exception class itself at line 280. |
| Tests | `tests/AgentSwarm.Messaging.Teams.Tests/Security/TenantValidationMiddlewareTests.cs` |

---

### US-02 — Unmapped user rejection (two-tier rejection model)

> _As an enterprise operator, I want activities from Teams users who have no
> internal identity mapping to be rejected politely at the conversation layer
> (not silently dropped), so that the requester knows how to request access._

**Acceptance criteria**:
1. `EntraIdentityResolver.ResolveAsync` looks up the internal user identity by
   `Activity.From.AadObjectId` (the Entra AAD object ID — not the Teams
   display name and not `Activity.From.Id`).
2. When the resolver returns null, the activity handler returns the request
   with **HTTP 200** (so the Bot Framework channel does not retry) AND a
   single Adaptive Card explaining the rejection plus the request-access flow
   — this is the "two-tier" rejection model in `tech-spec.md` §4.2 (tenant
   rejection happens at HTTP; identity rejection happens at activity layer).
3. Exactly **one** audit record is written: `EventType = "SecurityRejection"`,
   `Outcome = AuditOutcomes.Rejected`, `Action = "UnmappedUserRejected"`,
   `ActorId` set to the AAD object ID, `TenantId` set to the resolved tenant.

| Anchor | File / class |
|--------|--------------|
| Brief AC | "Unauthorized tenant/user is rejected." |
| Implementation | `src/AgentSwarm.Messaging.Teams/Security/EntraIdentityResolver.cs`; rejection card composition lives in `TeamsSwarmActivityHandler` |
| Tests | `tests/AgentSwarm.Messaging.Teams.Tests/Security/EntraIdentityResolverTests.cs`, `RbacAadIdentityRegressionTests.cs` |

---

### US-03 — RBAC role-to-command enforcement

> _As an enterprise operator, I want each Teams command to require a specific
> role from `RbacOptions`, so that non-privileged users cannot approve, reject,
> escalate, pause, or resume agent work._

**Acceptance criteria**:
1. `RbacAuthorizationService.AuthorizeAsync(...)` looks up the user's role via
   `IUserRoleProvider` (falling back to `RbacOptions.DefaultRole` when the
   user has no explicit assignment in
   `RbacOptions.TenantRoleAssignments[tenantId][aadObjectId]`), looks up the
   role's allowed commands in `RbacOptions.RoleCommands` (the
   `IDictionary<string, IReadOnlyCollection<string>>` keyed by role name),
   and returns an `AuthorizationResult` record exposing
   `IsAuthorized` (bool), `UserRole` (string?, null when unmapped), and
   `RequiredRole` (string?, populated on denial so the access-denied
   Adaptive Card can explain which role is needed).
2. The authorize call passes the **AAD object ID** (not the internal user id
   and not the Teams display name) — this is the long-lived stable
   identifier per Entra. Regression coverage in
   `RbacAadIdentityRegressionTests`.
3. On rejection, exactly **one** audit record is written:
   `EventType = "SecurityRejection"`, `Outcome = AuditOutcomes.Rejected`,
   `Action = "InsufficientRoleRejected"`, plus the actor / tenant ids.
4. The four canonical `AuditOutcomes` values are `Success`, `Rejected`,
   `Failed`, `DeadLettered` (per `tech-spec.md` §4.3); rejection-reason codes
   live in `Action`, never in `Outcome`.

| Anchor | File / class |
|--------|--------------|
| Brief Requirements | _Commands_ row, _Security_ row |
| Implementation | `src/AgentSwarm.Messaging.Teams/Security/RbacAuthorizationService.cs`, `RbacOptions.cs`, `StaticUserRoleProvider.cs` |
| Tests | `RbacAuthorizationServiceTests.cs`, `RbacOptionsTests.cs`, `StaticUserRoleProviderTests.cs`, `RbacAadIdentityRegressionTests.cs` |

---

### US-04 — Proactive send target resolution and not-found contract

> _As an agent runtime, when I dispatch a proactive `AgentQuestion` or
> `MessengerMessage` to a user / channel whose conversation reference is
> missing or inactive, I want a deterministic exception with the targeting
> tuple, so that the outbox engine can dead-letter the message and the audit
> trail can identify the missing reference._

**Acceptance criteria**:
1. `TeamsProactiveNotifier` resolves the target reference via
   `IConversationReferenceStore.GetByInternalUserIdAsync(tenantId, userId, ct)`
   for user-scoped targets or
   `IConversationReferenceStore.GetByChannelIdAsync(tenantId, channelId, ct)`
   for channel-scoped targets.
2. When the reference is null (missing / inactive / never installed), the
   notifier throws **`ConversationReferenceNotFoundException`** constructed via
   the factory methods `ConversationReferenceNotFoundException.ForUser(tenantId,
   userId)` or `.ForChannel(tenantId, channelId)` (with an optional `questionId`
   overload for blocking-question dispatch).
3. The exception carries the targeting tuple so the outbox engine can include
   `(TenantId, UserOrChannelId)` in its dead-letter audit record.

| Anchor | File / class |
|--------|--------------|
| Brief AC | "Conversation references are stored and reused for proactive messaging." |
| Implementation | `src/AgentSwarm.Messaging.Teams/TeamsProactiveNotifier.cs` (lines 183, 190, 236, 270, 277, 316); `src/AgentSwarm.Messaging.Teams/ConversationReferenceNotFoundException.cs` |
| Tests | `TeamsProactiveNotifierTests.cs`, `TeamsProactiveNotifierInstallationGateTests.cs` |

---

### US-05 — User-scoped installation gate

> _As an enterprise operator, I want proactive sends to a user whose Teams app
> installation has been removed to be skipped at the gate (before the Bot
> Framework call), dead-lettered, and audited, so that the system does not waste
> Bot Connector calls on uninstalled apps and the operator has a record._

**Acceptance criteria**:
1. `InstallationStateGate.CheckAsync(question, outboxEntryId, correlationId,
   ct)` (for `AgentQuestion`-driven sends) and
   `InstallationStateGate.CheckTargetAsync(tenantId, userId, channelId,
   correlationId, outboxEntryId, ct)` (for `MessengerMessage`-driven sends)
   branch on the target tuple and call
   `IConversationReferenceStore.IsActiveByInternalUserIdAsync(tenantId,
   internalUserId, ct)` for user-scoped targets — a **scope-specific**
   active-check, not a reverse-lookup through `GetByInternalUserIdAsync`
   (which only returns active references and cannot distinguish "inactive"
   from "missing"). Both methods return an `InstallationStateGateResult`
   record (`Allowed` bool + `Reason` string).
2. When the gate returns `Allowed = false`, `TeamsProactiveNotifier`:
   - **does not** call Bot Framework `ContinueConversationAsync`;
   - calls `IMessageOutbox.DeadLetterAsync(outboxEntryId, reason, ct)` with the
     `outboxEntryId` obtained from `ProactiveSendContext.CurrentOutboxEntryId`
     (set by the Phase 6 outbox engine via
     `ProactiveSendContext.WithOutboxEntryId`);
   - writes an audit record with `EventType = "Error"`,
     `Outcome = AuditOutcomes.Failed`, `Action = "InstallationGateRejected"`,
     and the rejection reason in detail;
   - throws `ConversationReferenceNotFoundException.ForUser(tenantId, userId)`
     so the caller's exception handler can surface the failure.
3. If the audit logger or the outbox dead-letter call itself fails, the gate
   throws `InstallationStateGateComplianceException` — the gate fails closed
   so a compliance-evidence outage cannot silently allow a send.
4. The gate uses the activity-driven installation state (`InstallationUpdate`
   activities from the bot handler — Stage 2.2), **not** Microsoft Graph (per
   `tech-spec.md` §5.1 R-5).

| Anchor | File / class |
|--------|--------------|
| Brief Requirements | _Identity_ row + _Reliability_ row |
| Implementation | `src/AgentSwarm.Messaging.Teams/Security/InstallationStateGate.cs`; `TeamsProactiveNotifier.cs` lines 159–195 |
| Tests | `InstallationStateGateTests.cs`, `TeamsProactiveNotifierInstallationGateTests.cs`, `TeamsMessengerConnectorInstallationGateTests.cs` |

---

### US-06 — Channel-scoped installation gate

> _As an enterprise operator, I want proactive sends to a channel whose Teams
> app has been uninstalled (via `MarkInactiveByChannelAsync`) to follow the
> same gate-then-dead-letter-then-audit sequence as user-scoped targets._

**Acceptance criteria**:
1. The same `InstallationStateGate.CheckAsync` / `CheckTargetAsync` methods
   from US-05 branch on the channel target and call
   `IConversationReferenceStore.IsActiveByChannelAsync(tenantId, channelId,
   ct)` (a single gate type covers both scopes; the question/target argument
   selects the path).
2. Inactive channel target → no Bot Framework call → `DeadLetterAsync` →
   audit `EventType = "Error"`, `Outcome = AuditOutcomes.Failed`,
   `Action = "InstallationGateRejected"` → throw
   `ConversationReferenceNotFoundException.ForChannel(tenantId, channelId)`.
3. Active channel target → gate allows → normal Bot Framework
   `ContinueConversationAsync` proceeds.
4. Compliance-evidence failure on the channel path also throws
   `InstallationStateGateComplianceException` (gate fails closed —
   symmetric with the user-scoped path).

| Anchor | File / class |
|--------|--------------|
| Implementation | `InstallationStateGate.cs`; `TeamsProactiveNotifier.cs` lines 244–280 |
| Tests | `TeamsProactiveNotifierInstallationGateTests.cs`, `TeamsMessengerConnectorInstallationGateTests.cs` |

---

### US-07 — Entra ID Bot Framework authentication

> _As an enterprise operator, I want Bot Framework token validation to be
> wired to the Entra ID tenant configuration, so that only tokens whose `tid`
> claim is on the allow-list AND whose caller AppId is on the allow-list are
> accepted._

**Acceptance criteria**:
1. `EntraBotFrameworkAuthenticationOptions` (bound from configuration section
   `Teams:BotFrameworkAuthentication`) exposes
   `AllowedCallers` (`IList<string>` of AAD AppIds permitted to call the
   webhook), `AllowedTenantIds` (`IList<string>` of Entra tenants the bot
   accepts — auto-populated from `TeamsMessagingOptions.AllowedTenantIds`
   when left empty), `RequireTenantClaim` (bool, default `false` — opt-in
   strict JWT-layer tenant check), `ValidateAuthority` (bool, default
   `true`), and `ChannelService` (Bot Framework SDK channel-service URL,
   typically empty for public Azure / `"https://botframework.azure.us"` for
   GovCloud).
2. The bot's `MicrosoftAppId` / `MicrosoftAppPassword` credentials are NOT
   part of `EntraBotFrameworkAuthenticationOptions` — they remain in the
   Bot Framework SDK's standard configuration keys consumed by
   `ConfigurationBotFrameworkAuthentication` (kept separate so the SDK's
   own credential-rotation tooling applies).
3. `AddEntraBotFrameworkAuthentication` composes
   `EntraTenantAwareClaimsValidator` (which itself composes
   `AllowedCallersClaimsValidator` with the tenant `tid` check) into the
   `AuthenticationConfiguration.ClaimsValidator`, then registers the
   resulting `BotFrameworkAuthentication`. The registration participates in
   DI before `AddTeamsSecurity` composes the pipeline.
4. Startup validation is enforced for `TeamsAppPolicyOptions` via
   `ValidateOnStart` (covers the bot-identity / deployment-policy options);
   `EntraBotFrameworkAuthenticationOptions` itself is bound directly and
   validated lazily on first claim-validation call — startup-time validation
   for this options class is intentionally deferred so an operator can hot-
   reload the caller / tenant allow-lists without restarting the host.

| Anchor | File / class |
|--------|--------------|
| Brief Requirements | _Identity_ row |
| Implementation | `src/AgentSwarm.Messaging.Teams/Security/EntraBotFrameworkAuthenticationOptions.cs`; `TeamsSecurityServiceCollectionExtensions.AddEntraBotFrameworkAuthentication` |
| Tests | `EntraBotFrameworkAuthenticationTests.cs` |

---

### US-08 — Teams app policy options + admin deployment

> _As an enterprise operator, I want a typed configuration surface for the
> admin-consent / sideloading / app-catalog-scope deployment policy, so that
> startup validates the bot registration before serving traffic._

**Acceptance criteria**:
1. `TeamsAppPolicyOptions` exposes `RequireAdminConsent` (bool, default
   `true`), `AllowedAppCatalogScopes` (`organization` / `personal`), and
   `BlockSideloading` (bool, default `true` in production).
2. `TeamsAppPolicyOptionsValidator` validates the options at startup (via
   `ValidateOnStart`), failing fast when the options describe an unsupported
   combination.
3. The deployment-checklist doc captures the Entra + Teams admin centre setup
   steps that align with these options (see
   `docs/stories/qq-MICROSOFT-TEAMS-MESS/deployment-checklist.md`).

| Anchor | File / class |
|--------|--------------|
| Brief Requirements | _Security_ row + _Identity_ row |
| Implementation | `TeamsAppPolicyOptions.cs`, `TeamsAppPolicyOptionsValidator.cs` |
| Tests | `TeamsAppPolicyOptionsTests.cs` |

---

### US-09 — Teams app policy health check

> _As an enterprise operator, I want a health-check endpoint that reports
> `Healthy` only when (a) `MicrosoftAppId` is configured, (b)
> `BotFrameworkAuthentication` can acquire a Bot Connector token, and (c)
> `IConversationReferenceStore` is reachable — and `Degraded` with a
> human-readable detail otherwise, so that orchestration can drain traffic
> from a broken instance before users see failures._

**Acceptance criteria**:
1. `TeamsAppPolicyHealthCheck : IHealthCheck` calls `BotFrameworkAuthentication
   .CreateConnectorFactory(identity).CreateAsync(serviceUrl, audience, ct)` to
   verify token acquisition (the SDK 4.22.7 two-step pattern — there is no
   single `CreateConnectorClientAsync` on `BotFrameworkAuthentication`).
2. Failures return `HealthCheckResult.Degraded` carrying which dependency was
   unhealthy; no exception escapes the health check.
3. **No Microsoft Graph call** — installation state is tracked locally via
   `InstallationUpdate` activities, not Graph (per `tech-spec.md` §5.1 R-5).

| Anchor | File / class |
|--------|--------------|
| Brief Requirements | _Identity_ row, _Reliability_ row |
| Implementation | `TeamsAppPolicyHealthCheck.cs` |
| Tests | `TeamsAppPolicyHealthCheckTests.cs` |

---

### US-10 — Audit envelope contract

> _As a compliance auditor, I want every security decision (allow / reject /
> dead-letter) to land in the audit log with a stable envelope, so that
> downstream SIEM tooling can filter by `EventType` / `Outcome` / `Action`
> without scraping log strings._

**Acceptance criteria**:
1. The four canonical `AuditOutcomes` values are `Success`, `Rejected`,
   `Failed`, `DeadLettered`. No others.
2. Rejection-reason codes live in `Action`, never in `Outcome`. Known codes:
   - `UnauthorizedTenantRejected` (US-01)
   - `UnmappedUserRejected` (US-02)
   - `InsufficientRoleRejected` (US-03)
   - `InstallationGateRejected` (US-05 / US-06)
3. Every audit record carries `ActorId` (AAD object ID where available),
   `TenantId`, `CorrelationId`, and `Timestamp`.
4. Stage 5.1 uses an in-memory mock `IAuditLogger` for tests; **Stage 5.2**
   swaps in `SqlAuditLogger` for production. End-to-end audit persistence is
   validated in Phase 7.

| Anchor | File / class |
|--------|--------------|
| Brief Requirements | _Compliance_ row |
| Implementation | `IAuditLogger` (Stage 1.2 contract); concrete `SqlAuditLogger` lands in Stage 5.2 |
| Tests | Audit assertions appear in every Security/* test that exercises a rejection or dead-letter path |

---

## 3. Explicitly out-of-scope for Stage 5.1

These items are part of the broader story `qq:MICROSOFT-TEAMS-MESS` but are
delivered by **other stages**. Stage 5.1 must not invent contracts for them.

| Brief item | Owning stage | Notes |
|------------|--------------|-------|
| Adaptive Card composition for `agent ask` / `approve` / `reject` / `escalate` | Stage 3.1 + Stage 3.2 | Card templates and command dispatcher already in `feature/teams`. |
| Conversation reference persistence | Stage 4.1 | `IConversationReferenceStore` and SQL backing store. |
| Outbox dead-letter ENGINE (the loop that calls `DeadLetterAsync`) | Stage 6.1 | Stage 5.1 wires the **hook** (`IMessageOutbox.DeadLetterAsync` call inside the gate path); the dispatch loop that drives it is Stage 6.1. |
| `SqlAuditLogger` production implementation | Stage 5.2 | This stage uses mock `IAuditLogger`. |
| **P95 card-delivery SLA < 3000ms perf test** | **Stage 6.3** | Owned by `Stage 6.3: Performance Monitoring and Health Checks` per `implementation-plan.md` line 432 ("Delivery histogram" scenario: 100 outbound messages, `OutboxRetryEngine` dispatch loop, P95 < 3000ms). Stage 5.1 has no dispatch loop to measure — the `OutboxRetryEngine` is not introduced until Phase 6 — so Stage 5.1 deliberately does **not** introduce a P95 perf assertion. See `tech-spec.md` §4.4 and `e2e-scenarios.md` _Feature: Performance — Card Delivery SLA_. |
| Message update/delete on already-sent cards | Stage 4.2 | Uses stored message IDs from the conversation reference store. |

---

## 4. Cross-stage traceability — brief Requirements row → Stage 5.1 anchor

| Brief Requirement | Stage 5.1 user story / file |
|-------------------|----------------------------|
| _Protocol_: Bot Framework / Teams Bot APIs | Pre-existing (Stage 2.x) — no Stage 5.1 change. |
| _C# library_: `Microsoft.Bot.Builder` ecosystem | Pre-existing. |
| _Identity_: Entra ID + Teams app installation | US-02, US-05, US-06, US-07, US-09 |
| _Interaction model_: personal chat, channel, cards, proactive | Adaptive-card composition is Stage 3.x; Stage 5.1 gates the proactive path. |
| _Commands_: `agent ask`, `agent status`, `approve`, `reject`, `escalate`, `pause`, `resume` | RBAC enforcement (US-03) maps role → allowed commands. |
| _Proactive messages_: store conversation references | Reference store is Stage 4.1; Stage 5.1 gates the lookup (US-04, US-05, US-06). |
| _Cards_: Adaptive Cards for questions / approvals / gates | Card templates are Stage 3.1. |
| _Reliability_: persist outbound + retry transient failures | Outbox dead-letter HOOK (US-05) wires this stage to Phase 6's retry engine. |
| _Performance_: P95 card delivery < 3000ms | **Out of scope (Stage 6.3)** — see §3 row. |
| _Security_: tenant, identity, install, RBAC | US-01, US-02, US-03, US-05, US-06, US-08 |
| _Compliance_: immutable audit trail | US-10 (envelope) + Stage 5.2 (`SqlAuditLogger`) |

---

## 5. Cross-stage traceability — brief Acceptance Criterion → coverage

| Brief AC | Stage 5.1 coverage | Owning stage |
|----------|-------------------|--------------|
| "User can message the Teams bot: `agent ask ...`" | Authorisation gate (US-03) | Command dispatch is Stage 3.2. |
| "Agent can proactively send a blocking question to the correct user." | Target-resolution + install-gate (US-04, US-05) | Question composition is Stage 3.x. |
| "Human can approve/reject through Adaptive Card actions." | RBAC enforcement (US-03) | Card actions are Stage 3.x. |
| "Conversation references are stored and reused for proactive messaging." | Lookup contract (US-04) | Store impl is Stage 4.1. |
| **"Unauthorized tenant/user is rejected."** | **US-01 (tenant) + US-02 (unmapped user) + US-03 (insufficient role)** | **This stage.** |
| "Message update/delete works for already-sent approval cards." | _(not gated by this stage — pre-existing reference store retrieval)_ | Stage 4.2. |

---

## 6. Implementation surfaces created or modified by Stage 5.1

| Source file | Role |
|-------------|------|
| `Security/TenantValidationMiddleware.cs` | US-01 |
| `Security/EntraIdentityResolver.cs` | US-02 |
| `Security/RbacAuthorizationService.cs`, `RbacOptions.cs`, `StaticUserRoleProvider.cs`, `IUserRoleProvider.cs`, `IUserDirectory.cs`, `StaticUserDirectory.cs` | US-03 |
| `ConversationReferenceNotFoundException.cs` | US-04 |
| `Security/InstallationStateGate.cs`; gate calls in `TeamsProactiveNotifier.cs` (lines 159–280, 449–470) and `TeamsMessengerConnector.cs` | US-05, US-06 |
| `Security/EntraBotFrameworkAuthenticationOptions.cs`; `TeamsSecurityServiceCollectionExtensions.AddEntraBotFrameworkAuthentication` | US-07 |
| `Security/TeamsAppPolicyOptions.cs`, `TeamsAppPolicyOptionsValidator.cs` | US-08 |
| `Security/TeamsAppPolicyHealthCheck.cs` | US-09 |
| `ProactiveSendContext.cs` | Async-local correlation between outbox dispatch and the gate's dead-letter call (US-05) |
| `Security/TeamsSecurityApplicationBuilderExtensions.cs`, `TeamsSecurityServiceCollectionExtensions.cs` | DI + pipeline composition |

| Test class | Story coverage |
|------------|----------------|
| `TenantValidationMiddlewareTests` | US-01 (HTTP 403 + audit envelope; audit-sink failure throws `UnauthorizedTenantAuditException`) |
| `EntraIdentityResolverTests`, `RbacAadIdentityRegressionTests` | US-02 (AAD-object-id resolution; regression: never use display name or `Activity.From.Id`) |
| `RbacAuthorizationServiceTests`, `RbacOptionsTests`, `StaticUserRoleProviderTests`, `StaticUserDirectoryTests` | US-03 |
| `TeamsProactiveNotifierInstallationGateTests`, `TeamsMessengerConnectorInstallationGateTests` | US-04, US-05, US-06 (gate path: inactive → no Bot Framework call → `DeadLetterAsync` → audit → `ConversationReferenceNotFoundException`) |
| `InstallationStateGateTests` | US-05, US-06 (gate unit tests) |
| `EntraBotFrameworkAuthenticationTests` | US-07 |
| `TeamsAppPolicyOptionsTests` | US-08 |
| `TeamsAppPolicyHealthCheckTests` | US-09 |
| `TeamsSecurityServiceCollectionExtensionsTests` | DI composition: idempotent `AddTeamsSecurity`, `BridgeTeamsMessagingOptions` sentinel guard, configure-only + factory + type registrations |

---

## 7. Operator-action items (post-Stage-5.1)

These are operator deployment steps; the code is already in place.

1. Set `AllowedTenantIds` in configuration to the production tenant id(s).
2. Populate `RbacOptions.RoleCommands` (and optionally
   `RbacOptions.TenantRoleAssignments` / `RbacOptions.DefaultRole`) with the
   production role→command mapping under configuration section `Teams:Rbac`.
3. Provision the Entra ID app registration. Set the bot's `MicrosoftAppId`
   and `MicrosoftAppPassword` (or managed-identity equivalents) under the
   Bot Framework SDK's standard configuration keys consumed by
   `ConfigurationBotFrameworkAuthentication` — these credentials are NOT on
   `EntraBotFrameworkAuthenticationOptions`. No Microsoft Graph permissions
   required.
4. Provision Azure Bot Service resource; connect Teams channel.
5. Configure Teams admin centre app-setup policy with
   `RequireAdminConsent = true` under
   `TeamsAppPolicyOptions` (this options class is the one wired to
   `ValidateOnStart`, so a misconfiguration here fails the host at startup).
6. Enable the `IHealthCheck` endpoint exposed by `TeamsAppPolicyHealthCheck`
   in the orchestration health-probe configuration.
7. (Stage 5.2 onward) Swap the test `IAuditLogger` for the production
   `SqlAuditLogger`; configure immutable audit-store retention per
   compliance requirements.

---

_End of user-stories document._

---

## 8. Contract verification appendix (canonical signatures)

This appendix lists the production source signatures that the user-story
text above is reconciled against. A reviewer can re-grep each entry to
confirm this document has not drifted from the code; any divergence
between this appendix and the source files is a bug in this document and
should be reported.

### 8.1 Tenant rejection — HTTP-body, not JWT

- `TenantValidationMiddleware.InvokeAsync(HttpContext, RequestDelegate)`
  buffers the request, calls `ExtractTenantIdAsync(request, ct)`, and
  short-circuits with HTTP 403 when the extracted tenant ID is empty or
  not in `AllowedTenantIds`
  (`src/AgentSwarm.Messaging.Teams/Security/TenantValidationMiddleware.cs:82–110`).
- `TenantValidationMiddleware.ExtractTenantIdAsync(request, ct)` parses
  the JSON body and reads `channelData.tenant.id` first, then
  `conversation.tenantId` as a fallback. It does **not** inspect the
  bearer JWT
  (`src/AgentSwarm.Messaging.Teams/Security/TenantValidationMiddleware.cs:122–163`).
- The JWT `tid` claim check is owned by
  `EntraTenantAwareClaimsValidator` in the Bot Framework SDK token-
  validation pipeline (US-07), not by `TenantValidationMiddleware`.

### 8.2 RBAC — option keys and result shape

- `RbacOptions.RoleCommands` is
  `IDictionary<string, IReadOnlyCollection<string>>`, keyed by role
  name (case-insensitive)
  (`src/AgentSwarm.Messaging.Teams/Security/RbacOptions.cs:59`). There
  is no `RoleToCommands` property on `RbacOptions`.
- `RbacAuthorizationService.AuthorizeAsync(string tenantId, string userId, string command, CancellationToken ct)`
  returns `AuthorizationResult(bool IsAuthorized, string? UserRole, string? RequiredRole)`
  (`src/AgentSwarm.Messaging.Teams/Security/RbacAuthorizationService.cs:67–120`).
  The `userId` argument is the user's AAD object ID; see `RbacAuthorizationService`
  remarks at lines 35–41.

### 8.3 Conversation reference store — method names

- `IConversationReferenceStore.GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)`
  (`src/AgentSwarm.Messaging.Teams/IConversationReferenceStore.cs:62`).
- `IConversationReferenceStore.GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct)`
  (`src/AgentSwarm.Messaging.Teams/IConversationReferenceStore.cs:69`).
  There is no `GetByChannelAsync` on this interface.
- `IConversationReferenceStore.IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)`
  (`src/AgentSwarm.Messaging.Teams/IConversationReferenceStore.cs:109`).
- `IConversationReferenceStore.IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct)`
  (`src/AgentSwarm.Messaging.Teams/IConversationReferenceStore.cs:120`).

### 8.4 Installation state gate — entry points

- `InstallationStateGate.CheckAsync(...)`
  (`src/AgentSwarm.Messaging.Teams/Security/InstallationStateGate.cs:96`)
  — accepts an `AgentQuestion` (for question-driven sends) and branches
  on the target tuple.
- `InstallationStateGate.CheckTargetAsync(...)`
  (`src/AgentSwarm.Messaging.Teams/Security/InstallationStateGate.cs:176`)
  — accepts an explicit `(tenantId, userId?, channelId?, correlationId?, outboxEntryId?)`
  tuple (for `MessengerMessage`-driven sends).
- `InstallationStateGate.RejectMessageRoutingAsync(...)`
  (`src/AgentSwarm.Messaging.Teams/Security/InstallationStateGate.cs:288`)
  — emits the `InstallationGateRejected` audit + dead-letter for the
  message-routing path inside `TeamsMessengerConnector.SendMessageAsync`.
- There is no `CheckUserAsync` and no `CheckChannelAsync` on
  `InstallationStateGate`; the single gate covers both scopes by branching
  on the target inside `CheckAsync` / `CheckTargetAsync`.

### 8.5 Bot Framework authentication options — where the keys live

- `EntraBotFrameworkAuthenticationOptions` exposes only:
  `AllowedCallers`, `AllowedTenantIds`, `RequireTenantClaim`,
  `ValidateAuthority`, `ChannelService`
  (`src/AgentSwarm.Messaging.Teams/Security/EntraBotFrameworkAuthenticationOptions.cs:44–90`).
- The bot's `MicrosoftAppId` and `MicrosoftAppPassword` credentials live
  on `TeamsMessagingOptions`
  (`src/AgentSwarm.Messaging.Teams/TeamsMessagingOptions.cs:42–45`),
  **not** on `EntraBotFrameworkAuthenticationOptions`. Stage 5.1 keeps
  credentials and token-validation policy on separate options classes so
  the Bot Framework SDK's own credential-rotation tooling continues to
  apply to the credential bag without touching tenant / caller allow-lists.
