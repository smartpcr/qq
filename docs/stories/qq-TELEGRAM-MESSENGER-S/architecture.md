# Architecture — Telegram Messenger Support (qq-TELEGRAM-MESSENGER-S)

## 1. Problem Statement

The agent swarm (100+ autonomous agents) requires a Telegram-based human interface so that mobile operators can start tasks, answer blocking questions, approve/reject actions, and receive urgent alerts — all without access to a dashboard or CLI. The Telegram connector must slot into the shared `IMessengerConnector` abstraction planned for the Messenger Gateway epic while meeting Telegram-specific requirements for webhook transport, inline buttons, and the 2-second P95 send-latency target.

**Reliability and performance summary:** The architecture guarantees **at-least-once delivery with dead-letter fallback** — every outbound message is either delivered to Telegram (possibly more than once in narrow crash windows; see §3.1 Gap A) or dead-lettered with a traceable reason and retained for operator-initiated manual replay. Under extreme queue depth, Low-severity messages may be backpressure-dead-lettered without a send attempt (see §10.4); Critical, High, and Normal messages are never backpressure-DLQ'd. The **P95 ≤ 2 s send-latency SLO** (measured on `telegram.send.first_attempt_latency_ms` — first-attempt sends that did not receive a Telegram 429 response, per operator answer to `p95-metric-scope`; local token-bucket wait is included) is met in **steady state** (queue depth < 100) across all severities, and extends to **bounded bursts** (≤ 50 Critical+High messages in a 100+ agent burst, **distributed across ≥ 10 operator chats** — this deployment topology is a **formal operator decision** per §11.1 D-BURST: ≥ 10 operator chats is the expected topology given 100+ agents spanning multiple tenants/workspaces with distinct operator assignments; deployments with fewer than 10 operator chats must accept that the P95 SLO applies only under steady-state load, not during bursts — see §10.4 for the per-chat analysis and assumption framing) via severity-based priority queuing at 30 msg/s throughput with token-bucket burst capacity allowing the first 30 messages to drain without token wait (see §10.4 burst math). The P95 of 50 priority messages (the 48th message) completes enqueue-to-HTTP-200 at approximately 1,900 ms — safely under 2 s. Between 50 and 60 Critical+High messages, P95 approaches and may exceed 2 s depending on HTTP variance. Beyond ~60, P95 clearly exceeds 2 s — this is an accepted capacity trade-off, not a message-loss risk. Sends that receive Telegram 429 responses are excluded from the acceptance gate metric and tracked separately via `telegram.send.all_attempts_latency_ms` for capacity planning (per §11.1 D-P95). See §10.4 for the full analysis.

---

## 2. Component Overview

### 2.1 Component Diagram

```text
┌─────────────────────────────────────────────────────────────────┐
│                    Messenger Gateway (Worker Service)           │
│                                                                 │
│  ┌──────────────────┐   ┌────────────────────┐                  │
│  │ Webhook Endpoint │   │ TelegramPollingService │                 │
│  │ (ASP.NET Core)   │   │ (BackgroundService) │                 │
│  │ - Secret-token   │   │                     │                 │
│  │   validation     │   │                     │                 │
│  └────────┬─────────┘   └────────┬────────────┘                 │
│           │  Inbound Update       │  Inbound Update             │
│           ▼                       ▼                             │
│  ┌──────────────────────────────────────────────┐               │
│  │         TelegramUpdateRouter                 │               │
│  │  - Deduplication (update_id idempotency)     │               │
│  │  - Allowlist gate                            │               │
│  └────────────────────┬─────────────────────────┘               │
│                       │                                         │
│                       ▼                                         │
│  ┌──────────────────────────────────────────────┐               │
│  │         CommandDispatcher                    │               │
│  │  - Parses /start, /status, /agents, /ask,    │               │
│  │    /approve, /reject, /handoff, /pause,      │               │
│  │    /resume                                   │               │
│  │  - Callback-query handler (inline buttons)   │               │
│  └────────────────────┬─────────────────────────┘               │
│                       │                                         │
│       ┌───────────────┼───────────────┐                         │
│       ▼               ▼               ▼                         │
│  ┌─────────┐   ┌────────────┐   ┌──────────────┐               │
│  │ AuthZ   │   │ Operator   │   │ Swarm        │               │
│  │ Service │   │ Registry   │   │ Command Bus  │               │
│  └─────────┘   └────────────┘   └──────┬───────┘               │
│                                        │                        │
│                       ┌────────────────┘                        │
│                       ▼                                         │
│  ┌──────────────────────────────────────────────┐               │
│  │         OutboundMessageQueue                 │               │
│  │  - Persistent store + Channel<T> hot buffer  │               │
│  │  - Retry + exponential back-off              │               │
│  │  - Dead-letter queue                         │               │
│  │  - Deduplication (idempotency key)           │               │
│  └────────────────────┬─────────────────────────┘               │
│                       ▼                                         │
│  ┌──────────────────────────────────────────────┐               │
│  │      TelegramSender (ITelegramBotClient)     │               │
│  │  - Rate limiter (30 msg/s global,            │               │
│  │    20 msg/min per chat)                       │               │
│  │  - Inline keyboard builder                   │               │
│  │  - Markdown V2 formatter                     │               │
│  └──────────────────────────────────────────────┘               │
│                                                                 │
│  ┌──────────────────────────────────────────────┐               │
│  │          AuditLogger                         │               │
│  │  - Persists every human response             │               │
│  │  - Fields: MessageId, UserId, AgentId,       │               │
│  │    Timestamp, CorrelationId                   │               │
│  └──────────────────────────────────────────────┘               │
└─────────────────────────────────────────────────────────────────┘
                        │
                        ▼
          ┌──────────────────────────┐
          │  Agent Swarm Orchestrator│
          └──────────────────────────┘
```

### 2.2 Component Responsibilities

| Component | Planned Assembly | Responsibility |
|---|---|---|
| **Webhook Endpoint** | `AgentSwarm.Messaging.Telegram` (to be created) | ASP.NET Core controller that receives Telegram `Update` POSTs. Validates the `X-Telegram-Bot-Api-Secret-Token` header, persists the `InboundUpdate` record (including the full raw `Update` JSON payload) for deduplication and crash recovery, and returns `200 OK`. Authorization, command parsing, and all further processing happen asynchronously inside `ITelegramUpdatePipeline.ProcessAsync` (see §5.1, implementation-plan.md Stage 2.2/2.4). This boundary ensures Telegram receives a fast acknowledgement while authorization failures are handled as pipeline-level rejections with an outbound reply, not as HTTP error codes. |
| **TelegramPollingService** | `AgentSwarm.Messaging.Telegram` (to be created) | `BackgroundService` that calls `GetUpdatesAsync` in a loop. Enabled when `TelegramOptions.UsePolling = true`; used in local/dev only. Mutually exclusive with webhook mode — startup fails if both `UsePolling = true` and `WebhookUrl` is set (per implementation-plan.md Stage 2.5). Shares the same downstream pipeline as the webhook. |
| **TelegramUpdateRouter** | `AgentSwarm.Messaging.Telegram` (to be created) | Central inbound pipeline stage (inside `ITelegramUpdatePipeline`). Deduplicates by `update_id`, performs authorization via `IUserAuthorizationService` (operator allowlist and binding checks), enriches with correlation ID, and dispatches to `CommandDispatcher` or `CallbackQueryHandler`. Unauthorized commands receive a rejection reply via the outbound queue. |
| **CommandDispatcher** | `AgentSwarm.Messaging.Telegram` (to be created) | Maps incoming text commands to strongly typed `SwarmCommand` objects. Delegates callback-query payloads (button presses) to `CallbackQueryHandler` which produces `HumanDecisionEvent`. |
| **AuthZ Service** | `AgentSwarm.Messaging.Core` (to be created) | Validates that the Telegram user ID + chat ID pair is in the authorized operator registry. Returns tenant/workspace binding or rejects the request. |
| **Operator Registry** | `AgentSwarm.Messaging.Core` (to be created) | Persistent map of `(TelegramUserId, TelegramChatId) → one or more OperatorBinding(TenantId, WorkspaceId, Roles, OperatorAlias)`. Runtime authorization first checks whether any active binding exists for the `(TelegramUserId, TelegramChatId)` pair via `IsAuthorizedAsync`; when multiple bindings exist (the operator is registered in several workspaces for the same chat), `GetBindingsAsync` returns all matching rows and the caller disambiguates workspace via inline keyboard (see §4.3, §7.1). The `UNIQUE` constraint is on `(TelegramUserId, TelegramChatId, WorkspaceId)` to prevent duplicate bindings for the same workspace. Populated via `/start` registration flow and admin configuration. |
| **Swarm Command Bus** | `AgentSwarm.Messaging.Core` (to be created) | Publishes validated, strongly typed commands to the agent swarm orchestrator. Subscribes to agent-originated events (questions, alerts, status) via `SubscribeAsync` and routes them to the correct outbound connector. Both command publishing and event subscription are on the single `ISwarmCommandBus` interface (see §4.6). |
| **OutboundMessageQueue** | `AgentSwarm.Messaging.Abstractions` (interface) / `AgentSwarm.Messaging.Core` (implementation) (to be created) | Durable queue for outbound messages backed by a persistent store (database) with an in-memory `Channel<T>` hot buffer for low-latency dequeue. The persistent store is the source of truth; the `Channel<T>` is a read-through acceleration layer, not a standalone queue. Provides at-least-once delivery, deduplication by idempotency key, severity-based priority ordering (Critical > High > Normal > Low), retry with configurable exponential back-off (default max 5 attempts), and dead-letter after exhaustion. |
| **TelegramSender** | `AgentSwarm.Messaging.Telegram` (to be created) | Concrete `TelegramMessageSender` implementing `IMessageSender` (§4.12, defined in `AgentSwarm.Messaging.Core`). Both methods return `Task<SendResult>` carrying the Telegram-assigned `message_id`. Wraps `ITelegramBotClient` from the `Telegram.Bot` library. **Sole owner of Telegram-specific rendering**: formats messages with MarkdownV2, builds `InlineKeyboardMarkup` for agent questions (rendered at send time from `SourceEnvelopeJson`, not from pre-rendered `Payload`), writes `HumanAction` entries to `IDistributedCache`, and enforces Telegram rate limits. `OutboundQueueProcessor` invokes the sender through the `IMessageSender` abstraction, keeping `AgentSwarm.Messaging.Worker` independent of the Telegram assembly. |
| **AuditLogger** | `AgentSwarm.Messaging.Persistence` (to be created) | Writes an immutable audit record for every human response. Includes message ID, user ID, agent ID, timestamp, and correlation ID. Backed by append-only store. |
| **OutboundQueueProcessor** | `AgentSwarm.Messaging.Worker` (to be created) | `BackgroundService` with configurable concurrency (`OutboundQueue:ProcessorConcurrency`, default 10 workers). Each worker independently dequeues the highest-severity pending `OutboundMessage`, dispatches to `IMessageSender` based on `SourceType` (question → `SendQuestionAsync`; all others → `SendTextAsync` with pre-rendered `Payload`), calls `IOutboundQueue.MarkSentAsync` with the `SendResult.TelegramMessageId`, and for question messages calls `IPendingQuestionStore.StoreAsync` as a post-send hook (§5.2 invariant 1). On failure, increments `AttemptCount` and schedules retry or dead-letters. Depends on `IOutboundQueue`, `IMessageSender`, `IPendingQuestionStore`. See §10.4 for concurrency and burst analysis. |
| **InboundRecoverySweep** | `AgentSwarm.Messaging.Worker` (to be created) | `BackgroundService` that runs on startup and periodically (configurable interval, default 60 s via `InboundRecovery:SweepIntervalSeconds`). Queries `IInboundUpdateStore` for `InboundUpdate` records with `IdempotencyStatus` in `Received`, `Processing`, or `Failed` where `AttemptCount < MaxRetries` (configurable via `InboundRecovery:MaxRetries`, default 3). Deserializes each record's `RawPayload` as a Telegram `Update`, maps it through `TelegramUpdateMapper` to produce a `MessengerEvent`, then passes the `MessengerEvent` to `ITelegramUpdatePipeline.ProcessAsync` for idempotent re-processing. Increments `AttemptCount` on failure. This component owns the no-command-loss guarantee across process restarts — see §3.1 `InboundUpdate` and §5.1 for the recovery flow. |
| **QuestionRecoverySweep** | `AgentSwarm.Messaging.Telegram` (to be created) | `BackgroundService` that runs on startup and periodically (configurable interval, default 60 s). Queries for `OutboundMessage` records with `SourceType = Question` and `Status = Sent` that lack a corresponding `PendingQuestionRecord`, then backfills the missing records using the stored `TelegramMessageId`, `SourceId` (as `QuestionId`), and `SourceEnvelopeJson` (deserialized to extract `AgentQuestion` fields, `ProposedDefaultActionId`, and the resolved `DefaultActionValue`). This component owns the Gap B mitigation — see §3.1 `PendingQuestionRecord` for the crash-window analysis. |

---

## 3. Data Model

### 3.1 Entities

#### OperatorBinding

Links a Telegram identity to the swarm's authorization model. Each row represents one (user, chat, workspace) binding, supporting multi-chat and multi-workspace scenarios described in e2e-scenarios (§Agent Routing and Tenant Mapping).

| Field | Type | Description |
|---|---|---|
| `Id` | `Guid` | Surrogate primary key. |
| `TelegramUserId` | `long` | Telegram's unique user identifier. |
| `TelegramChatId` | `long` | Telegram chat ID (private chat or group). |
| `ChatType` | `enum` | `Private`, `Group`, `Supergroup`. Used for group attribution logic: commands in groups are attributed to the sending `TelegramUserId`, not the group `TelegramChatId`. |
| `OperatorAlias` | `string` | Human-readable alias (e.g., `@operator-1`) used in `/handoff TASK-ID @alias` lookups. |
| `TenantId` | `string` | Swarm tenant this operator belongs to. |
| `WorkspaceId` | `string` | Workspace within the tenant. One operator may have bindings in multiple workspaces; when a command is ambiguous, the bot presents an inline keyboard for workspace disambiguation (per e2e-scenarios). |
| `Roles` | `string[]` | `Operator`, `Approver`, `Admin`. |
| `RegisteredAt` | `DateTimeOffset` | When `/start` was executed. |
| `IsActive` | `bool` | Soft-disable without deleting. |

**Constraints:**
- `UNIQUE (TelegramUserId, TelegramChatId, WorkspaceId)` — prevents duplicate bindings.
- Composite index on `(TelegramUserId, TelegramChatId)` — used for authorization lookups (validates the chat/user pair).
- `UNIQUE (OperatorAlias, TenantId)` — ensures alias uniqueness within a tenant. `/handoff @alias` resolution calls `GetByAliasAsync(alias, tenantId)` which uses this index; because the index is tenant-scoped, two tenants may independently use the same alias without collision, and a `/handoff` in one tenant cannot accidentally resolve an operator in a different tenant.

#### MessengerMessage (shared model — to be defined in planned `AgentSwarm.Messaging.Abstractions`)

The common message envelope used by `IMessengerConnector.SendMessageAsync` for all non-question outbound messages (status updates, command acknowledgements, alert notifications). Carries the full metadata required by tech-spec HC-9 and implementation-plan.md Stage 1.2.

| Field | Type | Description |
|---|---|---|
| `MessageId` | `string` | Unique message identifier (UUID). |
| `CorrelationId` | `string` | End-to-end trace/correlation ID. |
| `AgentId` | `string?` | Originating or target agent identifier. Nullable because some outbound messages (e.g., `/ask` acknowledgements, `/start` welcome messages) are sent before an agent is assigned. When null, the message is not agent-scoped. Required by tech-spec HC-9 when an agent context exists. |
| `TaskId` | `string?` | Associated task/work-item identifier. Nullable because some outbound messages (e.g., `/start` welcome, `/ask` acknowledgement before task creation completes, `/status` summaries) exist before a task/work-item is assigned. Required by tech-spec HC-9 when a task context exists; when null, the message is not task-scoped. |
| `ConversationId` | `string` | Conversation context identifier linking related messages. Required by tech-spec HC-9. |
| `Timestamp` | `DateTimeOffset` | Message creation time (UTC). Required by tech-spec HC-9. |
| `Text` | `string` | Message body text. |
| `Severity` | `MessageSeverity` | Enum: `Critical`, `High`, `Normal`, `Low`. Determines outbound priority queue ordering. |
| `Metadata` | `Dictionary<string, string>` | Extensible key-value pairs for connector-specific context (e.g., formatting hints, routing overrides). |

> **Cross-doc alignment (MessengerMessage):** Implementation-plan.md Stage 1.2 defines `MessengerMessage` with these exact fields (`MessageId`, `CorrelationId`, `AgentId`, `TaskId`, `ConversationId`, `Timestamp`, `Text`, `Severity`, `Metadata`). Both `AgentId` and `TaskId` are nullable (`string?`) because some outbound messages (e.g., `/ask` acknowledgements, `/start` welcome messages, `/status` summaries) exist before an agent or task is assigned; tech-spec HC-9 requires `AgentId` and `TaskId` when their respective contexts exist. The `OutboundMessage` entity (below) stores the rendered Telegram payload in `Payload` and the original source envelope (when applicable) in `SourceEnvelopeJson`, adding queue-specific fields (`IdempotencyKey`, `Status`, `AttemptCount`, etc.) for durable delivery.

**Cardinality examples:**
- 1:1 chat, single workspace: one row per operator.
- 1:1 chat, multiple workspaces: multiple rows with same `(UserId, ChatId)` but different `WorkspaceId`.
- Group chat: rows for each authorized operator in the group, with `ChatType = Group`. Commands are attributed to `TelegramUserId`; unauthorized users in the same group are rejected.

#### InboundUpdate (deduplication + durable work-queue record)

| Field | Type | Description |
|---|---|---|
| `UpdateId` | `long` | Telegram's monotonic `update_id`. Primary key. |
| `RawPayload` | `string` | Full serialized Telegram `Update` JSON. Persisted before returning `200 OK` so that a crash after acknowledgement does not lose the command. On restart, `InboundRecoverySweep` re-processes any records with `IdempotencyStatus` in `Received`, `Processing`, or `Failed` (where `AttemptCount < MaxRetries`) by deserializing `RawPayload` and feeding it back into the command pipeline. `Received`/`Processing` records represent crash recovery; `Failed` records represent transient handler errors eligible for automatic retry. Records with `AttemptCount ≥ MaxRetries` remain in `Failed` for manual investigation and alerting. |
| `ReceivedAt` | `DateTimeOffset` | First receipt timestamp. |
| `ProcessedAt` | `DateTimeOffset?` | When processing completed (null = in-flight). |
| `IdempotencyStatus` | `enum` | `Received`, `Processing`, `Completed`, `Failed`. The four-status model is canonical; permanently failing updates stay in `Failed` with `AttemptCount ≥ MaxRetries` and are excluded from recovery sweeps (see below). |
| `AttemptCount` | `int` | Default 0. Incremented on each reprocessing attempt by `InboundRecoverySweep`. When `AttemptCount ≥ MaxRetries` (configurable via `InboundRecovery:MaxRetries`, default 3), the record stays in `Failed` and is excluded from future recovery sweeps. |
| `ErrorDetail` | `string?` | Stores the latest failure reason for diagnostics. Written by `MarkFailedAsync` on each failed processing attempt. |

#### OutboundMessage

