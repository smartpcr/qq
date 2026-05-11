# Technical Specification — Telegram Messenger Support

**Story:** `qq:TELEGRAM-MESSENGER-S` · 13 SP
**Status:** Draft — Iteration 18
**Last updated:** 2026-05-11

---

## 1  Problem Statement

The agent swarm (100+ autonomous agents performing planning, coding, testing, release, and incident response) currently has no human-facing communication channel. Operators need a mobile-first interface to issue commands, answer blocking agent questions, approve or reject proposed actions, and receive urgent alerts — all from a phone.

This story delivers a **Telegram Bot connector** that plugs into the Messenger Gateway architecture described in the story attachment (`.forge-attachments/agent_swarm_messenger_user_stories.md`). The connector implements `IMessengerConnector`, speaks the Telegram Bot API over HTTPS, and integrates with the shared data model (`AgentQuestion`, `HumanAction`, `HumanDecisionEvent`).

The system must handle bursty traffic from 100+ concurrent agents without message loss, deduplicate webhook replays, and maintain a full audit trail with correlation IDs.

---

## 2  In-Scope

| # | Area | Detail |
|---|------|--------|
| S-1 | **Telegram Bot API integration** | HTTPS transport via `Telegram.Bot` NuGet package. Webhook receive in production; long-polling receive in dev/local. |
| S-2 | **Command handling** | `/start`, `/status`, `/agents`, `/ask`, `/approve`, `/reject`, `/handoff`, `/pause`, `/resume` — parsed, validated, and dispatched to the Agent Swarm Orchestrator. `/handoff TASK-ID @operator-alias` semantics: all sibling documents are aligned on **full oversight transfer** (Decided). Architecture.md §5.5 (lines 513–526) defines the design; implementation-plan.md Stage 3.2 (lines 218–243) implements `HandoffCommandHandler` with full transfer — task validation, operator resolution via `IOperatorRegistry`, `TaskOversight` record creation, bidirectional notification, and audit; e2e-scenarios.md (lines 360–376) tests this flow end-to-end. See D-4. |
| S-3 | **Agent-to-human questions** | Render `AgentQuestion` as Telegram messages with inline keyboard buttons for each `HumanAction`. Include context, severity, timeout, and proposed default action in the message body. **Default-action design:** The sibling documents currently diverge on where the proposed default action lives. **e2e-scenarios.md** (lines 57–77) models `DefaultAction` as a **first-class nullable property on `AgentQuestion`** — the fixture sets `DefaultAction = act-1` directly on the question, and the connector reads `DefaultAction` from the model. **architecture.md** §3.1 (lines 167–190) models the shared `AgentQuestion` **without** a `DefaultAction` property and instead carries the proposed default as `ProposedDefaultActionId` sidecar metadata on the `AgentQuestionEnvelope`. **This spec does not declare either document authoritative over the other.** The divergence must be resolved by aligning the sibling documents in a future iteration. Regardless of which approach is adopted, the behavioral contract is identical: the Telegram connector obtains the proposed default action ID (from the model or envelope), denormalizes it into `PendingQuestionRecord.DefaultActionId`, displays it in the message body, and applies it on timeout. See Section 10 alignment notes. |
| S-4 | **Strongly typed decision events** | Button taps and text replies are converted to `HumanDecisionEvent` and published to the orchestrator. |
| S-5 | **Operator identity mapping** | Map Telegram `chat_id` + `user_id` to an authorized operator record with tenant/workspace binding. Reject unmapped users. Applies in both 1:1 and group-chat contexts — commands in groups are attributed to the sending `user_id`, not the group `chat_id`. |
| S-6 | **Durable outbound queue** | Persistent queue (outbox pattern) with retry, exponential back-off, deduplication, and dead-letter queue for Telegram API sends. |
| S-7 | **Webhook idempotency** | Deduplicate inbound webhook deliveries by `update_id` to prevent double command execution. |
| S-8 | **Secret management** | Bot token retrieved from Azure Key Vault (or Kubernetes secret / DPAPI in dev). Token never logged or serialized to telemetry. |
| S-9 | **Audit logging** | Every human response persisted with: `message_id`, `user_id`, `agent_id`, `timestamp`, `correlation_id`. |
| S-10 | **Observability** | OpenTelemetry traces and metrics; structured logging; health-check endpoint for the connector. |
| S-11 | **Rich formatting** | Markdown message bodies, inline buttons, reply correlation, deep links where the Telegram API supports them. |

