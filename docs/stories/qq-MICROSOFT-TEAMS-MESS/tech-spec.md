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
| **Bot Framework integration** | ASP.NET Core bot endpoint using `Microsoft.Bot.Builder`, `Microsoft.Bot.Builder.Integration.AspNet.Core`, and `Microsoft.Bot.Connector.Teams`. |
| **Command handling** | `agent ask`, `agent status`, `approve`, `reject`, `escalate`, `pause`, `resume` — parsed from personal chat and team channel messages. |
| **Adaptive Cards** | Card templates for: agent questions, approval gates, release gates, incident summaries. Card actions map to `HumanAction` values. Card update and delete for already-sent cards. |
| **Proactive messaging** | Store `ConversationReference` per authorized user/channel. Rehydrate after restart. Deliver agent-initiated questions, approval requests, and incident notifications. |
| **Identity & access** | Validate Entra ID tenant ID on every inbound activity. Map Teams `AadObjectId` to internal user identity. Enforce RBAC (operator, approver, viewer roles). Reject unauthorized tenants/users. |
| **Teams app manifest** | Manifest v1.16+ defining bot capabilities, personal scope, team scope, and message extension stubs. |
| **Interaction scopes** | Personal (1:1) chat and team channel conversations. |
| **Message actions** | Teams message-extension action commands to forward context to agents. |
| **Reliability** | Durable outbound notification queue. Retry transient Bot Connector failures (HTTP 429, 500, 502, 503, 504) with exponential backoff + jitter. Dead-letter after exhausting retries. Connector restart recovery using persisted conversation references. |
| **Performance** | P95 Adaptive Card delivery < 3 seconds after queue pickup. Connector recovery < 30 seconds. Support 1000+ concurrent users. |
| **Compliance** | Immutable audit trail for all inbound commands and outbound notifications. Include `CorrelationId`, `AgentId`, `TaskId`, `ConversationId`, `Timestamp`, user identity, and action taken. |
| **Observability** | OpenTelemetry traces and metrics. Structured logging. Health-check endpoint. Latency histograms for card delivery. |
| **Shared abstractions** | Implement `IMessengerConnector` (defined in `AgentSwarm.Messaging.Abstractions`) for Teams. Use shared data models: `AgentQuestion`, `HumanAction`, `HumanDecisionEvent`, `MessengerMessage`. |
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
| `Microsoft.Bot.Builder` + `Microsoft.Bot.Builder.Integration.AspNet.Core` | Story description requirement |
| `Microsoft.Bot.Connector.Teams` (Teams extension types) | Story description requirement (see `.forge-attachments/agent_swarm_messenger_user_stories.md`, §Recommended C# Libraries under MSG-MT-001) |
| ASP.NET Core hosting | Required by Bot Builder Integration package |

### 4.2 Identity & Security

| Constraint | Detail |
|------------|--------|
| **Tenant ID validation** | Every inbound `Activity` must have its `ChannelData.Tenant.Id` checked against an allow-list. Activities from disallowed tenants are rejected with HTTP 403 before processing. |
| **User identity via Entra ID** | The bot must resolve `Activity.From.AadObjectId` to an internal user record. Unauthenticated or unmapped users are rejected. |
| **Teams app installation gate** | Proactive messaging requires the app to be installed in the user's personal scope or in the team. The connector tracks installation state via `InstallationUpdate` activities: when an uninstall event is received, the corresponding `ConversationReference` is marked inactive and no proactive sends are attempted. This is distinct from *stale persisted references* (see R-2 in §5.1), which are references that became invalid without a prior uninstall event (e.g., user removed from tenant); those are detected reactively via 403/404 on send attempt. |
| **RBAC enforcement** | Each command maps to a required role. `approve`/`reject` require `Approver` role. `agent ask`, `pause`, `resume`, `escalate` require `Operator` role. `agent status` requires `Viewer` role (or above). |
| **Secret storage** | Bot Framework `MicrosoftAppId` and `MicrosoftAppPassword` (or certificate) must be stored in Azure Key Vault or equivalent secure store. Never logged, never in source. |

### 4.3 Compliance

| Constraint | Detail |
|------------|--------|
| **Immutable audit trail** | Every inbound command, outbound notification, and Adaptive Card action callback must produce an append-only audit record. |
| **Audit record fields** | `Timestamp`, `CorrelationId`, `AgentId`, `TaskId`, `ConversationId`, `UserId` (Entra OID), `Action`, `Payload` (sanitized), `Outcome`. |
| **Retention** | Audit records must be retained for the duration mandated by the enterprise compliance policy (configurable, default 7 years). |

### 4.4 Reliability & Performance

| Constraint | Detail |
|------------|--------|
| **P95 Adaptive Card delivery** | < 3 seconds from outbound queue pickup to Bot Connector acknowledgement. |
| **Zero message loss** | Outbound notifications must be durably queued before acknowledgement to the agent. If Bot Connector is unreachable, the message is retried — not dropped. |
| **Connector recovery** | After crash or restart, the connector must resume processing within 30 seconds using persisted conversation references and durable queues. |
| **Idempotency** | Duplicate inbound activities (Teams sometimes retries webhook delivery) must be deduplicated using `Activity.Id` or `Activity.ReplyToId`. |
| **Concurrent users** | Must support 1000+ concurrent users issuing commands and receiving proactive notifications without degradation. |

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
| `AgentSwarm.Messaging.Abstractions` — `IMessengerConnector`, `AgentQuestion`, `HumanAction`, `HumanDecisionEvent`, `MessengerMessage` | Shared / Epic | Must be stable before Teams connector implementation begins |
| `AgentSwarm.Messaging.Core` — Retry engine, rate limiter, deduplication, outbox/inbox | Shared / Epic | Must be available; Teams connector consumes these services |
| `AgentSwarm.Messaging.Persistence` — Durable queue, conversation reference store, audit log sink | Shared / Epic | Must be available; Teams connector depends on persistence for conversation references and audit trail |

### 6.2 External Dependencies

| Dependency | Detail |
|------------|--------|
| **Azure Bot Service** | Bot channel registration in Azure. Required for Bot Framework authentication. |
| **Microsoft Entra ID** | App registration with Teams channel enabled. `MicrosoftAppId` + secret/certificate. |
| **Teams Admin Center** | App policy to allow or require installation for target users/groups. |
| **NuGet packages** | `Microsoft.Bot.Builder` (≥ 4.22), `Microsoft.Bot.Builder.Integration.AspNet.Core` (≥ 4.22), `Microsoft.Bot.Connector.Teams` (≥ 4.22), `AdaptiveCards` (≥ 3.1). |
| **Azure Key Vault** (or equivalent) | Secure storage for bot credentials. |

---

## 7. Assumptions

1. The `AgentSwarm.Messaging.Abstractions` and `AgentSwarm.Messaging.Core` packages are developed in parallel (or already exist) and will be stable by the time the Teams connector reaches integration testing.
2. A dedicated Azure Bot Service registration and Entra ID app registration will be provisioned before development begins.
3. A test tenant with Teams licenses and admin consent for the bot app will be available for integration and E2E testing.
4. The agent orchestrator publishes events (questions, approval requests, notifications) to a durable queue that the Teams connector subscribes to — the connector does not poll the orchestrator.
5. The bot will be deployed as a single-tenant app (not multi-tenant SaaS) unless the operator explicitly configures multiple allowed tenant IDs.
6. Adaptive Cards schema version 1.4 is the minimum supported by the target Teams clients (desktop, web, mobile).
7. The persistence layer for conversation references and audit logs is provided by `AgentSwarm.Messaging.Persistence` and supports the required durability and retention guarantees.

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