| Field | Type | Description |
|---|---|---|
| `MessageId` | `Guid` | Internal unique identifier. Primary key. |
| `IdempotencyKey` | `string` | Deterministic key preventing duplicate sends. Derivation depends on message origin — see below. |
| `ChatId` | `long` | Target Telegram chat. |
| `Payload` | `string` | Outbox payload whose semantics depend on `SourceType`. **For `CommandAck`, `StatusUpdate`, and `Alert`**: pre-rendered Telegram message content (MarkdownV2 text) ready for `IMessageSender.SendTextAsync` — rendering is performed by `TelegramMessengerConnector` at enqueue time. **For `Question`**: a human-readable preview of the question (title, severity, body summary) stored for debugging, dead-letter inspection, and replay diagnostics — **not** the actual send content. For questions, the actual Telegram send is rendered at send time by `TelegramMessageSender.SendQuestionAsync`, which reads from `SourceEnvelopeJson` (below) to build the inline keyboard, write `IDistributedCache` entries, and format the full MarkdownV2 message. This split ensures that question rendering — which involves side effects (cache writes) and Telegram-specific layout (inline keyboards) — happens exactly once at send time, not at enqueue time. |
| `SourceEnvelopeJson` | `string?` | Serialized original source envelope. Populated only when `SourceType = Question` (stores the full `AgentQuestionEnvelope` JSON) or `SourceType = Alert` (stores the full `AgentAlertEvent` JSON). Used by `QuestionRecoverySweep` to backfill `PendingQuestionRecord` fields (e.g., `DefaultActionId`, `DefaultActionValue`, `ExpiresAt`, `AgentQuestion`) when a crash occurs between `MarkSentAsync` and `PendingQuestionRecord` persistence (see Gap B below). Also preserved verbatim in `DeadLetterMessage.SourceEnvelopeJson` for replay (not `DeadLetterMessage.Payload`, which holds the rendered Telegram content). Null for `CommandAck` and `StatusUpdate` source types. |
| `Severity` | `enum` | `Critical`, `High`, `Normal`, `Low`. Determines priority queue ordering. |
| `Status` | `enum` | `Pending`, `Sending`, `Sent`, `Failed`, `DeadLettered`. |
| `AttemptCount` | `int` | Number of delivery attempts so far. |
| `NextRetryAt` | `DateTimeOffset?` | Scheduled next attempt. |
| `CreatedAt` | `DateTimeOffset` | Enqueue time. |
| `SentAt` | `DateTimeOffset?` | Telegram confirmation time. |
| `TelegramMessageId` | `long?` | Telegram's returned `message_id` on success. **Canonical type is `long`** (nullable `long?` here because it is null before the first successful send). All references across the architecture use `long` consistently: `OutboundMessage.TelegramMessageId` (`long?`), `PendingQuestionRecord.TelegramMessageId` (`long`, non-nullable — the record is created only after a successful send), `IPendingQuestionStore.StoreAsync` parameter (`long telegramMessageId`), `IPendingQuestionStore.GetByTelegramMessageAsync` parameter (`long telegramMessageId`), and `IOutboundQueue.MarkSentAsync` parameter (`long telegramMessageId`). Telegram's `message_id` is an `int` in the Bot API JSON, but is stored as `long` to accommodate future Bot API changes and to unify the type across all internal interfaces. This document (architecture.md) is the **canonical source** for the `long`/`long?` type convention; implementation-plan.md Stage 1.4's `IOutboundQueue.MarkSentAsync` adopts `MarkSentAsync(Guid messageId, long telegramMessageId, CancellationToken)` — not `int`. |
| `CorrelationId` | `string` | End-to-end trace ID. |
| `SourceType` | `enum` | `Question`, `Alert`, `StatusUpdate`, `CommandAck`. Discriminator for origin type. |
| `SourceId` | `string?` | The `QuestionId` for question messages; alert rule ID for alerts; command correlation ID for acks. Null only for fire-and-forget status broadcasts. |
| `ErrorDetail` | `string?` | Last error message for diagnostics. |

**Idempotency key derivation by `SourceType`:**

| SourceType | IdempotencyKey formula | Example |
|---|---|---|
| `Question` | `q:{AgentId}:{QuestionId}` | `q:build-agent-3:Q-42` |
| `Alert` | `alert:{AgentId}:{AlertId}` | `alert:monitor-1:alert-77` |
| `StatusUpdate` | `s:{AgentId}:{CorrelationId}` | `s:deploy-2:trace-def` |
| `CommandAck` | `c:{CorrelationId}` | `c:trace-ghi` |

The `UNIQUE` constraint on `IdempotencyKey` in the outbox table prevents duplicate enqueue regardless of message origin. For question messages, the key includes `QuestionId` so re-delivery of the same agent question is deduplicated. For alert messages, the key includes `AlertId` (the unique identifier from `AgentAlertEvent`) so re-delivery of the same alert event is deduplicated. For acks and status updates, the key is derived from the correlation context, ensuring each distinct event produces exactly one outbound message.

#### AgentQuestion (shared model — to be defined in planned `AgentSwarm.Messaging.Abstractions`)

The shared `AgentQuestion` model represents a blocking question from an agent to a human operator. The shared model does **not** include a `DefaultAction` property; the proposed default action is carried as sidecar metadata via `ProposedDefaultActionId` in the `AgentQuestionEnvelope` (see below). This separation keeps the shared model focused on the question itself, while routing/context metadata (including the default action and connector-specific routing hints) lives in the envelope.

| Field | Type | Description |
|---|---|---|
| `QuestionId` | `string` | Unique question identifier. |
| `AgentId` | `string` | Originating agent. |
| `TaskId` | `string` | Associated work item / task. |
| `Title` | `string` | Short summary. |
| `Body` | `string` | Full context for the operator. |
| `Severity` | `MessageSeverity` | Enum: `Critical`, `High`, `Normal`, `Low`. `MessageSeverity` is the canonical enum defined in `AgentSwarm.Messaging.Abstractions` and used consistently across all data models, interfaces, and priority-queuing logic. |
| `AllowedActions` | `HumanAction[]` | Buttons to render. See `HumanAction` definition below. |
| `ExpiresAt` | `DateTimeOffset` | Timeout deadline. |
| `CorrelationId` | `string` | Trace ID. |

**Constraints:**
- `QuestionId` is constrained to a maximum of **30 characters** to satisfy the Telegram `callback_data` format `QuestionId:ActionId` (see §11 and `HumanAction.ActionId` below). Combined with the `:` separator and `ActionId` (also ≤ 30 characters), the total payload is ≤ 61 bytes, within Telegram's 64-byte `callback_data` limit.

#### HumanAction (shared model — to be defined in planned `AgentSwarm.Messaging.Abstractions`)

Represents a single action button that an agent question can offer to a human operator. Used as the element type of `AgentQuestion.AllowedActions` and the payload stored in `IDistributedCache` for callback resolution.

| Field | Type | Description |
|---|---|---|
| `ActionId` | `string` | Unique identifier for this action within the question's `AllowedActions` list. Constrained to a maximum of **30 characters** to fit within the Telegram `callback_data` format `QuestionId:ActionId` (≤ 61 bytes total, within Telegram's 64-byte limit). Used as the value in `callback_data`, in `PendingQuestionRecord.SelectedActionId`, and in `PendingQuestionRecord.DefaultActionId` (denormalized from `AgentQuestionEnvelope.ProposedDefaultActionId`). **Not** directly emitted in `HumanDecisionEvent.ActionValue` — the emitted value is always `HumanAction.Value`, whether resolved interactively (button tap / text reply) or via timeout (denormalized as `DefaultActionValue`). See `Value` below. |
| `Label` | `string` | Human-readable button text displayed on the inline keyboard (e.g., "Approve", "Reject", "Need more info"). Maximum 64 characters (Telegram inline button text limit). |
| `Value` | `string` | Machine-readable action value carried in `HumanDecisionEvent.ActionValue` when this action is selected. This is the **canonical source** for the `ActionValue` field in the emitted event. When the operator taps an inline button, the `CallbackQueryHandler` parses `ActionId` from the `callback_data`, looks up the full `HumanAction` from `IDistributedCache` (keyed by `QuestionId:ActionId`), and reads `HumanAction.Value` to populate `HumanDecisionEvent.ActionValue`. Typically matches `ActionId` but may differ when the consuming agent expects a different semantic value (e.g., `ActionId = "approve-btn"`, `Value = "approve"`). Consistent with e2e-scenarios.md line 141 which states "ActionValue carries the HumanAction.Value from AllowedActions." |
| `RequiresComment` | `bool` | When `true`, the `CallbackQueryHandler` defers `HumanDecisionEvent` emission: it sets `PendingQuestionRecord.Status = AwaitingComment`, prompts the operator for a text reply, and emits the event only after the reply arrives (see §5.2). When `false`, the event is emitted immediately on button tap. Default: `false`. |

**Constraints:**
- `ActionId` must be unique within a single `AgentQuestion.AllowedActions` array.
- `ActionId` is constrained to ≤ 30 characters (see `AgentQuestion` constraints above and §11).
- `Label` is constrained to ≤ 64 characters (Telegram inline button text limit).

#### AgentQuestionEnvelope (shared model — to be defined in planned `AgentSwarm.Messaging.Abstractions`)

Wraps an `AgentQuestion` with routing and context metadata. The envelope is the unit of transport through `IMessengerConnector.SendQuestionAsync` (§4.1) and `IPendingQuestionStore.StoreAsync`.

| Field | Type | Description |
|---|---|---|
| `Question` | `AgentQuestion` | The question payload. |
| `ProposedDefaultActionId` | `string?` | The `ActionId` from `AllowedActions` to apply automatically on timeout. When `null`, the question expires with `ActionValue = "__timeout__"`. Carried as sidecar metadata, not a property of the shared question model. |
| `RoutingMetadata` | `Dictionary<string, string>` | Extensible key-value pairs for connector-specific routing (e.g., `TelegramChatId`). |

> **Default action flow.** When the Telegram connector renders an `AgentQuestionEnvelope` as an inline-keyboard message, the sender (`TelegramMessageSender.SendQuestionAsync`) reads `ProposedDefaultActionId` from the envelope and, when present, displays the proposed default in the message body (e.g., "Default action if no response: Approve"). The sender does **not** persist or denormalize the default action — that responsibility belongs to the `OutboundQueueProcessor` post-send hook (§5.2 invariant 1): after `MarkSentAsync` succeeds, the processor calls `IPendingQuestionStore.StoreAsync`, and the store implementation denormalizes `ProposedDefaultActionId` into both `PendingQuestionRecord.DefaultActionId` (for display/audit) and `PendingQuestionRecord.DefaultActionValue` (for event emission) at persistence time. The store resolves `DefaultActionValue` by finding the `HumanAction` in `AgentQuestion.AllowedActions` whose `ActionId` matches `ProposedDefaultActionId` and reading its `Value`. This enables `QuestionTimeoutService` to poll for expired questions and read `PendingQuestionRecord.DefaultActionValue`, then publish a `HumanDecisionEvent` with `DefaultActionValue` as the `ActionValue` — consistent with the interactive button-tap path where `HumanAction.Value` is always the canonical `ActionValue` (see `HumanAction.Value` definition above). No `IDistributedCache` lookup or `AgentQuestion` JSON deserialization is needed at timeout. When `ProposedDefaultActionId` is `null`, both `DefaultActionId` and `DefaultActionValue` are `null`, the question expires with `ActionValue = "__timeout__"`, and no automatic decision is applied.

#### PendingQuestionRecord (to be defined in planned `AgentSwarm.Messaging.Persistence`)

Tracks an `AgentQuestion` that has been sent to an operator and is awaiting a response. The record is persisted **after** the Telegram API call succeeds — i.e., after `TelegramSender.SendMessageAsync` returns a valid `message_id` — consistent with implementation-plan.md Stage 3.5 which states "after successfully sending a question to Telegram, persist the question with its Telegram message ID for later lookup." This means `TelegramMessageId` is always populated at creation time, and no intermediate `Sending` status exists.

**Crash-window analysis — two gaps and their mitigations:**

The outbound send flow for a question message is: (1) `OutboundMessage` dequeued, `Status` set to `Sending`; (2) `TelegramSender` calls Telegram API; (3) on success, `MarkSentAsync` updates `OutboundMessage` to `Status = Sent` with `TelegramMessageId`; (4) `PendingQuestionRecord` is persisted. Two crash windows exist:

**Gap A — Crash between Telegram API success (step 2) and `MarkSentAsync` (step 3).** The `OutboundMessage` is still in `Sending` status with no `TelegramMessageId`. On restart, the queue processor treats `Sending` records as incomplete and re-sends them, producing a duplicate Telegram message. This is the inherent cost of at-least-once delivery over an external API without two-phase commit. **Mitigation:** The duplicate message is operationally benign because (a) the operator sees two identical button messages but the `PendingQuestionRecord` (keyed by `QuestionId`) ensures only one pending question exists — whichever callback the operator taps resolves correctly, and (b) `QuestionRecoverySweep` (see below) detects `OutboundMessage` records with `SourceType = Question` and `Status = Sent` that lack a corresponding `PendingQuestionRecord` and backfills the missing record using the `TelegramMessageId` from the *latest* successful send. The `IdempotencyKey` (`q:{AgentId}:{QuestionId}`) prevents the same question from being *enqueued* twice, but cannot prevent duplicate sends from the same `OutboundMessage` across crash boundaries — this is documented as an accepted at-least-once trade-off.

**Gap B — Crash between `MarkSentAsync` (step 3) and `PendingQuestionRecord` persistence (step 4).** The `OutboundMessage` has `Status = Sent` and `TelegramMessageId` populated, but no `PendingQuestionRecord` exists. **Mitigation:** On restart, `QuestionRecoverySweep` queries for `OutboundMessage` records with `SourceType = Question` and `Status = Sent` that lack a corresponding `PendingQuestionRecord`, and backfills the missing records using the stored `TelegramMessageId`, `SourceId` (as `QuestionId`), and the original `AgentQuestionEnvelope` (preserved in `OutboundMessage.SourceEnvelopeJson` — distinct from the rendered `Payload`). The sweep deserializes `SourceEnvelopeJson` to extract `AgentQuestion` fields (`ExpiresAt`, `AllowedActions`, etc.) and `ProposedDefaultActionId`, then denormalizes both `DefaultActionId` and `DefaultActionValue` (by resolving the matching `HumanAction.Value` from `AllowedActions`) into the backfilled `PendingQuestionRecord`. This ensures no operator-visible button message is left untracked.

This two-gap analysis avoids a pre-send placeholder record (no `Sending` state in `PendingQuestionRecord`) while accepting the well-bounded at-least-once duplicate risk inherent in any non-transactional external API call.

| Field | Type | Description |
|---|---|---|
| `QuestionId` | `string` | Foreign key to the `AgentQuestion.QuestionId` this record tracks. Primary key. |
| `AgentQuestion` | `string` | The full `AgentQuestion` serialized as JSON. Preserves complete question context for display, audit, and timeout handling without requiring a separate lookup. |
| `TelegramChatId` | `long` | Telegram chat the question was sent to. |
| `TelegramMessageId` | `long` | Telegram `message_id` of the sent inline-keyboard message. Always populated at record creation (the record is only persisted after a successful send). Typed as `long` to match implementation-plan.md Stage 1.3 `PendingQuestion.TelegramMessageId` and Stage 3.5 `PendingQuestionRecord.TelegramMessageId`. |
| `DefaultActionId` | `string?` | Denormalized from `AgentQuestionEnvelope.ProposedDefaultActionId` at question-send time. Identifies which `HumanAction` in `AllowedActions` is the proposed default. Used for display/audit (e.g., showing the default action label in timeout messages) and for `QuestionRecoverySweep` backfill correlation. When `null`, the question has no proposed default action. |
| `DefaultActionValue` | `string?` | The `HumanAction.Value` of the default action, resolved and denormalized at question-send time by `IPendingQuestionStore.StoreAsync`: the store looks up the `HumanAction` in `AgentQuestion.AllowedActions` whose `ActionId` matches `ProposedDefaultActionId` and persists its `Value` here. When present, `QuestionTimeoutService` publishes a `HumanDecisionEvent` with `DefaultActionValue` as the `ActionValue` — consistent with the interactive button-tap path where `HumanAction.Value` is always the canonical `ActionValue` (see `HumanAction.Value` definition above, §5.2, §10.3). No `IDistributedCache` lookup or `AgentQuestion` JSON deserialization is required at timeout. When `null` (either because `ProposedDefaultActionId` is null or the matching `HumanAction` was not found in `AllowedActions`), the question expires with `ActionValue = "__timeout__"`. |
| `ExpiresAt` | `DateTimeOffset` | Copied from `AgentQuestion.ExpiresAt` for efficient timeout polling. |
| `Status` | `enum` | `Pending`, `Answered`, `AwaitingComment`, `TimedOut` — aligned with implementation-plan.md Stage 3.5. `Pending` is the initial state (record is created only after a successful Telegram send). `AwaitingComment` is set when the operator taps a button whose `HumanAction.RequiresComment = true`; the bot prompts for a text reply and defers `HumanDecisionEvent` emission until the reply arrives (see §5.2). `Answered` is set when the operator completes their response (button tap or comment reply). `TimedOut` is the single canonical enum value for timed-out questions, used consistently across the abstraction DTO (`PendingQuestion.Status` — implementation-plan.md Stage 1.3), the persistence entity (`PendingQuestionRecord.Status` — implementation-plan.md Stage 3.5), and the timeout flow (`QuestionTimeoutService`). |
| `SelectedActionId` | `string?` | Set when the operator taps an inline button: stores the `ActionId` parsed from `callback_data`. Null while `Status = Pending`; populated when `Status` transitions to `AwaitingComment` or `Answered`. When `Status = AwaitingComment`, this field holds the action selected by the operator (e.g., `"need-info"`) so that when the follow-up text reply arrives, the `HumanDecisionEvent` can carry both the `ActionValue` and the `Comment`. When `Status = Answered` via direct button tap (no comment required), the field records which action was chosen for audit. |
| `SelectedActionValue` | `string?` | The `HumanAction.Value` corresponding to `SelectedActionId`, resolved at button-tap time and persisted by `IPendingQuestionStore.RecordSelectionAsync` (§4.7). The `CallbackQueryHandler` resolves `HumanAction.Value` from `IDistributedCache` (primary path) or, on cache miss, from the durable `AgentQuestion` JSON stored in `PendingQuestionRecord.AgentQuestion` (fallback — see §5.2). Persisting the resolved value here ensures the `RequiresComment` text-reply path can emit `HumanDecisionEvent.ActionValue` from durable storage without depending on the volatile cache, which may expire or be evicted between button tap and text reply. For direct (non-`RequiresComment`) button taps, the `ActionValue` is resolved and emitted immediately — no persistence race exists. |
| `RespondentUserId` | `long?` | Telegram user ID of the operator who tapped the inline button. Set at the same time as `SelectedActionId` by `IPendingQuestionStore.RecordSelectionAsync` (§4.7). Used together with `TelegramChatId` and `Status = AwaitingComment` to correlate the follow-up text reply to the correct pending question — the text-reply handler calls `IPendingQuestionStore.GetAwaitingCommentAsync(chatId, respondentUserId)` which returns the oldest `AwaitingComment` record by `StoredAt`. When only one question is `AwaitingComment` for a given `(chatId, userId)` pair, the match is exact; if the same operator has multiple `AwaitingComment` questions in the same chat (an unlikely but possible race when the operator taps buttons on two questions before replying), the handler applies deterministic tie-breaking by selecting the **oldest** by `StoredAt`, processes it first, and warns the operator about the remaining pending questions. |
| `StoredAt` | `DateTimeOffset` | When the record was persisted (after successful Telegram send). |
| `CorrelationId` | `string` | Trace/correlation ID for end-to-end observability. |

**Constraints:**
- `UNIQUE (QuestionId)` — one pending record per question.
- Index on `(Status, ExpiresAt)` — used by `QuestionTimeoutService` to poll for expired questions.
- Index on `(TelegramChatId, RespondentUserId, Status)` — used by `IPendingQuestionStore.GetAwaitingCommentAsync` to match a follow-up comment to the correct `AwaitingComment` question. This is a **non-unique** index: it is possible (though unlikely) for the same operator to have multiple `AwaitingComment` questions in the same chat if they tap buttons on two questions in rapid succession before replying. When multiple rows match `(TelegramChatId, RespondentUserId, Status = AwaitingComment)`, `GetAwaitingCommentAsync` returns the **oldest** record by `StoredAt` for deterministic tie-breaking; the text-reply handler processes it first and warns the operator about remaining pending questions.
- Index on `(TelegramChatId, TelegramMessageId)` — composite index because Telegram `message_id` is only unique within a chat; used by `QuestionRecoverySweep` (which correlates `OutboundMessage` rows with their `PendingQuestionRecord` by chat+message pair) and by `QuestionTimeoutService` (which edits the original Telegram message using both identifiers). **Not** used for callback query resolution — the `CallbackQueryHandler` parses `QuestionId` from the `callback_data` field (`QuestionId:ActionId` format) and calls `IPendingQuestionStore.GetAsync(questionId)`, which looks up by the primary key `QuestionId`. This avoids cross-chat `message_id` collisions entirely.

#### HumanDecisionEvent (shared model — to be defined in planned `AgentSwarm.Messaging.Abstractions`)