---

## 3  Out of Scope

| # | Item | Rationale |
|---|------|-----------|
| O-1 | Discord, Slack, Teams connectors | Separate stories in the Messenger Gateway epic; this story covers only the Telegram connector. |
| O-2 | Connector-agnostic orchestration logic | The Worker Service host (`AgentSwarm.Messaging.Worker`) **is in scope** — it is created by this story (implementation-plan.md Stage 1.1) as the container for the Telegram connector. What is out of scope is any connector-agnostic routing, multi-connector dispatch, or orchestration logic beyond what the Telegram adapter itself requires. |
| O-3 | Agent Swarm Orchestrator internals | We consume its API surface; we do not modify it. |
| O-4 | Bot registration / BotFather setup | Operational runbook, not code. Document the required steps in a companion ops guide. |
| O-5 | Group-chat moderation features | V1 supports basic group-chat operation (allowlist enforcement per `user_id`, command attribution to the sending operator, unauthorized-button rejection) as defined in e2e-scenarios.md. Full group-moderation features (thread management, role-based visibility, per-group notification preferences) are follow-on. |
| O-6 | Media attachments (images, files) | Text and inline-button interactions only in V1. |
| O-7 | Telegram Payments or inline-query mode | Not relevant to operator workflows. |
| O-8 | Custom Telegram Bot API server (tdlib) | We use the official cloud Bot API endpoint. |

---

## 4  Non-Goals

1. **Real-time streaming / WebSocket push to Telegram.** Telegram Bot API is request/response; we do not attempt to work around this.
2. **Multi-language / i18n for bot messages.** English-only for V1; localization is a future epic.
3. **Replacing the orchestrator's internal event bus.** The Telegram connector is a leaf adapter; it does not become a message broker.
4. **Mobile app beyond Telegram.** No custom app; we rely on the Telegram client.

---

## 5  Hard Constraints

