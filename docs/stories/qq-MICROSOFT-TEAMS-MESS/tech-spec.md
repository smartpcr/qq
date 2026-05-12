# Tech Spec — Microsoft Teams Messenger Support

**Story:** `qq:MICROSOFT-TEAMS-MESS`
**Status:** Draft
**Author:** Wen Zhong (Architect)

---

## 1. Problem Statement

The agent-swarm software factory operates 100+ autonomous agents performing product planning, architecture, coding, testing, release orchestration, incident response, and operational remediation. Humans interact with these agents **exclusively through messenger applications**. This story delivers the Microsoft Teams connector so that enterprise operators can command, approve, and monitor agent-swarm work from within Microsoft Teams — leveraging Entra ID for identity, Teams app installation policies for access control, and Adaptive Cards for structured interactions.

Today, no Teams integration exists. Agents cannot reach operators inside Teams, and operators cannot issue commands (`agent ask`, `approve`, `reject`, `escalate`, `pause`, `resume`, `agent status`) from a Teams conversation. Without this integration, enterprise customers who standardize on Microsoft 365 are forced onto a secondary channel, breaking their compliance and collaboration workflows.

### 1.1 Core Problem Decomposition

| # | Gap | Impact |
|---|-----|--------|
| G-1 | No Teams Bot endpoint exists | Operators cannot send commands from Teams |
| G-2 | No proactive messaging capability | Agents cannot push blocking questions, approval requests, or incident notifications to Teams |
| G-3 | No Adaptive Card rendering | Structured approve/reject/escalate interactions are unavailable |
| G-4 | No Entra ID / tenant validation | Enterprise identity and RBAC cannot be enforced |
| G-5 | No conversation reference persistence | Proactive messages cannot survive service restarts |
| G-6 | No audit trail for Teams interactions | Enterprise compliance reviews cannot cover Teams-originated decisions |

---

## 2. Scope

### 2.1 In Scope

| Area | Detail |
|------|--------|
| **Bot Framework integration** | ASP.NET Core bot endpoint using `Microsoft.Bot.Builder` (≥ 4.22), `Microsoft.Bot.Builder.Integration.AspNet.Core` (≥ 4.22), and `Microsoft.Bot.Connector.Teams` (≥ 4.22). `Microsoft.Bot.Builder` includes `TeamsActivityHandler` (extends `ActivityHandler` with Teams-specific overrides such as `OnTeamsChannelCreatedAsync`, `OnTeamsMembersAddedAsync`), Teams middleware, and helper methods — no separate `Microsoft.Bot.Builder.Teams` NuGet is required (aligned with `implementation-plan.md` Stage 2.1). `Microsoft.Bot.Connector.Teams` provides Teams-specific model types (`TeamsChannelData`, `TeamInfo`, `TeamsChannelAccount`). |
| **Command handling** | `agent ask`, `agent status`, `approve`, `reject`, `escalate`, `pause`, `resume` — parsed from personal chat and team channel messages. |
| **Adaptive Cards** | Card templates for: agent questions, approval gates, release gates, incident summaries. Card actions map to `HumanAction` values. Card update and delete for already-sent cards. |
| **Proactive messaging** | Store `ConversationReference` per authorized user/channel. Rehydrate after restart. Deliver agent-initiated questions, approval requests, and incident notifications. |
| **Identity & access** | Validate Entra ID tenant ID on every inbound activity. Map Teams `AadObjectId` to internal user identity. Enforce RBAC (operator, approver, viewer roles). Reject unauthorized tenants/users. |
| **Teams app manifest** | Manifest v1.16+ defining bot capabilities, personal scope, team scope, and message extension stubs. |
| **Interaction scopes** | Personal (1:1) chat and team channel conversations. |
| **Message actions** | Teams message-extension action commands to forward context to agents. |
| **Reliability** | Durable outbound notification queue. Retry transient Bot Connector failures (HTTP 429, 500, 502, 503, 504) per the canonical retry policy (§4.4). Dead-letter after exhausting retries. Connector restart recovery using persisted conversation references. |
| **Performance** | P95 Adaptive Card delivery < 3 seconds after queue pickup. Connector recovery < 30 seconds. Support 1000+ concurrent users. |
| **Compliance** | Immutable audit trail for all inbound commands and outbound notifications. Include `CorrelationId`, `AgentId`, `TaskId`, `ConversationId`, `Timestamp`, user identity, and action taken. |
| **Observability** | OpenTelemetry traces and metrics. Structured logging. Health-check endpoint. Latency histograms for card delivery. |
| **Shared abstractions** | Implement `IMessengerConnector` (planned in `AgentSwarm.Messaging.Abstractions` — to be created as part of the epic-level shared infrastructure; see §6.1) for Teams. Use shared data models: `AgentQuestion`, `HumanAction`, `HumanDecisionEvent`, `MessengerMessage`. |
| **Teams-specific contracts** | `IConversationReferenceStore` is defined in `AgentSwarm.Messaging.Teams` (not in the shared abstractions) because conversation reference shapes are platform-specific. `implementation-plan.md` Stage 1.2 places the interface definition step in the shared connector-interface stage for convenience of ordering, but the assembly owner is `AgentSwarm.Messaging.Teams`. This is consistent with `architecture.md` §2.8 and §4.2. |
| **Solution structure** | New project `AgentSwarm.Messaging.Teams` within the recommended solution structure. |

