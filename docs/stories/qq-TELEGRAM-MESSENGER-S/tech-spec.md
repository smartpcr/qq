# Technical Specification — Telegram Messenger Support

**Story:** `qq:TELEGRAM-MESSENGER-S` · 13 SP
**Status:** Draft — Iteration 32
**Last updated:** 2026-05-11

---

## 1  Problem Statement

The agent swarm (100+ autonomous agents performing planning, coding, testing, release, and incident response) currently has no human-facing communication channel. Operators need a mobile-first interface to issue commands, answer blocking agent questions, approve or reject proposed actions, and receive urgent alerts — all from a phone.

This story delivers a **Telegram Bot connector** that plugs into the Messenger Gateway architecture described in the story's original brief and the sibling architecture document (`architecture.md`). The connector implements `IMessengerConnector`, speaks the Telegram Bot API over HTTPS, and integrates with the shared data model (`AgentQuestion`, `HumanAction`, `HumanDecisionEvent`).

The system must handle bursty traffic from 100+ concurrent agents without message loss, deduplicate webhook replays, and maintain a full audit trail with correlation IDs.

---

## 2  In-Scope

| # | Area | Detail |
|---|------|--------|
| S-1 | **Telegram Bot API integration** | HTTPS transport via `Telegram.Bot` NuGet package. Webhook receive in production; long-polling receive in dev/local. |
| S-2 | **Command handling** | `/start`, `/status`, `/agents`, `/ask`, `/approve`, `/reject`, `/handoff`, `/pause`, `/resume` — parsed, validated, and dispatched to the Agent Swarm Orchestrator. `/handoff TASK-ID @operator-alias` transfers full human oversight of a task to another operator (see D-4). The design is specified in architecture.md §5.5, implemented via `HandoffCommandHandler` in implementation-plan.md Stage 3.2, and tested end-to-end in e2e-scenarios.md. |
| S-3 | **Agent-to-human questions** | Render `AgentQuestion` as Telegram messages with inline keyboard buttons for each `HumanAction`. Include context, severity, timeout, and proposed default action in the message body. All sibling documents use the **sidecar envelope model**: `AgentQuestion` has no `DefaultAction` property; the proposed default is carried as `ProposedDefaultActionId` on `AgentQuestionEnvelope` (see architecture.md §3.1, implementation-plan.md Stage 1.2, e2e-scenarios.md). The Telegram connector reads `ProposedDefaultActionId` from the envelope, denormalizes it into `PendingQuestionRecord.DefaultActionId`, displays the default in the message body, and applies it on timeout. **Timeout behavior:** when `DefaultActionId` is non-null, `QuestionTimeoutService` applies the proposed default action automatically; when `DefaultActionId` is null, the question expires with `ActionValue = "__timeout__"` (no action is taken on behalf of the operator). |
| S-4 | **Strongly typed decision events** | Button taps and text replies are converted to `HumanDecisionEvent` and published to the orchestrator. |
| S-5 | **Operator identity mapping** | Map Telegram `chat_id` + `user_id` to an authorized operator record with tenant/workspace binding. Reject unmapped users. Applies in both 1:1 and group-chat contexts — commands in groups are attributed to the sending `user_id`, not the group `chat_id`. |
| S-6 | **Durable outbound queue** | Persistent queue (outbox pattern) with retry, exponential back-off, deduplication, and dead-letter queue for Telegram API sends. |
| S-7 | **Webhook idempotency** | Deduplicate inbound webhook deliveries by `update_id` to prevent double command execution. |
| S-8 | **Secret management** | Bot token retrieved from Azure Key Vault in production; .NET User Secrets for local development (per implementation-plan.md Stage 5.1). Token never logged or serialized to telemetry. |
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
| HC-2 | Must implement `IMessengerConnector` from planned `AgentSwarm.Messaging.Abstractions` (no source tree exists yet — created by this story per implementation-plan.md Stage 1.1) | Epic shared interface (FR-001) |
| HC-3 | Must use the shared data model: `AgentQuestion`, `HumanAction`, `HumanDecisionEvent`. The story requires "proposed default action" handling. All sibling documents use the **sidecar envelope model**: `AgentQuestion` has no `DefaultAction` property; the proposed default is carried as `ProposedDefaultActionId` on `AgentQuestionEnvelope` (architecture.md §3.1, implementation-plan.md Stage 1.2, e2e-scenarios.md). **Timeout behavior:** the Telegram connector reads `ProposedDefaultActionId` from the envelope and denormalizes it into `PendingQuestionRecord.DefaultActionId` for timeout polling. When `DefaultActionId` is non-null, `QuestionTimeoutService` applies the proposed default action automatically. When `DefaultActionId` is null, the question expires with `ActionValue = "__timeout__"` — no action is taken on behalf of the operator, and the requesting agent is notified of the unresolved timeout. | Story requirement; sidecar envelope model aligned across all sibling documents |
| HC-4 | P95 outbound send latency **< 2 seconds** after event is queued. **Scope clarification (operator answer to `p95-latency-scope`):** the P95 < 2 s target applies to **first-attempt, non-rate-limited sends only**; retried and rate-limited sends are tracked separately. This is not a weakening of the story requirement — it is an operator-directed refinement acknowledging that Telegram's own rate limits (~30 msg/s global, ~20 msg/min per chat) make it physically impossible to guarantee 2 s for sends delayed by 429 backoff. Three metrics form the latency observability model (defined in architecture.md §8 and §10.4 as the canonical source): **`telegram.send.first_attempt_latency_ms`** (acceptance gate) = elapsed time from **enqueue** (`OutboundMessage.CreatedAt`) to Telegram Bot API HTTP 200, measured only for messages that succeed on their first attempt without a 429 rate-limit hold. The **P95 ≤ 2 s** acceptance criterion applies to this metric. **`telegram.send.all_attempts_latency_ms`** (capacity planning) = enqueue to HTTP 200, measured for all messages regardless of attempt number or rate-limit holds. **`telegram.send.queue_dwell_ms`** (diagnostic) = enqueue to dequeue, monitors queue backlog. Under normal operating conditions, queue dwell is negligible (< 50 ms with 10 workers), so `first_attempt_latency_ms` comfortably meets P95 ≤ 2 s across all severities. Under burst, priority queuing ensures Critical/High messages are dequeued first. If a burst exceeds the capacity of the worker pool (e.g., > ~60 Critical+High messages at 30 msg/s), some first-attempt sends may breach 2 s due to queue dwell — `telegram.send.queue_dwell_ms` provides visibility for capacity planning. | Story requirement; operator answer `p95-latency-scope` confirms first-attempt, non-rate-limited scope; metric definitions follow architecture.md §8/§10.4 as canonical source |
| HC-5 | **Zero message loss** for Critical, High, and Normal severity messages under burst from 100+ agents (1 000+ queued events). Under extreme backpressure (queue depth exceeds `MaxQueueDepth`, default 5 000), **only Low-severity** messages that cannot be enqueued are immediately **dead-lettered** — not silently discarded, not shed. Dead-lettered messages are persisted in the dead-letter queue with full failure context, counted via the `telegram.messages.backpressure_dlq` counter (canonical name per architecture.md §8 Observability metrics table and §10.4 backpressure design), and trigger an ops-channel alert. Operators can replay dead-lettered messages from the DLQ once pressure subsides. This is consistent with architecture.md §10.4's priority-queuing design and the reliability model's "delivered-or-dead-lettered" contract. Critical, High, and Normal severity messages are never subject to backpressure rejection — they are always enqueued regardless of queue depth, consistent with architecture.md §10.4 which states "`Normal`, `High`, and `Critical` severity messages are always accepted." | Story + epic performance requirements + architecture.md §10.4 |
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
| R-1 | **Telegram Bot API rate limits** — `sendMessage` is limited to ~30 msg/s globally per bot and ~20 msg/min per individual chat (per architecture.md §10.4). Under 100-agent burst, we may hit 429 errors. The P95 < 2 s target (HC-4) applies to `telegram.send.first_attempt_latency_ms` — first-attempt, non-rate-limited sends only, measured from enqueue to HTTP 200 (per operator answer to `p95-latency-scope`). The all-inclusive metric `telegram.send.all_attempts_latency_ms` tracks all sends including retries and rate-limit waits for capacity planning. See HC-4. | High | High | Implement a dual token-bucket rate limiter in the outbound queue: one global bucket (30 msg/s) and per-chat buckets (20 msg/min). Back-off on 429 with `retry_after`. Spread alerts across operator chats when possible. |
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
| R-9 | **Telegram platform outage** — Extended downtime prevents operators from responding to blocking questions. | Low | High | `AgentQuestion.ExpiresAt` governs timeout behavior: if `ProposedDefaultActionId` is non-null, `QuestionTimeoutService` applies the proposed default action automatically; if `ProposedDefaultActionId` is null, the question expires with `ActionValue = "__timeout__"` and the requesting agent is notified of the unresolved timeout. In either case, alert operators via a secondary channel (email / PagerDuty) if Telegram send fails after all retries. |
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
| D-2 | Deduplication store for `update_id` | (a) In-memory concurrent dictionary with TTL, (b) Database table, (c) Redis | **(a) In-memory** for single-instance dev, **(b) Database** for multi-instance prod. Configuration-driven selection via `WebhookDeduplication:Store` setting (`InMemory` or `Database`). The database store uses the same EF Core provider as the outbox (SQLite dev, PostgreSQL/SQL Server prod). This is an acceptance criterion (HC-8 / story AC: "Duplicate webhook delivery does not execute the same human command twice"), so the design is decided, not optional. | Decided |
| D-3 | Callback data encoding for inline buttons | (a) Direct `QuestionId:ActionId` in `callback_data` with server-side `HumanAction` lookup, (b) Full payload encoding in `callback_data` | **(a) `QuestionId:ActionId` with `IDistributedCache` lookup** — aligned with architecture.md. `ActionId` is a short key; the full `HumanAction` is stored server-side in `IDistributedCache` keyed by `QuestionId:ActionId`, written when the inline keyboard is built and expiring at `AgentQuestion.ExpiresAt`. This keeps `callback_data` well within Telegram's 64-byte limit. | Decided |
| D-4 | `/handoff` command semantics | (a) Transfer human oversight of a task to another operator, (b) Reassign the question to another operator, (c) Transfer task to a different agent team | **(a) Full transfer of human oversight — Decided per architecture.md, aligned across all sibling documents.** Architecture.md §5.5 specifies full oversight transfer (Decided): the handler accepts `/handoff TASK-ID @operator-alias` syntax and performs: (1) validates syntax (two arguments: task ID and operator alias); (2) validates that the specified task exists and the sending operator currently has oversight; (3) validates that the target operator (`@operator-alias`) is registered via `OperatorRegistry`; (4) transfers oversight by creating/updating a `TaskOversight` record mapping the task to the target operator; (5) notifies both operators — the sender receives confirmation, the target receives a handoff notification with task context; (6) persists an audit record with handoff details (task ID, source operator, target operator, timestamp, `CorrelationId`); (7) returns error for invalid task ID, unregistered target operator, or missing arguments with usage help. Implementation-plan.md Stage 3.2 implements `HandoffCommandHandler` with full oversight transfer matching this specification — including task validation, operator resolution via `IOperatorRegistry`, `TaskOversight` record creation/update, bidirectional notification, and audit. E2e-scenarios.md tests the full transfer flow end-to-end, including success, nonexistent task, unregistered target, and invalid syntax scenarios. **All documents are aligned.** | Decided per architecture.md §5.5 |

