# Architecture — Telegram Messenger Support (qq-TELEGRAM-MESSENGER-S)

## 1. Problem Statement

The agent swarm (100+ autonomous agents) requires a Telegram-based human interface so that mobile operators can start tasks, answer blocking questions, approve/reject actions, and receive urgent alerts — all without access to a dashboard or CLI. The Telegram connector must slot into the shared `IMessengerConnector` abstraction defined by the Messenger Gateway epic while meeting Telegram-specific requirements for webhook transport, inline buttons, and the 2-second P95 send-latency target.

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

| Component | Assembly | Responsibility |
|---|---|---|
| **Webhook Endpoint** | `AgentSwarm.Messaging.Telegram` | ASP.NET Core controller that receives Telegram `Update` POSTs. Validates the `X-Telegram-Bot-Api-Secret-Token` header. Returns `200 OK` immediately, enqueues update for async processing. |
| **Long-Poll Receiver** | `AgentSwarm.Messaging.Telegram` | `BackgroundService` that calls `GetUpdatesAsync` in a loop. Used in local/dev only; disabled when webhook mode is configured. Shares the same downstream pipeline as the webhook. |
| **TelegramUpdateRouter** | `AgentSwarm.Messaging.Telegram` | Central inbound pipeline stage. Deduplicates by `update_id`, checks the operator allowlist, enriches with correlation ID, and dispatches to `CommandDispatcher` or `CallbackQueryHandler`. |
| **CommandDispatcher** | `AgentSwarm.Messaging.Telegram` | Maps incoming text commands to strongly typed `SwarmCommand` objects. Delegates callback-query payloads (button presses) to `CallbackQueryHandler` which produces `HumanDecisionEvent`. |
| **AuthZ Service** | `AgentSwarm.Messaging.Core` | Validates that the Telegram user ID + chat ID pair is in the authorized operator registry. Returns tenant/workspace binding or rejects the request. |
| **Operator Registry** | `AgentSwarm.Messaging.Core` | Persistent map of `TelegramUserId → OperatorIdentity(TenantId, WorkspaceId, Roles)`. Populated via `/start` registration flow and admin configuration. |
| **Swarm Command Bus** | `AgentSwarm.Messaging.Core` | Publishes validated, strongly typed commands to the agent swarm orchestrator. Consumes agent-originated events (questions, alerts, status) and routes them to the correct outbound connector. |
| **OutboundMessageQueue** | `AgentSwarm.Messaging.Core` | Durable queue for outbound messages. Provides at-least-once delivery, deduplication by idempotency key, retry with exponential back-off (max 5 attempts), and dead-letter after exhaustion. |
| **TelegramSender** | `AgentSwarm.Messaging.Telegram` | Wraps `ITelegramBotClient` from the `Telegram.Bot` library. Formats messages with MarkdownV2, builds `InlineKeyboardMarkup` for agent questions, enforces Telegram rate limits. |
| **AuditLogger** | `AgentSwarm.Messaging.Persistence` | Writes an immutable audit record for every human response. Includes message ID, user ID, agent ID, timestamp, and correlation ID. Backed by append-only store. |

---

## 3. Data Model

### 3.1 Entities

#### OperatorBinding

Links a Telegram identity to the swarm's authorization model.

| Field | Type | Description |
|---|---|---|
| `TelegramUserId` | `long` | Telegram's unique user identifier. Primary key. |
| `TelegramChatId` | `long` | Chat/group where commands are accepted. |
| `TenantId` | `string` | Swarm tenant this operator belongs to. |
| `WorkspaceId` | `string` | Workspace within the tenant. |
| `Roles` | `string[]` | `Operator`, `Approver`, `Admin`. |
| `RegisteredAt` | `DateTimeOffset` | When `/start` was executed. |
| `IsActive` | `bool` | Soft-disable without deleting. |

#### InboundUpdate (deduplication record)

| Field | Type | Description |
|---|---|---|
| `UpdateId` | `long` | Telegram's monotonic `update_id`. Primary key. |
| `ReceivedAt` | `DateTimeOffset` | First receipt timestamp. |
| `ProcessedAt` | `DateTimeOffset?` | When processing completed (null = in-flight). |
| `IdempotencyStatus` | `enum` | `Received`, `Processing`, `Completed`, `Failed`. |

#### OutboundMessage