| Field | Type | Description |
|---|---|---|
| `QuestionId` | `string` | Which question this answers. |
| `ActionValue` | `string` | Selected action (`approve`, `reject`, etc.). |
| `Comment` | `string?` | Optional operator comment. |
| `Messenger` | `string` | Always `"Telegram"` for this connector. |
| `ExternalUserId` | `string` | Telegram user ID (stringified). |
| `ExternalMessageId` | `string` | Telegram `message_id` of the callback. |
| `ReceivedAt` | `DateTimeOffset` | UTC receipt time. |
| `CorrelationId` | `string` | Trace ID. |

#### AuditEntry / AuditLogEntry

The abstraction-level entity is `AuditEntry` (defined in the planned `AgentSwarm.Messaging.Abstractions`, per implementation-plan.md Stage 1.3). The persistence-level entity is `AuditLogEntry` (defined in `AgentSwarm.Messaging.Persistence`, per implementation-plan.md Stage 5.3), which extends `AuditEntry` with `TenantId`, `Platform`, and database-specific columns. The `PersistentAuditLogger` maps from the abstraction `AuditEntry` to the persistence `AuditLogEntry` entity.

**`AuditEntry` (Abstractions layer — implementation-plan.md Stage 1.3):**

| Field | Type | Description |
|---|---|---|
| `EntryId` | `Guid` | Primary key (generated at creation). |
| `MessageId` | `string` | Telegram `message_id` or internal ID. |
| `UserId` | `string` | External user ID (e.g., Telegram user ID). |
| `AgentId` | `string?` | Target or source agent. Null for commands that operate without an agent context (e.g., `/start`, `/ask` before work-item creation). |
| `Action` | `string` | Command or decision value. |
| `Timestamp` | `DateTimeOffset` | UTC. |
| `CorrelationId` | `string` | Trace ID. |
| `Details` | `string` | Serialized additional context (JSON). |

**`AuditLogEntry` (Persistence layer — implementation-plan.md Stage 5.3):**

The persistence entity maps from `AuditEntry` and adds deployment-context columns:

| Field | Type | Description |
|---|---|---|
| `Id` | `Guid` | Primary key (mapped from `AuditEntry.EntryId`). |
| `MessageId` | `string` | From `AuditEntry.MessageId`. |
| `ExternalUserId` | `string` | From `AuditEntry.UserId`. Named `ExternalUserId` in the persistence schema to distinguish from internal identity. |
| `AgentId` | `string?` | From `AuditEntry.AgentId` (null when no agent context). |
| `Action` | `string` | From `AuditEntry.Action`. |
| `Timestamp` | `DateTimeOffset` | From `AuditEntry.Timestamp`. |
| `CorrelationId` | `string` | From `AuditEntry.CorrelationId`. |
| `TenantId` | `string` | Operator's tenant (derived from `OperatorBinding` at mapping time). |
| `Details` | `string` | From `AuditEntry.Details` (JSON). |
| `Platform` | `string` | Always `"Telegram"` for this connector. |

#### TaskOversight

Tracks which operator currently has oversight of which task. Created/updated by the `/handoff` command handler. Also used by the orchestrator subscription filter to route agent events to the correct operator.

| Field | Type | Description |
|---|---|---|
| `TaskId` | `string` | Primary key. The task being overseen. |
| `OperatorBindingId` | `Guid` | FK to `OperatorBinding.Id`. The operator who currently has oversight. Typed as `Guid` to match `OperatorBinding.Id` and implementation-plan.md Stage 1.2. |
| `AssignedAt` | `DateTimeOffset` | When oversight was assigned or last transferred. |
| `AssignedBy` | `string` | The operator who initiated the handoff (their `OperatorBinding.Id`). |
| `CorrelationId` | `string` | Trace ID for the handoff action. |

#### DeadLetterMessage

Records outbound messages that have exhausted all retry attempts. Preserves full message payload, all attempt timestamps, and error details for diagnostics, alerting, and manual replay. Created by `OutboundMessageQueue` when `AttemptCount ≥ MaxRetries` (default 5) or when a non-retryable error (HTTP 400, 403) is encountered.

| Field | Type | Description |
|---|---|---|
| `Id` | `Guid` | Primary key. |
| `OriginalMessageId` | `Guid` | FK to `OutboundMessage.MessageId`. Links back to the original outbox record (which remains in `DeadLettered` status). |
| `IdempotencyKey` | `string` | Copied from `OutboundMessage.IdempotencyKey` for cross-reference. |
| `ChatId` | `long` | Target Telegram chat. |
| `Payload` | `string` | Verbatim copy of `OutboundMessage.Payload`. For `CommandAck`, `StatusUpdate`, and `Alert` source types, this is pre-rendered Telegram message content (MarkdownV2 text). For `Question` source types, this is a human-readable preview (title, severity, body summary) — not the actual send content, which is rendered at send time from `SourceEnvelopeJson` (see §3.1 `OutboundMessage.Payload`). For replay of question messages, `SourceEnvelopeJson` (below) provides the full source envelope needed to reconstruct the send. |
| `SourceEnvelopeJson` | `string?` | Copied from `OutboundMessage.SourceEnvelopeJson`. Preserves the original source envelope (e.g., `AgentQuestionEnvelope` for question messages) so that replay can reconstruct the full send context without depending on external state. |
| `Severity` | `enum` | `Critical`, `High`, `Normal`, `Low`. Copied from the original message for priority-based alerting (Critical/High dead-letters trigger immediate ops alerts). |
| `SourceType` | `enum` | `Question`, `Alert`, `StatusUpdate`, `CommandAck`. Copied from `OutboundMessage.SourceType`. |
| `SourceId` | `string?` | Copied from `OutboundMessage.SourceId` (e.g., `QuestionId` for question messages). |
| `CorrelationId` | `string` | End-to-end trace ID for the original message. |
| `AttemptCount` | `int` | Total number of delivery attempts made before dead-lettering. |
| `AttemptTimestamps` | `string` | JSON array of `DateTimeOffset` values, one per attempt. Example: `["2026-05-11T18:00:00Z","2026-05-11T18:00:02Z","2026-05-11T18:00:06Z","2026-05-11T18:00:14Z","2026-05-11T18:00:30Z"]`. |
| `FinalError` | `string` | Error message/exception from the last failed attempt. |
| `ErrorHistory` | `string` | JSON array of `{ "attempt": int, "timestamp": DateTimeOffset, "error": string, "httpStatus": int? }` objects — one per attempt. Preserves the full failure progression for diagnostics. |
| `AlertStatus` | `enum` | `Pending`, `Sent`, `Acknowledged`. Tracks whether the ops alert for this dead-letter has been dispatched and acknowledged. |
| `AlertSentAt` | `DateTimeOffset?` | When the ops alert was sent (null until `AlertStatus` transitions to `Sent`). |
| `ReplayStatus` | `enum` | `None`, `Queued`, `Succeeded`, `Failed`. Tracks manual replay attempts. `Queued` = re-enqueued to `OutboundMessageQueue`; `Succeeded`/`Failed` = outcome of the replay send. |
| `ReplayCorrelationId` | `string?` | Correlation ID of the replay attempt (links the replayed `OutboundMessage` back to this dead-letter record). Null until replay is attempted. |
| `DeadLetteredAt` | `DateTimeOffset` | When the message was moved to dead-letter. |
| `CreatedAt` | `DateTimeOffset` | Original `OutboundMessage.CreatedAt` — preserves the original enqueue time for latency analysis. |

**Constraints:**
- `UNIQUE (OriginalMessageId)` — one dead-letter record per outbound message.
- Index on `(AlertStatus, Severity)` — used by the alerting loop to find un-alerted Critical/High dead-letters.
- Index on `(ReplayStatus)` — used by operators reviewing replay-eligible messages.
- Index on `DeadLetteredAt` — used for retention and pruning queries.

### 3.2 Entity Relationships

```text
OperatorBinding *──* OutboundMessage       (via OperatorBinding.TelegramChatId = OutboundMessage.ChatId;
                                            resolved through tenant/workspace routing)
OutboundMessage *──0..1 AgentQuestion      (via OutboundMessage.SourceId = AgentQuestion.QuestionId
                                            when OutboundMessage.SourceType = Question)
OutboundMessage        (alerts, acks, status updates have no AgentQuestion relationship)
InboundUpdate   1──0..1 HumanDecisionEvent (for callback queries; linked by processing context,
                                            not a direct FK — the update triggers the event)
PendingQuestionRecord *──1 AgentQuestion   (via PendingQuestionRecord.QuestionId = AgentQuestion.QuestionId)
AuditLogEntry   *──1 OperatorBinding       (via AuditLogEntry.ExternalUserId → OperatorBinding lookup;
                                            resolved at query time through the operator registry, not a direct FK)
AuditLogEntry   *──0..1 AgentQuestion      (via AuditLogEntry.CorrelationId; joins through the
                                            correlation context, not a direct FK)
TaskOversight   *──1 OperatorBinding       (via TaskOversight.OperatorBindingId = OperatorBinding.Id;
                                            tracks which operator currently oversees the task)
DeadLetterMessage 1──1 OutboundMessage     (via DeadLetterMessage.OriginalMessageId = OutboundMessage.MessageId;
                                            preserves full payload and attempt history for messages
                                            that exhausted retries or hit non-retryable errors)
```

---

## 4. Interfaces Between Components

### 4.1 IMessengerConnector (shared abstraction — to be defined in planned `AgentSwarm.Messaging.Abstractions`)

The Telegram connector implements the common gateway interface to be defined in the planned `AgentSwarm.Messaging.Abstractions` project:

```csharp
public interface IMessengerConnector
{
    Task SendMessageAsync(MessengerMessage message, CancellationToken ct);
    Task SendQuestionAsync(AgentQuestionEnvelope envelope, CancellationToken ct);
    Task<IReadOnlyList<MessengerEvent>> ReceiveAsync(CancellationToken ct);
}
```

`TelegramMessengerConnector : IMessengerConnector` delegates `SendMessageAsync` and `SendQuestionAsync` to the `OutboundMessageQueue` and implements `ReceiveAsync` by draining processed inbound events. `SendQuestionAsync` accepts the full `AgentQuestionEnvelope` so the connector can read `ProposedDefaultActionId` and `RoutingMetadata` (e.g., `TelegramChatId`) from the envelope sidecar. This signature aligns with implementation-plan.md Stage 1.3.

### 4.2 ITelegramUpdatePipeline (to be defined in planned `AgentSwarm.Messaging.Abstractions`)

```csharp
public interface ITelegramUpdatePipeline
{
    /// Processes a mapped MessengerEvent through dedup, allowlist, dispatch.
    /// Returns a PipelineResult indicating whether the event was handled,
    /// an optional response text, and the CorrelationId.
    Task<PipelineResult> ProcessAsync(MessengerEvent messengerEvent, CancellationToken ct);
}

public sealed record PipelineResult(bool Handled, string? ResponseText, string CorrelationId);
```

Both the webhook controller and `TelegramPollingService` first map the raw Telegram `Update` to a `MessengerEvent` using `TelegramUpdateMapper`, then pass the result to `ProcessAsync`. The `PipelineResult` return type (to be defined in the planned `AgentSwarm.Messaging.Abstractions` project, per implementation-plan Stage 1.3) provides structured outcome information: `Handled = true` when the pipeline fully processed the event (including duplicate short-circuits — a duplicate is "handled" by the deduplication stage, which returns `PipelineResult { Handled = true }` to signal that no further action is needed, consistent with implementation-plan Stage 2.2), `Handled = false` only when the event type is unrecognized or the pipeline cannot determine how to process it, `ResponseText` for any reply to send back to the user, and `CorrelationId` for tracing. Unauthorized events also return `Handled = true` with a rejection `ResponseText` enqueued to the outbound queue — the pipeline handled the event by rejecting it. This boundary aligns with implementation-plan Stage 2.5 and keeps `ITelegramUpdatePipeline` transport-agnostic — the pipeline never sees a Telegram-specific `Update` object.

### 4.3 IOperatorRegistry

```csharp
public interface IOperatorRegistry
{
    Task<IReadOnlyList<OperatorBinding>> GetBindingsAsync(long telegramUserId, long chatId, CancellationToken ct);
    Task<IReadOnlyList<OperatorBinding>> GetAllBindingsAsync(long telegramUserId, CancellationToken ct);
    Task<OperatorBinding?> GetByAliasAsync(string operatorAlias, string tenantId, CancellationToken ct);
    Task<IReadOnlyList<OperatorBinding>> GetByWorkspaceAsync(string workspaceId, CancellationToken ct);
    Task RegisterAsync(OperatorRegistration registration, CancellationToken ct);
    Task<bool> IsAuthorizedAsync(long telegramUserId, long chatId, CancellationToken ct);
}
```

**Multi-workspace disambiguation:** `GetBindingsAsync(userId, chatId)` returns **all** `OperatorBinding` rows matching the (user, chat) pair — one per workspace. Callers handle cardinality as follows:

- **Zero results:** The user+chat pair has no binding → command is rejected (unauthorized).
- **Exactly one result:** Unambiguous → command proceeds with that binding's `TenantId`/`WorkspaceId`.
- **Multiple results:** The user has bindings in multiple workspaces for this chat → the `CommandDispatcher` presents an inline keyboard listing the available workspaces and waits for the operator to select one (per e2e-scenarios workspace disambiguation flow). The selected workspace is cached for the session to avoid repeated prompts.

`GetAllBindingsAsync(userId)` returns all bindings across all chats for a user, used for administrative queries and `/status` across workspaces. `GetByWorkspaceAsync(workspaceId)` returns all active `OperatorBinding` rows for a given workspace, used by alert fallback routing (§5.6) when no `TaskOversight` record exists for an alert's `TaskId` — the first active binding in the workspace receives the alert. `IsAuthorizedAsync(userId, chatId)` is a fast-path check returning `true` if at least one active binding exists for the pair — used by the allowlist gate before command processing. `GetByAliasAsync(alias, tenantId)` resolves an operator by alias within a tenant, used by `/handoff` target resolution; the `UNIQUE (OperatorAlias, TenantId)` constraint on `OperatorBinding` ensures at most one result per (alias, tenant) pair, preventing cross-tenant mis-resolution.

#### OperatorRegistration (value object)

`RegisterAsync` accepts an `OperatorRegistration` value object that carries all fields required to create an `OperatorBinding`:

```csharp
public sealed record OperatorRegistration(
    long TelegramUserId,
    long TelegramChatId,
    ChatType ChatType,        // Private, Group, Supergroup — derived from Update.Message.Chat.Type
    string TenantId,
    string WorkspaceId,
    string[] Roles,            // e.g., ["Operator", "Approver"]
    string OperatorAlias       // e.g., "@alice"
);
```

The `/start` handler constructs an `OperatorRegistration` from the Telegram `Update` (for `TelegramUserId`, `TelegramChatId`, `ChatType`) and the `Telegram:UserTenantMappings` configuration entry (for `TenantId`, `WorkspaceId`, `Roles`, `OperatorAlias`) and passes it to `IOperatorRegistry.RegisterAsync`, which creates the `OperatorBinding` with all fields populated (see §7.1).

> **Cross-doc canonical status (IOperatorRegistry).** This document (§4.3) is the **canonical source** for the `IOperatorRegistry` interface contract. The contract has **six** methods: `GetBindingsAsync`, `GetAllBindingsAsync`, `GetByAliasAsync`, `GetByWorkspaceAsync`, `RegisterAsync`, and `IsAuthorizedAsync`. All signatures are specified above in the code block. Implementation-plan.md Stage 2.7 alert fallback routing (line 242) calls `IOperatorRegistry.GetByWorkspaceAsync(event.WorkspaceId)` to resolve the workspace-default operator when no `TaskOversight` record exists. Implementation-plan.md Stage 1.3 defines all six methods with matching signatures — including `GetByWorkspaceAsync(string workspaceId, CancellationToken)` returning `IReadOnlyList<OperatorBinding>` — and Stage 3.4 `PersistentOperatorRegistry` implements it by querying active `OperatorBinding` rows filtered by `WorkspaceId`.

### 4.4 IOutboundQueue (to be defined in planned `AgentSwarm.Messaging.Abstractions`)

```csharp
public interface IOutboundQueue
{
    Task EnqueueAsync(OutboundMessage message, CancellationToken ct);
    Task<OutboundMessage?> DequeueAsync(CancellationToken ct);
    Task MarkSentAsync(Guid messageId, long telegramMessageId, CancellationToken ct);
    Task MarkFailedAsync(Guid messageId, string error, CancellationToken ct);
    Task DeadLetterAsync(Guid messageId, CancellationToken ct);
}
```

### 4.5 IAuditLogger

```csharp
public interface IAuditLogger
{
    Task LogAsync(AuditEntry entry, CancellationToken ct);
}
```

`AuditEntry` is defined in the planned `AgentSwarm.Messaging.Abstractions` project (per implementation-plan.md Stage 1.3) with properties: `EntryId`, `MessageId`, `UserId`, `AgentId`, `Action`, `Timestamp`, `CorrelationId`, `Details` (see §3.1 for the full field table). The concrete persistence entity `AuditLogEntry` (implementation-plan.md Stage 5.3) maps from `AuditEntry` and adds `TenantId`, `Platform`, and renames `UserId` → `ExternalUserId` at the persistence boundary (see §3.1 for the full `AuditLogEntry` field table).

### 4.6 ISwarmCommandBus (shared abstraction — to be defined in planned `AgentSwarm.Messaging.Abstractions`)

```csharp
public interface ISwarmCommandBus
{
    Task PublishCommandAsync(SwarmCommand command, CancellationToken ct);
    Task<SwarmStatusSummary> QueryStatusAsync(SwarmStatusQuery query, CancellationToken ct);
    Task<IReadOnlyList<AgentInfo>> QueryAgentsAsync(SwarmAgentsQuery query, CancellationToken ct);
    IAsyncEnumerable<SwarmEvent> SubscribeAsync(string tenantId, CancellationToken ct);
}
```

