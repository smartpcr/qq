# Architecture — Telegram Messenger Support (qq-TELEGRAM-MESSENGER-S)

## 1. Problem Statement

The agent swarm (100+ autonomous agents) requires a Telegram-based human interface so that mobile operators can start tasks, answer blocking questions, approve/reject actions, and receive urgent alerts — all without access to a dashboard or CLI. The Telegram connector must slot into the shared `IMessengerConnector` abstraction planned for the Messenger Gateway epic while meeting Telegram-specific requirements for webhook transport, inline buttons, and the 2-second P95 send-latency target.

---

## 2. Component Overview

### 2.1 Component Diagram

```text
┌─────────────────────────────────────────────────────────────────┐
│                    Messenger Gateway (Worker Service)           │
│                                                                 │
│  ┌──────────────────┐   ┌────────────────────┐                  │
│  │ Webhook Endpoint │   │ Long-Poll Receiver  │                 │
│  │ (ASP.NET Core)   │   │ (BackgroundService) │                 │
│  └────────┬─────────┘   └────────┬────────────┘                 │
│           │  Inbound Update       │  Inbound Update             │
│           ▼                       ▼                             │
│  ┌──────────────────────────────────────────────┐               │
│  │         TelegramUpdateRouter                 │               │
│  │  - Webhook secret validation                 │               │
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
│  │  - Durable queue (Channel / persistent store)│               │
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
| **Webhook Endpoint** | `AgentSwarm.Messaging.Telegram` (to be created) | ASP.NET Core controller that receives Telegram `Update` POSTs. Validates the `X-Telegram-Bot-Api-Secret-Token` header, persists the `InboundUpdate` record (including the full raw `Update` JSON payload) for deduplication and crash recovery, and validates the sender against the operator allowlist — all before returning `200 OK`. Command processing proceeds asynchronously after the response. |
| **Long-Poll Receiver** | `AgentSwarm.Messaging.Telegram` (to be created) | `BackgroundService` that calls `GetUpdatesAsync` in a loop. Used in local/dev only; disabled when webhook mode is configured. Shares the same downstream pipeline as the webhook. |
| **TelegramUpdateRouter** | `AgentSwarm.Messaging.Telegram` (to be created) | Central inbound pipeline stage. Deduplicates by `update_id`, checks the operator allowlist, enriches with correlation ID, and dispatches to `CommandDispatcher` or `CallbackQueryHandler`. |
| **CommandDispatcher** | `AgentSwarm.Messaging.Telegram` (to be created) | Maps incoming text commands to strongly typed `SwarmCommand` objects. Delegates callback-query payloads (button presses) to `CallbackQueryHandler` which produces `HumanDecisionEvent`. |
| **AuthZ Service** | `AgentSwarm.Messaging.Core` (to be created) | Validates that the Telegram user ID + chat ID pair is in the authorized operator registry. Returns tenant/workspace binding or rejects the request. |
| **Operator Registry** | `AgentSwarm.Messaging.Core` (to be created) | Persistent map of `TelegramUserId → OperatorIdentity(TenantId, WorkspaceId, Roles)`. Populated via `/start` registration flow and admin configuration. |
| **Swarm Command Bus** | `AgentSwarm.Messaging.Core` (to be created) | Publishes validated, strongly typed commands to the agent swarm orchestrator. Subscribes to agent-originated events (questions, alerts, status) via `SubscribeAsync` and routes them to the correct outbound connector. Both command publishing and event subscription are on the single `ISwarmCommandBus` interface (see §4.6). |
| **OutboundMessageQueue** | `AgentSwarm.Messaging.Core` (to be created) | Durable queue for outbound messages. Provides at-least-once delivery, deduplication by idempotency key, severity-based priority ordering (Critical > High > Normal > Low), retry with configurable exponential back-off (default max 5 attempts), and dead-letter after exhaustion. |
| **TelegramSender** | `AgentSwarm.Messaging.Telegram` (to be created) | Wraps `ITelegramBotClient` from the `Telegram.Bot` library. Formats messages with MarkdownV2, builds `InlineKeyboardMarkup` for agent questions, enforces Telegram rate limits. |
| **AuditLogger** | `AgentSwarm.Messaging.Persistence` (to be created) | Writes an immutable audit record for every human response. Includes message ID, user ID, agent ID, timestamp, and correlation ID. Backed by append-only store. |

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
- Index on `OperatorAlias` — used for `/handoff` target resolution.

**Cardinality examples:**
- 1:1 chat, single workspace: one row per operator.
- 1:1 chat, multiple workspaces: multiple rows with same `(UserId, ChatId)` but different `WorkspaceId`.
- Group chat: rows for each authorized operator in the group, with `ChatType = Group`. Commands are attributed to `TelegramUserId`; unauthorized users in the same group are rejected.

#### InboundUpdate (deduplication + durable work-queue record)

| Field | Type | Description |
|---|---|---|
| `UpdateId` | `long` | Telegram's monotonic `update_id`. Primary key. |
| `RawPayload` | `string` | Full serialized Telegram `Update` JSON. Persisted before returning `200 OK` so that a crash after acknowledgement does not lose the command. On restart, a recovery sweep re-processes any records with `IdempotencyStatus = Received` or `Processing` by deserializing `RawPayload` and feeding it back into the command pipeline. |
| `ReceivedAt` | `DateTimeOffset` | First receipt timestamp. |
| `ProcessedAt` | `DateTimeOffset?` | When processing completed (null = in-flight). |
| `IdempotencyStatus` | `enum` | `Received`, `Processing`, `Completed`, `Failed`. |

#### OutboundMessage

| Field | Type | Description |
|---|---|---|
| `MessageId` | `Guid` | Internal unique identifier. Primary key. |
| `IdempotencyKey` | `string` | Deterministic key preventing duplicate sends. Derivation depends on message origin — see below. |
| `ChatId` | `long` | Target Telegram chat. |
| `Payload` | `string` | Serialized message content (MarkdownV2 text + optional inline keyboard JSON). |
| `Severity` | `enum` | `Critical`, `High`, `Normal`, `Low`. Determines priority queue ordering. |
| `Status` | `enum` | `Pending`, `Sending`, `Sent`, `Failed`, `DeadLettered`. |
| `AttemptCount` | `int` | Number of delivery attempts so far. |
| `NextRetryAt` | `DateTimeOffset?` | Scheduled next attempt. |
| `CreatedAt` | `DateTimeOffset` | Enqueue time. |
| `SentAt` | `DateTimeOffset?` | Telegram confirmation time. |
| `TelegramMessageId` | `int?` | Telegram's returned `message_id` on success. |
| `CorrelationId` | `string` | End-to-end trace ID. |
| `SourceType` | `enum` | `Question`, `Alert`, `StatusUpdate`, `CommandAck`. Discriminator for origin type. |
| `SourceId` | `string?` | The `QuestionId` for question messages; alert rule ID for alerts; command correlation ID for acks. Null only for fire-and-forget status broadcasts. |
| `ErrorDetail` | `string?` | Last error message for diagnostics. |

**Idempotency key derivation by `SourceType`:**

| SourceType | IdempotencyKey formula | Example |
|---|---|---|
| `Question` | `q:{AgentId}:{QuestionId}` | `q:build-agent-3:Q-42` |
| `Alert` | `a:{AgentId}:{CorrelationId}` | `a:monitor-1:trace-abc` |
| `StatusUpdate` | `s:{AgentId}:{CorrelationId}` | `s:deploy-2:trace-def` |
| `CommandAck` | `c:{CorrelationId}` | `c:trace-ghi` |

The `UNIQUE` constraint on `IdempotencyKey` in the outbox table prevents duplicate enqueue regardless of message origin. For question messages, the key includes `QuestionId` so re-delivery of the same agent question is deduplicated. For non-question messages (alerts, acks, status updates), the key is derived from the correlation context, ensuring each distinct event produces exactly one outbound message.

#### AgentQuestion (shared model — to be defined in planned `AgentSwarm.Messaging.Abstractions`)

The shared `AgentQuestion` model represents a blocking question from an agent to a human operator. The shared model does **not** include a `DefaultAction` property; the proposed default action is carried as sidecar metadata via `ProposedDefaultActionId` in the `AgentQuestionEnvelope` (see below). This separation keeps the shared model focused on the question itself, while routing/context metadata (including the default action and connector-specific routing hints) lives in the envelope.

| Field | Type | Description |
|---|---|---|
| `QuestionId` | `string` | Unique question identifier. |
| `AgentId` | `string` | Originating agent. |
| `TaskId` | `string` | Associated work item / task. |
| `Title` | `string` | Short summary. |
| `Body` | `string` | Full context for the operator. |
| `Severity` | `string` | `Critical`, `High`, `Normal`, `Low`. |
| `AllowedActions` | `HumanAction[]` | Buttons to render. |
| `ExpiresAt` | `DateTimeOffset` | Timeout deadline. |
| `CorrelationId` | `string` | Trace ID. |

#### AgentQuestionEnvelope (shared model — to be defined in planned `AgentSwarm.Messaging.Abstractions`)

Wraps an `AgentQuestion` with routing and context metadata. The envelope is the unit of transport through `IMessengerConnector.SendQuestionAsync` (§4.1) and `IPendingQuestionStore.StoreAsync`.

| Field | Type | Description |
|---|---|---|
| `Question` | `AgentQuestion` | The question payload. |
| `ProposedDefaultActionId` | `string?` | The `ActionId` from `AllowedActions` to apply automatically on timeout. When `null`, the question expires with `ActionValue = "__timeout__"`. Carried as sidecar metadata, not a property of the shared question model. |
| `RoutingMetadata` | `Dictionary<string, string>` | Extensible key-value pairs for connector-specific routing (e.g., `TelegramChatId`). |

> **Default action flow.** When the Telegram connector renders an `AgentQuestionEnvelope` as an inline-keyboard message, it reads `ProposedDefaultActionId` from the envelope. When present, the connector displays the proposed default in the message body (e.g., "Default action if no response: Approve") and denormalizes the `ActionId` into `PendingQuestionRecord.DefaultActionId` for efficient timeout polling (see below). This enables `QuestionTimeoutService` to poll for expired questions and resolve the default via `IDistributedCache` without re-fetching the full envelope. When `ProposedDefaultActionId` is `null`, `PendingQuestionRecord.DefaultActionId` is `null`, the question expires with a `__timeout__` action value, and no automatic decision is applied.
>
> **Cross-doc status — DefaultAction placement RESOLVED.** All sibling documents are now aligned on the sidecar envelope model. The shared `AgentQuestion` model does **not** include a `DefaultAction` property; the proposed default action is carried as `ProposedDefaultActionId` on the `AgentQuestionEnvelope`. Specifically: this architecture (§3.1) defines the sidecar approach; implementation-plan.md Stage 1.2 follows it; e2e-scenarios.md line 646 confirms "All sibling documents are aligned on the sidecar envelope model" and uses `AgentQuestionEnvelope.ProposedDefaultActionId` throughout its scenario fixtures (lines 59–62, 78, 95, 106–107, 117–118); tech-spec.md §10 alignment table references this architecture's §3.1 definition. The behavioral contract is: the Telegram connector reads `ProposedDefaultActionId` from the envelope, denormalizes it into `PendingQuestionRecord.DefaultActionId`, displays the default in the message body, and applies it on timeout. No remaining divergence.

#### PendingQuestionRecord (Telegram-specific — to be defined in planned `AgentSwarm.Messaging.Telegram`)

Tracks an `AgentQuestion` that has been sent to an operator and is awaiting a response. Created when the Telegram connector renders an `AgentQuestion` as an inline-keyboard message.

| Field | Type | Description |
|---|---|---|
| `QuestionId` | `string` | Foreign key to the `AgentQuestion.QuestionId` this record tracks. Primary key. |
| `ChatId` | `long` | Telegram chat the question was sent to. |
| `TelegramMessageId` | `int` | Telegram `message_id` of the sent inline-keyboard message. |
| `DefaultActionId` | `string?` | Denormalized from `AgentQuestionEnvelope.ProposedDefaultActionId` at question-send time. Stored here so that `QuestionTimeoutService` can poll for expired questions and resolve the default via `IDistributedCache` without re-fetching the full envelope. When present, the timeout service resolves the full `HumanAction` and applies it automatically. When `null`, the question expires with `ActionValue = "__timeout__"`. |
| `ExpiresAt` | `DateTimeOffset` | Copied from `AgentQuestion.ExpiresAt` for efficient timeout polling. |
| `Status` | `enum` | `Pending`, `Answered`, `TimedOut`. |
| `CreatedAt` | `DateTimeOffset` | When the question was sent to Telegram. |

**Constraints:**
- `UNIQUE (QuestionId)` — one pending record per question.
- Index on `(Status, ExpiresAt)` — used by `QuestionTimeoutService` to poll for expired questions.

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

#### AuditRecord

| Field | Type | Description |
|---|---|---|
| `Id` | `Guid` | Primary key. |
| `MessageId` | `string` | Telegram `message_id` or internal ID. |
| `UserId` | `string` | Telegram user ID. |
| `OperatorBindingId` | `string` | FK to `OperatorBinding.Id`. Unambiguously identifies the operator binding (a user may have multiple bindings across workspaces). |
| `TelegramChatId` | `long` | Telegram chat ID where the action occurred. Disambiguates multi-workspace and group-chat contexts. |
| `WorkspaceId` | `string` | Tenant/workspace the action applies to. Derived from the `OperatorBinding`. |
| `AgentId` | `string` | Target or source agent. |
| `Action` | `string` | Command or decision value. |
| `Timestamp` | `DateTimeOffset` | UTC. |
| `CorrelationId` | `string` | Trace ID. |
| `TenantId` | `string` | Operator's tenant. |
| `RawPayload` | `string` | Serialized original message for forensics. |

#### TaskOversight

Tracks which operator currently has oversight of which task. Created/updated by the `/handoff` command handler. Also used by the orchestrator subscription filter to route agent events to the correct operator.

| Field | Type | Description |
|---|---|---|
| `TaskId` | `string` | Primary key. The task being overseen. |
| `OperatorBindingId` | `string` | FK to `OperatorBinding.Id`. The operator who currently has oversight. |
| `AssignedAt` | `DateTimeOffset` | When oversight was assigned or last transferred. |
| `AssignedBy` | `string` | The operator who initiated the handoff (their `OperatorBinding.Id`). |
| `CorrelationId` | `string` | Trace ID for the handoff action. |

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
AuditRecord     *──1 OperatorBinding       (via AuditRecord.OperatorBindingId = OperatorBinding.Id;
                                            unambiguous FK — not just UserId, which can have multiple bindings)
AuditRecord     *──0..1 AgentQuestion      (via AuditRecord.CorrelationId; joins through the
                                            correlation context, not a direct FK)
TaskOversight   *──1 OperatorBinding       (via TaskOversight.OperatorBindingId = OperatorBinding.Id;
                                            tracks which operator currently oversees the task)
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

### 4.2 ITelegramUpdatePipeline (internal)

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

Both the webhook controller and long-poll receiver first map the raw Telegram `Update` to a `MessengerEvent` using `TelegramUpdateMapper`, then pass the result to `ProcessAsync`. The `PipelineResult` return type (to be defined in the planned `AgentSwarm.Messaging.Abstractions` project, per implementation-plan Stage 1.3) provides structured outcome information: `Handled = false` for duplicates or unauthorized events, `ResponseText` for any reply to send back to the user, and `CorrelationId` for tracing. This boundary aligns with implementation-plan Stage 2.5 and keeps `ITelegramUpdatePipeline` transport-agnostic — the pipeline never sees a Telegram-specific `Update` object.

### 4.3 IOperatorRegistry

```csharp
public interface IOperatorRegistry
{
    Task<IReadOnlyList<OperatorBinding>> GetBindingsAsync(long telegramUserId, long chatId, CancellationToken ct);
    Task<IReadOnlyList<OperatorBinding>> GetAllBindingsAsync(long telegramUserId, CancellationToken ct);
    Task<OperatorBinding?> GetByAliasAsync(string operatorAlias, CancellationToken ct);
    Task RegisterAsync(long telegramUserId, long chatId, string tenantId, string workspaceId, CancellationToken ct);
    Task<bool> IsAuthorizedAsync(long telegramUserId, long chatId, CancellationToken ct);
}
```

**Multi-workspace disambiguation:** `GetBindingsAsync(userId, chatId)` returns **all** `OperatorBinding` rows matching the (user, chat) pair — one per workspace. Callers handle cardinality as follows:

- **Zero results:** The user+chat pair has no binding → command is rejected (unauthorized).
- **Exactly one result:** Unambiguous → command proceeds with that binding's `TenantId`/`WorkspaceId`.
- **Multiple results:** The user has bindings in multiple workspaces for this chat → the `CommandDispatcher` presents an inline keyboard listing the available workspaces and waits for the operator to select one (per e2e-scenarios workspace disambiguation flow). The selected workspace is cached for the session to avoid repeated prompts.

`GetAllBindingsAsync(userId)` returns all bindings across all chats for a user, used for administrative queries and `/status` across workspaces. `IsAuthorizedAsync(userId, chatId)` is a fast-path check returning `true` if at least one active binding exists for the pair — used by the allowlist gate before command processing.

### 4.4 IOutboundQueue

```csharp
public interface IOutboundQueue
{
    Task EnqueueAsync(OutboundMessage message, CancellationToken ct);
    Task<OutboundMessage?> DequeueAsync(CancellationToken ct);
    Task MarkSentAsync(Guid messageId, int telegramMessageId, CancellationToken ct);
    Task MarkFailedAsync(Guid messageId, string error, CancellationToken ct);
    Task DeadLetterAsync(Guid messageId, CancellationToken ct);
}
```

### 4.5 IAuditLog

```csharp
public interface IAuditLog
{
    Task RecordAsync(AuditRecord record, CancellationToken ct);
}
```

### 4.6 ISwarmCommandBus (shared abstraction — to be defined in planned `AgentSwarm.Messaging.Abstractions`)

```csharp
public interface ISwarmCommandBus
{
    Task PublishCommandAsync(SwarmCommand command, CancellationToken ct);
    Task<SwarmStatusSummary> QueryStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<AgentInfo>> QueryAgentsAsync(CancellationToken ct);
    IAsyncEnumerable<SwarmEvent> SubscribeAsync(string tenantId, CancellationToken ct);
}
```

The Telegram connector publishes commands (task creation, approvals, pauses) via `PublishCommandAsync` and queries swarm state via `QueryStatusAsync` and `QueryAgentsAsync`. These three outbound methods align with implementation-plan.md Stage 1.3.

**Event ingress (agent → connector):** The connector must also receive inbound events from the swarm (agent questions, status updates, alerts) to render them in Telegram. Event ingress is handled via `SubscribeAsync` on the same `ISwarmCommandBus` interface, keeping a single swarm integration surface consistent with implementation-plan.md Stage 1.3, which defines `ISwarmCommandBus` as the sole port to the agent swarm orchestrator.

`SwarmEvent` is a discriminated union (or base class with subtypes) covering `AgentQuestionEvent`, `AgentAlertEvent`, and `AgentStatusUpdateEvent`. The Telegram connector's `BackgroundService` calls `SubscribeAsync` at startup for each active tenant and processes events as they arrive — rendering questions as inline-keyboard messages, alerts as priority text, and status updates as informational messages. The transport backing this subscription (in-process `Channel<T>`, message broker, gRPC stream) is outside this story's scope; the interface abstracts it.

> **Cross-doc alignment:** Implementation-plan.md Stage 1.3 now defines all four methods on `ISwarmCommandBus`: `PublishCommandAsync`, `QueryStatusAsync`, `QueryAgentsAsync`, and `SubscribeAsync(string tenantId, CancellationToken)` returning `IAsyncEnumerable<SwarmEvent>`. All sibling documents are aligned on this interface contract.

---

## 5. End-to-End Sequence Flows

### 5.1 Scenario: Human sends `/ask build release notes for Solution12`

```text
Human (Telegram)                Webhook Endpoint       UpdateRouter          CommandDispatcher       AuthZ       SwarmCommandBus       Orchestrator
      │                              │                      │                      │                   │               │                    │
      │──POST /webhook──────────────▶│                      │                      │                   │               │                    │
      │                              │──validate secret─────│                      │                   │               │                    │
      │                              │──persist InboundUpdate (update_id)──────────▶│                   │               │                    │
      │                              │   (UNIQUE constraint; INSERT fails ──▶ duplicate ──▶ return 200)│               │                    │
      │                              │──check allowlist────▶│──IsAuthorized?──────▶│                   │               │                    │
      │                              │                      │◀──yes + binding──────│                   │               │                    │
      │◀─────200 OK─────────────────│                      │                      │                   │               │                    │
      │                              │        ┌─────────────┤  (async boundary)    │                   │               │                    │
      │                              │        │ background  │                      │                   │               │                    │
      │                              │        ▼ processing  │                      │                   │               │                    │
      │                              │                      │──parse "/ask ..."───▶│                   │               │                    │
      │                              │                      │                      │──CreateTaskCmd───▶│──publish──────▶│                    │
      │                              │                      │                      │                   │               │──create work item─▶│
      │                              │                      │                      │                   │               │                    │
      │                              │                      │                      │◀──ack + taskId───│◀──────────────│                    │
      │                              │                      │                      │                   │               │                    │
      │◀──"Task created: #T-42"─────│◀──────enqueue reply──│◀──────────────────────│                   │               │                    │