| ID | Constraint | Source |
|----|-----------|--------|
| HC-1 | Implementation language: **C# / .NET 8+** | Epic requirement |
| HC-2 | Must implement `IMessengerConnector` from `AgentSwarm.Messaging.Abstractions` | Epic shared interface (FR-001) |
| HC-3 | Must use the shared data model: `AgentQuestion`, `HumanAction`, `HumanDecisionEvent`. The story requires "proposed default action" handling. **Cross-document divergence exists:** e2e-scenarios.md (lines 57–77) models `DefaultAction` as a **first-class nullable property on `AgentQuestion`** — the fixture sets `DefaultAction = act-1` directly on the question, and the Telegram connector reads `DefaultAction` from the model and denormalizes it into `PendingQuestionRecord.DefaultActionId`. Architecture.md §3.1 (lines 167–190) models `AgentQuestion` **without** a `DefaultAction` property and instead carries the proposed default as `ProposedDefaultActionId` sidecar metadata on the `AgentQuestionEnvelope`. **This spec does not declare either document authoritative.** The divergence must be resolved by aligning the two documents. Regardless of which approach is adopted, the behavioral contract is identical: the connector obtains the proposed default action ID (from `AgentQuestion.DefaultAction` per e2e-scenarios.md, or from `AgentQuestionEnvelope.ProposedDefaultActionId` per architecture.md), denormalizes the `ActionId` into `PendingQuestionRecord.DefaultActionId` for efficient timeout polling, and applies it on timeout. When the question times out, `QuestionTimeoutService` reads `DefaultActionId`, resolves the full `HumanAction` from `IDistributedCache`, and publishes a `HumanDecisionEvent`; if absent (`null`), it publishes a timeout event with `ActionValue = "__timeout__"`. See Section 10. | Story requirement; **requires sibling-doc alignment** (e2e-scenarios.md vs. architecture.md) |
| HC-4 | P95 outbound send latency **< 2 seconds** after event is queued. Per architecture.md §10.4 (lines 673–700) and e2e-scenarios.md (lines 264–279), two complementary metrics are defined: **`telegram.send.latency_ms`** (primary) measures elapsed time from `OutboundMessage.CreatedAt` to Telegram Bot API HTTP 200, measured **only** for messages that succeed on their **first delivery attempt** and are **not** waiting behind a 429 rate-limit hold. This first-attempt, non-rate-limited metric is the one the **P95 ≤ 2 s acceptance criterion** applies to. Messages that are retried or rate-limited are excluded from this metric and tracked separately. **`telegram.send.all_attempts_latency_ms`** (diagnostic) measures the same interval but covers **all** messages regardless of attempt number or rate-limit holds — this broader metric captures the end-to-end experience including retries and rate-limit waits, useful for capacity planning. Additionally, `telegram.send.retry_latency_ms` (diagnostic) tracks latency for retried messages, and `telegram.send.rate_limited_wait_ms` (diagnostic) tracks time spent in 429 backoff. Under normal operating conditions (no rate-limit hits, first-attempt success), the vast majority of sends comfortably meet the 2-second target. Under extreme burst conditions (100+ agents), the priority queuing design ensures Critical/High severity messages are dispatched first and meet the 2-second target on first attempt, while Normal/Low severity messages may queue-delay beyond 2 seconds. | Story requirement + architecture.md §10.4 (lines 673–700, metric definitions) + e2e-scenarios.md (lines 264–279) |
| HC-5 | **Zero message loss** for Critical, High, and Normal severity messages under burst from 100+ agents (1 000+ queued events). Under extreme backpressure (queue depth exceeds `MaxQueueDepth`, default 5 000), **only Low-severity** messages that cannot be enqueued are immediately **dead-lettered** — not silently discarded, not shed. Dead-lettered messages are persisted in the dead-letter queue with full failure context, counted via a `telegram.messages.deadlettered_backpressure` metric, and trigger an ops-channel alert. Operators can replay dead-lettered messages from the DLQ once pressure subsides. This is consistent with architecture.md §10.4's priority-queuing design and the reliability model's "delivered-or-dead-lettered" contract. Critical, High, and Normal severity messages are never subject to backpressure rejection — they are always enqueued regardless of queue depth, consistent with architecture.md §10.4 which states "`Normal`, `High`, and `Critical` severity messages are always accepted." | Story + epic performance requirements + architecture.md §10.4 |
| HC-6 | Bot token stored in **Key Vault or equivalent**; never logged | Story security requirement |
| HC-7 | Webhook secret validation using Telegram's `X-Telegram-Bot-Api-Secret-Token` header | Telegram Bot API best practice + story security |
| HC-8 | Idempotent inbound processing — duplicate `update_id` must not re-execute commands | Story AC-003 |
| HC-9 | All messages carry `CorrelationId`, `AgentId`, `TaskId`, `ConversationId`, `Timestamp` | Epic FR-004 |
| HC-10 | Connector must recover from restart within **30 seconds** | Epic FR-007 |
| HC-11 | NuGet dependency: prefer `Telegram.Bot`; fall back to `Telegram.BotAPI` only if Bot API surface coverage is required | Story library preference |

---

## 6  Identified Risks