### 2.2 Out of Scope

| Area | Rationale |
|------|-----------|
| **Other messenger platforms** | Telegram, Discord, Slack are separate stories. |
| **Agent orchestrator internals** | This story delivers the Teams connector; agent task routing, scheduling, and execution are upstream. |
| **Teams Meeting integration** | Meeting bots, call handling, and live-event participation are not required. |
| **File/media upload from Teams** | Only text commands and Adaptive Card actions are in scope; file attachments are deferred. |
| **Teams tab or dashboard app** | No embedded web UI inside Teams; interaction is conversational only. |
| **Multi-tenant SaaS distribution** | The bot targets a single enterprise tenant (or explicit allow-list of tenants). Public app store publication is not in scope. |
| **Graph API beyond bot registration** | No calendar, mail, or SharePoint integration. |
| **Custom compliance connectors** | Audit logs are persisted internally; integration with Microsoft Purview or eDiscovery is deferred. |
| **Localization / i18n** | English-only for initial release. |

---

## 3. Non-Goals

These items are explicitly excluded from this story's deliverables to bound the effort. They may become goals in follow-on work.

1. **Replace existing command-line or API entry points** — Teams is an additional channel, not a replacement.
2. **Build a generic Bot Framework abstraction** — The `IMessengerConnector` interface is the abstraction layer; no separate Bot Framework middleware library is needed beyond what `Microsoft.Bot.Builder` provides.
3. **Implement end-to-end encryption beyond TLS** — Bot Framework traffic is TLS-encrypted. Message payload encryption at rest relies on the persistence layer's encryption-at-rest configuration, not a custom envelope encryption scheme.
4. **Achieve sub-second card delivery** — The P95 target is 3 seconds; optimizing below that is not a goal.
5. **Support guest users** — Only users with Entra ID identities within the allowed tenant(s) are supported. B2B guest accounts are excluded.
6. **Provide offline/batched command processing** — Commands are processed in near-real-time; queuing commands for scheduled execution is deferred.

---

## 4. Hard Constraints

These are non-negotiable requirements drawn from the story description, enterprise policy, and platform limitations.

### 4.1 Technology Stack