```

**Key invariants:**
1. Webhook returns `200 OK` **after** deduplication and authorization but **before** command processing begins. The endpoint performs three synchronous steps before responding: (a) validates the `X-Telegram-Bot-Api-Secret-Token` header, (b) persists the `InboundUpdate` record — including the full `RawPayload` (serialized Telegram `Update` JSON) — with the `update_id` as primary key; if the `UNIQUE` constraint fails, the update is a duplicate and the endpoint returns `200 OK` immediately without further processing, (c) validates the sender against the operator allowlist. Only after these durable steps does the endpoint return `200 OK`. This eliminates the command-loss window: if the process crashes after Telegram receives `200`, the `InboundUpdate` record (with full `RawPayload`) is already persisted. On restart, a recovery sweep queries for records with `IdempotencyStatus = Received` or `Processing`, deserializes their `RawPayload`, and re-feeds them into the command pipeline for idempotent re-processing. All subsequent steps (command parsing, publishing) happen asynchronously in a background processor.
2. `update_id` is persisted **before** `200 OK`; duplicate POSTs are dropped at the database constraint level.
3. Authorization check runs before `200 OK` is returned — unauthorized commands are rejected synchronously.
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
      │                    │                     │  + PendingQuestion│                  │                   │
      │                    │                     │──enqueue──────────▶│                  │                   │
      │                    │                     │                   │──dequeue────────▶│                   │
      │                    │                     │                   │                  │──sendMessage──────▶│
      │                    │                     │                   │                  │  [Approve][Reject] │
      │                    │                     │                   │◀─markSent────────│                   │
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
1. The question includes `Severity`, `ExpiresAt`, `AllowedActions` rendered as inline keyboard buttons, and the proposed default action (if any). The default action is carried as sidecar metadata in `AgentQuestionEnvelope.ProposedDefaultActionId` (see §3.1). When the connector builds the inline keyboard, it reads `ProposedDefaultActionId` from the envelope and creates a `PendingQuestionRecord` with `DefaultActionId` denormalized from that field for efficient timeout polling.
2. The `callback_data` field carries `QuestionId:ActionId` (≤ 64 bytes). `ActionId` is a short key that maps to the full `HumanAction` payload stored server-side in `IDistributedCache` (see tech-spec D-3). The cache entry is written when the inline keyboard is built and expires at `AgentQuestion.ExpiresAt`.
3. Button press produces a strongly typed `HumanDecisionEvent` — never a raw string.
4. The `answerCallbackQuery` call removes the loading spinner on the operator's device.
5. Audit record is written with `MessageId`, `UserId`, `AgentId`, timestamp, and `CorrelationId`.
6. If no operator responds before `ExpiresAt`, a timeout handler reads `PendingQuestionRecord.DefaultActionId` (denormalized from `AgentQuestionEnvelope.ProposedDefaultActionId` at send time), resolves the corresponding `HumanAction`, fires a `HumanDecisionEvent` with that action, and updates the Telegram message to reflect the timeout.

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
- Dead-letter record preserves full message payload, all attempt timestamps, and error details
- Alert is sent to a secondary notification channel (ops Telegram group or fallback messenger)

> **Cross-doc retry default alignment:** This architecture, implementation-plan.md (Stage 4.2: `RetryPolicy.MaxAttempts` default `5`), and e2e-scenarios.md (Background: "max 5 attempts", dead-letter scenario: "dead-letter after attempt 5") are **aligned** on `MaxRetries = 5`.

### 5.4 Scenario: Duplicate webhook delivery (idempotency)

```text
Telegram Cloud          Webhook Endpoint        DB (InboundUpdate)
      │                       │                      │
      │──POST update_id=999──▶│                      │
      │                       │──INSERT update 999──▶│
      │                       │◀──OK (new)──────────│
      │                       │──validate allowlist──│
      │◀──200 OK─────────────│                      │
      │                       │──async: process cmd─▶ ...
      │                       │                      │
      │  (Telegram retries — network glitch)         │
      │──POST update_id=999──▶│                      │
      │                       │──INSERT update 999──▶│
      │                       │◀──CONFLICT (dup)────│
      │◀──200 OK─────────────│──DROP (no-op)        │