### 6.1  Technical Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|----|------|-----------|--------|------------|
| R-1 | **Telegram Bot API rate limits** — `sendMessage` is limited to ~30 msg/s globally per bot and ~20 msg/min per individual chat (per architecture.md §10.4). Under 100-agent burst, we may hit 429 errors. The P95 < 2 s target (HC-4) applies to the **first-attempt, non-rate-limited `telegram.send.latency_ms` metric** (architecture.md §10.4 lines 673–700; e2e-scenarios.md lines 264–279). Messages that encounter rate-limiting or require retries are excluded from this primary metric and tracked via the diagnostic `telegram.send.all_attempts_latency_ms` (all sends regardless of attempt/rate-limit), `telegram.send.retry_latency_ms` (retried messages), and `telegram.send.rate_limited_wait_ms` (429 backoff time). Under normal conditions most sends succeed on first attempt and comfortably meet the 2-second target; under extreme burst, priority queuing ensures Critical/High messages are dispatched first and meet the target on first attempt, while Normal/Low may queue-delay beyond 2 seconds. | High | High | Implement a dual token-bucket rate limiter in the outbound queue: one global bucket (30 msg/s) and per-chat buckets (20 msg/min). Back-off on 429 with `retry_after`. Spread alerts across operator chats when possible. |
| R-2 | **Webhook endpoint availability** — If the ASP.NET host is behind a load balancer restart or deployment, Telegram may fail to deliver updates and retry for a limited window. | Medium | Medium | Register webhook with `max_connections` tuned to replica count. Implement `/setWebhook` on startup with `drop_pending_updates=false`. For blue/green deploys, keep old pod alive until new webhook is confirmed. |
| R-3 | **Inline keyboard callback data limit** — Telegram limits `callback_data` to 64 bytes. Complex `HumanAction` payloads won't fit. | High | Medium | Encode `QuestionId:ActionId` in `callback_data` (aligned with architecture.md). `ActionId` is a short key; the full `HumanAction` payload is stored server-side in `IDistributedCache` keyed by `QuestionId:ActionId`, with entries expiring at `AgentQuestion.ExpiresAt`. On callback, the handler looks up the cache entry to resolve the chosen action. This keeps `callback_data` well within the 64-byte limit. |
| R-4 | **Long-polling ↔ webhook mode switching** — Telegram does not allow both simultaneously; switching requires calling `deleteWebhook` first. | Low | Low | Configuration-driven mode selection at startup. Guard with a startup check that calls `deleteWebhook` before starting long-polling and `setWebhook` before starting webhook mode. |
| R-5 | **Secret rotation** — If the Key Vault token is rotated while the connector is running, the bot loses API access until restart. | Medium | High | Use Key Vault SDK's `SecretClient` with `CacheControl` / periodic refresh (e.g., every 5 minutes via `IOptionsMonitor<T>` reload). |
| R-6 | **Outbox queue durability** — If the outbox is in-process only (e.g., `Channel<T>`), a process crash loses queued messages. | Medium | High | Use a durable store (database outbox table or external queue like Azure Service Bus) for the outbound queue. In-process channel serves only as a hot buffer in front of the durable store. |
| R-7 | **Update ID gap detection** — Telegram guarantees sequential `update_id` but may skip IDs. Deduplication must not treat gaps as missing messages. | Low | Medium | Deduplicate by storing processed `update_id` values in a sliding window (e.g., last 10 000 IDs) rather than relying on strict sequence. |

### 6.2  Operational Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|----|------|-----------|--------|------------|
| R-8 | **Operator loses phone / Telegram account compromised** — Attacker could issue `/approve` commands. | Low | Critical | Allowlist validation is necessary but not sufficient. Add optional 2FA challenge for high-severity approvals (e.g., require a confirmation code from a second channel). Log all commands with full audit trail for forensic review. |
| R-9 | **Telegram platform outage** — Extended downtime prevents operators from responding to blocking questions. | Low | High | `AgentQuestion.ExpiresAt` triggers the proposed default action if no human response is received. Alert operators via a secondary channel (email / PagerDuty) if Telegram send fails after all retries. |
| R-10 | **Regulatory / data-residency** — Telegram servers are outside the operator's jurisdiction; message content may include sensitive project names. | Medium | Medium | Do not send source code or secrets in Telegram messages. Limit message content to task summaries, question text, and action labels. Document data-classification policy for Telegram message content. |