| Constraint | Source |
|------------|--------|
| C# / .NET 8+ | Epic-level mandate (see `.forge-attachments/agent_swarm_messenger_user_stories.md`, §Overview: "Implementation language must be C# / .NET 8+") |
| `Microsoft.Bot.Builder` (≥ 4.22) + `Microsoft.Bot.Builder.Integration.AspNet.Core` (≥ 4.22) | Story description requirement. Provides `ActivityHandler`, `BotAdapter`, ASP.NET Core integration, and Teams-specific functionality including `TeamsActivityHandler` (extends `ActivityHandler` with Teams-specific overrides such as `OnTeamsChannelCreatedAsync`, `OnTeamsMembersAddedAsync`), Teams middleware, and helper methods. No separate `Microsoft.Bot.Builder.Teams` NuGet package is required — all Teams support is bundled in `Microsoft.Bot.Builder`. Aligned with `implementation-plan.md` Stage 2.1. |
| `Microsoft.Bot.Connector.Teams` (≥ 4.22) | Story description requirement (see `.forge-attachments/agent_swarm_messenger_user_stories.md`, §Recommended C# Libraries under MSG-MT-001). Provides Teams-specific model types such as `TeamsChannelData`, `TeamInfo`, and `TeamsChannelAccount`. |
| ASP.NET Core hosting | Required by Bot Builder Integration package |

### 4.2 Identity & Security

| Constraint | Detail |
|------------|--------|
| **Tenant ID validation** | Every inbound `Activity` must have its `ChannelData.Tenant.Id` checked against an allow-list. Activities from disallowed tenants are rejected at the middleware layer before the bot handler runs (see rejection matrix below). |
| **User identity via Entra ID** | The bot must resolve `Activity.From.AadObjectId` to an internal user record. Unmapped users are rejected (see rejection matrix below). |
| **Teams app installation gate** | Proactive messaging requires the app to be installed in the user's personal scope or in the team. The connector tracks installation state via `InstallationUpdate` activities: when an uninstall event is received, the corresponding `ConversationReference` is marked inactive and no proactive sends are attempted. This is distinct from *stale persisted references* (see R-2 in §5.1), which are references that became invalid without a prior uninstall event (e.g., user removed from tenant); those are detected reactively via 403/404 on send attempt. |
| **RBAC enforcement** | Each command maps to a required role. `approve`/`reject` require `Approver` role. `agent ask`, `pause`, `resume`, `escalate` require `Operator` role. `agent status` requires `Viewer` role (or above). Users with insufficient role receive a polite card (see rejection matrix below). |
| **Secret storage** | Bot Framework `MicrosoftAppId` and `MicrosoftAppPassword` (or certificate) must be stored in Azure Key Vault or equivalent secure store. Never logged, never in source. |

#### Rejection Behavior Matrix

Tenant-level and user-level rejections are handled at **different layers** with **different responses**:

| Condition | Layer | Response | Audit Event |
|-----------|-------|----------|-------------|
| Invalid Bot Framework JWT | Bot Connector auth | HTTP 401 — request never reaches bot handler | N/A — no audit entry emitted (request is rejected by the Bot Framework `CloudAdapter` authentication pipeline before any application code or middleware runs; see design rationale below) |
| Valid JWT, tenant ID not in allow-list | `TenantValidationMiddleware` (runs before bot handler) | HTTP 403 — no bot response sent | `UnauthorizedTenantRejected` |
| Allowed tenant, `AadObjectId` not mapped to internal user | `IIdentityResolver` (inside bot handler) | HTTP 200 + Adaptive Card explaining access denial and how to request access | `UnmappedUserRejected` |
| Allowed tenant, mapped user, insufficient RBAC role | `IUserAuthorizationService` (inside bot handler) | HTTP 200 + Adaptive Card explaining insufficient permissions | `InsufficientRoleRejected` |

> **Design rationale:** Tenant-level rejection uses HTTP 403 because the middleware intercepts the request before the bot handler runs — there is no conversation context in which to send a card. User-level rejections (unmapped identity, insufficient RBAC) occur inside the bot handler where a conversation turn is active, so a polite Adaptive Card is the appropriate response. This two-tier rejection model is the canonical behavior across all plan docs — `implementation-plan.md` and `e2e-scenarios.md` have been aligned accordingly.
>
> **Cross-doc note on `architecture.md` §10.3:** The architecture doc's error handling table lists "Authentication failure → Log `SecurityRejection` audit entry; return 403." This refers specifically to the **tenant validation** case (row 2 above), not to JWT validation (row 1). JWT validation failures (row 1) are handled automatically by the Bot Framework `CloudAdapter` authentication pipeline, which returns HTTP 401 before any application code runs — no `SecurityRejection` audit entry is emitted because the request never reaches the bot handler or middleware. The architecture doc's "Authentication failure" label is shorthand for "tenant/identity-level rejection at the application layer" and is consistent with this matrix.

### 4.3 Compliance <!-- anchor: compliance-audit-schema -->

| Constraint | Detail |
|------------|--------|
| **Immutable audit trail** | Every inbound command, outbound notification, and Adaptive Card action callback must produce an append-only audit record. |
| **Retention** | Audit records must be retained for the duration mandated by the enterprise compliance policy (configurable, default 7 years). |

#### Canonical Audit Record Schema (source of truth)

This is the **minimum required** field set for all audit records. Sibling docs (`architecture.md`, `implementation-plan.md`) may add implementation-specific fields (e.g., `Checksum` for tamper detection, surrogate `AuditEntryId` primary key) but **must include all fields listed here** and **must use the canonical `EventType` values** defined in this table. Cross-doc references should use the stable anchor `tech-spec.md §4.3 (compliance-audit-schema)` or the table name "Canonical Audit Record Schema" to avoid brittleness if section numbers shift.

> The canonical **audit** `EventType` values defined by this spec are: `CommandReceived`, `MessageSent`, `CardActionReceived`, `SecurityRejection`, `ProactiveNotification`, `MessageActionReceived`, `Error`. The canonical set contains exactly seven values. Message actions (Teams message-extension submissions) log as `MessageActionReceived` — a dedicated audit event type distinct from `CommandReceived` — because message-action submissions arrive through the `composeExtension/submitAction` invoke mechanism rather than direct text commands, and distinguishing them in the audit trail supports compliance filtering and forensic analysis. The `Source` field on the domain `MessengerEvent` additionally marks the origination (`Source = MessageAction`) for downstream processing.
>
> **Cross-doc alignment status:** All sibling docs (`architecture.md` §3.2 line 432, `implementation-plan.md` §1.3 line 48 / §3.4 line 206 / §5.2 line 291, `e2e-scenarios.md` line 773 and line 905) define exactly seven canonical audit `EventType` values including `MessageActionReceived`. All four plan docs are now aligned on this seven-value canonical set.
>
> **Important distinction:** The `EventType` field in the canonical **audit record** schema (this table) is a different concept from the `EventType` discriminator on the `MessengerEvent` domain model. The audit `EventType` categorizes audit log entries (`CommandReceived`, `MessageSent`, `CardActionReceived`, `SecurityRejection`, `ProactiveNotification`, `MessageActionReceived`, `Error`). The `MessengerEvent.EventType` discriminator identifies the domain event subtype (`AgentTaskRequest`, `Command`, `Escalation`, `PauseAgent`, `ResumeAgent`, `Decision`, `Text`, `InstallUpdate`, `Reaction`) as defined in `architecture.md` §3.1 and `e2e-scenarios.md` §Audit Trail compliance scenarios. These are intentionally separate enumerations serving different purposes — audit categorization vs. domain event polymorphism — and are not expected to share values.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `Timestamp` | `DateTimeOffset` | Yes | UTC time the event occurred. |
| `CorrelationId` | `string` | Yes | End-to-end trace ID for distributed tracing. |
| `EventType` | `string` | Yes | Describes the event category: `CommandReceived`, `MessageSent`, `CardActionReceived`, `SecurityRejection`, `ProactiveNotification`, `MessageActionReceived`, `Error`. The canonical set contains exactly seven values. Message actions (Teams message-extension submissions) log as `MessageActionReceived` — a dedicated audit event type distinct from `CommandReceived` — because they arrive through the `composeExtension/submitAction` invoke mechanism rather than direct text commands. This field is the source of truth; sibling docs are aligned. |
| `ActorId` | `string` | Yes | Identity of the actor — Entra AAD object ID for users, agent ID for agent-originated events. |
| `ActorType` | `string` | Yes | `User` or `Agent` — disambiguates `ActorId`. |
| `TenantId` | `string` | Yes | Entra ID tenant of the actor. |
| `TaskId` | `string` | No | Agent task/work-item ID (null for security rejection events). |
| `ConversationId` | `string` | No | Teams conversation ID (null for events outside a conversation). |
| `Action` | `string` | Yes | The specific action taken (e.g., `approve`, `reject`, `agent ask`, `send_card`). |
| `PayloadJson` | `string` | Yes | JSON-serialized event payload (sanitized — no secrets or PII beyond identity). |
| `Outcome` | `string` | Yes | Result of the action: `Success`, `Rejected`, `Failed`, `DeadLettered`. |

> **Field mapping guidance for sibling docs:** `AgentId` from the story requirements maps to `ActorId` when `ActorType = Agent`. `UserId` maps to `ActorId` when `ActorType = User`. `Payload` maps to `PayloadJson`. The architecture doc's `AuditEntryId` is an implementation-specific surrogate key. The implementation plan's `Actor` maps to `ActorId`, `Resource` maps to `TaskId` + `ConversationId`, `Details` maps to `PayloadJson`, and `Checksum` is an implementation addition for tamper detection.

### 4.4 Reliability & Performance

| Constraint | Detail |
|------------|--------|
| **P95 Adaptive Card delivery** | < 3 seconds from outbound queue pickup to Bot Connector acknowledgement. |
| **Zero message loss** | Outbound notifications must be durably queued before acknowledgement to the agent. If Bot Connector is unreachable, the message is retried — not dropped. |
| **Connector recovery** | After crash or restart, the connector must resume processing within 30 seconds using persisted conversation references and durable queues. |
| **Idempotency** | Duplicate inbound activities (Teams sometimes retries webhook delivery) must be deduplicated using `Activity.Id` or `Activity.ReplyToId`. |
| **Concurrent users** | Must support 1000+ concurrent users issuing commands and receiving proactive notifications without degradation. |

#### Canonical Retry Policy (source of truth)

This is the **authoritative retry schedule** for transient Bot Connector failures (HTTP 429, 500, 502, 503, 504). Sibling docs must align to these values.

> All sibling docs (`implementation-plan.md`, `e2e-scenarios.md`) have been aligned to this canonical retry schedule.
>
> **Relationship to `ConnectorOptions` base-class defaults:** `implementation-plan.md` Stage 1.2 defines `ConnectorOptions` with generic defaults (`RetryCount = 3`, `RetryDelayMs = 1000`). These are base-class starter values for the shared `IMessengerConnector` abstraction across all messenger platforms. The canonical retry policy below **overrides** those base-class defaults for the Teams connector specifically — `TeamsMessagingOptions` (see `implementation-plan.md` Stage 2.1) sets `MaxRetryAttempts = 5` and `RetryBaseDelaySeconds = 2`. Tests asserting retry behavior for the Teams connector must use the canonical values from this table, not the `ConnectorOptions` generic defaults.

| Parameter | Value | Notes |
|-----------|-------|-------|
| Initial attempt | 1 | The first delivery attempt (not counted as a retry). |
| Max retries | 4 | Up to 4 retries after the initial attempt (5 total attempts). |
| Base delay | 2 seconds | Delay before the first retry. |
| Multiplier | 2× | Each subsequent retry doubles the delay. |
| Max delay cap | 60 seconds | No single retry delay exceeds 60 seconds. |
| Jitter | ±25% | Applied to each computed delay to avoid thundering herd. |
| `Retry-After` override | Yes | When Bot Framework returns HTTP 429 with `Retry-After` header, use that value as the minimum delay if it exceeds the computed backoff. |
| Dead-letter | After final failed attempt | Messages that fail all 5 attempts are moved to dead-letter queue. |

Computed retry delays (before jitter): 2s → 4s → 8s → 16s.

### 4.5 Bot Framework / Teams Platform

| Constraint | Detail |
|------------|--------|
| **Conversation reference storage** | `ConversationReference` objects must be serialized and persisted (database or durable store) for proactive messaging. They must survive service restarts. |
| **Proactive messaging flow** | Use `BotAdapter.ContinueConversationAsync` with stored `ConversationReference` to push proactive messages. Requires `MicrosoftAppId`. |
| **Adaptive Card schema** | Cards must conform to Adaptive Cards schema ≥ 1.4 (Teams-supported version). |
| **Card update/delete** | The connector must store `Activity.Id` for sent cards so they can be updated (e.g., mark approval as completed) or deleted. |
| **Rate limits** | Bot Framework enforces per-conversation and per-bot rate limits. The connector must respect `Retry-After` headers. |

---

## 5. Identified Risks

### 5.1 Technical Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|----|------|------------|--------|------------|
| R-1 | **Bot Framework rate limiting under burst** — 100+ agents sending proactive notifications simultaneously may hit Bot Connector rate limits (default ~50 msgs/sec per bot). | Medium | High — delayed card delivery, missed SLO | Implement token-bucket rate limiter in the outbound pipeline. Respect `Retry-After` headers. Queue with priority (approval cards > status updates). |
| R-2 | **Stale persisted conversation references** — A stored `ConversationReference` may become invalid without a corresponding `InstallationUpdate` uninstall event (e.g., user removed from tenant, team deleted). Unlike known-uninstalled contexts (handled proactively in §4.2), stale references are detected reactively when a proactive send returns HTTP 403 or 404. | High | Medium — failed proactive delivery, alert noise | Catch `ErrorResponseException` on proactive send. On 403/404, mark the reference as stale (inactive) and emit a `teams.proactive.reference.stale` metric. Do not retry permanently-failed references. Reactivate if a fresh inbound `Activity` arrives from the same user/channel. |
| R-3 | **Adaptive Card schema drift** — Teams client updates may change supported card schema versions or rendering behavior. | Low | Medium — broken card layouts | Pin card schema version in templates. Include card-rendering integration tests against Teams test tenant. |
| R-4 | **Entra ID token validation latency** — Validating JWT tokens from Bot Framework on every request adds latency to the inbound pipeline. | Low | Low — increased P95 but still within budget | Cache JWKS keys with appropriate TTL. Use `Microsoft.Bot.Connector.Authentication` built-in caching. |
| R-5 | **Bot registration and admin consent** — The bot's Entra app registration requires Azure Bot Service channel registration and Teams app manifest approval. Admin consent for the Teams app installation policy may be delayed by enterprise IT. | Medium | High — blocks deployment entirely | Document required Azure Bot Service setup and Teams admin-center approval steps early. Provide admin-consent request template. Include in deployment checklist. No Microsoft Graph API permissions are required — proactive messaging uses `BotAdapter.ContinueConversationAsync` with the bot's own `MicrosoftAppId`, not Graph endpoints. |

### 5.2 Operational Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|----|------|------------|--------|------------|
| R-6 | **Dead-letter queue growth** — Persistent Bot Framework failures (e.g., app misconfiguration, revoked credentials) cause unbounded DLQ growth. | Medium | Medium — storage cost, alert fatigue | DLQ size metric + alert. Runbook for DLQ drain. Auto-pause outbound after configurable DLQ threshold. |
| R-7 | **Audit log volume** — 1000+ concurrent users × 100+ agents can produce high audit log throughput. | Medium | Low — storage cost | Use append-only structured log sink (e.g., Azure Table Storage, dedicated SQL table). Partition by date. |
| R-8 | **Proactive message consent** — Teams requires admin consent or user-initiated conversation before proactive messages. If the app is deployed via policy but users haven't interacted, first proactive send may fail. | Medium | Medium — silent delivery failure for new users | On app-install event (`InstallationUpdate`), capture and store `ConversationReference`. Document that admin-pushed installs trigger the install event. |

### 5.3 Project Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|----|------|------------|--------|------------|
| R-9 | **Shared abstraction interface stability** — `IMessengerConnector` is shared across four messenger stories. Interface changes in a sibling story could break the Teams implementation. | Medium | Medium — rework | Treat `AgentSwarm.Messaging.Abstractions` as a stable contract. Any change requires cross-story review. Pin version in this story's implementation. |
| R-10 | **Teams test tenant availability** — Integration and E2E tests require a Teams tenant with the bot installed. If the test tenant is unavailable or misconfigured, CI is blocked. | Medium | Medium — blocked testing | Maintain dedicated test tenant. Mock Bot Connector for unit tests. Use Teams Test Framework for integration tests. Include tenant health check in CI pre-flight. |
| R-11 | **13-point story scope creep** — At 13 story points, this is a large story. Scope creep (e.g., adding file uploads, meeting bots) could push delivery beyond sprint. | Medium | Medium — delayed delivery | Enforce out-of-scope list strictly (§2.2). Any new requirement triggers a separate story. |

---

## 6. Dependencies

### 6.1 Internal Dependencies

| Dependency | Owner | Status |
|------------|-------|--------|
| `AgentSwarm.Messaging.Abstractions` — `IMessengerConnector`, `AgentQuestion`, `HumanAction`, `HumanDecisionEvent`, `MessengerMessage` | Shared / Epic | **Planned** — to be created as part of epic-level shared infrastructure (see `implementation-plan.md` Stage 1.1). Must be stable before Teams connector implementation begins. |
| `AgentSwarm.Messaging.Core` — Retry engine, rate limiter, deduplication, outbox/inbox | Shared / Epic | **Planned** — to be created as part of epic-level shared infrastructure. Must be available before Teams connector integration testing. |
| `AgentSwarm.Messaging.Persistence` — Durable queue, conversation reference store, audit log sink | Shared / Epic | **Planned** — to be created as part of epic-level shared infrastructure (see `implementation-plan.md` Stage 1.3). Must be available before Teams connector integration testing. |

### 6.2 External Dependencies

| Dependency | Detail |
|------------|--------|
| **Azure Bot Service** | Bot channel registration in Azure. Required for Bot Framework authentication. |
| **Microsoft Entra ID** | App registration with Teams channel enabled. `MicrosoftAppId` + secret/certificate. |
| **Teams Admin Center** | App policy to allow or require installation for target users/groups. |
| **NuGet packages** | `Microsoft.Bot.Builder` (≥ 4.22), `Microsoft.Bot.Builder.Integration.AspNet.Core` (≥ 4.22), `Microsoft.Bot.Connector.Teams` (≥ 4.22), `AdaptiveCards` (≥ 3.1). No separate `Microsoft.Bot.Builder.Teams` NuGet is required — Teams support is included in `Microsoft.Bot.Builder`. |
| **Azure Key Vault** (or equivalent) | Secure storage for bot credentials. |

---

## 7. Assumptions

1. The `AgentSwarm.Messaging.Abstractions` and `AgentSwarm.Messaging.Core` packages are developed in parallel as part of epic-level shared infrastructure and will be stable by the time the Teams connector reaches integration testing.
2. A dedicated Azure Bot Service registration and Entra ID app registration will be provisioned before development begins.
3. A test tenant with Teams licenses and admin consent for the bot app will be available for integration and E2E testing.
4. The agent orchestrator publishes events (questions, approval requests, notifications) to a durable queue that the Teams connector subscribes to — the connector does not poll the orchestrator.
5. The bot will be deployed as a single-tenant app (not multi-tenant SaaS) unless the operator explicitly configures multiple allowed tenant IDs.
6. Adaptive Cards schema version 1.4 is the minimum supported by the target Teams clients (desktop, web, mobile).
7. The persistence layer for conversation references and audit logs will be provided by `AgentSwarm.Messaging.Persistence` (planned) and will support the required durability and retention guarantees.

---

## 8. Success Metrics

| Metric | Target | Measurement |
|--------|--------|-------------|
| **Acceptance criteria pass rate** | 6/6 criteria from story description pass in E2E tests | Automated E2E test suite |
| **P95 card delivery latency** | < 3 seconds | OpenTelemetry histogram: `teams.card.delivery.duration_ms` |
| **Connector recovery time** | < 30 seconds | Health-check probe after simulated crash |
| **Proactive message success rate** | > 99% (excluding permanently-failed references) | `teams.proactive.send.success` / `teams.proactive.send.total` |
| **Unauthorized request rejection rate** | 100% of invalid-tenant/invalid-user requests rejected | Audit log query + E2E security test |
| **Audit trail completeness** | 100% of commands and notifications have audit records | Reconciliation check: outbound queue count == audit record count |

---

## 9. Glossary

| Term | Definition |
|------|------------|
| **Activity** | Bot Framework message unit — represents a message, event, or invocation from Teams. |
| **Adaptive Card** | JSON-based card format rendered natively by Teams for structured interactions. |
| **Conversation Reference** | Serializable Bot Framework object containing the addressing information needed to send a proactive message to a specific user or channel. |
| **DLQ** | Dead-letter queue — holds messages that failed delivery after all retries are exhausted. |
| **Entra ID** | Microsoft's identity platform (formerly Azure Active Directory). |
| **Proactive Message** | A message initiated by the bot (not in response to a user message). Requires a stored `ConversationReference` and the app to be installed. |
| **RBAC** | Role-based access control. |
| **Outbox Pattern** | Persist outbound messages to a durable store before sending; a background processor delivers them. Guarantees at-least-once delivery. |

---

*Sibling plan documents in this story folder (`docs/stories/qq-MICROSOFT-TEAMS-MESS/`): `architecture.md`, `implementation-plan.md`, `e2e-scenarios.md`.*

---

## Iteration Summary

**File:** `docs/stories/qq-MICROSOFT-TEAMS-MESS/tech-spec.md`
**Byte count:** verified post-edit (see below).
**Covers:** Problem statement (§1), in/out-of-scope (§2), non-goals (§3), hard constraints (§4 — security, performance, compliance with canonical seven-value audit schema), identified risks (§5), dependencies (§6), assumptions (§7), success metrics (§8), glossary (§9). All sections anchored to story description details: Bot Framework, Entra ID, Teams scopes, Adaptive Cards, proactive messaging, commands, P95 delivery, audit trail.

### Prior feedback resolution

- [x] 1. FIXED — `e2e-scenarios.md` — Removed all stale six-value audit references from the file. The Gherkin step at former line 774 already used "MessageActionReceived" and "seven values" (fixed in a prior architecture.md iteration that edited this file). The prior-feedback resolution block (former lines 915–937) contained stale six-value claims from iter 9/10 — replaced with a single consolidated line. Verified: no occurrences of the stale phrase remain in e2e-scenarios.md.

- [x] 2. FIXED — `tech-spec.md` prior-feedback block — Rephrased item 2 in the iter 12 prior-feedback resolution (former line 307) to remove the stale six-value claim phrase. The item now describes the fix without quoting the removed text. Verified: the stale phrase no longer appears in any normative or prior-feedback section of this file.

- [x] 3. FIXED — `implementation-plan.md` lines 48, 206, 292 — These already define exactly seven canonical audit values including `MessageActionReceived` (aligned in a prior iteration). The stale iteration summary at former line 415 (which said "six canonical EventType values") and the stale cross-doc inconsistency note at former line 417 (which said tech-spec defines six values) were already removed by a sibling agent's edit. Current state confirmed: all three normative sections say "exactly seven canonical values" with `MessageActionReceived`. Verification:
```
$ grep -nF "exactly six" docs/stories/qq-MICROSOFT-TEAMS-MESS/implementation-plan.md
(empty — no remaining six-value claims)
```

- [x] 4. FIXED — `e2e-scenarios.md` line 905 (coverage section) — Updated from "six values…CommandReceived" to "seven values…MessageActionReceived" listing all seven canonical EventType values. Verification:
```
$ grep -nF "seven values per tech-spec" docs/stories/qq-MICROSOFT-TEAMS-MESS/e2e-scenarios.md
906:- Compliance: immutable audit trail with canonical EventType values (seven values per tech-spec §4.3: CommandReceived, ...
```

- [x] 5. FIXED — `e2e-scenarios.md` former line 939 — Replaced the stale cross-doc inconsistency note (which claimed tech-spec defines six values and that architecture/implementation should change) with a "Cross-doc alignment status" note confirming all four plan docs are aligned on seven values. Verification:
```
$ grep -nF "defines exactly six" docs/stories/qq-MICROSOFT-TEAMS-MESS/e2e-scenarios.md
(empty — stale note removed)
```

- [x] 6. FIXED — `e2e-scenarios.md` former line 371 — `UnmappedUserRejected` was in the `Outcome` field, contradicting the canonical outcome vocabulary (`Success`, `Rejected`, `Failed`, `DeadLettered`). Moved `UnmappedUserRejected` to an `Action` field and set `Outcome` to `Rejected`, consistent with `tech-spec.md` §4.3 and `implementation-plan.md` §5.1 line 287. Verification:
```
$ grep -nF "Outcome   | UnmappedUserRejected" docs/stories/qq-MICROSOFT-TEAMS-MESS/e2e-scenarios.md
(empty — moved to Action field)
```

### Open questions

None.