| Field | Type | Description |
|---|---|---|
| `MessageId` | `Guid` | Internal unique identifier. Primary key. |
| `IdempotencyKey` | `string` | Deterministic key derived from `(AgentId, QuestionId, CorrelationId)`. Prevents duplicate sends. |
| `ChatId` | `long` | Target Telegram chat. |
| `Payload` | `string` | Serialized message content (MarkdownV2 text + optional inline keyboard JSON). |
| `Status` | `enum` | `Pending`, `Sending`, `Sent`, `Failed`, `DeadLettered`. |
| `AttemptCount` | `int` | Number of delivery attempts so far. |
| `NextRetryAt` | `DateTimeOffset?` | Scheduled next attempt. |
| `CreatedAt` | `DateTimeOffset` | Enqueue time. |
| `SentAt` | `DateTimeOffset?` | Telegram confirmation time. |
| `TelegramMessageId` | `int?` | Telegram's returned `message_id` on success. |
| `CorrelationId` | `string` | End-to-end trace ID. |
| `ErrorDetail` | `string?` | Last error message for diagnostics. |

#### AgentQuestion (shared model — defined in `AgentSwarm.Messaging.Abstractions`)

| Field | Type | Description |
|---|---|---|
| `QuestionId` | `string` | Unique question identifier. |
| `AgentId` | `string` | Originating agent. |
| `TaskId` | `string` | Associated work item / task. |
| `Title` | `string` | Short summary. |
| `Body` | `string` | Full context for the operator. |
| `Severity` | `string` | `Critical`, `High`, `Normal`, `Low`. |
| `AllowedActions` | `HumanAction[]` | Buttons to render. |
| `ExpiresAt` | `DateTimeOffset` | Timeout; after which `DefaultAction` fires. |
| `DefaultAction` | `string` | Action value applied on timeout. |
| `CorrelationId` | `string` | Trace ID. |

#### HumanDecisionEvent (shared model)

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
| `AgentId` | `string` | Target or source agent. |
| `Action` | `string` | Command or decision value. |
| `Timestamp` | `DateTimeOffset` | UTC. |
| `CorrelationId` | `string` | Trace ID. |
| `TenantId` | `string` | Operator's tenant. |
| `RawPayload` | `string` | Serialized original message for forensics. |

### 3.2 Entity Relationships

```text
OperatorBinding 1──* InboundUpdate     (via TelegramUserId)
OperatorBinding 1──* OutboundMessage   (via ChatId)
OutboundMessage *──1 AgentQuestion     (via IdempotencyKey containing QuestionId)
InboundUpdate   1──1 HumanDecisionEvent (for callback queries)
AuditRecord     *──1 OperatorBinding   (via UserId)
AuditRecord     *──1 AgentQuestion     (via CorrelationId)
```

---

## 4. Interfaces Between Components

### 4.1 IMessengerConnector (shared abstraction)

The Telegram connector implements the common gateway interface defined in `AgentSwarm.Messaging.Abstractions`:

```csharp
public interface IMessengerConnector
{
    Task SendMessageAsync(MessengerMessage message, CancellationToken ct);
    Task SendQuestionAsync(AgentQuestion question, CancellationToken ct);
    Task<IReadOnlyList<MessengerEvent>> ReceiveAsync(CancellationToken ct);
}
```

`TelegramMessengerConnector : IMessengerConnector` delegates `SendMessageAsync` and `SendQuestionAsync` to the `OutboundMessageQueue` and implements `ReceiveAsync` by draining processed inbound events.

### 4.2 ITelegramUpdatePipeline (internal)

```csharp
public interface ITelegramUpdatePipeline
{
    /// Returns false if the update was a duplicate or unauthorized.
    Task<bool> ProcessAsync(Update update, CancellationToken ct);
}
```

Called by both the webhook controller and the long-poll receiver, ensuring a single processing path.

### 4.3 IOperatorRegistry

```csharp
public interface IOperatorRegistry
{
    Task<OperatorBinding?> GetByTelegramUserAsync(long telegramUserId, CancellationToken ct);
    Task RegisterAsync(long telegramUserId, long chatId, CancellationToken ct);
    Task<bool> IsAuthorizedAsync(long telegramUserId, long chatId, CancellationToken ct);
}
```

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

### 4.6 ISwarmCommandBus (shared abstraction)

```csharp
public interface ISwarmCommandBus
{
    Task PublishCommandAsync(SwarmCommand command, CancellationToken ct);
    IAsyncEnumerable<SwarmEvent> SubscribeAsync(string tenantId, CancellationToken ct);
}
```

The Telegram connector publishes commands (task creation, approvals, pauses) and subscribes to events (agent questions, status updates, alerts) for the operator's tenant.

---