---

## 8  Acceptance Criteria Traceability

| Story AC | Spec Coverage |
|----------|--------------|
| Human sends `/ask build release notes for Solution12` → swarm creates work item | S-2 (command handling), S-5 (identity mapping), HC-9 (correlation ID) |
| Agent asks blocking question → Telegram user answers from mobile | S-3 (question rendering), S-4 (decision events), S-11 (inline buttons). All sibling documents use the sidecar envelope model: `ProposedDefaultActionId` on `AgentQuestionEnvelope` is denormalized into `PendingQuestionRecord.DefaultActionId`. On timeout, `QuestionTimeoutService` applies the default action if present, or emits `ActionValue = "__timeout__"` if absent. |
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

The following items were identified as cross-document alignment points during iteration reviews. **Cross-document status: ALL ALIGNED.** Metric definitions, behavioral contracts, and design decisions are aligned across all sibling documents. Architecture.md §10.4 (line 697) confirms: "No divergences remain." E2e-scenarios.md footer (line 755) confirms all sibling documents are aligned on metric names, enqueue-to-HTTP-200 measurement points, and P95 scope. No stale editorial notes remain in any sibling document.

| Item | This spec | architecture.md | implementation-plan.md | e2e-scenarios.md | Resolution |
|------|-----------|-----------------|----------------------|-----------------|------------|
| `/handoff` semantics (D-4) | Decided — full oversight transfer per architecture.md §5.5, with validation, notification, and audit | §5.5: "Full oversight transfer (Decided)" — specifies full transfer with validation, `TaskOversight` record creation, notification, and audit. **Aligned** with this spec. | Stage 3.2: implements `HandoffCommandHandler` with **full oversight transfer** — task validation, operator resolution via `IOperatorRegistry`, `TaskOversight` record creation/update, bidirectional notification, and audit. **Aligned** with this spec and architecture.md. | Six `/handoff` scenarios — success transfer, nonexistent task, unregistered target operator, invalid syntax (no args), invalid syntax (one arg). Tests full oversight transfer with task validation, operator resolution via `IOperatorRegistry`, `TaskOversight` record creation, bidirectional notification, and audit. **Aligned** with this spec and architecture.md. | **All documents aligned.** No reconciliation needed. |
| Rate-limit model (R-1) | 30 msg/s global, 20 msg/min per chat | 30 msg/s global, 20 msg/min per chat | Not specified numerically | N/A | **Aligned** with architecture.md. |
| P95 latency scoping (HC-4) | **This spec follows architecture.md §8/§10.4 as canonical source.** `telegram.send.first_attempt_latency_ms` (acceptance gate) = **enqueue** (`OutboundMessage.CreatedAt`) to HTTP 200, first-attempt, non-rate-limited; P95 ≤ 2 s applies. `telegram.send.all_attempts_latency_ms` (all-inclusive) = enqueue to HTTP 200, all sends regardless of attempt or rate-limit — capacity planning. `telegram.send.queue_dwell_ms` (diagnostic) = enqueue to dequeue. Under normal load, queue dwell is negligible so the enqueue-based gate comfortably meets P95 ≤ 2 s. Under burst, the P95 ≤ 2 s target holds only when ≤ ~60 Critical+High messages are in the burst (architecture.md §10.4 bounded-volume caveat); larger bursts exceed 2 s for some messages. Per operator answer to `p95-metric-scope`, the P95 target applies to first-attempt, non-rate-limited sends only. | §8 Observability table: defines `telegram.send.first_attempt_latency_ms` as "enqueue — `OutboundMessage.CreatedAt` — to HTTP 200, first-attempt, non-rate-limited sends only — P95 ≤ 2 s target." All-inclusive metric: `telegram.send.all_attempts_latency_ms` (enqueue to HTTP 200 regardless of attempt or rate-limit). §10.4: same metric definitions. §10.4 (line 697) cross-document status confirms: "No divergences remain." **Verified — fully aligned.** | **Verified — aligned.** Implementation-plan.md Stage 4.1 (outbound processor, lines 372–374) and PERF001 (line 563) define `telegram.send.first_attempt_latency_ms` as **enqueue** (`OutboundMessage.CreatedAt`) to HTTP 200, first-attempt, non-rate-limited — P95 ≤ 2 s acceptance gate. All-inclusive metric: `telegram.send.all_attempts_latency_ms` (enqueue to HTTP 200, all sends). Diagnostic: `telegram.send.queue_dwell_ms` (enqueue to dequeue). Cross-document alignment note (line 574) confirms architecture.md §10.4 as canonical source. **All metric names, measurement points, and scopes match architecture.md and this spec.** | **Verified — aligned.** E2e-scenarios.md scenarios (lines 234–235, 267–286, 296–301) and metrics table (lines 651–653) define `telegram.send.first_attempt_latency_ms` as **enqueue** (`OutboundMessage.CreatedAt`) to HTTP 200, first-attempt, non-rate-limited — P95 ≤ 2 s. All-inclusive metric: `telegram.send.all_attempts_latency_ms` (enqueue to HTTP 200, all sends). Diagnostic: `telegram.send.queue_dwell_ms` (enqueue to dequeue). Footer (line 755) confirms all sibling documents are aligned on metric names, enqueue-to-HTTP-200 measurement points, and P95 scope; architecture.md §10.4 is the canonical source. **Metric definitions aligned.** | **Metric definitions aligned across all documents.** All four documents use enqueue-to-HTTP-200 measurement, `telegram.send.first_attempt_latency_ms` (acceptance gate), `telegram.send.all_attempts_latency_ms` (all-inclusive), and `telegram.send.queue_dwell_ms` (diagnostic). P95 ≤ 2 s applies to first-attempt, non-rate-limited sends only (per operator answer to `p95-metric-scope`). Architecture.md §10.4 (line 697) confirms: "No divergences remain." E2e-scenarios.md footer (line 755) confirms all sibling documents are aligned on metric names, measurement points, and P95 scope. **No stale editorial notes remain in any sibling document.** |
| Callback data format (R-3, D-3) | `QuestionId:ActionId` with `IDistributedCache` lookup for full `HumanAction` | `QuestionId:ActionId` with `IDistributedCache` lookup, cache expires at `ExpiresAt` | `QuestionId:ActionId` | N/A | **All documents aligned.** `ActionId` is a short key; full `HumanAction` resolved via server-side cache. |
| DefaultAction model (HC-3) | **Sidecar envelope model RESOLVED.** `AgentQuestion` has no `DefaultAction` property; the proposed default is carried as `ProposedDefaultActionId` on `AgentQuestionEnvelope`. | §3.1: models `AgentQuestion` **without** a `DefaultAction` property. Proposed default carried as `ProposedDefaultActionId` sidecar metadata on `AgentQuestionEnvelope`. `PendingQuestionRecord` is the persistence model that denormalizes `DefaultActionId` from the envelope. The sidecar design is embodied in the model definitions. **Aligned.** | Stage 1.2: defines `AgentQuestion` **without** `DefaultAction`; creates `AgentQuestionEnvelope` with `ProposedDefaultActionId`. **Aligned.** | Explicitly adopts the sidecar envelope model. Confirms all sibling documents mark DefaultAction placement as RESOLVED. Footer reiterates the sidecar model. **Aligned.** | **All documents aligned** on the sidecar envelope model. No remaining contradictions. |
| Persistence provider (D-1) | EF Core: SQLite for dev/local, PostgreSQL or SQL Server for production | EF Core; provider is a deployment decision; SQLite as V1 default (§11.3) | SQLite for dev/local; PostgreSQL or SQL Server for production (Stages 4.1, 4.3, 5.1, 5.3) | N/A | **All documents aligned.** SQLite for dev/local, production-grade RDBMS for scaled deployments. |
| Low-severity backpressure (HC-5) | Dead-letter under extreme backpressure (>MaxQueueDepth); delivered-or-dead-lettered contract | MaxQueueDepth=5000; priority queuing; low-severity subject to backpressure | Not specified | N/A | **Aligned** with architecture.md. Dead-lettered messages are persisted and replayable from the DLQ — consistent with the delivered-or-dead-lettered reliability contract. |