### 6.3  Dependency Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|----|------|-----------|--------|------------|
| R-11 | **`Telegram.Bot` NuGet package breaking changes** — Major version bumps have historically changed the API surface significantly. | Medium | Medium | Pin to a specific major version in the `.csproj`. Wrap all Telegram SDK calls behind an internal `ITelegramClientWrapper` so upgrades are localized. |
| R-12 | **Shared abstractions not yet stable** — `IMessengerConnector` and the shared data model may evolve as Discord/Slack/Teams stories proceed in parallel. | High | Medium | Code against the interface as defined in the epic attachment. Flag any required interface changes as cross-story coordination items in the iteration summary. |

---

## 7  Key Decisions Required

| # | Decision | Options | Recommendation | Status |
|---|----------|---------|----------------|--------|
| D-1 | Durable outbox backing store | (a) Database table (EF Core), (b) Azure Service Bus, (c) In-process only | **(a) Database table via EF Core** — no external queue dependency. **SQLite for dev/local environments; PostgreSQL or SQL Server for production** — consistent with implementation-plan.md (Stages 4.1, 4.3, 5.1, 5.3) which specifies "SQLite for dev/local, PostgreSQL or SQL Server for production" for outbox, dead-letter, deduplication, and audit stores. Architecture.md (§11.3) frames the production provider as a deployment decision, which is compatible: the EF Core abstraction makes the provider a configuration swap, not a schema change. | Decided |
| D-2 | Deduplication store for `update_id` | (a) In-memory concurrent dictionary with TTL, (b) Database table, (c) Redis | **(a) In-memory** for single-instance dev, **(b) Database** for multi-instance prod. Configuration-driven. | Proposed |
| D-3 | Callback data encoding for inline buttons | (a) Direct `QuestionId:ActionId` in `callback_data` with server-side `HumanAction` lookup, (b) Full payload encoding in `callback_data` | **(a) `QuestionId:ActionId` with `IDistributedCache` lookup** — aligned with architecture.md. `ActionId` is a short key; the full `HumanAction` is stored server-side in `IDistributedCache` keyed by `QuestionId:ActionId`, written when the inline keyboard is built and expiring at `AgentQuestion.ExpiresAt`. This keeps `callback_data` well within Telegram's 64-byte limit. | Decided |
| D-4 | `/handoff` command semantics | (a) Transfer human oversight of a task to another operator, (b) Reassign the question to another operator, (c) Transfer task to a different agent team | **(a) Full transfer of human oversight — Decided per architecture.md, aligned across all sibling documents.** Architecture.md §5.5 (lines 513–526) specifies full oversight transfer (Decided): the handler accepts `/handoff TASK-ID @operator-alias` syntax and performs: (1) validates syntax (two arguments: task ID and operator alias); (2) validates that the specified task exists and the sending operator currently has oversight; (3) validates that the target operator (`@operator-alias`) is registered via `OperatorRegistry`; (4) transfers oversight by creating/updating a `TaskOversight` record (architecture.md §5.5 line 524) mapping the task to the target operator; (5) notifies both operators — the sender receives confirmation, the target receives a handoff notification with task context; (6) persists an audit record with handoff details (task ID, source operator, target operator, timestamp, `CorrelationId`); (7) returns error for invalid task ID, unregistered target operator, or missing arguments with usage help. Implementation-plan.md Stage 3.2 (lines 218–243) implements `HandoffCommandHandler` with full oversight transfer matching this specification — including task validation, operator resolution via `IOperatorRegistry`, `TaskOversight` record creation/update, bidirectional notification, and audit. E2e-scenarios.md (lines 360–376) tests the full transfer flow end-to-end. **All documents are aligned.** | Decided per architecture.md §5.5 |

---

## 8  Acceptance Criteria Traceability