**`SwarmStatusQuery`** carries the operator's `WorkspaceId` (required — scopes the query to the operator's workspace, derived from `OperatorBinding`) and an optional `TaskId` (when provided, narrows the result to a single task's state, assigned agent, and last activity — used by `/status TASK-ID`). **`SwarmAgentsQuery`** carries `WorkspaceId` (required) and an optional `Filter` string (free-text agent name/role filter — used by `/agents FILTER` per §5.7). Both query parameter objects are records defined alongside `SwarmCommand` in `AgentSwarm.Messaging.Abstractions`.

The Telegram connector publishes commands (task creation, approvals, pauses) via `PublishCommandAsync` and queries swarm state via `QueryStatusAsync` and `QueryAgentsAsync`. The `/status` handler constructs `SwarmStatusQuery { WorkspaceId, TaskId? }` from the operator's binding and optional command argument; the `/agents` handler constructs `SwarmAgentsQuery { WorkspaceId, Filter? }` similarly (see §5.7 command table). These four outbound methods are the canonical contract defined by this architecture document.

> **Canonical query signatures:** `QueryStatusAsync(SwarmStatusQuery, CancellationToken)` and `QueryAgentsAsync(SwarmAgentsQuery, CancellationToken)` are the definitive signatures defined by this architecture document. The query-parameter objects carry the workspace scope and optional filter/task-ID required by §5.7. The stub `StubSwarmCommandBus` (implementation-plan.md Stage 6.3) should adopt these signatures and return empty collections scoped to the query objects' workspace. Implementation-plan.md Stage 1.3 now defines these same parameterized signatures with `SwarmStatusQuery` and `SwarmAgentsQuery` records — cross-document alignment confirmed.

**Event ingress (agent → connector):** The connector must also receive inbound events from the swarm (agent questions, status updates, alerts) to render them in Telegram. Event ingress is handled via `SubscribeAsync` on the same `ISwarmCommandBus` interface, keeping a single swarm integration surface consistent with implementation-plan.md Stage 1.3, which defines `ISwarmCommandBus` as the sole port to the agent swarm orchestrator.

`SwarmEvent` is a discriminated union (or base class with subtypes) covering `AgentQuestionEvent`, `AgentAlertEvent`, and `AgentStatusUpdateEvent`. The Telegram connector's `BackgroundService` calls `SubscribeAsync` at startup for each active tenant and processes events as they arrive — rendering questions as inline-keyboard messages, alerts as priority text, and status updates as informational messages. The transport backing this subscription (in-process `Channel<T>`, message broker, gRPC stream) is outside this story's scope; the interface abstracts it.

**`AgentAlertEvent` fields:** `AlertId` (string, unique), `AgentId` (string), `TaskId` (string — the task the agent was executing when the alert fired; required for `TaskOversight` routing in §5.6), `Title` (string — short alert headline, e.g., "Build failure"), `Body` (string — detailed alert description), `Severity` (`MessageSeverity`), `WorkspaceId` (string), `TenantId` (string), `CorrelationId` (string), `Timestamp` (DateTimeOffset). The `TaskId` field is always populated because agents execute within a task context; the alert routing in §5.6 depends on this to resolve the oversight operator via `ITaskOversightRepository.GetByTaskIdAsync(taskId)`.

> **Cross-doc canonical status (AgentAlertEvent):** Implementation-plan.md Stage 1.3 defines `AgentAlertEvent` with the full ten-field set: `AlertId`, `AgentId`, `TaskId`, `Title`, `Body`, `Severity`, `WorkspaceId`, `TenantId`, `CorrelationId`, and `Timestamp` — matching this architecture document exactly. The field names `Title` and `Body` are aligned across both documents; earlier drafts used `AlertType`/`Summary` which have been fully retired. Implementation-plan.md Stage 2.7 alert routing uses `TaskOversight` lookup with fallback to workspace default operator via `IOperatorRegistry.GetByWorkspaceAsync` (line 242), consistent with §5.6. The `IOperatorRegistry` interface (§4.3) is the canonical contract — see §4.3 for the full six-method definition including `GetByWorkspaceAsync`. E2e-scenarios.md bounded-burst threshold is ≤ 50 Critical+High messages, consistent with §10.4. The receive counter is `telegram.messages.received` across all documents.

### 4.7 IPendingQuestionStore (to be defined in planned `AgentSwarm.Messaging.Abstractions`)

Manages the lifecycle of pending agent questions awaiting operator response.

```csharp
public interface IPendingQuestionStore
{
    Task StoreAsync(AgentQuestionEnvelope envelope, long telegramChatId, long telegramMessageId, CancellationToken ct);
    Task<PendingQuestion?> GetAsync(string questionId, CancellationToken ct);
    Task<PendingQuestion?> GetByTelegramMessageAsync(long telegramChatId, long telegramMessageId, CancellationToken ct);
    Task MarkAnsweredAsync(string questionId, CancellationToken ct);
    Task MarkAwaitingCommentAsync(string questionId, CancellationToken ct);
    Task RecordSelectionAsync(string questionId, string selectedActionId, string selectedActionValue, long respondentUserId, CancellationToken ct);
    Task<PendingQuestion?> GetAwaitingCommentAsync(long telegramChatId, long respondentUserId, CancellationToken ct);
    Task<IReadOnlyList<PendingQuestion>> GetExpiredAsync(CancellationToken ct);
}
```

`StoreAsync` accepts the full `AgentQuestionEnvelope` so the store can extract `AgentQuestion` fields and denormalize `ProposedDefaultActionId` into `PendingQuestion.DefaultActionId` and resolve the corresponding `HumanAction.Value` into `PendingQuestion.DefaultActionValue` (by finding the `HumanAction` in `AllowedActions` whose `ActionId` matches `ProposedDefaultActionId`). `GetAsync(questionId)` is the **primary callback lookup path**: when an operator taps an inline button, the `CallbackQueryHandler` parses `QuestionId` from the `callback_data` field (`QuestionId:ActionId` format per §5.2) and calls `GetAsync(questionId)` — this uses the `QuestionId` primary key and is immune to cross-chat `message_id` collisions. `GetByTelegramMessageAsync(chatId, messageId)` uses the composite `(TelegramChatId, TelegramMessageId)` pair (because Telegram `message_id` is only unique within a chat) and is used only by `QuestionRecoverySweep` for backfill correlation — never for callback resolution. `MarkAwaitingCommentAsync(questionId)` sets `Status = AwaitingComment`; the `CallbackQueryHandler` calls this when the tapped button has `RequiresComment = true`. `RecordSelectionAsync(questionId, selectedActionId, selectedActionValue, respondentUserId)` persists the operator's selected action ID, the resolved `HumanAction.Value` (as `SelectedActionValue`), and Telegram user ID on the pending question record — called by `CallbackQueryHandler` **before** `MarkAwaitingCommentAsync` to record which action was tapped, its canonical value, and by whom, enabling the text-reply handler to emit the `HumanDecisionEvent` with the correct `ActionValue` from durable storage without depending on `IDistributedCache`. This method is part of the abstraction interface so that `CallbackQueryHandler` (in `AgentSwarm.Messaging.Telegram`) can record selections without depending on the `AgentSwarm.Messaging.Persistence` assembly — the concrete `PersistentPendingQuestionStore` implements it via EF Core. `GetAwaitingCommentAsync(telegramChatId, respondentUserId)` returns the **oldest** pending question (by `StoredAt`) with `Status = AwaitingComment` for the given `(TelegramChatId, RespondentUserId)` pair — used by the text-reply handler to match a follow-up comment to the correct pending question. When multiple `AwaitingComment` questions exist for the same operator in the same chat (an unlikely but possible race), deterministic oldest-first tie-breaking ensures predictable resolution; the handler warns the operator about remaining pending questions (see §3.1 `PendingQuestionRecord` constraints for the index definition). `GetExpiredAsync` returns all questions with `Status` in `Pending` or `AwaitingComment` that are past their `ExpiresAt`, used by `QuestionTimeoutService` (§10.3) — including `AwaitingComment` ensures that questions where the operator tapped a `RequiresComment` button but never sent the follow-up text reply are also timed out rather than left permanently stuck. All query methods return the `PendingQuestion` abstraction DTO; the concrete `PersistentPendingQuestionStore` (implementation-plan Stage 3.5) maps between the persistence entity `PendingQuestionRecord` and the DTO.

> **Cross-doc alignment (IPendingQuestionStore).** This document (§4.7) is the **canonical source** for the `IPendingQuestionStore` interface contract. The canonical interface includes eight methods: `StoreAsync`, `GetAsync`, `GetByTelegramMessageAsync`, `MarkAnsweredAsync`, `MarkAwaitingCommentAsync`, `RecordSelectionAsync`, `GetAwaitingCommentAsync`, and `GetExpiredAsync`. **RESOLVED (iteration 13):** `RecordSelectionAsync` now accepts four parameters (`questionId`, `selectedActionId`, `selectedActionValue`, `respondentUserId`) to persist the resolved `HumanAction.Value` at button-tap time; implementation-plan.md Stage 1.3 now adopts this updated signature. `StoreAsync` now denormalizes both `DefaultActionId` and `DefaultActionValue`; implementation-plan.md Stage 3.5 now reflects this. `CallbackQueryHandler` and the text-reply handler operate through `IPendingQuestionStore` without a compile-time dependency on the Persistence assembly. The canonical method name `GetByTelegramMessageAsync(long telegramChatId, long telegramMessageId, CancellationToken)` accepts **two** identifiers because Telegram `message_id` is only unique within a chat. Implementation-plan.md Stage 1.3 uses this exact composite-key signature and method name (`GetByTelegramMessageAsync`, not `GetByTelegramMessageIdAsync`).

### 4.8 IInboundUpdateStore (to be defined in planned `AgentSwarm.Messaging.Abstractions`)

Provides persistence and idempotency tracking for inbound Telegram webhook updates.

```csharp
public interface IInboundUpdateStore
{
    Task<bool> PersistAsync(InboundUpdate update, CancellationToken ct);
    Task MarkProcessingAsync(long updateId, CancellationToken ct);
    Task MarkCompletedAsync(long updateId, CancellationToken ct);
    Task MarkFailedAsync(long updateId, string errorDetail, CancellationToken ct);
    Task<IReadOnlyList<InboundUpdate>> GetRecoverableAsync(int maxRetries, CancellationToken ct);
    Task<int> GetExhaustedRetryCountAsync(int maxRetries, CancellationToken ct);
}
```

`PersistAsync` returns `false` if the `update_id` already exists (the `UNIQUE` constraint on `UpdateId` is the canonical deduplication mechanism for webhook delivery — see §5.4). `GetRecoverableAsync` returns updates with `IdempotencyStatus` in `Received`, `Processing`, or `Failed` where `AttemptCount < maxRetries` — all three statuses represent records that need reprocessing: `Received`/`Processing` from crash recovery, `Failed` from transient handler errors (aligned with implementation-plan.md Stage 2.4). `GetExhaustedRetryCountAsync` returns the count of `Failed` records with `AttemptCount >= maxRetries`, used for health-check alerting. Status transitions follow the `InboundUpdate.IdempotencyStatus` enum: `Received → Processing → Completed|Failed`.

### 4.9 IDeduplicationService (to be defined in planned `AgentSwarm.Messaging.Abstractions`)

Provides fast in-pipeline deduplication as a supplementary layer above `IInboundUpdateStore`.

```csharp
public interface IDeduplicationService
{
    Task<bool> IsProcessedAsync(string eventId, CancellationToken ct);
    Task MarkProcessedAsync(string eventId, CancellationToken ct);
}
```

Backed by a sliding-window cache (e.g., `IDistributedCache`) with a configurable TTL (default 1 hour, per implementation-plan Stage 4.3). This provides fast in-pipeline deduplication for the `TelegramUpdatePipeline` processing path without a database query. The `IInboundUpdateStore` (§4.8) remains the canonical deduplication mechanism; this service is an acceleration layer. Both layers must agree an event is new before it is processed.

### 4.10 ITaskOversightRepository (to be defined in planned `AgentSwarm.Messaging.Core`)

Manages task-to-operator oversight assignments for the `/handoff` flow.

```csharp
public interface ITaskOversightRepository
{
    Task<TaskOversight?> GetByTaskIdAsync(string taskId, CancellationToken ct);
    Task UpsertAsync(TaskOversight oversight, CancellationToken ct);
    Task<IReadOnlyList<TaskOversight>> GetByOperatorAsync(Guid operatorBindingId, CancellationToken ct);
}
```

`GetByTaskIdAsync` returns the current oversight assignment for routing agent questions and alerts to the responsible operator. `UpsertAsync` creates or updates an oversight record binding a task to an operator — used for both initial assignment and `/handoff` reassignment (upsert semantics replace `AssignAsync`/`ReassignAsync` with a single idempotent operation). `GetByOperatorAsync` returns all tasks overseen by a given operator (used by `/status` to show operator-scoped task lists). This interface aligns with implementation-plan.md Stage 1.3 which defines the same three methods. The concrete `PersistentTaskOversightRepository` (implementation-plan Stage 3.2) stores `TaskOversight` entities via EF Core.

### 4.11 IUserAuthorizationService (to be defined in planned `AgentSwarm.Messaging.Abstractions`)

Performs two-tier authorization for inbound Telegram commands. Referenced by `TelegramUpdateRouter` (§2.2) and `ITelegramUpdatePipeline.ProcessAsync` (§5.1). The interface contract is defined here; the concrete `TelegramUserAuthorizationService` implementation is specified in implementation-plan.md Stage 4.3.

```csharp
public interface IUserAuthorizationService
{
    Task<AuthorizationResult> AuthorizeAsync(
        string externalUserId,
        string chatId,
        string? commandName,
        CancellationToken ct);
}
```

**`AuthorizationResult`** contains: `IsAuthorized` (bool), `Bindings` (`IReadOnlyList<OperatorBinding>` — the set of active bindings for the user/chat pair; empty when unauthorized), and `DenialReason` (string? — human-readable reason for rejection).

**Two-tier dispatch via `commandName`:** When `commandName == "start"`, the service performs **Tier 1 (onboarding)** authorization — checks the static `Telegram:AllowedUserIds` allowlist (§7) and, if the user is allowed, creates an `OperatorBinding` via `IOperatorRegistry.RegisterAsync`. For all other `commandName` values (including `null`), the service performs **Tier 2 (runtime)** authorization — calls `IOperatorRegistry.GetBindingsAsync(userId, chatId)` to retrieve active bindings and populates `AuthorizationResult.Bindings`. The pipeline then handles cardinality: zero bindings → unauthorized rejection; one binding → construct `AuthorizedOperator` directly; multiple bindings → present workspace disambiguation via inline keyboard (per e2e-scenarios.md multi-workspace flow).

> **Cross-doc alignment (IUserAuthorizationService):** Implementation-plan.md Stage 1.3 defines `IUserAuthorizationService` with the same `AuthorizeAsync(string externalUserId, string chatId, string? commandName, CancellationToken)` signature. Stage 4.3 specifies the concrete `TelegramUserAuthorizationService` implementation. This architecture document provides the contract; the implementation plan provides the implementation details.

### 4.12 IMessageSender (to be defined in planned `AgentSwarm.Messaging.Core`)

Platform-agnostic outbound sending contract used by `OutboundQueueProcessor` (§10.4) to send messages without depending on the `AgentSwarm.Messaging.Telegram` assembly directly.

```csharp
public interface IMessageSender
{
    Task<SendResult> SendTextAsync(long chatId, string text, CancellationToken ct);
    Task<SendResult> SendQuestionAsync(long chatId, AgentQuestionEnvelope envelope, CancellationToken ct);
}

/// <summary>
/// Return value from IMessageSender methods, carrying the Telegram-assigned message ID
/// so the caller (OutboundQueueProcessor) can pass it to MarkSentAsync and StoreAsync.
/// </summary>
public sealed record SendResult(long TelegramMessageId);
```

Both methods return `SendResult` containing the `TelegramMessageId` assigned by the Telegram Bot API. The `OutboundQueueProcessor` (§5.2, §10.4) uses `SendResult.TelegramMessageId` for two post-send operations: (1) `IOutboundQueue.MarkSentAsync(messageId, telegramMessageId)` to persist the Telegram message ID on the `OutboundMessage` record, and (2) `IPendingQuestionStore.StoreAsync(envelope, chatId, telegramMessageId)` for question messages to create the `PendingQuestionRecord` with the correct `TelegramMessageId`. `SendResult` is defined in `AgentSwarm.Messaging.Core` alongside `IMessageSender`.

The concrete `TelegramMessageSender` (in `AgentSwarm.Messaging.Telegram`) implements this interface and wraps `ITelegramBotClient` from the `Telegram.Bot` library. It is the **sole owner of Telegram-specific rendering** — it formats messages with MarkdownV2, builds `InlineKeyboardMarkup` for agent questions, writes `HumanAction` entries to `IDistributedCache`, and enforces Telegram rate limits via the dual-layer token-bucket limiter (§10.4). Rendering happens at send time, not at enqueue time — this is the canonical rendering boundary (see §3.1 `OutboundMessage.Payload` for how this interacts with the outbox payload). In component diagrams and sequence flows throughout this document, **`TelegramSender`** refers to this concrete `TelegramMessageSender` implementation.

> **Cross-doc alignment (IMessageSender).** This document (§4.12) is the **canonical source** for the `IMessageSender` interface contract. Both methods return `Task<SendResult>` (not bare `Task`), where `SendResult` carries the `TelegramMessageId` assigned by the Bot API. **RESOLVED (iteration 13):** implementation-plan.md Stage 1.4 now specifies `Task<SendResult>` return types for both methods, and Stage 4.1 now specifies that `OutboundQueueProcessor` sends via `IMessageSender` (not the concrete `TelegramMessageSender`), so Core/Worker assemblies do not depend on `AgentSwarm.Messaging.Telegram`. The component table in §2.2 lists `TelegramSender` as the short name for the concrete implementation — this is the same component as implementation-plan.md's `TelegramMessageSender`.

---

## 5. End-to-End Sequence Flows

### 5.1 Scenario: Human sends `/ask build release notes for Solution12`

```text
Human (Telegram)                Webhook Endpoint       UpdatePipeline        CommandDispatcher       AuthZ       SwarmCommandBus       Orchestrator
      │                              │                      │                      │                   │               │                    │
      │──POST /api/telegram/webhook─▶│                      │                      │                   │               │                    │
      │                              │──validate secret─────│                      │                   │               │                    │
      │                              │──persist InboundUpdate (update_id)──────────▶│                   │               │                    │
      │                              │   (UNIQUE constraint; INSERT fails ──▶ duplicate ──▶ return 200)│               │                    │
      │◀─────200 OK─────────────────│                      │                      │                   │               │                    │
      │                              │──fire-and-forget─────▶│  (async boundary)   │                   │               │                    │
      │                              │                      │──dedup check────────▶│                   │               │                    │
      │                              │                      │──IsAuthorized?──────▶│──check binding───▶│               │                    │
      │                              │                      │                      │◀──yes + binding──│               │                    │
      │                              │                      │──parse "/ask ..."───▶│                   │               │                    │
      │                              │                      │                      │──CreateTaskCmd───▶│──publish──────▶│                    │
      │                              │                      │                      │                   │               │──create work item─▶│
      │                              │                      │                      │                   │               │                    │
      │                              │                      │                      │◀──ack + taskId───│◀──────────────│                    │
      │                              │                      │                      │                   │               │                    │
      │◀──"Task created: #T-42"─────│◀──────enqueue reply──│◀──────────────────────│                   │               │                    │
```

**Key invariants:**
1. Webhook returns `200 OK` **after** secret-token validation, `InboundUpdate` persistence, and deduplication — but **before** authorization or command processing. The endpoint performs two synchronous steps before responding: (a) validates the `X-Telegram-Bot-Api-Secret-Token` header, (b) persists the `InboundUpdate` record — including the full `RawPayload` (serialized Telegram `Update` JSON), `AttemptCount = 0`, and `ErrorDetail = null` — with the `update_id` as primary key; if the `UNIQUE` constraint fails, the update is a duplicate and the endpoint returns `200 OK` immediately without further processing. Only after these durable steps does the endpoint return `200 OK`. Authorization, command parsing, and routing then proceed asynchronously inside `ITelegramUpdatePipeline.ProcessAsync` (implementation-plan.md Stage 2.2), which performs deduplication, Tier 1/Tier 2 authorization via `IUserAuthorizationService`, role enforcement, and command dispatch. Unauthorized commands receive a rejection reply enqueued to the outbound queue — they are not rejected at the HTTP level, because Telegram does not distinguish HTTP 200 from HTTP 403 for webhook retries. This eliminates the command-loss window: if the process crashes after Telegram receives `200`, the `InboundUpdate` record (with full `RawPayload`) is already persisted. On restart, `InboundRecoverySweep` queries for records with `IdempotencyStatus` in `Received`, `Processing`, or `Failed` where `AttemptCount < MaxRetries` (configurable, default 3), deserializes their `RawPayload`, and re-feeds them into the command pipeline for idempotent re-processing. `Received`/`Processing` records represent crash recovery; `Failed` records represent transient handler errors eligible for automatic retry. On each failed reprocessing attempt, `AttemptCount` is incremented and `ErrorDetail` is updated with the failure reason. Records with `AttemptCount ≥ MaxRetries` remain in `Failed` status (consistent with the four-status model) and are excluded from future recovery sweeps; an `inbound_update_exhausted_retries` metric is emitted for alerting.
2. `update_id` is persisted **before** `200 OK`; duplicate POSTs are dropped at the database constraint level.
3. Authorization runs inside `ITelegramUpdatePipeline.ProcessAsync` (after `200 OK` is returned), not at the HTTP endpoint level. Unauthorized commands receive a rejection reply enqueued to the outbound queue. This is consistent with implementation-plan.md Stage 2.2 which places `IUserAuthorizationService` inside the pipeline, and Stage 2.4 which returns HTTP 200 immediately after persisting `InboundUpdate`.
4. The reply ("Task created") is enqueued to `OutboundMessageQueue`, not sent inline, preserving the durable-delivery guarantee.
5. `AuditLogger` records the `/ask` command with correlation ID.

### 5.2 Scenario: Agent asks a blocking question, operator answers via button

```text
Orchestrator        SwarmCommandBus       TelegramConnector    OutboundQueue     TelegramSender      Human (Telegram)
      │                    │                     │                   │                  │                   │
      │──AgentQuestion────▶│                     │                   │                  │                   │
      │  (severity=High,   │──deliver event─────▶│                   │                  │                   │
      │   timeout=30min)   │                     │──build message────▶                  │                   │
      │                    │                     │  + InlineKeyboard │                  │                   │
      │                    │                     │──enqueue──────────▶│                  │                   │
      │                    │                     │                   │──dequeue────────▶│                   │
      │                    │                     │                   │                  │──sendMessage──────▶│
      │                    │                     │                   │                  │  [Approve][Reject] │
      │                    │                     │                   │◀─markSent────────│                   │
      │                    │                     │                   │──persist PendingQ│                   │
      │                    │                     │                   │  (OutboundQueue- │                   │
      │                    │                     │                   │   Processor hook: │                   │
      │                    │                     │                   │   StoreAsync with │                   │
      │                    │                     │                   │   SourceEnvelope) │                   │
      │                    │                     │                   │                  │                   │
      │                    │                     │                   │                  │  (operator taps    │
      │                    │                     │                   │                  │   "Approve")       │
      │                    │                     │                   │                  │◀──CallbackQuery───│
      │                    │                     │◀──route callback──│                  │                   │
      │                    │                     │──parse action─────▶                  │                   │
      │                    │                     │──HumanDecisionEvent                  │                   │
      │                    │◀──publish decision──│                   │                  │                   │
      │◀──deliver decision─│                     │                   │                  │                   │
      │                    │                     │──audit record──────────────────────────────────────────▶ │
      │                    │                     │──answerCallback───▶                  │──ack to TG───────▶│
```

**Key invariants:**
1. The question includes `Severity`, `ExpiresAt`, `AllowedActions` rendered as inline keyboard buttons, and the proposed default action (if any). The default action is carried as sidecar metadata in `AgentQuestionEnvelope.ProposedDefaultActionId` (see §3.1). **Rendering boundary:** The `OutboundQueueProcessor` dispatches to `IMessageSender` based on `SourceType`: for `Question` messages, the processor deserializes `OutboundMessage.SourceEnvelopeJson` to recover the `AgentQuestionEnvelope` and calls `IMessageSender.SendQuestionAsync(chatId, envelope)` — `TelegramMessageSender` is the **sole owner** of question rendering (extracting `AgentQuestion` fields, building the `InlineKeyboardMarkup`, writing `HumanAction` entries to `IDistributedCache`, and displaying the proposed default in the message body). For all other `SourceType` values (`Alert`, `StatusUpdate`, `CommandAck`), the processor calls `IMessageSender.SendTextAsync(chatId, payload)` using the pre-rendered `OutboundMessage.Payload` (rendered by `TelegramMessengerConnector` at enqueue time — see §3.1 `Payload` field). The sender does **not** create or write to `PendingQuestionRecord`. **Post-send persistence and denormalization:** After `IMessageSender.SendQuestionAsync` returns `SendResult` (containing `TelegramMessageId`), the `OutboundQueueProcessor` calls `IOutboundQueue.MarkSentAsync(messageId, sendResult.TelegramMessageId)` and then `IPendingQuestionStore.StoreAsync(envelope, chatId, sendResult.TelegramMessageId)`. The store implementation denormalizes `ProposedDefaultActionId` into both `PendingQuestionRecord.DefaultActionId` (for display/audit) and `PendingQuestionRecord.DefaultActionValue` (by resolving the `HumanAction.Value` from `AllowedActions` — for timeout event emission). The record is created with `Status = Pending`. For non-question messages, the processor calls `MarkSentAsync` only (no `PendingQuestionRecord`).
>
> **Cross-doc alignment (implementation-plan.md Stage 2.3).** Implementation-plan.md Stage 1.4 and Stage 2.3 state that the sender reads `ProposedDefaultActionId` to *display* the proposed default in the message body only — it does not denormalize or persist `PendingQuestionRecord.DefaultActionId`/`DefaultActionValue`. Denormalization is owned by `OutboundQueueProcessor` via `IPendingQuestionStore.StoreAsync` (this §5.2 invariant 1). Both methods on `IMessageSender` return `Task<SendResult>` so the processor can obtain the `TelegramMessageId` (see §4.12). **RESOLVED (iteration 13):** implementation-plan.md Stage 1.4 now specifies `Task<SendResult>` return types; Stage 4.1 now sends via `IMessageSender` (not the concrete `TelegramMessageSender`). See Appendix A items 11 and 14.

   This is the **concrete component and hook point** responsible for step 4 of the crash-window analysis (§3.1). Two crash windows exist between the Telegram API call and record persistence — see §3.1 `PendingQuestionRecord` for the full two-gap analysis and mitigations (Gap A: crash before `MarkSentAsync` may cause a duplicate Telegram message, accepted as at-least-once trade-off; Gap B: crash before `PendingQuestionRecord` persistence is backfilled by `QuestionRecoverySweep`).
2. The `callback_data` field carries the format `QuestionId:ActionId` (aligned with tech-spec D-3 and implementation-plan Stages 2.3/3.3). Both `QuestionId` and `ActionId` are constrained to a maximum of 30 characters each (see §3.1 `AgentQuestion` constraints and `HumanAction.ActionId` definition), ensuring the combined `callback_data` (including the `:` separator) fits within Telegram's 64-byte limit (max 61 bytes). The server stores the full `HumanAction` payload in `IDistributedCache` keyed by `QuestionId:ActionId`, written when the inline keyboard is built and expiring at `AgentQuestion.ExpiresAt + 5 minutes` (the 5-minute grace window ensures `CallbackQueryHandler` can still resolve the cached `HumanAction` for late button taps near the `ExpiresAt` boundary — aligned with implementation-plan.md Stage 2.3 and tech-spec D-3/R-3; `QuestionTimeoutService` does not depend on the cache — see §10.3). The `QuestionTimeoutService` does not depend on the cache for timeout resolution — it reads `PendingQuestionRecord.DefaultActionValue` (the `HumanAction.Value` resolved and denormalized at send time) and publishes it as `HumanDecisionEvent.ActionValue` — consistent with the interactive button-tap path where `HumanAction.Value` is always the canonical `ActionValue` (see §3.1 `HumanAction.Value`, §10.3). On callback, the handler parses `QuestionId:ActionId` from the callback data, looks up the full `HumanAction` from cache (with fallback to `PendingQuestionRecord.AgentQuestion` on cache miss — see invariant 3 below), and reads `HumanAction.Value` to populate `HumanDecisionEvent.ActionValue` (consistent with e2e-scenarios.md line 141: "ActionValue carries the HumanAction.Value from AllowedActions").
3. Button press produces a strongly typed `HumanDecisionEvent` — never a raw string. The `CallbackQueryHandler` resolves the full `HumanAction` via a **two-tier lookup**: first from `IDistributedCache` (keyed by `QuestionId:ActionId`); on cache miss (e.g., cache eviction under memory pressure), the handler falls back to deserializing `PendingQuestionRecord.AgentQuestion` JSON and finding the `HumanAction` in `AllowedActions` whose `ActionId` matches. This fallback ensures the handler never fails due to volatile cache state — the durable `AgentQuestion` JSON in `PendingQuestionRecord` is always available. **However**, when the selected `HumanAction.RequiresComment` is `true` (e.g., the "Need info" action in e2e-scenarios.md), the `HumanDecisionEvent` is **deferred**: the `CallbackQueryHandler` resolves `HumanAction.Value` (via cache or durable fallback), then calls `IPendingQuestionStore.RecordSelectionAsync(questionId, actionId, actionValue, respondentUserId)` to persist the selected action ID, the resolved `HumanAction.Value` (as `SelectedActionValue`), and the tapping operator's Telegram user ID, then calls `IPendingQuestionStore.MarkAwaitingCommentAsync(questionId)` to set `PendingQuestionRecord.Status = AwaitingComment`. Both calls go through the abstraction interface defined in §4.7, so `CallbackQueryHandler` (in `AgentSwarm.Messaging.Telegram`) does not depend on the `AgentSwarm.Messaging.Persistence` assembly. The handler sends a prompt to the operator ("Please reply with your comment") and returns without emitting the event. When the operator's text reply arrives via the inbound pipeline's text-reply handler, it is matched to the `AwaitingComment` record by calling `IPendingQuestionStore.GetAwaitingCommentAsync(telegramChatId, respondentUserId)`, which returns the oldest `AwaitingComment` question (by `StoredAt`) for the given `(TelegramChatId, RespondentUserId)` pair. If the same operator has multiple `AwaitingComment` questions in the same chat (an unlikely but possible race when the operator taps buttons on two questions before replying), the deterministic oldest-first tie-breaking ensures predictable resolution; the handler warns the operator about remaining pending questions (see §3.1 `PendingQuestionRecord` constraints for the index definition). The `HumanDecisionEvent` is then published with `ActionValue` read from `PendingQuestionRecord.SelectedActionValue` (persisted at button-tap time, immune to cache expiry) and the `Comment` from the operator's text reply, and the record transitions to `Answered`. This two-step flow is tested by e2e-scenarios.md "RequiresComment defers decision" and implementation-plan.md Stage 3.5 "RequiresComment flow."
4. The `answerCallbackQuery` call removes the loading spinner on the operator's device.
5. Audit record is written with `MessageId`, `UserId`, `AgentId`, timestamp, and `CorrelationId`.
6. If no operator responds before `ExpiresAt`, a timeout handler reads `PendingQuestionRecord.DefaultActionValue` (the `HumanAction.Value` of the default action, resolved and denormalized at send time — see §3.1 `PendingQuestionRecord.DefaultActionValue`). **Two branches:** (a) when `DefaultActionValue` is non-null, the handler fires a `HumanDecisionEvent` with `DefaultActionValue` as the `ActionValue` — consistent with the interactive button-tap path where `HumanAction.Value` is always the canonical `ActionValue` — and updates the Telegram message to "⏰ Timed out — default action applied: {action}"; (b) when `DefaultActionValue` is null, the handler fires a `HumanDecisionEvent` with `ActionValue = "__timeout__"` (no automatic action is taken on behalf of the operator), and updates the Telegram message to "⏰ Timed out — no default action". In both branches, `PendingQuestionRecord.Status` transitions to `TimedOut` and an audit record is written (see §10.3 for the full timeout flow).

### 5.3 Scenario: Outbound send failure with retry and dead-letter

```text
OutboundQueue         TelegramSender         Telegram API         DeadLetterQueue       AlertChannel
      │                     │                     │                      │                    │
      │──dequeue msg────────▶                     │                      │                    │
      │                     │──sendMessage────────▶│                      │                    │
      │                     │◀──429 Too Many Req──│                      │                    │
      │◀──markFailed(1)─────│                     │                      │                    │
      │   nextRetry=now+2s  │                     │                      │                    │
      │                     │                     │                      │                    │
      │  ... retry after 2s ...                   │                      │                    │
      │──dequeue msg────────▶                     │                      │                    │
      │                     │──sendMessage────────▶│                      │                    │
      │                     │◀──500 Server Error──│                      │                    │
      │◀──markFailed(2)─────│                     │                      │                    │
      │   nextRetry=now+4s  │                     │                      │                    │
      │                     │                     │                      │                    │
      │  ... (attempts 3 and 4 fail similarly) ...│                      │                    │
      │                     │                     │                      │                    │
      │──dequeue msg────────▶                     │                      │                    │
      │                     │──sendMessage────────▶│                      │                    │
      │                     │◀──500 Server Error──│                      │                    │
      │◀──markFailed(5)─────│                     │                      │                    │
      │──deadLetter─────────▶─────────────────────▶──────────────────────▶│                    │
      │                     │                     │                      │──alert operator────▶│
```

**Retry policy (configurable via `OutboundQueue:MaxRetries` and `OutboundQueue:BaseRetryDelaySeconds`):**
- Max attempts: configurable (default `5`, aligned with implementation-plan Stage 4.2 `RetryPolicy.MaxAttempts` default of `5`)
- Back-off: exponential (`BaseRetryDelaySeconds` ^ attempt, e.g. 2s, 4s, 8s capped) with jitter (±25%)
- Retryable errors: HTTP 429 (with `retry_after`), 5xx, network timeouts
- Non-retryable: HTTP 400 (bad request), 403 (bot blocked) — dead-letter immediately
- Dead-letter record preserves full message payload, all attempt timestamps, and error details (see `DeadLetterMessage` entity in §3.1 for the complete field model including `AttemptTimestamps`, `ErrorHistory`, `AlertStatus`, and `ReplayStatus`)
- Alert is sent to a secondary notification channel (ops Telegram group or fallback messenger)

> **Cross-doc retry default alignment:** This architecture, implementation-plan.md (Stage 4.2: `RetryPolicy.MaxAttempts` default `5`), and e2e-scenarios.md (Background: "max 5 attempts", dead-letter scenario: "dead-letter after attempt 5") are **aligned** on `MaxRetries = 5`.

### 5.4 Scenario: Duplicate webhook delivery (idempotency)

```text
Telegram Cloud          Webhook Endpoint        DB (InboundUpdate)        UpdatePipeline (async)
      │                       │                      │                          │
      │──POST update_id=999──▶│                      │                          │
      │                       │──validate secret─────│                          │
      │                       │──INSERT update 999──▶│                          │
      │                       │◀──OK (new)──────────│                          │
      │◀──200 OK─────────────│                      │                          │
      │                       │──fire-and-forget─────▶──────────────────────────▶│
      │                       │                      │    dedup + allowlist ─────▶
      │                       │                      │    parse + dispatch ─────▶
      │                       │                      │                          │
      │  (Telegram retries — network glitch)         │                          │
      │──POST update_id=999──▶│                      │                          │
      │                       │──validate secret─────│                          │
      │                       │──INSERT update 999──▶│                          │
      │                       │◀──CONFLICT (dup)────│                          │
      │◀──200 OK─────────────│──DROP (no-op)        │                          │
```

**Key invariants:**
1. Endpoint always returns `200 OK` regardless of duplicate status — prevents Telegram from retrying further. The webhook endpoint performs **only** secret-token validation and `InboundUpdate` persistence before responding; allowlist/authorization validation happens asynchronously inside `ITelegramUpdatePipeline.ProcessAsync` (consistent with §2.2, §5.1, and implementation-plan.md Stage 2.2/2.4).
2. Deduplication uses `update_id` as a natural idempotency key with a `UNIQUE` constraint. The `INSERT` happens **before** `200 OK`, so a crash after Telegram receives `200` does not lose the record.
3. Webhook deduplication operates in two layers, each with a distinct scope and TTL: **(a) `InboundUpdate` table (persistence-layer, canonical):** The `UNIQUE` constraint on `update_id` provides permanent deduplication for webhook POSTs — no TTL, records are retained for at least 24 hours before pruning, and duplicate `INSERT` attempts are rejected at the database level regardless of age. This is the primary and canonical deduplication mechanism for webhook delivery. **(b) `IDeduplicationService` (pipeline-layer, supplementary):** A sliding-window cache of processed `EventId` values with a configurable TTL (default 1 hour, per implementation-plan Stage 4.3). This provides fast in-pipeline deduplication for the `TelegramUpdatePipeline` processing path — it catches re-deliveries during the same processing session without a database query. The `InboundUpdate` table TTL (24 hours) is intentionally longer than the `IDeduplicationService` TTL (1 hour) to cover edge cases where the pipeline-layer cache has expired but the same `update_id` is re-delivered (e.g., after a long outage recovery). Both layers must agree that an event is new before it is processed; either layer rejecting is sufficient to prevent duplicate execution.

### 5.5 Scenario: `/approve` and `/reject` via command text (non-button path)

```text
Human (Telegram)        UpdateRouter          CommandDispatcher       AuthZ          SwarmCommandBus
      │                      │                      │                   │                  │
      │──"/approve Q-17"────▶│                      │                   │                  │
      │                      │──dedup + allowlist──▶│──parse command───▶│                  │
      │                      │                      │──lookup Q-17──────▶                  │
      │                      │                      │  (validate question│                  │
      │                      │                      │   exists & is open)│                  │
      │                      │                      │──IsAuthorized?────▶│                  │
      │                      │                      │◀─yes (Approver)───│                  │
      │                      │                      │──HumanDecisionEvent                  │
      │                      │                      │  (action="approve")│                  │
      │                      │                      │──publish──────────▶│──publish────────▶│
      │◀──"Approved Q-17"───│◀──enqueue reply──────│                   │                  │
```

Commands `/approve` and `/reject` accept a question ID argument and produce the same `HumanDecisionEvent` as inline buttons.

> **`/handoff` semantics — Full oversight transfer (Decided).**
>
> `/handoff TASK-ID @operator-alias` performs a full oversight transfer. The handler:
> 1. Validates syntax (two arguments: task ID and operator alias). If invalid, returns usage help: "Usage: `/handoff TASK-ID @operator-alias`".
> 2. Validates that the specified task exists and the sending operator currently has oversight.
> 3. Resolves the target operator (`@operator-alias`) via `IOperatorRegistry.GetByAliasAsync(alias, tenantId)` where `tenantId` is the sending operator's tenant. The `UNIQUE (OperatorAlias, TenantId)` index ensures the lookup cannot resolve an operator in a different tenant. If the alias is not registered in the sending operator's tenant, returns an error.
> 4. Transfers oversight by creating or updating a `TaskOversight` record (see below) mapping the task to the target operator.
> 5. Notifies both operators — the sender receives confirmation, the target receives a handoff notification with task context.
> 6. Persists an audit record with handoff details (task ID, source operator, target operator, timestamp, `CorrelationId`).
> 7. Returns error for invalid task ID, unregistered target operator, or missing arguments with usage help.
>
> **`TaskOversight` entity:** Defined in §3.1 above. A lightweight entity mapping `(TaskId, OperatorBindingId)` to track which operator currently has oversight of which task. The `/handoff` handler creates or updates this record, and the orchestrator subscription filter reads it to route agent events to the correct operator.
>
> **Cross-doc alignment note:** All three sibling documents (implementation-plan.md, e2e-scenarios.md, tech-spec.md) are now aligned on full oversight transfer as the decided `/handoff` behavior. Implementation-plan.md Stage 3.2 specifies `HandoffCommandHandler` with full oversight transfer including validation, target resolution via `IOperatorRegistry.GetByAliasAsync(alias, tenantId)`, `TaskOversight` record mutation, dual-operator notification, and audit. E2e-scenarios.md tests the full transfer flow. Tech-spec D-4 documents the decision. The `UNIQUE (OperatorAlias, TenantId)` constraint on `OperatorBinding` ensures alias resolution is tenant-scoped, preventing cross-tenant mis-resolution during `/handoff`.

### 5.6 Scenario: Urgent agent alert delivery to operator

```text
AgentSwarm           ISwarmCommandBus      TelegramConnector    OutboundQueue     TelegramSender      Human (Telegram)
      │                    │                     │                   │                  │                   │
      │──AgentAlertEvent───▶│                     │                   │                  │                   │
      │  (agentId=build-7,  │                     │                   │                  │                   │
      │   taskId=TASK-42,   │──deliver event─────▶│                   │                  │                   │
      │   severity=Critical,│                     │                   │                  │                   │
      │   title="Build      │                     │                   │                  │                   │
      │    failure",         │                     │                   │                  │                   │
      │   correlationId=C1) │                     │                   │                  │                   │
      │                    │                     │──resolve tenant────▶                  │                   │
      │                    │                     │  (event.TaskId     │                  │                   │
      │                    │                     │   →TaskOversight   │                  │                   │
      │                    │                     │   →operatorChatId) │                  │                   │
      │                    │                     │                   │                  │                   │
      │                    │                     │──build alert msg──▶│                  │                   │
      │                    │                     │  severity=Critical │                  │                   │
      │                    │                     │  idempotencyKey=   │                  │                   │
      │                    │                     │  alert:{agentId}:  │                  │                   │
      │                    │                     │  {alertId}         │                  │                   │
      │                    │                     │                   │──priority dequeue▶│                   │
      │                    │                     │                   │  (Critical first) │                   │
      │                    │                     │                   │                  │──sendMessage──────▶│
      │                    │                     │                   │                  │  🚨 Critical Alert │
      │                    │                     │                   │                  │  Agent: build-7    │
      │                    │                     │                   │◀─markSent────────│  Error: build fail │
      │                    │                     │                   │                  │                   │
      │                    │                     │──audit record──────────────────────────────────────────▶ │
      │                    │                     │  (agentId, alertId,│                  │                   │
      │                    │                     │   correlationId=C1)│                  │                   │
```

**Key invariants:**

1. **Event ingress:** The `TelegramConnector` receives `AgentAlertEvent` via `ISwarmCommandBus.SubscribeAsync(tenantId)` — the same event subscription used for `AgentQuestionEvent` and `AgentStatusUpdateEvent` (see §4.6). The `SwarmEvent` discriminated union routes the event to the alert-handling path.
2. **Tenant/workspace routing:** `AgentAlertEvent` includes a `TaskId` field (the task the agent was executing when the alert fired — agents always operate in the context of a task). The connector resolves the target operator chat by looking up `AgentAlertEvent.TaskId` → `ITaskOversightRepository.GetByTaskIdAsync(taskId)` (§4.10) → `TaskOversight.OperatorBindingId` → `OperatorBinding.TelegramChatId`. If no `TaskOversight` record exists for that `TaskId`, the alert falls back to the workspace's default operator (the first active `OperatorBinding` for that `WorkspaceId`, resolved via `IOperatorRegistry.GetByWorkspaceAsync`). This ensures alerts are routed even for tasks not currently under explicit oversight. No `AgentId → TaskId` lookup is needed because the event carries both fields.
3. **Priority queuing:** Alert messages are enqueued to `OutboundMessageQueue` with severity copied from `AgentAlertEvent.Severity` (typically `Critical` or `High`). The priority queue (§10.4) ensures these are dequeued ahead of `Normal`/`Low` messages, meeting the P95 ≤ 2 s SLO for the bounded burst envelope (≤ 50 Critical+High messages; between 50–60, P95 approaches the boundary).
4. **Message formatting:** Alert messages include a severity badge (🚨 Critical, ⚠️ High), the agent ID, alert title (`AgentAlertEvent.Title`), body text (`AgentAlertEvent.Body`), and `CorrelationId` for traceability. No inline keyboard — alerts are informational, not interactive (operators use `/status TASK-ID` or `/pause AGENT-ID` to act on alerts).
5. **Idempotency:** The `OutboundMessage.IdempotencyKey` for alerts is `alert:{agentId}:{alertId}`, preventing the same alert from being enqueued twice if the event subscription delivers it more than once.
6. **Correlation and audit:** The `CorrelationId` from the originating agent event flows through the outbound message to the audit record, enabling end-to-end tracing from agent error → alert event → Telegram delivery → operator visibility.
7. **Burst behavior:** Under a 100+ agent burst, alert messages compete for priority queue position by severity. With ≤ 50 Critical+High alerts in the burst, P95 send latency stays under 2 s (see §10.4 burst math). Between 50–60, P95 approaches and may exceed 2 s. Beyond ~60, P95 clearly exceeds 2 s. Normal/Low alerts experience longer dwell times but are never lost.

### 5.7 Command Mapping Table

All nine required commands are defined below with their inputs, required authorization role, emitted event or query behavior, and audit semantics.

| Command | Syntax | Required Role | Behavior | Emitted Event / Query | Audit |
|---|---|---|---|---|---|
| `/start` | `/start` | None (Tier 1 allowlist only) | If user's Telegram ID is in `Telegram:AllowedUserIds`, creates or reactivates `OperatorBinding` from `Telegram:UserTenantMappings`. If not in allowlist, returns "not authorized" reply. | None (local state mutation) | `Action = "start"`, records binding creation or rejection. |
| `/status` | `/status` or `/status TASK-ID` | Binding only (Tier 2) | Without argument: queries `ISwarmCommandBus` for a summary of active tasks, pending questions, and agent counts for the operator's workspace. With `TASK-ID`: queries the specific task's current state, assigned agent, and last activity. Returns formatted summary via outbound queue. | `SwarmCommand.QueryStatus { TaskId?, WorkspaceId }` — read-only query, no side effects. | `Action = "status"`, records query parameters. |
| `/agents` | `/agents` or `/agents FILTER` | Binding only (Tier 2) | Lists active agents in the operator's workspace. Optional `FILTER` argument filters by agent name prefix or status (`idle`, `busy`, `error`). Returns formatted agent list via outbound queue. | `SwarmCommand.QueryAgents { WorkspaceId, Filter? }` — read-only query, no side effects. | `Action = "agents"`, records filter if provided. |
| `/ask` | `/ask <free text>` | Binding only (Tier 2) | Parses free text after `/ask` as a task description. Creates a `SwarmCommand.CreateTask` and publishes via `ISwarmCommandBus`. Returns confirmation with assigned task ID. | `SwarmCommand.CreateTask { Description, OperatorId, WorkspaceId, CorrelationId }` | `Action = "ask"`, records full command text and assigned task ID. |
| `/approve` | `/approve QUESTION-ID` | `Approver` | Looks up `PendingQuestionRecord` by `QuestionId`, validates it exists and has `Status = Pending`, and verifies that the pending question's `TelegramChatId` matches the current chat and that the operator's authorized binding (tenant/workspace) matches the question's originating route — this prevents an operator with a guessed `QuestionId` from approving a question routed to another chat or workspace. Produces `HumanDecisionEvent` with `ActionValue = "approve"`. Updates `PendingQuestionRecord.Status = Answered`. | `HumanDecisionEvent { QuestionId, ActionValue = "approve" }` | `Action = "approve"`, records question ID and agent ID. |
| `/reject` | `/reject QUESTION-ID [reason]` | `Approver` | Same as `/approve` but with `ActionValue = "reject"`. Performs the same route validation (chat and tenant/workspace match). Optional reason text is carried in `HumanDecisionEvent.Comment`. | `HumanDecisionEvent { QuestionId, ActionValue = "reject", Comment? }` | `Action = "reject"`, records question ID, agent ID, and reason if provided. |
| `/handoff` | `/handoff TASK-ID @alias` | Binding only (Tier 2) | Full oversight transfer (see §5.5 detailed flow above). Validates task ownership, resolves target operator by alias within tenant, updates `TaskOversight`, notifies both operators. | `SwarmCommand.TransferOversight { TaskId, SourceOperatorId, TargetOperatorId }` | `Action = "handoff"`, records task ID, source and target operator, timestamp. |
| `/pause` | `/pause AGENT-ID` or `/pause all` | `Operator` | Sends a pause directive to a specific agent or all agents in the operator's workspace. The agent suspends autonomous work and enters an idle state until resumed. `all` is a convenience alias scoped to the operator's workspace. | `SwarmCommand.PauseAgent { AgentId?, WorkspaceId, Scope = "single" \| "all" }` | `Action = "pause"`, records agent ID or "all" scope. |
| `/resume` | `/resume AGENT-ID` or `/resume all` | `Operator` | Sends a resume directive to a previously paused agent or all paused agents in the workspace. The agent resumes autonomous work from its last checkpoint. | `SwarmCommand.ResumeAgent { AgentId?, WorkspaceId, Scope = "single" \| "all" }` | `Action = "resume"`, records agent ID or "all" scope. |

**Common command behaviors:**
- All commands pass through `ITelegramUpdatePipeline` (deduplication → authorization → dispatch) before reaching their handler.
- All commands produce an acknowledgement reply enqueued to `OutboundMessageQueue` (never sent inline).
- All commands write an `AuditEntry` with `MessageId`, `UserId`, `AgentId` (null when no agent context exists, e.g., `/start` or `/ask` before agent assignment), `Timestamp`, and `CorrelationId`.
- Unrecognized commands receive a "Unknown command. Use /start for help." reply.

---

## 6. Assembly Map (Proposed)

> **Note:** The following projects do not yet exist in the repository. They are the planned assembly structure to be created during implementation. No `.sln`, `.csproj`, or `src/` tree currently exists.

```text
AgentSwarm.Messaging.sln  (to be created)
│
├── src/
│   ├── AgentSwarm.Messaging.Abstractions     ← IMessengerConnector, AgentQuestion,
│   │                                            HumanDecisionEvent, HumanAction,
│   │                                            MessengerMessage, MessengerEvent,
│   │                                            AgentQuestionEnvelope,
│   │                                            ISwarmCommandBus, IAuditLogger (interface),
│   │                                            IUserAuthorizationService,
│   │                                            AuditEntry, ITelegramUpdatePipeline,
│   │                                            PipelineResult, IDeduplicationService,
│   │                                            IPendingQuestionStore, PendingQuestion,
│   │                                            IOutboundQueue
│   │
│   ├── AgentSwarm.Messaging.Core← IOperatorRegistry,
│   │                                            IMessageSender, SendResult,
│   │                                            AuthZ service, TaskOversight (entity),
│   │                                            ITaskOversightRepository,
│   │                                            CommandDispatcher base,
│   │                                            RetryPolicy
│   │
│   ├── AgentSwarm.Messaging.Telegram         ← TelegramMessengerConnector,
│   │                                            TelegramUpdateRouter,
│   │                                            TelegramCommandDispatcher,
│   │                                            CallbackQueryHandler,
│   │                                            TelegramMessageSender (impl of IMessageSender),
│   │                                            WebhookController,
│   │                                            TelegramPollingService,
│   │                                            QuestionTimeoutService,
│   │                                            QuestionRecoverySweep,
│   │                                            TelegramOptions (config POCO)
│   │
│   ├── AgentSwarm.Messaging.Persistence      ← PersistentAuditLogger (impl of IAuditLogger),
│   │                                            OutboundQueueStore,
│   │                                            InboundUpdateStore,
│   │                                            OperatorBindingStore,
│   │                                            PendingQuestionRecord,
│   │                                            PersistentPendingQuestionStore,
│   │                                            DeduplicationService (impl of IDeduplicationService),
│   │                                            AuditLogEntry (persistence entity),
│   │                                            EF Core DbContext + migrations
│   │
│   └── AgentSwarm.Messaging.Worker           ← ASP.NET Core host,
│                                                DI registration,
│                                                OutboundQueueProcessor (BackgroundService),
│                                                InboundRecoverySweep,
│                                                Health checks,
│                                                OpenTelemetry bootstrap
│
└── tests/
    └── AgentSwarm.Messaging.Tests            ← Unit + integration tests
```

---

## 7. Configuration & Secrets

| Setting | Source | Notes |
|---|---|---|
| `Telegram:BotToken` | Azure Key Vault (production) / .NET User Secrets (local dev) | Never logged. In production, loaded via `SecretClient` with periodic refresh (default every 5 min) through `IOptionsMonitor<TelegramOptions>`, enabling rotation without restart (per tech-spec.md R-5). In local/dev environments, loaded from .NET User Secrets (`dotnet user-secrets set "Telegram:BotToken" "<value>"`) per tech-spec.md S-8, avoiding plaintext tokens in `appsettings.json` or environment variables. |
| `Telegram:WebhookUrl` | App configuration | Public HTTPS URL for production webhook receive mode. When empty/unset and `UsePolling = true`, the system uses polling mode instead. |
| `Telegram:UsePolling` | App configuration | `bool`, default `false`. When `true`, enables `TelegramPollingService` for local/dev receive mode. Mutually exclusive with `WebhookUrl` — startup fails if both `UsePolling = true` and `WebhookUrl` is set (per implementation-plan.md Stage 2.5). |
| `Telegram:SecretToken` | Key Vault (production) / .NET User Secrets (local dev) | Header value Telegram sends with each webhook POST; validated by `WebhookController`. |
| `Telegram:AllowedUserIds` | App configuration | Comma-separated allowlist of Telegram **user IDs** authorized to register via `/start`. See "Allowlist Authorization Model" below for how user IDs, chat IDs, and tenant/workspace bindings interact. |
| `Telegram:RateLimits:GlobalPerSecond` | App configuration | Default `30`. |
| `Telegram:RateLimits:PerChatPerMinute` | App configuration | Default `20`. |
| `Telegram:RateLimits:PerChatBurstCapacity` | App configuration | Default `5`. Number of tokens in the per-chat token bucket's burst allowance. During a burst, the first N messages per chat drain without per-chat wait. See §10.4 burst scenario for the math. |
| `OutboundQueue:MaxRetries` | App configuration | Default `5` (aligned with implementation-plan Stage 4.2 `RetryPolicy.MaxAttempts` default of `5` and e2e-scenarios.md "max 5 attempts"). |
| `OutboundQueue:BaseRetryDelaySeconds` | App configuration | Default `2`. |
| `OutboundQueue:ProcessorConcurrency` | App configuration | Default `10`. Number of concurrent send workers. |
| `OutboundQueue:MaxQueueDepth` | App configuration | Default `5000`. Backpressure threshold; low-severity messages are dead-lettered when exceeded. |
| `InboundRecovery:MaxRetries` | App configuration | Default `3`. Maximum reprocessing attempts for `InboundUpdate` records before they are excluded from recovery sweeps (see §2.2 InboundRecoverySweep). |
| `InboundRecovery:SweepIntervalSeconds` | App configuration | Default `60`. Interval between `InboundRecoverySweep` polling runs. |
| `QuestionRecovery:SweepIntervalSeconds` | App configuration | Default `60`. Interval between `QuestionRecoverySweep` polling runs. |

### 7.1 Allowlist Authorization Model

Authorization uses a **two-tier model**: a static configuration-time allowlist gates onboarding, and dynamic runtime bindings gate command execution.

| Tier | What is checked | Where it lives | When it is checked |
|---|---|---|---|
| **Tier 1: Onboarding allowlist** | `Telegram:AllowedUserIds` — a static list of Telegram **user IDs** (not chat IDs). | App configuration (environment variable, appsettings, or config provider). | At `/start` time only. If the user's Telegram user ID is not in this list, `/start` is rejected and no `OperatorBinding` is created. |
| **Tier 2: Runtime bindings** | `OperatorBinding` records — each row contains **both** `TelegramUserId` and `TelegramChatId`, plus `TenantId`, `WorkspaceId`, and `Roles`. | Persistence store (database). | On every inbound command (after deduplication). The `AuthZ Service` calls `IOperatorRegistry.IsAuthorizedAsync(userId, chatId)` which queries the `OperatorBinding` table for an active row matching both the user ID and the chat ID. If no matching binding exists, the command is rejected. |

**`/start` registration data sources:** When `/start` succeeds (user ID is in `Telegram:AllowedUserIds`), the system creates an `OperatorBinding` record. The required fields — `TenantId`, `WorkspaceId`, `Roles`, and `OperatorAlias` — are sourced from a pre-configured **user-to-tenant mapping** stored in app configuration under `Telegram:UserTenantMappings`. Each entry maps a Telegram user ID to its tenant, workspace, roles, and alias:

```json
{
  "Telegram": {
    "AllowedUserIds": ["12345", "67890"],
    "UserTenantMappings": {
      "12345": [
        { "TenantId": "acme", "WorkspaceId": "factory-1", "Roles": ["Operator", "Approver"], "OperatorAlias": "@alice" }
      ],
      "67890": [
        { "TenantId": "acme", "WorkspaceId": "factory-2", "Roles": ["Operator"], "OperatorAlias": "@bob" },
        { "TenantId": "acme", "WorkspaceId": "factory-3", "Roles": ["Operator"], "OperatorAlias": "@bob-f3" }
      ]
    }
  }
}
```

> **JSON shape (canonical): each user ID maps to a JSON array** (not a single object). The array contains one element per workspace. User `12345` has one workspace entry (single-element array); user `67890` has two workspace entries (two-element array). The `/start` handler iterates the array and calls `IOperatorRegistry.RegisterAsync` once per entry, creating one `OperatorBinding` per workspace. This document (architecture.md §7.1) is the **canonical source** for the `UserTenantMappings` JSON shape. The JSON schema above — where each user ID key maps to a **JSON array** of workspace entry objects — is the only valid shape. Implementation-plan.md Stage 6.3's `appsettings.json` example already uses this array shape (confirmed aligned).

Each user ID maps to an **array** of workspace entries. Most operators have a single entry; operators who oversee multiple workspaces (like user `67890` above) have multiple entries. When `/start` is received, the `StartCommandHandler` iterates the array and creates one `OperatorBinding` per entry.

When `/start` is received, the `StartCommandHandler` (1) checks `AllowedUserIds`, (2) looks up the user's entry in `UserTenantMappings` to obtain the array of workspace entries (each containing `TenantId`, `WorkspaceId`, `Roles`, and `OperatorAlias`), (3) derives `ChatType` from `Update.Message.Chat.Type`, and (4) for each workspace entry in the array, calls `IOperatorRegistry.RegisterAsync` with an `OperatorRegistration` value object carrying all required fields (`TelegramUserId`, `TelegramChatId`, `ChatType`, `TenantId`, `WorkspaceId`, `Roles`, `OperatorAlias`) to create one `OperatorBinding` record per workspace. Most operators have a single workspace entry; operators overseeing multiple workspaces get one binding per entry. Subsequent commands trigger workspace disambiguation via inline keyboard when multiple bindings exist for the same (user, chat) pair (per §4.3). The `ChatType` field (`Private`, `Group`, `Supergroup`) is derived from the Telegram `Update.Message.Chat.Type` field at `/start` time.

**Key design decisions:**
- **Chat IDs are not independently allow-listed in configuration**, but the story's "validate chat/user allowlist" requirement is fully satisfied by the two-tier model. Here is why: the story requires that commands from unauthorized chats/users are rejected. Tier 2 accomplishes this — every inbound command is checked against `OperatorBinding` records, which store both `TelegramUserId` and `TelegramChatId`. A command from an unregistered (user, chat) pair is rejected, even if the user has a binding in a different chat. This is functionally equivalent to maintaining a separate `AllowedChatIds` configuration list, but more secure: chat authorization is always tied to a specific (user, chat, workspace) triple created through the auditable `/start` onboarding flow, rather than a static config list that could drift from reality. In effect, the `OperatorBinding` table **is** the chat/user allowlist — it is just persisted in the database rather than in configuration.
- **Group chat attribution:** In group chats, commands are attributed to the sending `TelegramUserId` (the `from.id` field on the Telegram `Update`), not the group's `TelegramChatId`. This means each group member must have their own `OperatorBinding` — an unauthorized user in an authorized group is rejected (per tech-spec S-5).
- **Multi-workspace:** An operator may have `OperatorBinding` rows in multiple workspaces. When a command is ambiguous (the user has bindings in multiple workspaces for the same chat), the bot presents an inline keyboard for workspace disambiguation (per e2e-scenarios).

---

## 8. Observability

| Signal | Implementation |
|---|---|
| **Traces** | OpenTelemetry `ActivitySource("AgentSwarm.Messaging.Telegram")`. Every inbound update and outbound send starts a span carrying `CorrelationId` as a baggage item. |
| **Metrics** | Counters: `telegram.messages.received`, `telegram.messages.sent`, `telegram.messages.dead_lettered`, `telegram.commands.processed`, `telegram.messages.backpressure_dlq`. Histograms: `telegram.send.first_attempt_latency_ms` (acceptance gate; enqueue — `OutboundMessage.CreatedAt` — to HTTP 200, first-attempt sends only, excludes sends that received Telegram 429; includes queue dwell and local token-bucket wait — P95 ≤ 2 s target; see §10.4), `telegram.send.all_attempts_latency_ms` (all-inclusive; enqueue to HTTP 200 regardless of attempt number or rate-limit holds — capacity planning), `telegram.send.queue_dwell_ms` (diagnostic; enqueue to dequeue — queue backlog monitoring), `telegram.send.retry_latency_ms` (diagnostic; retried sends), `telegram.send.rate_limited_wait_ms` (diagnostic; Telegram 429 backoff duration). |
| **Logs** | Structured logging via `ILogger<T>`. Correlation ID included in every log scope. Bot token is excluded from all log output via a custom redaction enricher. |
| **Health** | `/healthz` endpoint (aligning with implementation-plan and Dockerfile `HEALTHCHECK`). Aggregates checks: Telegram API reachable (`getMe`), outbound queue depth < threshold, dead-letter queue depth < configurable threshold, database connectivity. Returns JSON detail output with per-check status. |

---

## 9. Security Model

1. **Webhook validation** — Every inbound POST must carry the `X-Telegram-Bot-Api-Secret-Token` header matching the configured `Telegram:SecretToken`. Requests with a missing or invalid secret token return `403 Forbidden`. This aligns with e2e-scenarios and implementation-plan which both specify HTTP 403 for webhook secret validation failures.
2. **Operator allowlist** — `TelegramUserId` and `ChatId` are checked against `OperatorBinding` records. Unregistered users receive a generic "not authorized" reply and the attempt is logged.
3. **Role enforcement** — Two-tier authorization model (per §7.1 and implementation-plan.md Stage 5.2): Tier 1 (onboarding) checks `TelegramOptions.AllowedUserIds` for `/start` only. Tier 2 (runtime) requires an active `OperatorBinding` for all other commands. Beyond Tier 2 binding, only role-gated commands require a specific role: `/approve` and `/reject` require the `Approver` role; `/pause` and `/resume` require the `Operator` role. Commands `/status`, `/agents`, `/ask`, and `/handoff` have no role requirement beyond Tier 2 binding authorization (aligned with implementation-plan.md Stage 5.2). If an operator lacks the required role for a role-gated command, the command is rejected with an "insufficient permissions" reply and an audit log entry at Warning level.
4. **Secret isolation and rotation** — Bot token is loaded from Azure Key Vault in production using `SecretClient` and from .NET User Secrets in local/dev environments (per tech-spec.md S-8: `dotnet user-secrets set "Telegram:BotToken" "<value>"`), and injected via `IOptionsMonitor<TelegramOptions>`. In production, the connector supports **periodic token refresh** (every 5 minutes by default, configurable via `Telegram:SecretRefreshIntervalMinutes`) using `IOptionsMonitor<T>`'s change-notification mechanism, so that a Key Vault rotation takes effect without a full process restart — consistent with tech-spec.md R-5's recommendation. The refreshed token is applied to the `TelegramBotClient` instance on the next API call. The token value is never serialized, logged, or exposed via health endpoints; in-memory representation uses a `SecureString`-equivalent wrapper that is cleared on disposal.
5. **Rate limiting** — Inbound commands are rate-limited per user (10 commands/minute) to prevent abuse from a compromised account.

---

## 10. Cross-Cutting Concerns

### 10.1 Correlation ID Propagation

Every inbound update generates or adopts a `CorrelationId` (UUID v7 for time-ordering). The ID flows through:
- `TelegramUpdateRouter` → `CommandDispatcher` → `SwarmCommandBus` (outbound to orchestrator)
- `SwarmCommandBus` (inbound event) → `OutboundMessageQueue` → `TelegramSender`
- All `AuditLogEntry` entries
- All OpenTelemetry spans (as `trace.correlation_id` attribute)

**Renderer invariant (outbound):** Every outbound Telegram message rendered by `TelegramSender` includes the `CorrelationId` in the message body — appended as a footer line in the format `🔗 trace: {CorrelationId}`. This satisfies the story acceptance criterion "All messages include trace/correlation ID" and ensures that operators, audit reviewers, and support engineers can visually match any Telegram message to its end-to-end trace. The `CorrelationId` is included in command acknowledgements, agent question renders, alert notifications, status updates, and error replies — no outbound message type is exempt.

### 10.2 Receive-Mode Switching

The connector supports two receive modes controlled by the `Telegram:UsePolling` configuration switch (§7) and the presence of `Telegram:WebhookUrl`:

| Mode | When | Mechanism |
|---|---|---|
| **Webhook** | Production, staging | ASP.NET Core controller at `/api/telegram/webhook`. On startup, calls `setWebhook` with the configured URL and secret token. |
| **Long polling** | Local dev, CI | `TelegramPollingService` BackgroundService calls `getUpdates` in a loop with 30-second timeout. Enabled when `TelegramOptions.UsePolling = true`. On startup, calls `deleteWebhook` to avoid conflicts. Mutually exclusive with webhook mode — startup fails if both `UsePolling = true` and `WebhookUrl` is set. |

Both modes feed into the same `ITelegramUpdatePipeline`, so all downstream logic — deduplication, authorization, command dispatch — is mode-agnostic.

### 10.3 Question Timeout Handling

A `QuestionTimeoutService` (BackgroundService) polls for `PendingQuestionRecord` entries with `Status` in `Pending` or `AwaitingComment` that are past their `ExpiresAt`. Both statuses represent questions awaiting operator completion: `Pending` questions have received no interaction; `AwaitingComment` questions have received a button tap for a `RequiresComment` action but the operator never sent the follow-up text reply. Without including `AwaitingComment`, an operator who taps a "Need info" button and then never replies would leave the question permanently stuck. When a question times out:
1. Reads `PendingQuestionRecord.DefaultActionValue` (the `HumanAction.Value` of the default action, resolved and denormalized at send time from the `HumanAction` in `AllowedActions` whose `ActionId` matches `ProposedDefaultActionId` — see §3.1 `PendingQuestionRecord.DefaultActionValue`). If present, the timeout service publishes a `HumanDecisionEvent` with `DefaultActionValue` as the `ActionValue` — consistent with the interactive button-tap path where `HumanAction.Value` is always the canonical `ActionValue` (see §3.1 `HumanAction.Value`, §5.2 invariant 6). No `IDistributedCache` lookup or `AgentQuestion` JSON deserialization is required. If `DefaultActionValue` is absent (`null`), publishes a `HumanDecisionEvent` with `ActionValue = "__timeout__"` so the agent is notified of timeout without an automatic decision.
2. Updates the original Telegram message (using `PendingQuestionRecord.TelegramMessageId`) to indicate the timeout ("⏰ Timed out — default action applied: *skip*" or "⏰ Timed out — no default action").
3. Sets `PendingQuestionRecord.Status = TimedOut`.
4. Writes an audit record noting the timeout.

### 10.4 Performance: Concurrency, Backpressure, and Rate Limiting

The 2-second P95 send-latency target and the 100+ agent burst requirement demand explicit concurrency, priority queuing, and backpressure design.

#### Acceptance vs. Degraded Burst — Explicit Distinction

The story says: "P95 send latency under 2 seconds after event is queued; support burst alerts from 100+ agents without message loss." These are **two distinct requirements** with different satisfaction criteria:

| Requirement | Scope | How satisfied |
|---|---|---|
| **P95 ≤ 2 s** (acceptance gate metric: `telegram.send.first_attempt_latency_ms`) | **Operator-scoped, bounded burst**: applies to first-attempt, non-429 sends (formal decision D-P95 per §11.1) under steady state (queue depth < 100) and bounded bursts (≤ 50 Critical+High messages **distributed across ≥ 10 operator chats** — formal decision D-BURST per §11.1; deployments with fewer chats accept degraded burst P95 — see per-chat constraint below). This is the acceptance criterion that is tested and measured. | Priority queuing + token-bucket burst capacity + 10 concurrent workers. The 48th message of 50 completes in ~1,900 ms (see burst math below). |
| **100+ agents without message loss** | **System-wide, unbounded**: applies to any burst size, including a cascading failure where all 100+ agents alert simultaneously. This is a **delivery guarantee**, not a latency guarantee. | At-least-once delivery or dead-letter. Every message is either delivered to Telegram or dead-lettered with a traceable reason and retained for replay. No silent discard. Priority ordering ensures highest-severity messages drain first even when P95 exceeds 2 s. |

When a 100+ agent alert burst generates **more than 50 Critical+High messages** (e.g., 80–100 Critical alerts from a cascading failure), the system enters the **degraded burst** regime. In this regime: (a) all messages are still delivered at-least-once or dead-lettered — the "without message loss" guarantee holds unconditionally; (b) messages drain in severity-priority order — Critical before High before Normal before Low; (c) P95 send latency exceeds 2 s and scales linearly with volume (at 30 msg/s sustained throughput, 100 Critical messages drain in ~3.3 s, putting P95 at ~3.1 s); (d) the `telegram.send.queue_dwell_ms` and `telegram.send.all_attempts_latency_ms` metrics provide real-time visibility into the degraded regime for capacity planning and alerting. The acceptance gate metric `telegram.send.first_attempt_latency_ms` **continues to be recorded** in the degraded regime but is not expected to meet P95 ≤ 2 s — operators monitor the diagnostic metrics to detect burst conditions and scale capacity.

> **⚠ SCOPE NARROWING — OPERATOR-DIRECTED DECISION (p95-metric-scope) — see §11.1 D-P95**
>
> The story says: *"P95 send latency under 2 seconds after event is queued."*
>
> **Decision (operator-directed, persisted as D-P95 in §11.1):** The P95 measurement covers **first-attempt, non-rate-limited sends only**. Retried sends and Telegram 429-rate-limited sends are excluded from the acceptance gate and tracked separately. "Non-rate-limited" refers to sends that did not receive a Telegram 429 response; local token-bucket wait (proactive rate limiting before issuing the HTTP call) **is included** in the metric as it is part of the normal send path.
>
> **Risk:** If a future reviewer expects the P95 SLO to cover retried/rate-limited sends, the acceptance gate will appear to under-measure. The `telegram.send.all_attempts_latency_ms` metric (see table below) provides full-lifecycle visibility for capacity planning.

> **⚠ SCOPE CLARIFICATION — OPERATOR-DIRECTED DECISION (burst-topology-envelope) — see §11.1 D-BURST**
>
> The story says: *"Support burst alerts from 100+ agents without message loss"* alongside *"P95 send latency under 2 seconds."*
>
> The P95 ≤ 2 s SLO under burst assumes a deployment topology where ≤ 50 Critical+High messages are **distributed across ≥ 10 operator chats**, so the per-chat rate limit of 20 msg/min is not the binding constraint. This is narrower than the story's unconstrained "100+ agents" language.
>
> **Decision (operator-directed, persisted as D-BURST in §11.1):** ≥ 10 operator chats **is the expected deployment topology** — 100+ agents inherently span multiple tenants/workspaces with distinct operator assignments, making ≥ 10 operator chats a natural consequence. Deployments with fewer than 10 operator chats must accept that the P95 SLO applies only under steady-state load, not during bursts — this is documented as a capacity planning note, not a hidden limitation. The story's "without message loss" guarantee holds unconditionally regardless of chat count.
>
> **Risk:** If the deployment topology changes (e.g., consolidation to fewer operator chats), the burst P95 envelope narrows and may no longer hold. Monitor `telegram.send.queue_dwell_ms` per chat to detect topology-driven degradation.

#### P95 Metric Definition

This document (architecture.md §10.4) is the **single canonical source** for metric names and measurement semantics; sibling documents should defer to these definitions.

| Metric | Type | Definition |
|---|---|---|
| `telegram.send.first_attempt_latency_ms` | **Acceptance gate** | Enqueue (`OutboundMessage.CreatedAt`) → HTTP 200. First-attempt sends only; excludes sends that received a Telegram 429 response. **Includes** queue dwell and local token-bucket wait (proactive rate-limit blocking). **P95 ≤ 2 s target applies to this metric.** |
| `telegram.send.all_attempts_latency_ms` | Capacity planning | Enqueue → HTTP 200, all sends regardless of attempt number or rate-limit holds. |
| `telegram.send.queue_dwell_ms` | Diagnostic | Enqueue → dequeue. Monitors queue backlog under burst. |
| `telegram.send.retry_latency_ms` | Diagnostic | Enqueue → eventual success for retried messages. |
| `telegram.send.rate_limited_wait_ms` | Diagnostic | Duration of 429 backoff waits. |

#### What acceptance is and is not proving

The P95 ≤ 2 s acceptance gate is an **operator-scoped, bounded-burst SLO** — not a universal guarantee under arbitrary load. The distinction matters when the story says "support burst alerts from 100+ agents without message loss":

- **"Without message loss"** is satisfied unconditionally: every message is either delivered (at-least-once) or dead-lettered with a traceable reason, regardless of burst size.
- **"P95 ≤ 2 s"** is satisfied under **normal load** and **bounded bursts** (≤ 50 Critical+High messages). Beyond that bound, latency degrades gracefully — messages are still delivered, but the SLO is not met.

A 100+ agent alert burst may generate far more than 50 Critical+High messages (e.g., a cascading failure where all agents alert simultaneously). In that **degraded burst** regime, the system prioritizes delivery correctness over latency: messages queue, drain in severity order, and are delivered within seconds to minutes depending on volume. The `telegram.send.queue_dwell_ms` and `telegram.send.all_attempts_latency_ms` metrics provide real-time visibility into the degraded regime for capacity planning and alerting.

| Condition | P95 ≤ 2 s? | Message loss? |
|---|---|---|
| **Steady state** (queue depth < 100, no rate-limiting) | **Yes**, all severities | At-least-once delivered or dead-lettered |
| ↳ *e2e-scenarios.md SC-PERF-01 (100 paced messages, queue depth < 100, no 429s)* | *Falls within this envelope — P95 ≤ 2 s across all severities is correctly asserted* | |
| **Bounded burst** (≤ 50 Critical+High in a 1000-msg burst, distributed across ≥ 10 operator chats — formal decision D-BURST per §11.1; see §10.4 Burst Scenario item 3) | **Yes**, Critical+High only; Normal/Low exceed 2 s | At-least-once delivered or dead-lettered |
| **Near-boundary burst** (50–60 Critical+High) | **At risk** — P95 approaches/exceeds 2 s depending on HTTP variance | At-least-once delivered or dead-lettered |
| **Degraded burst** (> ~60 Critical+High, e.g., 100+ agent cascade) | **No** — queue dwell grows proportionally; P95 may reach 5–30 s depending on volume | At-least-once delivered or dead-lettered; priority ordering ensures highest-severity messages drain first |
| **Rate-limited sends** (Telegram 429) | **Excluded** from acceptance gate metric (tracked by `all_attempts_latency_ms`) | At-least-once delivered or dead-lettered |
| **Retried sends** | **Excluded** from acceptance gate metric (tracked by `all_attempts_latency_ms`) | At-least-once delivered or dead-lettered |

**In summary:** P95 ≤ 2 s is a **bounded-burst SLO** that holds under steady state and bursts with ≤ 50 Critical+High messages. Between 50–60 Critical+High messages, P95 approaches and may exceed 2 s. Beyond ~60 (including a full 100+ agent cascade), P95 clearly exceeds 2 s and the system enters the **degraded burst** regime — messages are still delivered at-least-once in severity-priority order, but latency scales with volume. The story's "without message loss" requirement is satisfied across all burst sizes: no message is silently discarded — every message is either delivered to Telegram (at-least-once) or dead-lettered with a traceable reason and retained for operator-initiated manual replay. Low-severity messages may be backpressure-dead-lettered under extreme queue depth (see below); Critical, High, and Normal messages are never backpressure-DLQ'd. At-least-once delivery implies narrow duplicate-send windows during crash recovery (see §3.1 Gap A).

The architecture meets the P95 target through:

| Condition | Behavior | Mechanism |
|---|---|---|
| **Normal load** (queue depth < 100) | P95 ≤ 2 s across all severities. Queue dwell < 50 ms; Telegram HTTP round-trip ~200–500 ms; total enqueue-to-HTTP-200 well under 2 s. | 10 concurrent workers drain faster than inflow. |
| **Burst load** (100+ agents, 1000+ messages) | P95 ≤ 2 s for Critical/High when ≤ 50 Critical+High messages. Between 50–60, at risk. Not guaranteed beyond ~60. Normal/Low experience longer dwell. | Priority queuing; `queue_dwell_ms` for visibility. |
| **Beyond capacity envelope** (sustained > 30 msg/s) | Low-severity backpressure-DLQ'd when queue depth exceeds `MaxQueueDepth` (5000). Critical/High/Normal always accepted. | Backpressure DLQ + `telegram.messages.backpressure_dlq` counter + operator alert. |

#### Queue Processor Concurrency

The `OutboundQueueProcessor` runs as a `BackgroundService` with configurable concurrency:

| Setting | Default | Description |
|---|---|---|
| `OutboundQueue:ProcessorConcurrency` | `10` | Number of concurrent dequeue-and-send workers. Each worker independently dequeues, sends via `TelegramSender`, and marks sent/failed. |
| `OutboundQueue:MaxQueueDepth` | `5000` | Backpressure threshold. When the durable queue exceeds this depth, `EnqueueAsync` applies backpressure: `Low`-severity messages are **dead-lettered immediately** (moved to the dead-letter queue with reason `backpressure:queue_depth_exceeded`) and a `telegram.messages.backpressure_dlq` counter is emitted. `Normal`, `High`, and `Critical` severity messages are always accepted regardless of queue depth. This preserves the delivery guarantee as defined in §10.4: no message is silently discarded; every message is either delivered to Telegram (at-least-once) or dead-lettered with a traceable reason and available for operator-initiated manual replay after the burst subsides. |

#### Priority Queuing

The outbound queue implements a **severity-based priority order**: `Critical` > `High` > `Normal` > `Low`. The `OutboundQueueProcessor` always dequeues the highest-severity pending message first. This ensures that under burst conditions, time-critical messages (blocking questions, approval requests, urgent alerts) are dispatched ahead of informational messages.

**Bounded-volume burst math:** The P95 metric (`telegram.send.first_attempt_latency_ms`) measures enqueue-to-HTTP-200, which includes queue dwell, local token-bucket wait (worker blocking until a token is available), and Telegram HTTP round-trip latency. **Clarification on "non-rate-limited" scope:** the metric excludes only sends that receive a Telegram 429 response (i.e., the remote API rate-limited the request); local token-bucket waiting (where a worker blocks before issuing the HTTP call to stay within the proactive rate limit) is **included** in the metric because it is part of the normal send path, not a rate-limit failure. This distinction is important: the burst math below accounts for token-bucket token availability as part of the timing, and those timings feed directly into the `first_attempt_latency_ms` measurement. The system uses a **token-bucket rate limiter** with burst capacity B=30 tokens and refill rate 30 tokens/s, combined with 10 concurrent workers (HTTP latency median ~350 ms, P95 ~500 ms). Under burst, this creates a two-phase drain pattern:

- **Phase 1 (messages 1–30):** The burst bucket has 30 pre-filled tokens, so no rate-limit wait. With 10 workers, messages drain in 3 batches: messages 1–10 start at t≈0 ms (complete ~350 ms), messages 11–20 start at t≈350 ms (complete ~700 ms), messages 21–30 start at t≈700 ms (complete ~1 050 ms). All 30 messages complete by ~1 050 ms (median HTTP) or ~1 200 ms (P95 HTTP).
- **Phase 2 (messages 31–60):** During Phase 1 (~1 050 ms), the bucket refilled ~31 tokens (1 050 ms ÷ 33 ms/token), replenishing the burst capacity. Messages 31–60 therefore also benefit from burst tokens: messages 31–40 start at ~1 050 ms, 41–50 at ~1 400 ms, 51–60 at ~1 750 ms.

The **P95 of 50 messages** is the 48th message (⌈0.95 × 50⌉). Message 48 falls in the 41–50 batch, starting HTTP at ~1 400 ms. With P95 HTTP latency of ~500 ms: enqueue-to-HTTP-200 ≈ 1 400 + 500 = **1 900 ms** — safely under 2 s. At median HTTP (~350 ms), message 48 completes at ~1 750 ms. The **P95 across a 50-message set** is comfortably within the 2 s SLO.

For 60 messages, the 57th message (⌈0.95 × 60⌉) falls in the 51–60 batch, starting HTTP at ~1 750 ms. With P95 HTTP latency: ~2 250 ms; at median HTTP: ~2 100 ms. **60 Critical+High messages is at/over the SLO boundary** — the P95 exceeds 2 s under realistic HTTP variance.

We set the bounded-volume claim at **≤ 50 Critical+High messages**, where P95 stays safely under 2 s. Between 50 and 60 messages, P95 approaches and may exceed 2 s depending on HTTP latency variance. Beyond ~60, P95 clearly exceeds 2 s. `telegram.send.queue_dwell_ms` provides visibility for capacity planning at all burst sizes.

#### Rate Limiting Under Burst

The `TelegramSender` enforces Telegram Bot API rate limits via a dual-layer token-bucket limiter:

1. **Global limiter** — `Telegram:RateLimits:GlobalPerSecond` (default `30`). Applies across all chats. When exhausted, workers block and wait for a token rather than issuing requests that will be 429'd.
2. **Per-chat limiter** — `Telegram:RateLimits:PerChatPerMinute` (default `20`). Prevents flooding a single operator's chat.

When the Telegram API returns `429 Too Many Requests`, the sender reads the `retry_after` header and pauses the affected worker for that duration. Rate-limited wait time is tracked via the dedicated `telegram.send.rate_limited_wait_ms` histogram for operational diagnostics. Messages that receive a Telegram 429 response are excluded from the acceptance gate metric `telegram.send.first_attempt_latency_ms` and are captured by the all-inclusive `telegram.send.all_attempts_latency_ms` for capacity planning. **Note:** local token-bucket blocking (workers waiting for a token before issuing the HTTP call) is included in `first_attempt_latency_ms` because it is part of the proactive rate-limiting path, not a Telegram-side rejection. The burst math in §10.4 accounts for this by modeling token availability as part of the enqueue-to-HTTP-200 timeline.

#### Burst Scenario (100+ Agents)

Under a burst of 1 000+ simultaneous agent events:

1. **Enqueue**: Events are written to the durable outbox store immediately (sub-millisecond per insert, batched where possible). Each event is tagged with its severity.
2. **Priority drain**: The 10 concurrent processor workers dequeue by severity priority. Critical/High messages (typically ≤ 50 per burst — blocking questions, approval requests, urgent alerts) are processed first. The token-bucket rate limiter has burst capacity B=30 with 30 tokens/s refill, creating a two-phase drain: Phase 1 drains messages 1–30 in ~1,050 ms using pre-filled burst tokens; Phase 2 drains messages 31–50 using refilled tokens (see Priority Queuing math above). The P95 of the 50-message set (the 48th message) completes enqueue-to-HTTP-200 at approximately 1,900 ms — safely under the 2 s target. Between 50–60 Critical+High messages, P95 approaches/exceeds 2 s. Beyond ~60, P95 clearly exceeds 2 s.
3. **Multi-chat fan-out and per-chat constraint**: In production, 100+ agents typically span multiple tenants/workspaces, routing to multiple operator chats. The per-chat rate limit (20 msg/min ≈ 0.33 msg/s) constrains individual operators, while the global rate limit (30 msg/s) applies across all chats. With messages distributed across N operator chats, effective throughput is `min(30, N × 20/60)` msg/s. **Formal decision D-BURST (§11.1):** The burst math assumes ≤ 50 Critical+High messages distributed across ≥ 10 operator chats. The operator decided during story planning that ≥ 10 operator chats is the expected deployment topology — 100+ agents inherently span multiple tenants/workspaces with distinct operator assignments, making this a natural consequence, not an arbitrary threshold. **For deployments with fewer than 10 operator chats**, the per-chat rate limit becomes the binding constraint and the P95 ≤ 2 s SLO cannot be met during bursts; such deployments must accept that the P95 SLO applies only under steady-state load. For 10+ operator chats, the global limit of 30 msg/s is the binding constraint and the ≤ 50 priority messages drain in ~1,900 ms (P95). The per-chat limiter (20 msg/min) is sized for sustained flow, not burst — during a burst, each per-chat token bucket has its own burst capacity (default 5 tokens, configurable via `Telegram:RateLimits:PerChatBurstCapacity`) that allows the first 5 messages per chat to drain without per-chat wait. With ≤ 50 messages spread across ≥ 10 operator chats (~5 messages per chat), the per-chat burst capacity absorbs the burst and the global 30 msg/s limit is the binding constraint — matching the burst math. **For fewer operator chats, the per-chat limit becomes the binding constraint and the P95 ≤ 2 s SLO cannot be met during bursts:** a single operator chat can sustain only ~0.33 msg/s, so 50 messages to a single chat would take ~150 s. Even 5 chats (each receiving 10 messages, 5 absorbed by burst capacity, 5 rate-limited) would exceed 2 s. Deployments with fewer than 10 operator chats must accept that the P95 SLO applies only under steady-state load, not during bursts, and individual chat delivery times will exceed 2 s during burst conditions.
4. **Throughput**: At 30 msg/s sustained, a burst of 1 000 messages drains in ~34 seconds. Priority queuing ensures Critical/High messages are dispatched first, reducing their enqueue-to-HTTP-200 latency relative to Normal/Low. The acceptance metric `telegram.send.first_attempt_latency_ms` (enqueue to HTTP 200, first-attempt, excludes Telegram 429s, includes local token-bucket wait per §10.4) targets P95 ≤ 2 s under **normal load** (steady state, queue depth < 100). Under burst, the first ≤ 50 priority messages meet the P95 target (queue dwell + HTTP latency < 2 s; see Priority Queuing burst math); messages in positions 50–60 are near/over the SLO boundary; messages beyond position 60 incur clearly longer queue dwell. The P95 ≤ 2 s is a steady-state SLO that extends to bounded bursts (≤ 50 Critical+High) and degrades gracefully beyond that while guaranteeing at-least-once delivery (or dead-letter) and priority ordering. Sends that receive Telegram 429 responses are tracked via `telegram.send.all_attempts_latency_ms` (all-inclusive, enqueue to 200) for capacity planning.
5. **Backpressure**: If queue depth exceeds `MaxQueueDepth`, low-severity messages are dead-lettered immediately with reason `backpressure:queue_depth_exceeded` (see §10.4 table) and a `telegram.messages.backpressure_dlq` counter is incremented. An alert is sent to the ops channel. Critical, High, and Normal messages are always accepted.
6. **Delivery guarantee**: The story's "without message loss" requirement is satisfied: all messages are either delivered to Telegram (at-least-once) or dead-lettered with a traceable reason and retained for operator-initiated manual replay — no silent discard (per e2e-scenarios burst test). Backpressure dead-lettering of Low-severity messages under extreme queue depth does not constitute message loss because the messages are durably retained and replayable; Critical, High, and Normal messages are never backpressure-DLQ'd. At-least-once delivery implies narrow duplicate-send windows in crash recovery (§3.1 Gap A). See §10.4 for the full reconciliation.

---

## 11. Design Decisions and Rationale

| Decision | Rationale |
|---|---|
| **`Telegram.Bot` as the client library** | Most widely adopted .NET Telegram library; strong community, practical examples, sufficient Bot API coverage. `Telegram.BotAPI` is the fallback if a newer Bot API feature is needed. |
| **Outbound queue is durable, not in-memory** | The 2-second P95 latency target assumes queuing overhead is minimal, but durability is non-negotiable given the at-least-once delivery requirement and the burst scenario (100+ agents). The persistent store (database) is the source of truth for all enqueued messages; an in-memory `Channel<T>` serves only as a hot buffer (read-through acceleration) in front of the persistent store to keep the dequeue hot path fast. Messages are persisted before being placed in the `Channel<T>`, so a process crash loses zero enqueued messages. |
| **`update_id` as the deduplication key** | Telegram guarantees `update_id` is unique and monotonically increasing per bot. Using it directly avoids the cost of hashing message content. |
| **Webhook secret token validation** | Telegram supports a `secret_token` parameter on `setWebhook` (added in Bot API 6.0). This is cheaper and simpler than IP-allowlisting Telegram's data-center ranges. |
| **Single `ITelegramUpdatePipeline`** | Forces webhook and polling modes through identical logic, eliminating a class of "works in dev, breaks in prod" bugs. |
| **Inline keyboard `callback_data` format: `QuestionId:ActionId`** | Encodes `QuestionId:ActionId` directly in `callback_data` (aligned with tech-spec D-3 and implementation-plan Stages 2.3/3.3). Both IDs are constrained to ≤ 30 characters — anchored in the data model: `AgentQuestion.QuestionId` (§3.1, constraints) and `HumanAction.ActionId` (§3.1, `HumanAction` definition) — keeping the combined payload within Telegram's 64-byte `callback_data` limit (max 61 bytes including the `:` separator). The full `HumanAction` payload is stored server-side in `IDistributedCache` keyed by `QuestionId:ActionId`, written at inline-keyboard build time with expiry at `AgentQuestion.ExpiresAt + 5 minutes` (the 5-minute grace window ensures `CallbackQueryHandler` can still resolve the cached `HumanAction` for late button taps near the `ExpiresAt` boundary — aligned with implementation-plan.md Stage 2.3 and tech-spec D-3). On cache miss, the handler falls back to deserializing `PendingQuestionRecord.AgentQuestion` JSON (see §5.2 invariant 3). The cache serves interactive button callbacks (which occur before or near `ExpiresAt`); the `QuestionTimeoutService` does not depend on the cache — it uses `DefaultActionValue` (the resolved `HumanAction.Value`) from `PendingQuestionRecord` (see §10.3). On callback, the handler parses the key, resolves the full `HumanAction` from cache or durable fallback, and reads `HumanAction.Value` to populate `HumanDecisionEvent.ActionValue`. |

### 11.1 Formal Decision Records (Operator-Directed)

The following decisions were made by the operator during story planning and are persisted here as formal architectural decision records. They narrow the acceptance envelope for the P95 ≤ 2 s SLO and must be reviewed if the deployment topology or measurement methodology changes.

#### D-P95 — P95 Metric Scope

- **Question ID:** `p95-metric-scope`
- **Decision:** The P95 ≤ 2 s SLO is measured on **first-attempt, non-rate-limited sends only**. "Non-rate-limited" means the send did not receive a Telegram 429 response; local token-bucket wait (proactive rate limiting before issuing the HTTP call) **is included** in the metric. Retried sends and Telegram-429-rate-limited sends are excluded from the acceptance gate metric (`telegram.send.first_attempt_latency_ms`) and tracked separately via `telegram.send.all_attempts_latency_ms` for capacity planning.
- **Rationale:** First-attempt latency measures the system's ability to drain queued messages promptly under normal and bounded-burst conditions. Including retried/rate-limited sends would conflate delivery reliability (retries) with send-path performance, making the SLO unactionable.
- **Impact:** If a future reviewer expects the P95 SLO to cover retried/rate-limited sends, the acceptance gate will appear to under-measure. The `all_attempts_latency_ms` metric provides full-lifecycle visibility.
- **Status:** Decided (operator answer, iteration 11).

#### D-BURST — Burst Topology Envelope

- **Question ID:** `burst-topology-envelope`
- **Decision:** The P95 ≤ 2 s burst SLO assumes ≤ 50 Critical+High messages **distributed across ≥ 10 operator chats**. This is the expected deployment topology: 100+ agents inherently span multiple tenants/workspaces with distinct operator assignments, making ≥ 10 operator chats a natural consequence. Deployments with fewer than 10 operator chats must accept that the P95 SLO applies only under steady-state load, not during bursts.
- **Rationale:** The per-chat rate limit (20 msg/min) becomes the binding constraint when messages concentrate in fewer chats. With ≥ 10 chats, the global rate limit (30 msg/s) is the binding constraint and the burst math (§10.4) holds.
- **Impact:** If the deployment topology consolidates to fewer operator chats, the burst P95 envelope narrows. Monitor `telegram.send.queue_dwell_ms` per chat to detect topology-driven degradation.
- **Status:** Decided (operator answer, iteration 11).

---

## 12. Constraints and Assumptions

1. **Single-bot deployment** — One Telegram bot per swarm instance. Multi-bot is not in scope.
2. **No file/media handling** — The connector handles text messages and inline buttons only. Photo/document attachments from operators are out of scope for this story.
3. **Persistence technology** — The architecture assumes EF Core for `OperatorBinding`, `InboundUpdate`, `OutboundMessage`, and `AuditLogEntry`. Consistent with tech-spec.md decision D-1, the persistence provider is: **SQLite for dev/local environments; PostgreSQL or SQL Server for production.** The EF Core abstraction makes the provider selection a deployment-time configuration change (connection string + provider package), not a schema change. Implementation-plan.md (Stages 3.x–4.x) specifies the same dev/local vs. production split for outbox, dedup store, dead-letter queue, and audit stores.
4. **Swarm orchestrator interface** — `ISwarmCommandBus` is to be defined in the planned `AgentSwarm.Messaging.Abstractions` project. Its transport (in-process, message broker, gRPC) is outside this story's scope.
5. **Allowlist-based `/start` registration** — When a user sends `/start`, the connector checks whether their Telegram user ID is in the pre-configured allowlist (`Telegram:AllowedUserIds`). If present, the `OperatorBinding` is created or updated immediately with `IsActive = true`, sourcing `TenantId`, `WorkspaceId`, `Roles`, and `OperatorAlias` from the `Telegram:UserTenantMappings` configuration entry for that user ID (see §7.1). If absent, the user receives a "not authorized" reply. No admin approval step is required for allowlisted users; the `IsActive` flag remains available for future soft-disable workflows.

---

## Appendix A: Cross-Document Alignment Notes

This section tracks known discrepancies between architecture.md and sibling plan documents. Each item identifies the discrepancy, the canonical definition in this document, and the required update in the sibling document. These items are intended for the sibling document authors to resolve in their next iteration.

### A.1 Discrepancies with implementation-plan.md

All previously outstanding items have been resolved through iteration 16 by updating implementation-plan.md to match the canonical contracts defined in this architecture document.

#### Resolved items (iterations 3–16)

1. **`OutboundMessage.SourceEnvelopeJson` — RESOLVED (iteration 4).** Implementation-plan.md Stage 1.2 now includes `SourceEnvelopeJson` (`string?`) in the `OutboundMessage` record definition, matching architecture.md §3.1.

2. **`ISwarmCommandBus` query signatures — RESOLVED (iteration 4).** Implementation-plan.md Stage 1.3 now defines `QueryStatusAsync(SwarmStatusQuery query, CancellationToken)` and `QueryAgentsAsync(SwarmAgentsQuery query, CancellationToken)` with query-parameter records, matching architecture.md §4.6.

3. **`QuestionRecoverySweep` implementation stage — RESOLVED (iteration 5).** Implementation-plan.md now includes Stage 3.6 (`QuestionRecoverySweep`) with implementation steps covering Gap B detection, `SourceEnvelopeJson` deserialization, `PendingQuestionRecord` backfill, and three test scenarios.

4. **Observability metric names — RESOLVED (iteration 5).** Implementation-plan.md Stage 6.1 now configures the canonical metric set from architecture.md §8.

5. **`IDistributedCache` expiry — RESOLVED (iteration 5).** All documents now aligned on `ExpiresAt + 5 minutes`.

6. **`InboundRecoverySweep` assembly/configuration — RESOLVED (iteration 6).** Architecture.md §2.2 and implementation-plan.md Stage 2.4 both place `InboundRecoverySweep` in `AgentSwarm.Messaging.Worker`.

7. **Latency metric scope wording — RESOLVED (iteration 5).** All documents now use precise 429-only exclusion wording.

8. **`PendingQuestionRecord` post-send persistence — RESOLVED (iteration 10).** Implementation-plan.md Stage 1.4 and Stage 2.3 have been corrected: the sender reads `ProposedDefaultActionId` to *display* the proposed default in the message body; denormalization into `PendingQuestionRecord.DefaultActionId` is owned by `OutboundQueueProcessor` via `IPendingQuestionStore.StoreAsync` (architecture.md §5.2 invariant 1). The test scenario in Stage 3.5 now targets `OutboundQueueProcessor`, not `SendQuestionAsync`.

9. **`IPendingQuestionStore` interface surface — RESOLVED (iteration 10).** Implementation-plan.md Stage 1.3 now includes all eight methods from architecture.md §4.7: `StoreAsync`, `GetAsync`, `GetByTelegramMessageAsync`, `MarkAnsweredAsync`, `MarkAwaitingCommentAsync`, `RecordSelectionAsync`, `GetAwaitingCommentAsync`, and `GetExpiredAsync`.

10. **`IOutboundQueue` assembly placement — RESOLVED (iteration 10).** Both architecture.md (§4.4, §6 assembly map) and implementation-plan.md (Stage 1.4) now place `IOutboundQueue` in `AgentSwarm.Messaging.Abstractions`.

11. **`IMessageSender` return type — RESOLVED (iteration 13).** Implementation-plan.md Stage 1.4 and Stage 2.3 now specify `Task<SendResult>` return types for both `SendTextAsync` and `SendQuestionAsync`, with `SendResult` carrying `TelegramMessageId` (long) — matching the canonical contract in architecture.md §4.12. The rendering boundary is explicit: `TelegramMessageSender` is the sole owner of Telegram-specific rendering.

12. **`RecordSelectionAsync` signature update — RESOLVED (iteration 13).** Implementation-plan.md Stage 1.3 now specifies `RecordSelectionAsync(string questionId, string selectedActionId, string selectedActionValue, long respondentUserId, CancellationToken)` with the `selectedActionValue` parameter — matching architecture.md §4.7.

13. **`StoreAsync` denormalization scope — RESOLVED (iteration 13).** Implementation-plan.md Stage 3.5 now specifies that `PendingQuestionRecord` includes both `DefaultActionId` and `DefaultActionValue` fields, and that `IPendingQuestionStore.StoreAsync` denormalizes both at persistence time by resolving `HumanAction.Value` from `AllowedActions`.

14. **`OutboundQueueProcessor` dependency boundary — RESOLVED (iteration 13).** Implementation-plan.md Stage 4.1 now specifies that `OutboundQueueProcessor` sends via `IMessageSender` (the abstraction defined in Core), not the concrete `TelegramMessageSender`. The `TelegramMessageId` flows from `SendResult` (returned by `IMessageSender`) to `MarkSentAsync` (which consumes it) and `StoreAsync` — the message-ID flow is now consistent with architecture.md §4.12 and §5.2.

15. **Timeout `ActionValue` semantics — RESOLVED (iteration 13).** Implementation-plan.md Stage 3.5 and e2e-scenarios.md timeout scenario now specify that the timeout path publishes `DefaultActionValue` (the resolved `HumanAction.Value`) as `ActionValue`, not `DefaultActionId` directly — consistent with architecture.md §3.1 and §10.3 where `HumanAction.Value` is the canonical `ActionValue` for both interactive and timeout paths.

16. **`PendingQuestion` DTO completeness — RESOLVED (iteration 15).** Implementation-plan.md Stage 1.3 `PendingQuestion` DTO now includes all fields required by the architecture's timeout and RequiresComment flows: `DefaultActionValue` (for timeout event emission), `SelectedActionId` (for tracking operator's button selection), `SelectedActionValue` (for emitting `HumanDecisionEvent.ActionValue` from durable storage), `RespondentUserId` (for correlating follow-up text replies), and `StoredAt` (for deterministic tie-breaking in `GetAwaitingCommentAsync`). These fields match the `PendingQuestionRecord` persistence entity defined in architecture.md §3.1.