## 5. End-to-End Sequence Flows

### 5.1 Scenario: Human sends `/ask build release notes for Solution12`

```text
Human (Telegram)                Webhook Endpoint       UpdateRouter          CommandDispatcher       AuthZ       SwarmCommandBus       Orchestrator
      │                              │                      │                      │                   │               │                    │
      │──POST /webhook──────────────▶│                      │                      │                   │               │                    │
      │                              │──validate secret────▶│                      │                   │               │                    │
      │◀─────200 OK─────────────────│                      │                      │                   │               │                    │
      │                              │                      │──check update_id───▶ │                   │               │                    │
      │                              │                      │   (not duplicate)    │                   │               │                    │
      │                              │                      │──check allowlist────▶│──IsAuthorized?───▶│               │                    │
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
1. Webhook returns `200 OK` before processing — Telegram will not retry.
2. `update_id` is persisted; duplicate POSTs are dropped.
3. Authorization check runs before command parsing.
4. The reply ("Task created") is enqueued to `OutboundMessageQueue`, not sent inline, preserving the durable-delivery guarantee.
5. `AuditLogger` records the `/ask` command with correlation ID.

### 5.2 Scenario: Agent asks a blocking question, operator answers via button

```text
Orchestrator        SwarmCommandBus       TelegramConnector    OutboundQueue     TelegramSender      Human (Telegram)
      │                    │                     │                   │                  │                   │
      │──AgentQuestion────▶│                     │                   │                  │                   │
      │  (severity=High,   │──deliver event─────▶│                   │                  │                   │
      │   timeout=30min,   │                     │──build message────▶                  │                   │
      │   defaultAction=   │                     │  + InlineKeyboard │                  │                   │
      │   "skip")          │                     │──enqueue──────────▶│                  │                   │
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
1. The question includes `Severity`, `ExpiresAt`, `DefaultAction`, and `AllowedActions` rendered as inline keyboard buttons.
2. The callback query carries `QuestionId:ActionValue` in its `callback_data` field (≤ 64 bytes).
3. Button press produces a strongly typed `HumanDecisionEvent` — never a raw string.
4. The `answerCallbackQuery` call removes the loading spinner on the operator's device.
5. Audit record is written with `MessageId`, `UserId`, `AgentId`, timestamp, and `CorrelationId`.
6. If no operator responds before `ExpiresAt`, a timeout handler fires `DefaultAction` and updates the Telegram message to reflect the timeout.

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
      │  ... (attempts 3-5 fail similarly) ...    │                      │                    │
      │                     │                     │                      │                    │
      │──attempt 5 fails───▶│                     │                      │                    │
      │──deadLetter─────────▶─────────────────────▶──────────────────────▶│                    │
      │                     │                     │                      │──alert operator────▶│
```

**Retry policy:**
- Max attempts: 5
- Back-off: exponential (2s, 4s, 8s, 16s, 32s) with jitter
- Retryable errors: HTTP 429, 5xx, network timeouts
- Non-retryable: HTTP 400 (bad request), 403 (bot blocked) — dead-letter immediately
- Dead-letter record preserves full message payload, all attempt timestamps, and error details
- Alert is sent to a secondary notification channel (ops Telegram group or fallback messenger)

### 5.4 Scenario: Duplicate webhook delivery (idempotency)

```text
Telegram Cloud          Webhook Endpoint        UpdateRouter         DB (InboundUpdate)
      │                       │                      │                      │
      │──POST update_id=999──▶│                      │                      │
      │◀──200 OK─────────────│──forward──────────────▶│                      │
      │                       │                      │──INSERT update 999──▶│
      │                       │                      │◀──OK (new)──────────│
      │                       │                      │──process command────▶ ...
      │                       │                      │                      │
      │  (Telegram retries — network glitch)         │                      │
      │──POST update_id=999──▶│                      │                      │
      │◀──200 OK─────────────│──forward──────────────▶│                      │
      │                       │                      │──INSERT update 999──▶│
      │                       │                      │◀──CONFLICT (dup)────│
      │                       │                      │──DROP (no-op)        │