| Story AC | Spec Coverage |
|----------|--------------|
| Human sends `/ask build release notes for Solution12` → swarm creates work item | S-2 (command handling), S-5 (identity mapping), HC-9 (correlation ID) |
| Agent asks blocking question → Telegram user answers from mobile | S-3 (question rendering), S-4 (decision events), S-11 (inline buttons). Question timeout/default action: sibling documents diverge on where `DefaultAction` lives — e2e-scenarios.md (lines 57–77) models it as a first-class property on `AgentQuestion`; architecture.md §3.1 (lines 167–190) carries it as `ProposedDefaultActionId` on the `AgentQuestionEnvelope`. Either way, the connector denormalizes the value into `PendingQuestionRecord.DefaultActionId` (HC-3), and `QuestionTimeoutService` reads this field at expiry to apply the default or emit `__timeout__`. See Section 10. |
| Approval/rejection buttons → strongly typed agent events | S-4 (`HumanDecisionEvent`), R-3 mitigation (callback data) |
| Duplicate webhook → no double execution | S-7 (idempotency), HC-8, R-7 |
| Telegram send failure → retry then dead-letter with alert | S-6 (durable outbound queue), R-1 (rate-limit handling) |
| All messages include trace/correlation ID | HC-9, S-9 (audit), S-10 (observability) |

---

## 9  Terminology

| Term | Definition |
|------|-----------|
| **Connector** | The Telegram-specific implementation of `IMessengerConnector`. Planned namespace: `AgentSwarm.Messaging.Telegram` (no source tree exists yet — see implementation-plan.md Stage 1.1 for solution scaffold). |
| **Gateway** | The host worker service that loads connectors. Planned namespace: `AgentSwarm.Messaging.Worker` (created by this story per implementation-plan.md Stage 1.1; no source tree exists yet). |
| **Outbox** | Durable table/queue that buffers outbound Telegram messages for reliable delivery. |
| **Operator** | A human authorized to interact with the swarm via Telegram. |
| **Decision Event** | A `HumanDecisionEvent` produced when an operator taps an inline button or replies to an `AgentQuestion`. |

---

*Cross-references: [architecture.md](architecture.md) (component diagram), [implementation-plan.md](implementation-plan.md) (task breakdown), [e2e-scenarios.md](e2e-scenarios.md) (acceptance test scripts).*

---

## 10  Sibling Document Alignment Notes

The following items were identified as cross-document alignment points during iteration reviews. This spec adopts a single authoritative design for each item and notes where sibling documents diverge without blocking implementation.