```

**Key invariants:**
1. Endpoint always returns `200 OK` regardless of duplicate status — prevents Telegram from retrying further.
2. Deduplication uses `update_id` as a natural idempotency key with a `UNIQUE` constraint. The `INSERT` happens **before** `200 OK`, so a crash after Telegram receives `200` does not lose the record.
3. The deduplication window is at least 24 hours; records older than that are pruned.

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
> 3. Resolves the target operator (`@operator-alias`) via `IOperatorRegistry.GetByAliasAsync`. If the alias is not registered, returns an error.
> 4. Transfers oversight by creating or updating a `TaskOversight` record (see below) mapping the task to the target operator.
> 5. Notifies both operators — the sender receives confirmation, the target receives a handoff notification with task context.
> 6. Persists an audit record with handoff details (task ID, source operator, target operator, timestamp, `CorrelationId`).
> 7. Returns error for invalid task ID, unregistered target operator, or missing arguments with usage help.
>
> **`TaskOversight` entity:** Defined in §3.1 above. A lightweight entity mapping `(TaskId, OperatorBindingId)` to track which operator currently has oversight of which task. The `/handoff` handler creates or updates this record, and the orchestrator subscription filter reads it to route agent events to the correct operator.
>
> **Cross-doc alignment note:** All four sibling documents are now aligned on full oversight transfer as the decided `/handoff` behavior. Implementation-plan.md Stage 3.2 specifies `HandoffCommandHandler` with full oversight transfer including validation, target resolution via `IOperatorRegistry.GetByAliasAsync`, `TaskOversight` record mutation, dual-operator notification, and audit. E2e-scenarios.md tests the full transfer flow. Tech-spec D-4 documents the decision. The `OperatorBinding.OperatorAlias` unique index supports alias resolution for the target operator lookup.

---

## 6. Assembly Map (Proposed)

> **Note:** The following projects do not yet exist in the repository. They are the planned assembly structure to be created during implementation. No `.sln`, `.csproj`, or `src/` tree currently exists.

```text
AgentSwarm.Messaging.sln  (to be created)
│
├── AgentSwarm.Messaging.Abstractions     ← IMessengerConnector, AgentQuestion,
│                                            HumanDecisionEvent, HumanAction,
│                                            MessengerMessage, MessengerEvent,
│                                            ISwarmCommandBus
│
├── AgentSwarm.Messaging.Core             ← IOutboundQueue, IOperatorRegistry,
│                                            IAuditLog, AuthZ service,
│                                            CommandDispatcher base,
│                                            RetryPolicy, DeduplicationService
│
├── AgentSwarm.Messaging.Telegram         ← TelegramMessengerConnector,
│                                            TelegramUpdateRouter,
│                                            TelegramCommandDispatcher,
│                                            CallbackQueryHandler,
│                                            TelegramSender,
│                                            WebhookController,
│                                            LongPollReceiver,
│                                            PendingQuestionRecord,
│                                            QuestionTimeoutService,
│                                            TelegramOptions (config POCO)
│
├── AgentSwarm.Messaging.Persistence      ← AuditLogger, OutboundQueueStore,
│                                            InboundUpdateStore,
│                                            OperatorBindingStore,
│                                            EF Core DbContext + migrations
│
├── AgentSwarm.Messaging.Worker           ← ASP.NET Core host,
│                                            DI registration,
│                                            Health checks,
│                                            OpenTelemetry bootstrap
│
└── AgentSwarm.Messaging.Tests            ← Unit + integration tests
```

---

## 7. Configuration & Secrets

| Setting | Source | Notes |
|---|---|---|
| `Telegram:BotToken` | Azure Key Vault / K8s secret | Never logged. Loaded at startup via `ISecretClient`. |
| `Telegram:WebhookUrl` | App configuration | Public HTTPS URL; set to empty to enable long-poll mode. |
| `Telegram:SecretToken` | Key Vault | Header value Telegram sends with each webhook POST; validated by `WebhookController`. |
| `Telegram:AllowedUserIds` | App configuration | Comma-separated allowlist of Telegram **user IDs** authorized to register via `/start`. See "Allowlist Authorization Model" below for how user IDs, chat IDs, and tenant/workspace bindings interact. |
| `Telegram:RateLimits:GlobalPerSecond` | App configuration | Default `30`. |
| `Telegram:RateLimits:PerChatPerMinute` | App configuration | Default `20`. |
| `OutboundQueue:MaxRetries` | App configuration | Default `5` (aligned with implementation-plan Stage 4.2 `RetryPolicy.MaxAttempts` default of `5` and e2e-scenarios.md "max 5 attempts"). |
| `OutboundQueue:BaseRetryDelaySeconds` | App configuration | Default `2`. |
| `OutboundQueue:ProcessorConcurrency` | App configuration | Default `10`. Number of concurrent send workers. |
| `OutboundQueue:MaxQueueDepth` | App configuration | Default `5000`. Backpressure threshold; low-severity messages are dead-lettered when exceeded. |

### 7.1 Allowlist Authorization Model

Authorization uses a **two-tier model**: a static configuration-time allowlist gates onboarding, and dynamic runtime bindings gate command execution.

| Tier | What is checked | Where it lives | When it is checked |
|---|---|---|---|
| **Tier 1: Onboarding allowlist** | `Telegram:AllowedUserIds` — a static list of Telegram **user IDs** (not chat IDs). | App configuration (environment variable, appsettings, or config provider). | At `/start` time only. If the user's Telegram user ID is not in this list, `/start` is rejected and no `OperatorBinding` is created. |
| **Tier 2: Runtime bindings** | `OperatorBinding` records — each row contains **both** `TelegramUserId` and `TelegramChatId`, plus `TenantId`, `WorkspaceId`, and `Roles`. | Persistence store (database). | On every inbound command (after deduplication). The `AuthZ Service` calls `IOperatorRegistry.IsAuthorizedAsync(userId, chatId)` which queries the `OperatorBinding` table for an active row matching both the user ID and the chat ID. If no matching binding exists, the command is rejected. |

**Key design decisions:**
- **Chat IDs are not independently allow-listed in configuration**, but the story's "validate chat/user allowlist" requirement is fully satisfied by the two-tier model. Here is why: the story requires that commands from unauthorized chats/users are rejected. Tier 2 accomplishes this — every inbound command is checked against `OperatorBinding` records, which store both `TelegramUserId` and `TelegramChatId`. A command from an unregistered (user, chat) pair is rejected, even if the user has a binding in a different chat. This is functionally equivalent to maintaining a separate `AllowedChatIds` configuration list, but more secure: chat authorization is always tied to a specific (user, chat, workspace) triple created through the auditable `/start` onboarding flow, rather than a static config list that could drift from reality. In effect, the `OperatorBinding` table **is** the chat/user allowlist — it is just persisted in the database rather than in configuration.
- **Group chat attribution:** In group chats, commands are attributed to the sending `TelegramUserId` (the `from.id` field on the Telegram `Update`), not the group's `TelegramChatId`. This means each group member must have their own `OperatorBinding` — an unauthorized user in an authorized group is rejected (per tech-spec S-5).
- **Multi-workspace:** An operator may have `OperatorBinding` rows in multiple workspaces. When a command is ambiguous (the user has bindings in multiple workspaces for the same chat), the bot presents an inline keyboard for workspace disambiguation (per e2e-scenarios).

---

## 8. Observability

| Signal | Implementation |
|---|---|
| **Traces** | OpenTelemetry `ActivitySource("AgentSwarm.Messaging.Telegram")`. Every inbound update and outbound send starts a span carrying `CorrelationId` as a baggage item. |
| **Metrics** | Counters: `telegram.updates.received`, `telegram.messages.sent`, `telegram.messages.dead_lettered`, `telegram.commands.processed`, `telegram.messages.backpressure_dlq`. Histograms: `telegram.send.latency_ms` (primary, all-inclusive; all sends from enqueue to HTTP 200 regardless of attempt number or rate-limit holds — capacity planning), `telegram.send.first_attempt_latency_ms` (acceptance gate; first-attempt, non-rate-limited sends only — P95 ≤ 2 s target; see §10.4), `telegram.send.retry_latency_ms` (diagnostic; retried sends), `telegram.send.rate_limited_wait_ms` (diagnostic; 429 backoff duration). |
| **Logs** | Structured logging via `ILogger<T>`. Correlation ID included in every log scope. Bot token is excluded from all log output via a custom redaction enricher. |
| **Health** | `/healthz` endpoint (aligning with implementation-plan and Dockerfile `HEALTHCHECK`). Aggregates checks: Telegram API reachable (`getMe`), outbound queue depth < threshold, dead-letter queue depth < configurable threshold, database connectivity. Returns JSON detail output with per-check status. |

---

## 9. Security Model

1. **Webhook validation** — Every inbound POST must carry the `X-Telegram-Bot-Api-Secret-Token` header matching the configured `Telegram:SecretToken`. Requests with a missing or invalid secret token return `403 Forbidden`. This aligns with e2e-scenarios and implementation-plan which both specify HTTP 403 for webhook secret validation failures.
2. **Operator allowlist** — `TelegramUserId` and `ChatId` are checked against `OperatorBinding` records. Unregistered users receive a generic "not authorized" reply and the attempt is logged.
3. **Role enforcement** — `/approve` and `/reject` require the `Approver` role. `/pause` and `/resume` require the `Operator` role. `/start` is open to any user: if the user's Telegram ID is in the pre-configured allowlist, the operator binding is created/updated immediately; if not, the user receives an "unauthorized" reply and the attempt is logged. No admin approval step is required for allowlisted users (see e2e-scenarios and implementation-plan).
4. **Secret isolation** — Bot token is loaded once at startup from Key Vault into an in-memory `SecureString`-equivalent. It is never serialized, logged, or exposed via health endpoints.
5. **Rate limiting** — Inbound commands are rate-limited per user (10 commands/minute) to prevent abuse from a compromised account.

---

## 10. Cross-Cutting Concerns

### 10.1 Correlation ID Propagation

Every inbound update generates or adopts a `CorrelationId` (UUID v7 for time-ordering). The ID flows through:
- `TelegramUpdateRouter` → `CommandDispatcher` → `SwarmCommandBus` (outbound to orchestrator)
- `SwarmCommandBus` (inbound event) → `OutboundMessageQueue` → `TelegramSender`
- All `AuditRecord` entries
- All OpenTelemetry spans (as `trace.correlation_id` attribute)

### 10.2 Receive-Mode Switching

The connector supports two receive modes controlled by configuration:

| Mode | When | Mechanism |
|---|---|---|
| **Webhook** | Production, staging | ASP.NET Core controller at `/api/telegram/webhook`. On startup, calls `setWebhook` with the configured URL and secret token. |
| **Long polling** | Local dev, CI | `LongPollReceiver` BackgroundService calls `getUpdates` in a loop with 30-second timeout. On startup, calls `deleteWebhook` to avoid conflicts. |

Both modes feed into the same `ITelegramUpdatePipeline`, so all downstream logic — deduplication, authorization, command dispatch — is mode-agnostic.

### 10.3 Question Timeout Handling

A `QuestionTimeoutService` (BackgroundService) polls for `PendingQuestionRecord` entries with `Status = Pending` past their `ExpiresAt`. When a question times out:
1. Reads `PendingQuestionRecord.DefaultActionId` (denormalized from `AgentQuestionEnvelope.ProposedDefaultActionId` at send time). If present, resolves the full `HumanAction` from `IDistributedCache` and publishes a `HumanDecisionEvent` with that action value. If absent (`null`), publishes a `HumanDecisionEvent` with `ActionValue = "__timeout__"` so the agent is notified of timeout without an automatic decision.
2. Updates the original Telegram message (using `PendingQuestionRecord.TelegramMessageId`) to indicate the timeout ("⏰ Timed out — default action applied: *skip*" or "⏰ Timed out — no default action").
3. Sets `PendingQuestionRecord.Status = TimedOut`.
4. Writes an audit record noting the timeout.

### 10.4 Performance: Concurrency, Backpressure, and Rate Limiting

The 2-second P95 send-latency target and the 100+ agent burst requirement demand explicit concurrency, priority queuing, and backpressure design.

#### P95 Metric Definition

The story requires "P95 send latency under 2 seconds after event is queued." This architecture defines the following latency metrics, using the canonical naming convention shared across all sibling documents:

> **`telegram.send.latency_ms`** (primary, all-inclusive) = elapsed time from `OutboundMessage.CreatedAt` (enqueue instant) to Telegram Bot API returning HTTP 200, measured for **all** messages regardless of attempt number or rate-limit holds. This all-inclusive metric captures the true end-to-end delivery experience including retries and rate-limit waits, useful for capacity planning and operational dashboards.
>
> **`telegram.send.first_attempt_latency_ms`** (acceptance gate) = elapsed time from `OutboundMessage.CreatedAt` (enqueue instant) to Telegram Bot API returning HTTP 200, measured **only** for messages that succeed on their **first delivery attempt** and are **not** waiting behind a 429 rate-limit hold. This metric isolates the system's inherent processing latency (queue → dequeue → format → send → HTTP 200) and is the one the **P95 ≤ 2 s** acceptance criterion applies to. Under normal operating conditions (no rate-limit hits, first-attempt success), the vast majority of sends are captured by this metric.
>
> **`telegram.send.retry_latency_ms`** (diagnostic) = latency for messages that required one or more retries, measured from original enqueue to eventual success.
>
> **`telegram.send.rate_limited_wait_ms`** (diagnostic) = time spent waiting during 429 backoff, tracked separately for operational diagnostics.

The 2-second P95 target applies to `telegram.send.first_attempt_latency_ms`, which covers first-attempt, non-rate-limited sends only. Per operator clarification (answer to `p95-metric-scope`), the P95 measurement includes only first-attempt, non-rate-limited sends — not retried sends or rate-limited wait time. This scoping ensures the acceptance criterion measures the system's intrinsic processing capability rather than external factors (Telegram rate limits, transient failures). The all-inclusive `telegram.send.latency_ms` primary metric provides visibility into the operator's actual end-to-end experience including retries and rate-limit waits.

**Acceptance SLO under burst:** The story requires "P95 send latency under 2 seconds after event is queued" and "support burst alerts from 100+ agents without message loss." The metric contract is:

- **P95 ≤ 2 s** applies to `telegram.send.first_attempt_latency_ms` — first-attempt, non-rate-limited sends, all severities.
- **Zero message loss** — every message is either delivered or dead-lettered with a traceable reason.

Because `telegram.send.first_attempt_latency_ms` captures only sends that succeed on first attempt without rate-limit holds, the P95 target is a measure of intrinsic system latency (queue dwell + HTTP round-trip), not of Telegram API rate-limit behavior. The architecture meets this through:

| Condition | Behavior | Mechanism |
|---|---|---|
| **Normal load** (queue depth < 100, no rate-limiting) | P95 `telegram.send.first_attempt_latency_ms` ≤ 2 s for all severities. Nearly all sends are first-attempt/non-rate-limited, so `telegram.send.latency_ms` (all-inclusive) closely tracks the acceptance metric. | 10 concurrent workers drain the queue faster than it fills. |
| **Burst load** (100+ agents, 1000+ messages) | P95 `telegram.send.first_attempt_latency_ms` ≤ 2 s for all severities that are first-attempt/non-rate-limited. Zero message loss. | Priority queuing dispatches Critical/High first. At 30 msg/s sustained, 1000 messages drain in ~34 s. Messages that encounter rate limits or require retries are excluded from the acceptance metric by definition and tracked via `telegram.send.latency_ms` (all-inclusive). |
| **Beyond capacity envelope** (sustained inflow > 30 msg/s) | Zero message loss; latency degrades gracefully. | Low-severity messages are backpressure-DLQ'd when queue depth exceeds `MaxQueueDepth` (5000). All other messages are delivered. |

Under **normal operating conditions**, the vast majority of sends are first-attempt and non-rate-limited, so the P95 of `telegram.send.first_attempt_latency_ms` directly reflects the end-to-end experience for all severities, and `telegram.send.latency_ms` (all-inclusive) closely matches it.

Under **extreme burst** (1000+ simultaneous messages), the priority queuing design ensures Critical/High messages (blocking questions, approval requests) are dequeued first and reach the Telegram API within 2 seconds. Normal/Low messages are also delivered without loss but may experience queue delay; sends that are first-attempt and non-rate-limited still meet the P95 target regardless of severity. The `telegram.send.latency_ms` all-inclusive metric captures the full end-to-end experience (including retries and rate-limit waits) for capacity planning.

> **Cross-doc status — metric naming ALIGNED.** All sibling documents now use the same canonical naming convention: `telegram.send.latency_ms` is the **all-inclusive** primary monitoring metric (all sends regardless of attempt number or rate-limit holds); `telegram.send.first_attempt_latency_ms` is the **acceptance gate** metric (first-attempt, non-rate-limited sends only; P95 ≤ 2 s). This convention matches tech-spec.md HC-4/§10 and e2e-scenarios.md metrics table/footer. The behavioral contract is consistent across all documents: P95 ≤ 2 s applies to first-attempt, non-rate-limited sends; all-inclusive latency is tracked separately for capacity planning. Additional diagnostics: `telegram.send.retry_latency_ms` (retried sends) and `telegram.send.rate_limited_wait_ms` (429 backoff duration). Backpressure dead-letter counter: `telegram.messages.backpressure_dlq` (canonical name across all sibling docs).

#### Queue Processor Concurrency

The `OutboundQueueProcessor` runs as a `BackgroundService` with configurable concurrency:

| Setting | Default | Description |
|---|---|---|
| `OutboundQueue:ProcessorConcurrency` | `10` | Number of concurrent dequeue-and-send workers. Each worker independently dequeues, sends via `TelegramSender`, and marks sent/failed. |
| `OutboundQueue:MaxQueueDepth` | `5000` | Backpressure threshold. When the durable queue exceeds this depth, `EnqueueAsync` applies backpressure: `Low`-severity messages are **dead-lettered immediately** (moved to the dead-letter queue with reason `backpressure:queue_depth_exceeded`) and a `telegram.messages.backpressure_dlq` counter is emitted. `Normal`, `High`, and `Critical` severity messages are always accepted. This preserves the zero-loss guarantee — no message is silently discarded; every message is either delivered or dead-lettered with a traceable reason. |

#### Priority Queuing

The outbound queue implements a **severity-based priority order**: `Critical` > `High` > `Normal` > `Low`. The `OutboundQueueProcessor` always dequeues the highest-severity pending message first. This ensures that under burst conditions, time-critical messages (blocking questions, approval requests, urgent alerts) are dispatched ahead of informational messages and reach operators within the 2-second P95 target even when the queue is deep.

#### Rate Limiting Under Burst

The `TelegramSender` enforces Telegram Bot API rate limits via a dual-layer token-bucket limiter:

1. **Global limiter** — `Telegram:RateLimits:GlobalPerSecond` (default `30`). Applies across all chats. When exhausted, workers block and wait for a token rather than issuing requests that will be 429'd.
2. **Per-chat limiter** — `Telegram:RateLimits:PerChatPerMinute` (default `20`). Prevents flooding a single operator's chat.

When the Telegram API returns `429 Too Many Requests`, the sender reads the `retry_after` header and pauses the affected worker for that duration. Rate-limited wait time is tracked via the dedicated `telegram.send.rate_limited_wait_ms` histogram for operational diagnostics. Messages that hit rate limits are excluded from the acceptance gate metric `telegram.send.first_attempt_latency_ms` and are captured by the all-inclusive `telegram.send.latency_ms` primary metric for capacity planning.

#### Burst Scenario (100+ Agents)

Under a burst of 1 000+ simultaneous agent events:

1. **Enqueue**: Events are written to the durable outbox store immediately (sub-millisecond per insert, batched where possible). Each event is tagged with its severity.
2. **Priority drain**: The 10 concurrent processor workers dequeue by severity priority. Critical/High messages (typically < 10% of burst volume — blocking questions, approval requests) are processed first and reach the Telegram API within the 2-second window.
3. **Multi-chat fan-out**: In production, 100+ agents typically span multiple tenants/workspaces, routing to multiple operator chats. The per-chat rate limit (20 msg/min) constrains individual operators, but the global rate limit (30 msg/s) applies across all chats. With messages distributed across N operator chats, effective throughput is `min(30, N × 20/60)` msg/s. For 10+ operator chats, the global limit of 30 msg/s is the binding constraint.
4. **Throughput**: At 30 msg/s sustained, a burst of 1 000 messages drains in ~34 seconds. With priority queuing, Critical/High messages are processed first. The acceptance metric `telegram.send.first_attempt_latency_ms` (first-attempt, non-rate-limited sends per §10.4) remains under 2 s P95 across all severities for sends that do not encounter rate limits or retries. Sends that hit rate limits are tracked via `telegram.send.latency_ms` (all-inclusive) for capacity planning.
5. **Backpressure**: If queue depth exceeds `MaxQueueDepth`, low-severity messages are dead-lettered immediately with reason `backpressure:queue_depth_exceeded` (see §10.4 table) and a `telegram.messages.backpressure_dlq` counter is incremented. An alert is sent to the ops channel. Critical, High, and Normal messages are always accepted.
6. **Zero loss guarantee**: All messages are either delivered or dead-lettered with a traceable reason — zero silent loss (per e2e-scenarios burst test). Dead-lettered messages (whether from retry exhaustion or backpressure) are available for manual replay.

---

## 11. Design Decisions and Rationale

| Decision | Rationale |
|---|---|
| **`Telegram.Bot` as the client library** | Most widely adopted .NET Telegram library; strong community, practical examples, sufficient Bot API coverage. `Telegram.BotAPI` is the fallback if a newer Bot API feature is needed. |
| **Outbound queue is durable, not in-memory** | The 2-second P95 latency target assumes queuing overhead is minimal, but durability is non-negotiable given the zero-message-loss requirement and the burst scenario (100+ agents). An in-memory `Channel<T>` sits in front of a persistent store to keep the hot path fast. |
| **`update_id` as the deduplication key** | Telegram guarantees `update_id` is unique and monotonically increasing per bot. Using it directly avoids the cost of hashing message content. |
| **Webhook secret token validation** | Telegram supports a `secret_token` parameter on `setWebhook` (added in Bot API 6.0). This is cheaper and simpler than IP-allowlisting Telegram's data-center ranges. |
| **Single `ITelegramUpdatePipeline`** | Forces webhook and long-poll modes through identical logic, eliminating a class of "works in dev, breaks in prod" bugs. |
| **Inline keyboard `callback_data` format: `QuestionId:ActionId`** | Fits within Telegram's 64-byte `callback_data` limit. `ActionId` is a short identifier; the full `HumanAction` payload is stored server-side in `IDistributedCache` keyed by `QuestionId:ActionId`, with expiry matching `AgentQuestion.ExpiresAt`. On callback, the handler looks up the original `AgentQuestion` and resolves the chosen action. This aligns with tech-spec decision D-3 and avoids encoding complex payloads in the 64-byte field. |

---

## 12. Constraints and Assumptions

1. **Single-bot deployment** — One Telegram bot per swarm instance. Multi-bot is not in scope.
2. **No file/media handling** — The connector handles text messages and inline buttons only. Photo/document attachments from operators are out of scope for this story.
3. **Persistence technology** — The architecture assumes EF Core for `OperatorBinding`, `InboundUpdate`, `OutboundMessage`, and `AuditRecord`. The implementation-plan (Stages 3.x–4.x) specifies **SQLite** as the initial provider for all persistence (outbox, dedup store, dead-letter queue, audit log), designed for swap to PostgreSQL or SQL Server via EF Core provider change for scaled deployments. This architecture aligns with that approach: SQLite is the V1 provider; production scaling to a full RDBMS is a configuration change, not a schema change.
4. **Swarm orchestrator interface** — `ISwarmCommandBus` is to be defined in the planned `AgentSwarm.Messaging.Abstractions` project. Its transport (in-process, message broker, gRPC) is outside this story's scope.
5. **Allowlist-based `/start` registration** — When a user sends `/start`, the connector checks whether their Telegram user ID is in the pre-configured allowlist. If present, the `OperatorBinding` is created or updated immediately with `IsActive = true`. If absent, the user receives a "not authorized" reply. No admin approval step is required for allowlisted users; the `IsActive` flag remains available for future soft-disable workflows.