17. **`OutboundMessage.Payload` semantics — RESOLVED (iteration 16).** Implementation-plan.md Stage 1.2 previously described `Payload` as "serialized MessengerMessage or AgentQuestion". Updated to match architecture.md §3.1: for `CommandAck`/`StatusUpdate`/`Alert`, `Payload` is pre-rendered Telegram MarkdownV2 text; for `Question`, it is a human-readable preview for debugging/dead-letter inspection — the actual Telegram send is rendered at send time by `TelegramMessageSender.SendQuestionAsync` from `SourceEnvelopeJson`.

18. **`PendingQuestionRecord` index alignment — RESOLVED (iteration 16).** Implementation-plan.md Stage 3.5 previously specified indexes only on `ExpiresAt` and `TelegramMessageId`. Updated to specify the three composite indexes required by architecture.md §3.1 constraints: `(Status, ExpiresAt)` for timeout polling, `(TelegramChatId, RespondentUserId, Status)` for comment correlation, and `(TelegramChatId, TelegramMessageId)` for sweep/timeout message editing. Also updated the `PersistentPendingQuestionStore` indexed lookup description to match.

19. **`QuestionRecoverySweep` `DefaultActionValue` backfill — RESOLVED (iteration 16).** Implementation-plan.md Stage 3.6 previously only mentioned `DefaultActionId` in the backfill step and test scenario. Updated to include `DefaultActionValue` (resolved by looking up the matching `HumanAction.Value` from `AllowedActions`) in both the implementation step and the Gap B test scenario — consistent with architecture.md §3.1 line 251 which requires the sweep to backfill both fields.