| Item | This spec | architecture.md | implementation-plan.md | e2e-scenarios.md | Resolution |
|------|-----------|-----------------|----------------------|-----------------|------------|
| `/handoff` semantics (D-4) | Decided — full oversight transfer per architecture.md §5.5 (lines 513–526), with validation, notification, and audit | §5.5 (lines 513–526): "Full oversight transfer (Decided)" — specifies full transfer with validation, `TaskOversight` record creation, notification, and audit. **Aligned** with this spec. | Stage 3.2 (lines 218–243): implements `HandoffCommandHandler` with **full oversight transfer** — task validation, operator resolution via `IOperatorRegistry`, `TaskOversight` record creation/update, bidirectional notification, and audit. **Aligned** with this spec and architecture.md. | Lines 360–376: "Scenario: /handoff transfers oversight to another operator" — tests full oversight transfer with task validation, operator resolution via `IOperatorRegistry`, `TaskOversight` record creation, bidirectional notification, and audit. **Aligned** with this spec and architecture.md. | **All documents are aligned** on full oversight transfer. No reconciliation needed. |
| Rate-limit model (R-1) | 30 msg/s global, 20 msg/min per chat | 30 msg/s global, 20 msg/min per chat | Not specified numerically | N/A | **Aligned** with architecture.md. |
| P95 latency scoping (HC-4) | < 2 s target applies to `telegram.send.latency_ms` (primary), which measures **first-attempt, non-rate-limited successes only**. A separate `telegram.send.all_attempts_latency_ms` (diagnostic) covers all messages regardless of attempt number or rate-limit holds. | §10.4 (lines 673–700): "`telegram.send.latency_ms` (primary) = … measured **only** for messages that succeed on their first delivery attempt and are **not** waiting behind a 429 rate-limit hold. This first-attempt, non-rate-limited metric is the one the P95 ≤ 2 s acceptance criterion applies to." `telegram.send.all_attempts_latency_ms` (diagnostic) = all messages regardless of attempt/rate-limit. **Aligned** with this spec. | Not specified numerically | Lines 264–279: "`telegram.send.latency_ms` (primary) = … measured ONLY for messages that succeed on first attempt and are NOT waiting behind a 429 rate-limit hold." P95 ≤ 2 s applies to this first-attempt metric. Separate `telegram.send.all_attempts_latency_ms` diagnostic histogram also emitted (line 279). **Aligned** with this spec and architecture.md. | **All documents are aligned.** The P95 ≤ 2 s target applies to the first-attempt, non-rate-limited `telegram.send.latency_ms` metric. The broader `telegram.send.all_attempts_latency_ms` is a diagnostic metric for capacity planning. |
| Callback data format (R-3, D-3) | `QuestionId:ActionId` with `IDistributedCache` lookup for full `HumanAction` | `QuestionId:ActionId` with `IDistributedCache` lookup, cache expires at `ExpiresAt` | `QuestionId:ActionId` | N/A | **Aligned** across all docs. `ActionId` is a short key; full `HumanAction` resolved via server-side cache. |
| DefaultAction model (HC-3) | **Cross-document divergence identified — not resolved by this spec.** The behavioral contract is identical regardless of approach: the Telegram connector obtains the proposed default action ID, denormalizes it into `PendingQuestionRecord.DefaultActionId`, displays it in the message body, and applies it on timeout. Only the read path differs (model property vs. envelope metadata). No implementation is blocked by this divergence. | §3.1 (lines 167–190): models `AgentQuestion` **without** a `DefaultAction` property. The proposed default action is carried as sidecar metadata via `ProposedDefaultActionId` on the `AgentQuestionEnvelope` (lines 183–190). §3.1 line 169: "The shared `AgentQuestion` model does **not** include a `DefaultAction` property; the proposed default action is carried as sidecar metadata." | Stage 1.2 (line 36): defines `AgentQuestion` with `DefaultAction` as a first-class property — aligns with e2e-scenarios.md but not with architecture.md. | Lines 57–77: models `DefaultAction` as a **first-class nullable property on `AgentQuestion`**. Line 57: "The shared AgentQuestion model includes a nullable DefaultAction property." Line 75: `DefaultAction = act-1`. Line 77: "the Telegram connector reads DefaultAction 'act-1' from the AgentQuestion model." **Diverges** from architecture.md's sidecar approach. | **Divergence exists between architecture.md (sidecar on envelope) and e2e-scenarios.md (first-class on model).** This spec surfaces the divergence for resolution by sibling-doc alignment. The connector's behavioral contract — obtain default action ID, denormalize into `PendingQuestionRecord.DefaultActionId`, apply on timeout — is identical under either approach. |
| Persistence provider (D-1) | EF Core: SQLite for dev/local, PostgreSQL or SQL Server for production | EF Core; provider is a deployment decision; SQLite as V1 default (§11.3, line 629) | SQLite for dev/local; PostgreSQL or SQL Server for production (Stages 4.1, 4.3, 5.1, 5.3) | N/A | **Aligned** across all docs. SQLite for dev/local, production-grade RDBMS for scaled deployments. |
| Low-severity backpressure (HC-5) | Dead-letter under extreme backpressure (>MaxQueueDepth); delivered-or-dead-lettered contract | MaxQueueDepth=5000; priority queuing; low-severity subject to backpressure | Not specified | N/A | **Aligned** with architecture.md. Dead-lettered messages are persisted and replayable from the DLQ — consistent with the delivered-or-dead-lettered reliability contract. |