```

**Key invariants:**
1. Endpoint always returns `200 OK` regardless of duplicate status — prevents Telegram from retrying further.
2. Deduplication uses `update_id` as a natural idempotency key with a `UNIQUE` constraint.
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

Commands `/approve` and `/reject` accept a question ID argument and produce the same `HumanDecisionEvent` as inline buttons. The `/handoff` command reassigns the question to another operator.

---

## 6. Assembly Map

```text
AgentSwarm.Messaging.sln
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
| `Telegram:AllowedChatIds` | App configuration | Comma-separated allowlist. Evaluated by `AuthZ Service`. |
| `Telegram:RateLimits:GlobalPerSecond` | App configuration | Default `30`. |
| `Telegram:RateLimits:PerChatPerMinute` | App configuration | Default `20`. |
| `OutboundQueue:MaxRetries` | App configuration | Default `5`. |
| `OutboundQueue:BaseRetryDelaySeconds` | App configuration | Default `2`. |

---

## 8. Observability

| Signal | Implementation |
|---|---|
| **Traces** | OpenTelemetry `ActivitySource("AgentSwarm.Messaging.Telegram")`. Every inbound update and outbound send starts a span carrying `CorrelationId` as a baggage item. |
| **Metrics** | Counters: `telegram.updates.received`, `telegram.messages.sent`, `telegram.messages.dead_lettered`, `telegram.commands.processed`. Histograms: `telegram.send.duration_ms`. |
| **Logs** | Structured logging via `ILogger<T>`. Correlation ID included in every log scope. Bot token is excluded from all log output via a custom redaction enricher. |
| **Health** | `/health/ready` checks: Telegram API reachable (`getMe`), outbound queue depth < threshold, database connectivity. `/health/live` checks: process alive. |

---

## 9. Security Model

1. **Webhook validation** — Every inbound POST must carry the `X-Telegram-Bot-Api-Secret-Token` header matching the configured `Telegram:SecretToken`. Requests without it return `401`.
2. **Operator allowlist** — `TelegramUserId` and `ChatId` are checked against `OperatorBinding` records. Unregistered users receive a generic "not authorized" reply and the attempt is logged.
3. **Role enforcement** — `/approve` and `/reject` require the `Approver` role. `/pause` and `/resume` require the `Operator` role. `/start` is open but merely initiates a pending registration that an admin must confirm.
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

A `QuestionTimeoutService` (BackgroundService) polls for open questions past their `ExpiresAt`. When a question times out:
1. Publishes a `HumanDecisionEvent` with the `DefaultAction` value.
2. Updates the original Telegram message to indicate the timeout ("⏰ Timed out — default action applied: *skip*").
3. Writes an audit record noting the timeout.

---

## 11. Design Decisions and Rationale

| Decision | Rationale |
|---|---|
| **`Telegram.Bot` as the client library** | Most widely adopted .NET Telegram library; strong community, practical examples, sufficient Bot API coverage. `Telegram.BotAPI` is the fallback if a newer Bot API feature is needed. |
| **Outbound queue is durable, not in-memory** | The 2-second P95 latency target assumes queuing overhead is minimal, but durability is non-negotiable given the zero-message-loss requirement and the burst scenario (100+ agents). An in-memory `Channel<T>` sits in front of a persistent store to keep the hot path fast. |
| **`update_id` as the deduplication key** | Telegram guarantees `update_id` is unique and monotonically increasing per bot. Using it directly avoids the cost of hashing message content. |
| **Webhook secret token validation** | Telegram supports a `secret_token` parameter on `setWebhook` (added in Bot API 6.0). This is cheaper and simpler than IP-allowlisting Telegram's data-center ranges. |
| **Single `ITelegramUpdatePipeline`** | Forces webhook and long-poll modes through identical logic, eliminating a class of "works in dev, breaks in prod" bugs. |
| **Inline keyboard `callback_data` format: `QuestionId:ActionValue`** | Fits within Telegram's 64-byte `callback_data` limit while carrying both the question identity and the chosen action. No database lookup is needed to decode the intent. |

---

## 12. Constraints and Assumptions

1. **Single-bot deployment** — One Telegram bot per swarm instance. Multi-bot is not in scope.
2. **No file/media handling** — The connector handles text messages and inline buttons only. Photo/document attachments from operators are out of scope for this story.
3. **Persistence technology** — The architecture assumes a relational database (PostgreSQL or SQL Server) via EF Core for `OperatorBinding`, `InboundUpdate`, `OutboundMessage`, and `AuditRecord`. The specific provider is a deployment decision.
4. **Swarm orchestrator interface** — `ISwarmCommandBus` is assumed to exist or be defined in `AgentSwarm.Messaging.Abstractions`. Its transport (in-process, message broker, gRPC) is outside this story's scope.
5. **Admin approval of `/start`** — New operator registrations via `/start` require admin confirmation. The admin flow (approve new operator) is out of scope for this story but the `OperatorBinding.IsActive` flag supports it.