### A.2 Discrepancies with tech-spec.md

All items resolved. Tech-spec.md S-3, HC-3, and §8 Acceptance Criteria now use `DefaultActionValue` (the resolved `HumanAction.Value`) as the canonical value for timeout event emission, consistent with architecture.md §3.1 and §10.3.

#### Resolved items (iteration 15)

17. **Timeout semantics `DefaultActionId` → `DefaultActionValue` — RESOLVED (iteration 15).** Tech-spec.md S-3 (line 25), HC-3 (line 67), and §8 Acceptance Criteria (line 126) previously described timeout behavior using only `DefaultActionId`. Updated all three locations to specify that the connector denormalizes into both `PendingQuestionRecord.DefaultActionId` (for display/audit) and `PendingQuestionRecord.DefaultActionValue` (the resolved `HumanAction.Value`, for timeout event emission), and that `QuestionTimeoutService` reads `DefaultActionValue` to publish `HumanDecisionEvent.ActionValue` — consistent with architecture.md §3.1 `PendingQuestionRecord.DefaultActionValue` and §10.3.

### A.3 Discrepancies with e2e-scenarios.md

All items resolved. E2e-scenarios.md question-rendering steps now include both `DefaultActionId` and `DefaultActionValue` in the expected `PendingQuestionRecord` persistence shape, consistent with the timeout scenario (line 111) and architecture.md §3.1.

#### Resolved items (iteration 15)

18. **Question-rendering `PendingQuestionRecord` shape — RESOLVED (iteration 15).** E2e-scenarios.md lines 82 and 99 previously asserted that the connector creates a `PendingQuestionRecord` with only `DefaultActionId "act-1"`. Updated both lines to assert `DefaultActionId "act-1" and DefaultActionValue "approve"` (resolved from `HumanAction.Value` where `ActionId` matches `ProposedDefaultActionId`), consistent with the timeout scenario at line 111 and architecture.md §3.1.
