# Architecture -- Discord Messenger Support (qq-DISCORD-MESSENGER-SU)

## 1. Problem Statement

The agent swarm (100+ autonomous agents) requires a Discord-based human interface so that engineering operators can start tasks, answer blocking questions, approve/reject actions, query agent status, and receive alerts -- all through team-style Discord channels. The Discord connector must slot into the shared `IMessengerConnector` abstraction defined in the Messenger Gateway epic (see `AgentSwarm.Messaging.Abstractions`) while meeting Discord-specific requirements for Gateway WebSocket inbound transport, REST API outbound delivery, slash commands with interactive message components, and role/guild/channel-based security.

**Protocol summary:** Inbound bot events arrive through the Discord Gateway WebSocket connection (managed by Discord.Net's `DiscordSocketClient`), where the bot declares Gateway Intents for the event groups it needs. Outbound messages use the Discord REST API for sending embeds, components, and thread replies. This dual-transport model is distinct from Telegram's webhook/polling model.

**Reliability and performance summary:** The architecture guarantees **at-least-once delivery with dead-letter fallback** -- every outbound message is either delivered to Discord or dead-lettered with a traceable reason. The bot automatically reconnects after Gateway disconnects with exponential backoff. Duplicate interaction IDs are ignored. High-priority alerts are delivered before low-priority status updates via severity-based priority queuing. The system supports at least 100 active agents posting status updates with rate-limit-aware batching.

---

## 2. Component Overview

### 2.1 Component Diagram

```text
+-----------------------------------------------------------------------+
| Messenger Gateway (Worker Service)                                    |
|                                                                       |
|  +------------------------+                                           |
|  | DiscordGatewayService  |                                           |
|  | (BackgroundService)    |                                           |
|  | - DiscordSocketClient  |                                           |
|  | - Gateway Intents      |                                           |
|  | - Auto-reconnect       |                                           |
|  +----------+-------------+                                           |
|             | Inbound interactions / messages                         |
|             v                                                         |
|  +----------------------------------------------+                    |
|  |         DiscordInteractionRouter              |                    |
|  |  - Deduplication (interaction ID idempotency) |                    |
|  |  - Guild/channel/role gate                    |                    |
|  +--------------------+-------------------------+                     |
|                       |                                               |
|                       v                                               |
|  +----------------------------------------------+                    |
|  |         SlashCommandDispatcher                |                    |
|  |  - Parses /agent ask, /agent status,          |                    |
|  |    /agent approve, /agent reject,             |                    |
|  |    /agent assign, /agent pause, /agent resume |                    |
|  |  - Component interaction handler (buttons,    |                    |
|  |    select menus)                              |                    |
|  +--------------------+-------------------------+                     |
|                       |                                               |
|       +---------------+---------------+                               |
|       v               v               v                               |
|  +---------+   +------------+   +--------------+                      |
|  | AuthZ   |   | Guild      |   | Swarm        |                      |
|  | Service |   | Registry   |   | Command Bus  |                      |
|  +---------+   +------------+   +------+-------+                      |
|                                        |                              |
|                       +----------------+                              |
|                       v                                               |
|  +----------------------------------------------+                    |
|  |         OutboundMessageQueue                  |                    |
|  |  - Persistent store + Channel<T> hot buffer   |                    |
|  |  - Retry + exponential back-off               |                    |
|  |  - Dead-letter queue                          |                    |
|  |  - Deduplication (idempotency key)            |                    |
|  +--------------------+-------------------------+                     |
|                       v                                               |
|  +----------------------------------------------+                    |
|  |      DiscordSender (IDiscordRestClient)       |                    |
|  |  - Rate limiter (REST rate-limit headers)     |                    |
|  |  - Embed builder                              |                    |
|  |  - Component builder (buttons, select menus)  |                    |
|  |  - Thread management                          |                    |
|  +----------------------------------------------+                    |
|                                                                       |
|  +----------------------------------------------+                    |
|  |          AuditLogger                          |                    |
|  |  - Persists every human response              |                    |
|  |  - Fields: GuildId, ChannelId, UserId,        |                    |
|  |    MessageId, CorrelationId, Timestamp        |                    |
|  +----------------------------------------------+                    |
+-----------------------------------------------------------------------+
                        |
                        v
          +--------------------------+
          |  Agent Swarm Orchestrator |
          +--------------------------+
```

### 2.2 Component Responsibilities

| Component | Planned Assembly | Responsibility |
|---|---|---|
| **DiscordGatewayService** | `AgentSwarm.Messaging.Discord` (to be created) | `BackgroundService` that manages the `DiscordSocketClient` lifecycle. Configures Gateway Intents (`Guilds`, `GuildMessages`, `MessageContent`, `GuildMessageReactions`), registers slash commands on startup, handles `InteractionCreated`, `MessageReceived`, `Disconnected`, and `Connected` events. On `Disconnected`, logs the close code and lets Discord.Net's built-in reconnection logic handle reconnection with exponential backoff. Maps Discord interactions to `MessengerEvent` via `DiscordInteractionMapper` and passes them to `IDiscordInteractionPipeline.ProcessAsync`. |
| **DiscordInteractionRouter** | `AgentSwarm.Messaging.Discord` (to be created) | Central inbound pipeline stage (inside `IDiscordInteractionPipeline`). Deduplicates by Discord interaction ID (each interaction has a globally unique snowflake ID), performs authorization via `IUserAuthorizationService` (guild/channel/role checks), enriches with correlation ID, and dispatches to `SlashCommandDispatcher` or `ComponentInteractionHandler`. Unauthorized interactions receive an ephemeral rejection reply. |
| **SlashCommandDispatcher** | `AgentSwarm.Messaging.Discord` (to be created) | Maps incoming slash command interactions to strongly typed `SwarmCommand` objects. The `/agent` command group uses subcommands: `ask`, `status`, `approve`, `reject`, `assign`, `pause`, `resume`. Delegates component interactions (button presses, select menu selections) to `ComponentInteractionHandler` which produces `HumanDecisionEvent`. Must acknowledge interactions within 3 seconds (Discord requirement) using `DeferAsync()`, then follow up with the full response. |
| **ComponentInteractionHandler** | `AgentSwarm.Messaging.Discord` (to be created) | Handles button clicks and select menu selections on agent question messages. Parses `QuestionId` and `ActionId` from the component `custom_id` field (format: `q:{QuestionId}:{ActionId}`), looks up the `PendingQuestionRecord`, resolves `HumanAction.Value`, and emits `HumanDecisionEvent`. For actions with `RequiresComment = true`, responds with a Discord modal to collect the comment text. |
| **AuthZ Service** | `AgentSwarm.Messaging.Core` (shared) | Validates that the Discord user has the required guild role and is operating in an authorized guild/channel. For Discord, `IUserAuthorizationService.AuthorizeAsync` receives the Discord user ID as `externalUserId` and the channel ID as `chatId`. The `GuildBinding` registry provides the guild/channel/role mapping. |
| **Guild Registry** | `AgentSwarm.Messaging.Discord` (to be created) | Persistent map of `(GuildId, ChannelId) -> GuildBinding(TenantId, WorkspaceId, ChannelPurpose, AllowedRoleIds)`. Populated via configuration. Provides `IGuildRegistry` interface for authorization lookups. Supports the channel model: one control channel, one alert channel, and optional per-workstream channels. |
| **Swarm Command Bus** | `AgentSwarm.Messaging.Abstractions` / `Core` (shared) | Publishes validated commands to the orchestrator via `ISwarmCommandBus.PublishCommandAsync`. Subscribes to agent events (questions, alerts, status) via `SubscribeAsync` and routes them to the Discord outbound pipeline. Same shared interface as other connectors. |
| **OutboundMessageQueue** | `AgentSwarm.Messaging.Abstractions` (interface) / `AgentSwarm.Messaging.Persistence` (impl) (shared) | Durable queue for outbound messages. Same `IOutboundQueue` interface as other connectors. The shared `OutboundMessage` record stores channel identity in the `ChatId` field as `long`; the Discord connector stores Discord channel snowflake IDs cast to `long` (safe because Discord snowflakes are unsigned 64-bit integers that fit in signed `long` for all currently-issued IDs). Provides severity-based priority ordering (`Critical > High > Normal > Low`), retry with exponential backoff, and dead-letter after exhaustion. |
| **DiscordSender** | `AgentSwarm.Messaging.Discord` (to be created) | Concrete `DiscordMessageSender` implementing `IMessageSender` (defined in `AgentSwarm.Messaging.Core`). Wraps Discord.Net's REST client. Builds Discord embeds with agent identity fields (name, role, current task, confidence score, blocking question). Builds component rows with action buttons and select menus. Manages thread creation for per-task conversations. Respects Discord REST rate limits by reading `X-RateLimit-*` response headers and queuing sends when approaching limits. |
| **OutboundQueueProcessor** | `AgentSwarm.Messaging.Worker` (shared) | `BackgroundService` with configurable concurrency. Dequeues highest-severity pending `OutboundMessage`, dispatches to `IMessageSender`, calls `MarkSentAsync` on success. For question messages, calls `IPendingQuestionStore.StoreAsync` as a post-send hook. Same shared component as other connectors. |
| **AuditLogger** | `AgentSwarm.Messaging.Persistence` (shared) | Writes immutable audit records for every command and response. For Discord, includes guild ID, channel ID, Discord user ID, message ID, interaction ID, and correlation ID. Uses the shared `IAuditLogger` interface. |

---

## 3. Data Model

### 3.1 Entities

#### GuildBinding

Links a Discord guild/channel to the swarm's authorization model. Each row represents one (guild, channel, workspace) binding, supporting multi-channel and multi-workspace scenarios.

| Field | Type | Description |
|---|---|---|
| `Id` | `Guid` | Surrogate primary key. |
| `GuildId` | `ulong` | Discord guild (server) snowflake ID. |
| `ChannelId` | `ulong` | Discord channel snowflake ID. |
| `ChannelPurpose` | `enum` | `Control`, `Alert`, `Workstream`. Determines which message types are routed to this channel. `Control` receives commands and agent questions. `Alert` receives priority alerts. `Workstream` receives task-specific updates and threads. |
| `TenantId` | `string` | Swarm tenant this guild/channel belongs to. |
| `WorkspaceId` | `string` | Workspace within the tenant. |
| `AllowedRoleIds` | `ulong[]` | Discord role snowflake IDs authorized to issue commands in this channel. Users must have at least one of these roles. |
| `CommandRestrictions` | `Dictionary<string, ulong[]>?` | Optional per-command role overrides. Key is the subcommand name (e.g., `"approve"`), value is the set of role IDs required. When null, `AllowedRoleIds` applies uniformly. Enables restricting `/agent approve` and `/agent reject` to a senior operator role while allowing `/agent status` for all authorized roles. |
| `RegisteredAt` | `DateTimeOffset` | When binding was created. |
| `IsActive` | `bool` | Soft-disable without deleting. |

**Constraints:**
- `UNIQUE (GuildId, ChannelId, WorkspaceId)` -- prevents duplicate bindings.
- Composite index on `(GuildId, ChannelId)` -- used for authorization lookups.
- Index on `(GuildId, ChannelPurpose)` -- used to resolve the alert channel for a guild.

#### DiscordInteractionRecord (deduplication + durable work-queue record)

| Field | Type | Description |
|---|---|---|
| `InteractionId` | `ulong` | Discord interaction snowflake ID. Primary key. Globally unique. |
| `InteractionType` | `enum` | `SlashCommand`, `ButtonClick`, `SelectMenu`, `ModalSubmit`. |
| `GuildId` | `ulong` | Guild where the interaction occurred. |
| `ChannelId` | `ulong` | Channel where the interaction occurred. |
| `UserId` | `ulong` | Discord user who triggered the interaction. |
| `RawPayload` | `string` | Full serialized interaction JSON. Persisted before acknowledging the interaction so that a crash after ACK does not lose the command. |
| `ReceivedAt` | `DateTimeOffset` | First receipt timestamp. |
| `ProcessedAt` | `DateTimeOffset?` | When processing completed (null = in-flight). |
| `IdempotencyStatus` | `enum` | `Received`, `Processing`, `Completed`, `Failed`. |
| `AttemptCount` | `int` | Default 0. Incremented on each reprocessing attempt. |
| `ErrorDetail` | `string?` | Latest failure reason for diagnostics. |

#### OutboundMessage (Discord adaptation of shared model)

The Discord connector uses the shared `OutboundMessage` record from `AgentSwarm.Messaging.Abstractions` with the following Discord-specific field semantics:

| Field | Discord Usage | Description |
|---|---|---|
| `ChatId` | `long` (cast from `ulong`) | Target Discord channel snowflake ID. Stored as `long` to match the shared `OutboundMessage.ChatId` type; cast to `ulong` at send time. |
| `Payload` | `string` | For `CommandAck`, `StatusUpdate`, and `Alert`: serialized Discord embed JSON ready for `IMessageSender.SendTextAsync`. For `Question`: a human-readable preview stored for debugging; actual rendering happens at send time from `SourceEnvelopeJson`. |
| `SourceEnvelopeJson` | `string?` | Serialized `AgentQuestionEnvelope` JSON for question messages. Used by `DiscordMessageSender.SendQuestionAsync` to build the embed and component rows at send time. |
| `SourceType` | `enum` | Same shared enum: `Question`, `Alert`, `StatusUpdate`, `CommandAck`. |

All other shared `OutboundMessage` fields are defined inline below (this document is self-contained and does not depend on sibling connector docs):

| Field | Type | Description |
|---|---|---|
| `MessageId` | `Guid` | Internal unique identifier. Primary key. |
| `IdempotencyKey` | `string` | Deterministic key preventing duplicate sends (see derivation table below). |
| `Severity` | `MessageSeverity` | `Critical`, `High`, `Normal`, `Low`. Determines priority queue ordering. |
| `Status` | `OutboundMessageStatus` | `Pending`, `Sending`, `Sent`, `Failed`, `DeadLettered`. |
| `AttemptCount` | `int` | Number of delivery attempts so far. |
| `MaxAttempts` | `int` | Maximum delivery attempts before dead-lettering (default 5). |
| `NextRetryAt` | `DateTimeOffset?` | Scheduled next attempt. |
| `CreatedAt` | `DateTimeOffset` | Enqueue time. |
| `SentAt` | `DateTimeOffset?` | Discord REST API confirmation time. |
| `PlatformMessageId` | `long?` | Discord message snowflake ID (cast to `long`; null before first successful send). The shared `OutboundMessage` record in `AgentSwarm.Messaging.Abstractions` defines this field generically so each connector can store its platform-specific message ID. |
| `CorrelationId` | `string` | End-to-end trace ID. |
| `SourceId` | `string?` | `QuestionId` for question messages; alert rule ID for alerts; command correlation ID for acks. |
| `ErrorDetail` | `string?` | Last error message for diagnostics. |

**Idempotency key derivation:** Same derivation rules as the shared model:

| SourceType | IdempotencyKey formula | Example |
|---|---|---|
| `Question` | `q:{AgentId}:{QuestionId}` | `q:build-agent-3:Q-42` |
| `Alert` | `alert:{AgentId}:{AlertId}` | `alert:monitor-1:alert-77` |
| `StatusUpdate` | `s:{AgentId}:{CorrelationId}` | `s:deploy-2:trace-def` |
| `CommandAck` | `c:{CorrelationId}` | `c:trace-ghi` |

#### AgentQuestion (shared model -- defined in `AgentSwarm.Messaging.Abstractions`)

The shared `AgentQuestion` model is defined in `AgentSwarm.Messaging.Abstractions` (proposed in the epic-level brief FR-001, `.forge-attachments/agent_swarm_messenger_user_stories.md` lines 831-841). Full field table:

| Field | Type | Description |
|---|---|---|
| `QuestionId` | `string` | Unique question identifier. Constrained to printable ASCII, max 30 characters, no `:` separator, no control characters. |
| `AgentId` | `string` | Originating agent. |
| `TaskId` | `string` | Associated work item / task. |
| `Title` | `string` | Short summary. |
| `Body` | `string` | Full context for the operator. |
| `Severity` | `MessageSeverity` | Enum: `Critical`, `High`, `Normal`, `Low`. |
| `AllowedActions` | `HumanAction[]` | Buttons to render. See `HumanAction` definition below. |
| `ExpiresAt` | `DateTimeOffset` | Timeout deadline. |
| `CorrelationId` | `string` | Trace ID. |

**Discord-specific constraint relaxation:** Discord's `custom_id` field for message components supports up to 100 characters (vs. Telegram's 64-byte `callback_data` limit). The `QuestionId:ActionId` format used in `custom_id` therefore has more headroom (`q:{QuestionId}:{ActionId}` with the `q:` prefix fits comfortably within 100 chars). However, the shared `AgentQuestion` model retains the 30-character `QuestionId` limit and ASCII-only constraint for cross-connector compatibility.

#### HumanAction (shared model -- defined in `AgentSwarm.Messaging.Abstractions`)

Same shared model as other connectors. Fields: `ActionId`, `Label`, `Value`, `RequiresComment`. Discord renders `Label` as button text or select menu option label. `ActionId` is encoded into the component `custom_id` as `q:{QuestionId}:{ActionId}`.

#### AgentQuestionEnvelope (shared model -- defined in `AgentSwarm.Messaging.Abstractions`)

Same shared envelope wrapping `AgentQuestion` with `ProposedDefaultActionId` and `RoutingMetadata`. For Discord, `RoutingMetadata` carries `DiscordChannelId` and optionally `DiscordThreadId` for routing the question to the correct channel/thread.

#### PendingQuestionRecord (Discord adaptation)

Tracks an `AgentQuestion` sent to Discord and awaiting operator response. Persisted after the Discord REST API call succeeds and returns a message snowflake ID.

| Field | Type | Description |
|---|---|---|
| `QuestionId` | `string` | Primary key. FK to `AgentQuestion.QuestionId`. |
| `AgentQuestion` | `string` | Full `AgentQuestion` serialized as JSON. |
| `DiscordChannelId` | `ulong` | Discord channel the question was sent to. |
| `DiscordMessageId` | `ulong` | Discord message snowflake ID. Always populated at creation. |
| `DiscordThreadId` | `ulong?` | Thread ID if the question was posted in a thread. |
| `DefaultActionId` | `string?` | Denormalized from `AgentQuestionEnvelope.ProposedDefaultActionId`. |
| `DefaultActionValue` | `string?` | Resolved `HumanAction.Value` for the default action. |
| `ExpiresAt` | `DateTimeOffset` | Copied from `AgentQuestion.ExpiresAt`. |
| `Status` | `enum` | `Pending`, `Answered`, `AwaitingComment`, `TimedOut`. |
| `SelectedActionId` | `string?` | Set when operator clicks a button or selects a menu option. |
| `SelectedActionValue` | `string?` | Resolved `HumanAction.Value` for the selected action. |
| `RespondentUserId` | `ulong?` | Discord user ID of the operator who interacted. |
| `StoredAt` | `DateTimeOffset` | When the record was persisted. |
| `CorrelationId` | `string` | Trace/correlation ID. |

**Constraints:**
- `UNIQUE (QuestionId)` -- one pending record per question.
- Index on `(Status, ExpiresAt)` -- used by `QuestionTimeoutService`.
- Index on `(DiscordChannelId, DiscordMessageId)` -- used by recovery sweep.

**Crash-window analysis:** The outbound send flow for a question message is: (1) `OutboundMessage` dequeued, `Status` set to `Sending`; (2) `DiscordMessageSender` calls Discord REST API; (3) on success, `MarkSentAsync` updates `OutboundMessage` to `Status = Sent` with `PlatformMessageId`; (4) `PendingQuestionRecord` is persisted. Two crash windows exist:

**Gap A -- Crash between Discord API success (step 2) and `MarkSentAsync` (step 3).** The `OutboundMessage` is still in `Sending` status with no `PlatformMessageId`. On restart, the queue processor treats `Sending` records as incomplete and re-sends them, producing a duplicate Discord message. This is the inherent cost of at-least-once delivery over an external API without two-phase commit. **Mitigation:** The duplicate message is operationally benign because the `PendingQuestionRecord` (keyed by `QuestionId`) ensures only one pending question exists -- whichever button the operator clicks resolves correctly. The `IdempotencyKey` (`q:{AgentId}:{QuestionId}`) prevents the same question from being *enqueued* twice, but cannot prevent duplicate sends from the same `OutboundMessage` across crash boundaries.

**Gap B -- Crash between `MarkSentAsync` (step 3) and `PendingQuestionRecord` persistence (step 4).** The `OutboundMessage` has `Status = Sent` and `PlatformMessageId` populated, but no `PendingQuestionRecord` exists. **Mitigation:** On restart, `QuestionRecoverySweep` queries for `OutboundMessage` records with `SourceType = Question` and `Status = Sent` that lack a corresponding `PendingQuestionRecord`, and backfills the missing records using the stored `PlatformMessageId`, `SourceId` (as `QuestionId`), and the original `AgentQuestionEnvelope` (preserved in `OutboundMessage.SourceEnvelopeJson`).

#### HumanDecisionEvent (shared model -- defined in `AgentSwarm.Messaging.Abstractions`)

Same shared model. For Discord: `Messenger` is always `"Discord"`, `ExternalUserId` is the Discord user snowflake ID (stringified), `ExternalMessageId` is the Discord interaction ID (stringified).

#### AuditLogEntry (Discord adaptation of shared model)

Uses the shared `AuditLogEntry` persistence entity with `Platform = "Discord"`. Discord-specific fields stored in `Details` (JSON):

| Detail Key | Type | Description |
|---|---|---|
| `GuildId` | `string` | Discord guild snowflake ID. |
| `ChannelId` | `string` | Discord channel snowflake ID. |
| `InteractionId` | `string` | Discord interaction snowflake ID. |
| `DiscordMessageId` | `string` | Discord message snowflake ID of the bot's response message. Present on every command acknowledgement and outbound agent message. For slash commands, this is the follow-up message ID returned by `ModifyOriginalResponseAsync`. For outbound agent messages, this is the message ID returned by the REST API send call. |
| `ThreadId` | `string?` | Discord thread snowflake ID (if applicable). |

The story audit requirement mandates that Discord message IDs are stored with every command and response. The `MessageId` field on the shared `AuditEntry` / `HumanResponseAuditEntry` records carries the Discord message snowflake ID (stringified) to satisfy this requirement at the abstraction layer. The `Details` JSON provides the full set of Discord-specific IDs for queries that need guild/channel/thread context.

#### DeadLetterMessage

Uses the shared `DeadLetterMessage` entity. For Discord, `ChatId` stores the Discord channel snowflake ID (cast to `long`). All other fields follow the shared schema.

### 3.2 Entity Relationships

```text
GuildBinding *--* OutboundMessage              (via GuildBinding.ChannelId = OutboundMessage.ChatId;
                                                resolved through tenant/workspace routing)
OutboundMessage *--0..1 AgentQuestion          (via OutboundMessage.SourceId = AgentQuestion.QuestionId
                                                when OutboundMessage.SourceType = Question)
DiscordInteractionRecord 1--0..1 HumanDecisionEvent
                                                (for component interactions; linked by processing
                                                context, not a direct FK)
PendingQuestionRecord *--1 AgentQuestion        (via PendingQuestionRecord.QuestionId)
AuditLogEntry *--1 GuildBinding                 (via AuditLogEntry Details.GuildId + Details.ChannelId;
                                                resolved at query time, not a direct FK)
DeadLetterMessage 1--1 OutboundMessage          (via DeadLetterMessage.OriginalMessageId)
```

---

## 4. Interfaces Between Components

### 4.1 IMessengerConnector (proposed contract -- evolves FR-001 from epic brief)

The epic-level FR-001 (`.forge-attachments/agent_swarm_messenger_user_stories.md` lines 43-54) defines the original shared interface with `SendQuestionAsync(AgentQuestion question, ...)`. This architecture evolves that contract to accept `AgentQuestionEnvelope` instead, wrapping `AgentQuestion` with routing metadata (`RoutingMetadata` dictionary for connector-specific fields like `DiscordChannelId`) and a proposed default action (`ProposedDefaultActionId`). The envelope pattern avoids polluting the shared `AgentQuestion` model with per-connector routing concerns. This is the proposed contract for all connectors:

```csharp
public interface IMessengerConnector
{
    Task SendMessageAsync(MessengerMessage message, CancellationToken ct);
    Task SendQuestionAsync(AgentQuestionEnvelope envelope, CancellationToken ct);
    Task<IReadOnlyList<MessengerEvent>> ReceiveAsync(CancellationToken ct);
}
```

> **Migration note:** The `AgentQuestionEnvelope` wrapper is an architecture-level decision that extends FR-001 without breaking it -- `AgentQuestion` remains accessible via `envelope.Question`. This pattern is consistent with the Telegram connector implementation on the `feature/telegram` branch (file `src/AgentSwarm.Messaging.Abstractions/IMessengerConnector.cs`), which already uses `AgentQuestionEnvelope`. The FR-001 brief's `SendQuestionAsync(AgentQuestion, CancellationToken)` is superseded by this signature.

`DiscordMessengerConnector : IMessengerConnector` delegates `SendMessageAsync` and `SendQuestionAsync` to the `OutboundMessageQueue`. `SendMessageAsync` pre-renders the Discord embed JSON (including agent identity fields) and enqueues with the rendered payload. `SendQuestionAsync` stores the full `AgentQuestionEnvelope` in `SourceEnvelopeJson` for send-time rendering (buttons, select menus, embeds require the full question context). `ReceiveAsync` drains processed inbound events from the Gateway pipeline.

### 4.2 IDiscordInteractionPipeline (to be defined in `AgentSwarm.Messaging.Discord`)

```csharp
public interface IDiscordInteractionPipeline
{
    Task<PipelineResult> ProcessAsync(MessengerEvent messengerEvent, CancellationToken ct);
}
```

The `PipelineResult(bool Handled, string? ResponseText, string CorrelationId)` return type provides structured outcome information. The `DiscordGatewayService` maps raw Discord interactions to `MessengerEvent` via `DiscordInteractionMapper`, then passes them to `ProcessAsync`. The pipeline handles deduplication, authorization, and command dispatch.

**Discord-specific pipeline behavior:** The `DiscordGatewayService` persists the `DiscordInteractionRecord` (including `RawPayload`) **before** calling `DeferAsync()`, ensuring the durable inbox has recorded the command before Discord considers the interaction acknowledged. `DeferAsync()` is called immediately after the durable persist, within the 3-second Discord interaction response deadline. Authorization, deduplication, and command processing then proceed asynchronously after the ACK. The deferred response is completed with the full result (or ephemeral error message) once processing finishes.

### 4.3 IGuildRegistry (to be defined in `AgentSwarm.Messaging.Discord`)

```csharp
public interface IGuildRegistry
{
    Task<GuildBinding?> GetBindingAsync(ulong guildId, ulong channelId, CancellationToken ct);
    Task<IReadOnlyList<GuildBinding>> GetBindingsByGuildAsync(ulong guildId, CancellationToken ct);
    Task<GuildBinding?> GetAlertChannelAsync(ulong guildId, CancellationToken ct);
    Task<IReadOnlyList<GuildBinding>> GetWorkstreamChannelsAsync(ulong guildId, string workspaceId, CancellationToken ct);
    Task<bool> IsAuthorizedChannelAsync(ulong guildId, ulong channelId, CancellationToken ct);
    Task RegisterAsync(GuildBinding binding, CancellationToken ct);
}
```

**Channel model resolution:**
- `GetBindingAsync(guildId, channelId)` returns the binding for a specific channel -- used by the authorization pipeline to validate commands.
- `GetAlertChannelAsync(guildId)` returns the binding with `ChannelPurpose = Alert` -- used to route priority alerts.
- `GetWorkstreamChannelsAsync(guildId, workspaceId)` returns all `Workstream` channels for a workspace -- used to route per-task status updates.
- `IsAuthorizedChannelAsync(guildId, channelId)` fast-path check used by the pipeline gate.

### 4.4 IOutboundQueue (proposed contract -- to be defined in `AgentSwarm.Messaging.Abstractions`)

Proposed shared interface for durable outbound message queuing. This interface is not yet defined in the repo; it is proposed by this architecture as part of the Messenger Gateway epic's shared abstractions. The Telegram connector on the `feature/telegram` branch has implemented a version of this interface at `src/AgentSwarm.Messaging.Abstractions/IOutboundQueue.cs`; the Discord connector will share the same contract:

```csharp
public interface IOutboundQueue
{
    Task EnqueueAsync(OutboundMessage message, CancellationToken ct);
    Task<OutboundMessage?> DequeueAsync(CancellationToken ct);
    Task MarkSentAsync(Guid messageId, long platformMessageId, CancellationToken ct);
    Task MarkFailedAsync(Guid messageId, string error, CancellationToken ct);
    Task DeadLetterAsync(Guid messageId, CancellationToken ct);
}
```

`DequeueAsync` returns the highest-severity pending message, oldest-first within a severity. The `platformMessageId` parameter on `MarkSentAsync` stores the Discord message snowflake ID (cast to `long`).

### 4.5 IUserAuthorizationService (proposed contract -- to be defined in `AgentSwarm.Messaging.Core`)

Proposed shared interface. The Telegram connector on the `feature/telegram` branch defines this at `src/AgentSwarm.Messaging.Core/IUserAuthorizationService.cs`. For Discord, the authorization service receives:
- `externalUserId`: Discord user snowflake ID (stringified)
- `chatId`: Discord channel snowflake ID (stringified)
- `commandName`: The slash subcommand name (e.g., `"ask"`, `"approve"`)

The Discord-specific authorization logic:
1. Resolves `GuildBinding` via `IGuildRegistry.GetBindingAsync(guildId, channelId)`.
2. Validates that the guild is authorized (binding exists and `IsActive = true`).
3. Validates that the user has at least one role from `AllowedRoleIds` (or from `CommandRestrictions[commandName]` if the subcommand has a role override).
4. Returns `AuthorizationResult` with the binding as the resolved context.

### 4.6 ISwarmCommandBus (proposed contract -- to be defined in `AgentSwarm.Messaging.Abstractions`)

Proposed shared interface for bidirectional communication between messenger connectors and the agent swarm orchestrator. The epic-level FR-001 (`.forge-attachments/agent_swarm_messenger_user_stories.md`) defines the requirement for agents to send progress updates, ask questions, and request approvals; this interface is the proposed realization. The Telegram connector on the `feature/telegram` branch implements a version at `src/AgentSwarm.Messaging.Abstractions/ISwarmCommandBus.cs`:

```csharp
public interface ISwarmCommandBus
{
    Task PublishCommandAsync(SwarmCommand command, CancellationToken ct);
    Task PublishHumanDecisionAsync(HumanDecisionEvent decision, CancellationToken ct);
    Task<SwarmStatusSummary> QueryStatusAsync(SwarmStatusQuery query, CancellationToken ct);
    Task<IReadOnlyList<AgentInfo>> QueryAgentsAsync(SwarmAgentsQuery query, CancellationToken ct);
    IAsyncEnumerable<SwarmEvent> SubscribeAsync(string tenantId, CancellationToken ct);
}
```

The Discord connector uses this interface identically: publishing commands via `PublishCommandAsync`, human decisions (from button/select interactions) via `PublishHumanDecisionAsync`, and querying swarm state via `QueryStatusAsync` and `QueryAgentsAsync`. The `DiscordGatewayService` subscribes to `SwarmEvent`s at startup for each active tenant and routes them to the appropriate Discord channel based on event type and `GuildBinding.ChannelPurpose`.

### 4.7 IPendingQuestionStore (proposed contract -- to be defined in `AgentSwarm.Messaging.Abstractions`)

Proposed shared interface for managing pending agent questions. The Telegram connector on the `feature/telegram` branch defines a version at `src/AgentSwarm.Messaging.Abstractions/IPendingQuestionStore.cs`. The Discord connector proposes the same interface with platform-agnostic parameter names:

```csharp
public interface IPendingQuestionStore
{
    Task StoreAsync(AgentQuestionEnvelope envelope, long channelId, long platformMessageId, CancellationToken ct);
    Task<PendingQuestion?> GetAsync(string questionId, CancellationToken ct);
    Task MarkAnsweredAsync(string questionId, CancellationToken ct);
    Task MarkAwaitingCommentAsync(string questionId, CancellationToken ct);
    Task RecordSelectionAsync(string questionId, string selectedActionId,
        string selectedActionValue, long respondentUserId, CancellationToken ct);
    Task<IReadOnlyList<PendingQuestion>> GetExpiredAsync(CancellationToken ct);
}
```

For Discord, `channelId` is the Discord channel snowflake ID (cast to `long`), `platformMessageId` is the Discord message snowflake ID (cast to `long`), and `respondentUserId` is the Discord user snowflake ID (cast to `long`). The `GetAsync(questionId)` path is the primary lookup: the `ComponentInteractionHandler` parses `QuestionId` from the component `custom_id` field (`q:{QuestionId}:{ActionId}` format).

### 4.8 IDiscordInteractionStore (to be defined in `AgentSwarm.Messaging.Discord`)

Provides persistence and idempotency tracking for inbound Discord interactions.

```csharp
public interface IDiscordInteractionStore
{
    Task<bool> PersistAsync(DiscordInteractionRecord record, CancellationToken ct);
    Task MarkProcessingAsync(ulong interactionId, CancellationToken ct);
    Task MarkCompletedAsync(ulong interactionId, CancellationToken ct);
    Task MarkFailedAsync(ulong interactionId, string errorDetail, CancellationToken ct);
    Task<IReadOnlyList<DiscordInteractionRecord>> GetRecoverableAsync(int maxRetries, CancellationToken ct);
}
```

`PersistAsync` returns `false` if the `InteractionId` already exists (the `UNIQUE` constraint on `InteractionId` is the canonical deduplication mechanism). Since Discord interaction IDs are globally unique snowflake IDs, deduplication is straightforward -- no equivalent to Telegram's `update_id` scoping is needed.

### 4.9 IMessageSender (proposed contract -- to be defined in `AgentSwarm.Messaging.Core`)

Proposed shared interface for platform-agnostic outbound sends. The Telegram connector on the `feature/telegram` branch defines a version at `src/AgentSwarm.Messaging.Core/IMessageSender.cs`. The Discord connector proposes the same interface with platform-agnostic parameter names:

```csharp
public interface IMessageSender
{
    Task<SendResult> SendTextAsync(long channelId, string text, CancellationToken ct);
    Task<SendResult> SendQuestionAsync(long channelId, AgentQuestionEnvelope envelope, CancellationToken ct);
}
```

The concrete `DiscordMessageSender` implements this interface and wraps Discord.Net's REST client. **Rendering boundary:** `DiscordMessageSender` is the sole owner of question rendering -- it builds Discord embeds, component rows (buttons/select menus), thread management, and agent identity display. For non-question messages, the pre-rendered embed JSON from `Payload` is sent directly.

**Agent identity rendering:** All outbound embeds include an author section showing agent identity:
- **Name**: Agent name (from `AgentId` or resolved display name)
- **Role**: Agent role (e.g., "Architect", "Coder", "Tester")
- **Current task**: Task ID and short description
- **Confidence score**: Displayed as a progress-bar emoji sequence (e.g., 4 filled + 1 empty = 80%)
- **Blocking question**: Indicator when the agent is blocked waiting for human input

### 4.10 IAuditLogger (proposed contract -- to be defined in `AgentSwarm.Messaging.Abstractions`)

Proposed shared interface for audit logging. The epic-level FR-006 (`.forge-attachments/agent_swarm_messenger_user_stories.md` lines 115-125) requires audit logging across all connectors. The Telegram connector on the `feature/telegram` branch defines a version at `src/AgentSwarm.Messaging.Abstractions/IAuditLogger.cs`:

```csharp
public interface IAuditLogger
{
    Task LogAsync(AuditEntry entry, CancellationToken ct);
    Task LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken ct);
}
```

For Discord, audit entries store guild ID, channel ID, interaction ID, and Discord message ID in the `Details` JSON field. The `MessageId` field on `AuditEntry` / `HumanResponseAuditEntry` carries the Discord message snowflake ID (stringified). `ExternalUserId` is the Discord user snowflake ID (stringified). `Platform` is always `"Discord"`.

### 4.11 IDeduplicationService (proposed contract -- to be defined in `AgentSwarm.Messaging.Abstractions`)

Proposed shared interface with a three-method contract (`TryReserveAsync`, `IsProcessedAsync`, `MarkProcessedAsync`). The Telegram connector on the `feature/telegram` branch defines a version at `src/AgentSwarm.Messaging.Abstractions/IDeduplicationService.cs`. For Discord, the event ID is the interaction snowflake ID (stringified). Backed by a sliding-window cache with configurable TTL (default 1 hour).

---

## 5. End-to-End Sequence Flows

### 5.1 Scenario: Human runs `/agent ask architect design update-service cache strategy`

```text
Human (Discord)    DiscordGatewayService   InteractionPipeline   SlashCommandDispatcher   AuthZ    SwarmCommandBus    Orchestrator
      |                    |                      |                      |                   |            |                |
      |--slash command---->|                      |                      |                   |            |                |
      |                    |--persist InteractionRecord (interaction_id) |                   |            |                |
      |                    |  (UNIQUE constraint; INSERT fails = dup --> skip + return)      |            |                |
      |                    |--DeferAsync()------->|                      |                   |            |                |
      |                    |  (3s ACK to Discord; sent AFTER durable persist)                |            |                |
      |                    |--map to MessengerEvent                     |                   |            |                |
      |                    |--ProcessAsync------->|                      |                   |            |                |
      |                    |                      |--dedup check-------->|                   |            |                |
      |                    |                      |--AuthorizeAsync----->|--check guild----->|            |                |
      |                    |                      |                      |  + channel + role |            |                |
      |                    |                      |                      |<--yes + binding---|            |                |
      |                    |                      |--parse "ask ..."---->|                   |            |                |
      |                    |                      |                      |--CreateTaskCmd---->|--publish-->|                |
      |                    |                      |                      |                   |            |--create work-->|
      |                    |                      |                      |<--ack + taskId----|<-----------|                |
      |                    |                      |                      |                   |            |                |
      |<--followup embed---|<--enqueue reply------|<--------------------|                   |            |                |
      |  "Task created:    |                      |                      |                   |            |                |
      |   #T-42 assigned   |                      |                      |                   |            |                |
      |   to architect"    |                      |                      |                   |            |                |
```

**Key invariants:**
1. The `DiscordInteractionRecord` (including `RawPayload`) is persisted **before** `DeferAsync()` acknowledges the interaction. If the `UNIQUE` constraint on `InteractionId` rejects the insert, the interaction is a duplicate and is dropped immediately without acknowledgement. This persist-before-ACK order eliminates the crash window where Discord considers the interaction delivered but the durable inbox has not recorded it. On restart, `InteractionRecoverySweep` re-processes any records with `IdempotencyStatus` in `Received`, `Processing`, or `Failed` (where `AttemptCount < MaxRetries`).
2. `DeferAsync()` is called immediately after the durable persist, within the 3-second Discord interaction response deadline. Authorization and command processing proceed asynchronously after the ACK.
3. Authorization validates guild ID, channel ID, and user roles against the `GuildBinding` registry. Unauthorized users receive an ephemeral error message visible only to them.
4. The reply is an embed posted as a follow-up to the deferred interaction, containing the task ID and assigned agent information.
5. `AuditLogger` records the `/agent ask` command with correlation ID, guild ID, channel ID, user ID, and the Discord message ID of the follow-up response.

### 5.2 Scenario: Agent asks a blocking question, operator answers via button

```text
Orchestrator    SwarmCommandBus   DiscordConnector    OutboundQueue   QueueProcessor   DiscordSender      Human (Discord)
      |               |                |                   |                |                |                |
      |--AgentQuestion->               |                   |                |                |                |
      |  (severity=High,               |                   |                |                |                |
      |   timeout=30min)               |                   |                |                |                |
      |               |--deliver------>|                   |                |                |                |
      |               |                |--enqueue--------->|                |                |                |
      |               |                |  (preview Payload |                |                |                |
      |               |                |   + envelope JSON)|                |                |                |
      |               |                |                   |--dequeue------>|                |                |
      |               |                |                   |                |--SendQuestion-->|                |
      |               |                |                   |                |  (build embed + |                |
      |               |                |                   |                |   component rows|                |
      |               |                |                   |                |   with buttons) |                |
      |               |                |                   |                |                |--send embed---->|
      |               |                |                   |                |                |  [Approve]      |
      |               |                |                   |                |                |  [Reject]       |
      |               |                |                   |                |                |  [Need info]    |
      |               |                |                   |                |                |  [Delegate]     |
      |               |                |                   |                |<--SendResult----|                |
      |               |                |                   |<--markSent-----|                |                |
      |               |                |                   |--persist PQ----|                |                |
      |               |                |                   |                |                |                |
      |               |                |                   |                |                | (user clicks    |
      |               |                |                   |                |                |  "Approve")     |
      |               |                |                   |                |                |<-component------|
      |               |                |<--route interaction|               |                |                |
      |               |                |--parse custom_id->|                |                |                |
      |               |                |  q:Q-42:approve   |                |                |                |
      |               |                |--lookup PQ------->|                |                |                |
      |               |                |--resolve Value--->|                |                |                |
      |               |                |--HumanDecisionEvent               |                |                |
      |               |<--publish------|                   |                |                |                |
      |<--deliver------|               |                   |                |                |                |
      |               |                |--audit record---->|                |                |                |
      |               |                |--update embed---->|                |                |--edit message-->|
      |               |                |  (disable buttons,|                |                |  "Approved by   |
      |               |                |   show result)    |                |                |   @operator"    |
```

**Key invariants:**
1. The question embed includes agent identity fields (name, role, task, confidence), the question title/body, severity indicator (color-coded embed sidebar), expiration time, and the proposed default action (if any).
2. Action buttons are rendered as a Discord component row. Each button's `custom_id` follows the format `q:{QuestionId}:{ActionId}`. When more than 5 actions exist, a select menu is used instead (Discord limits component rows to 5 buttons).
3. On button click, the `ComponentInteractionHandler` parses `QuestionId` and `ActionId` from `custom_id`, looks up the `PendingQuestionRecord` via `IPendingQuestionStore.GetAsync(questionId)`, resolves `HumanAction.Value` to populate `HumanDecisionEvent.ActionValue`.
4. For actions with `RequiresComment = true`, the handler responds with a Discord modal dialog to collect the comment text. The `PendingQuestionRecord.Status` transitions to `AwaitingComment` until the modal is submitted.
5. After the decision is recorded, the original embed is edited to disable buttons and show the result (who approved/rejected, when).
6. `AuditLogger` records the decision with guild ID, channel ID, user ID, interaction ID, Discord message ID, and correlation ID.

### 5.3 Scenario: Bot reconnects after Gateway disconnect

```text
Discord Gateway    DiscordGatewayService     DiscordSocketClient    Logger
      |                    |                        |                  |
      |--disconnect------->|                        |                  |
      |  (close code 4000) |                        |                  |
      |                    |--Disconnected event---->|                  |
      |                    |                        |--log close code-->|
      |                    |                        |  + reason         |
      |                    |                        |                  |
      |                    |  (Discord.Net auto-reconnect with         |
      |                    |   exponential backoff: 1s, 2s, 4s, 8s)   |
      |                    |                        |                  |
      |<--reconnect--------|                        |                  |
      |  (resume session   |                        |                  |
      |   or new session)  |                        |                  |
      |                    |--Connected event------->|                  |
      |                    |                        |--log reconnect--->|
      |                    |--re-register slash----->|                  |
      |                    |  commands (if needed)   |                  |
      |                    |--re-subscribe events--->|                  |
      |                    |                        |                  |
```

**Key invariants:**
1. Discord.Net's `DiscordSocketClient` has built-in reconnection with exponential backoff. The `DiscordGatewayService` does not implement custom reconnection logic -- it relies on the library.
2. On reconnection, Discord's Gateway supports session resumption (replaying missed events since the last sequence number). If the session cannot be resumed (e.g., session timed out), a new session is established and the bot re-registers slash commands.
3. The `DiscordGatewayService` subscribes to the `Disconnected` and `Connected` events for logging and metrics. A `discord.gateway.reconnect` counter metric is emitted on each reconnect.
4. The recovery target is < 15 seconds for Gateway reconnect (per performance requirements).
5. During disconnection, outbound messages continue to be enqueued in the `OutboundMessageQueue`. The `OutboundQueueProcessor` will encounter REST API failures during the disconnect window and retry with exponential backoff. No messages are lost.

### 5.4 Scenario: High-priority alert delivery

```text
Orchestrator    SwarmCommandBus   DiscordConnector    OutboundQueue   QueueProcessor   DiscordSender      Alert Channel
      |               |                |                   |                |                |                |
      |--AlertEvent--->|               |                   |                |                |                |
      |  (severity=    |--deliver----->|                   |                |                |                |
      |   Critical)    |               |--resolve alert    |                |                |                |
      |               |                |  channel via      |                |                |                |
      |               |                |  IGuildRegistry   |                |                |                |
      |               |                |--enqueue (Critical)>              |                |                |
      |               |                |                   |                |                |                |
      |               |                |                   |--dequeue------>|                |                |
      |               |                |                   |  (Critical     |                |                |
      |               |                |                   |   dequeued     |                |                |
      |               |                |                   |   before Low   |                |                |
      |               |                |                   |   status msgs) |                |                |
      |               |                |                   |                |--send embed---->|                |
      |               |                |                   |                |  (red sidebar,  |--deliver------>|
      |               |                |                   |                |   @here ping)   |                |
```

**Key invariants:**
1. Alerts are routed to the guild's alert channel (`ChannelPurpose = Alert`) via `IGuildRegistry.GetAlertChannelAsync`.
2. Critical and High severity alerts are dequeued before Normal and Low status updates, ensuring priority delivery.
3. Critical alerts include an `@here` mention to notify online guild members.
4. Alert embeds use color-coded sidebars: red for Critical, orange for High, blue for Normal, gray for Low.
5. Low-priority status updates may be batched (multiple agent statuses combined into a single embed) when queue depth exceeds a configurable threshold (default 50 pending messages), reducing API calls and respecting rate limits.

### 5.5 Scenario: Unauthorized user attempts command

```text
Human (Discord)    DiscordGatewayService   InteractionPipeline    AuthZ Service
      |                    |                      |                    |
      |--/agent ask ...--->|                      |                    |
      |                    |--DeferAsync()-------->|                    |
      |                    |--persist record------>|                    |
      |                    |--ProcessAsync-------->|                    |
      |                    |                      |--AuthorizeAsync---->|
      |                    |                      |                    |--check binding
      |                    |                      |                    |  (no GuildBinding
      |                    |                      |                    |   for this guild/
      |                    |                      |                    |   channel, OR user
      |                    |                      |                    |   lacks required
      |                    |                      |                    |   role)
      |                    |                      |<--denied + reason---|
      |                    |                      |--audit rejection--->|
      |<--ephemeral msg----|<--followup ephemeral--|                    |
      |  "You do not have  |                      |                    |
      |   permission..."   |                      |                    |
```

**Key invariants:**
1. The rejection message is ephemeral (visible only to the unauthorized user), preventing information leakage.
2. The denial is logged in the audit trail with the user's Discord ID, guild, channel, and the specific authorization failure reason.
3. Three authorization checks are performed in order: (a) guild ID is registered, (b) channel ID is authorized for the guild, (c) user has a required role. The first failing check determines the denial reason.

---

## 6. Assembly Map

| Assembly | Layer | New / Shared | Key Types |
|---|---|---|---|
| `AgentSwarm.Messaging.Abstractions` | Abstractions | Shared | `IMessengerConnector`, `IOutboundQueue`, `IDeduplicationService`, `IPendingQuestionStore`, `MessengerMessage`, `AgentQuestion`, `AgentQuestionEnvelope`, `HumanAction`, `HumanDecisionEvent`, `MessengerEvent`, `OutboundMessage`, `SwarmCommand`, `MessageSeverity` |
| `AgentSwarm.Messaging.Core` | Domain | Shared | `IMessageSender`, `IUserAuthorizationService`, `IOperatorRegistry`, `ITaskOversightRepository`, `AuthorizationResult`, `SendResult` |
| `AgentSwarm.Messaging.Discord` | Connector | **New** | `DiscordGatewayService`, `DiscordInteractionRouter`, `SlashCommandDispatcher`, `ComponentInteractionHandler`, `DiscordMessengerConnector`, `DiscordMessageSender`, `DiscordInteractionMapper`, `IGuildRegistry`, `IDiscordInteractionPipeline`, `IDiscordInteractionStore`, `DiscordOptions`, `DiscordServiceCollectionExtensions` |
| `AgentSwarm.Messaging.Persistence` | Infrastructure | Shared | `PersistentOutboundQueue`, `PersistentAuditLogger`, `PersistentPendingQuestionStore`, `MessagingDbContext` |
| `AgentSwarm.Messaging.Worker` | Host | Shared | `OutboundQueueProcessor`, `QuestionRecoverySweep`, `QuestionTimeoutService`, `Program` |
| `AgentSwarm.Messaging.Tests` | Test | Shared + New | `DiscordInteractionMapperTests`, `DiscordPipelineTests`, `GuildRegistryTests`, `ComponentInteractionHandlerTests`, `DiscordOptionsTests` |

**Dependency graph:**

```text
Worker --> Core --> Abstractions
Worker --> Persistence --> Abstractions
Discord --> Core --> Abstractions
Discord --> Abstractions
Tests --> Discord, Core, Abstractions, Persistence
```

`AgentSwarm.Messaging.Worker` depends on `AgentSwarm.Messaging.Discord` only at DI registration time (via `DiscordServiceCollectionExtensions`), not at compile time for business logic. The `OutboundQueueProcessor` dispatches through the `IMessageSender` abstraction in `Core`.

---

## 7. Configuration

### 7.1 DiscordOptions

```csharp
public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    public required string BotToken { get; set; }
    public required ulong GuildId { get; set; }
    public required ulong ControlChannelId { get; set; }
    public required ulong AlertChannelId { get; set; }
    public ulong[]? WorkstreamChannelIds { get; set; }
    public GatewayIntents GatewayIntents { get; set; } =
        GatewayIntents.Guilds |
        GatewayIntents.GuildMessages |
        GatewayIntents.MessageContent |
        GatewayIntents.GuildMessageReactions;
    public GuildBindingConfig[] GuildBindings { get; set; } = [];
}
```

**Security:** `BotToken` must be stored in Azure Key Vault, DPAPI-protected local storage, or Kubernetes secret. Never logged.

### 7.2 appsettings.json structure

```json
{
  "Discord": {
    "BotToken": "-- from secret store --",
    "GuildId": 123456789012345678,
    "ControlChannelId": 234567890123456789,
    "AlertChannelId": 345678901234567890,
    "WorkstreamChannelIds": [456789012345678901],
    "GuildBindings": [
      {
        "GuildId": 123456789012345678,
        "ChannelId": 234567890123456789,
        "ChannelPurpose": "Control",
        "TenantId": "tenant-1",
        "WorkspaceId": "ws-main",
        "AllowedRoleIds": [567890123456789012, 678901234567890123]
      }
    ]
  }
}
```

---

## 8. Discord-Specific Design Decisions

### 8.1 Slash Command Registration

Slash commands are registered as guild commands (not global commands) for faster propagation (guild commands update instantly; global commands take up to 1 hour). The `/agent` command group with subcommands (`ask`, `status`, `approve`, `reject`, `assign`, `pause`, `resume`) is registered on startup via `DiscordGatewayService`.

### 8.2 Interaction Acknowledgement Strategy

Discord requires interaction responses within 3 seconds. The pipeline uses `DeferAsync()` immediately on receiving any interaction, then follows up with the full response. This approach is simpler than trying to race the authorization + command processing against the deadline.

### 8.3 Component custom_id Format

Button and select menu `custom_id` values follow the format `q:{QuestionId}:{ActionId}`. This is shorter than the full capacity of Discord's 100-character `custom_id` limit but maintains consistency with the Telegram `callback_data` format. The `q:` prefix distinguishes question-related interactions from other potential component types in the future.

### 8.4 Rate Limit Handling

Discord REST API rate limits are per-route and communicated via response headers (`X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset`). The `DiscordMessageSender` implements a per-route token bucket that reads these headers and proactively delays sends when approaching the limit. Discord.Net also has built-in rate limit handling that queues requests when limits are hit -- the sender cooperates with this rather than duplicating it.

For 100+ active agents posting status updates, Low-severity messages are batched: multiple agent status updates are combined into a single embed with a table layout when queue depth exceeds the batching threshold (default 50 pending Low-severity messages). This reduces API calls from potentially 100+/minute to approximately 10/minute for status updates.

### 8.5 Thread Usage

Per-task conversations use Discord threads for isolation. When an agent question is posted to a `Workstream` channel, the `DiscordMessageSender` creates a thread (or reuses an existing thread named after the task ID). Follow-up messages, status updates, and additional questions for the same task are posted to the same thread. Thread auto-archive duration is set to 24 hours (configurable).

### 8.6 Embed Design for Agent Identity

Every agent-originated embed includes a consistent identity block:

```text
+--------------------------------------------------+
| [Agent Avatar]  build-agent-3 (Coder)            |
|                 Task: #T-42 - Cache strategy      |
|                 Confidence: [####-] 80%            |
|                 Status: Blocked - awaiting input  |
+--------------------------------------------------+
| Question Title                                    |
| Question body text...                            |
|                                                  |
| Default action if no response: Approve           |
| Expires: 30 minutes                              |
+--------------------------------------------------+
| [Approve] [Reject] [Need more info] [Delegate]  |
+--------------------------------------------------+
```

The embed uses Discord's author field for agent name/role, color sidebar for severity, fields for task/confidence/status, and a component action row for buttons.

---

## 9. Observability

| Metric | Type | Description |
|---|---|---|
| `discord.gateway.connected` | Gauge | 1 when connected, 0 when disconnected. |
| `discord.gateway.reconnect` | Counter | Incremented on each Gateway reconnect. |
| `discord.interactions.received` | Counter | Total interactions received, tagged by type. |
| `discord.interactions.processed` | Counter | Successfully processed interactions. |
| `discord.interactions.rejected` | Counter | Interactions rejected by authorization. |
| `discord.interactions.duplicated` | Counter | Duplicate interactions suppressed. |
| `discord.send.first_attempt_latency_ms` | Histogram | Enqueue-to-REST-200 latency for first attempts. |
| `discord.send.retry_count` | Counter | Total retry attempts for outbound messages. |
| `discord.send.dead_lettered` | Counter | Messages moved to dead-letter queue. |
| `discord.ratelimit.hits` | Counter | Times a Discord rate limit was hit, tagged by route. |
| `discord.outbound.queue_depth` | Gauge | Current outbound queue depth by severity. |

All metrics are emitted via OpenTelemetry. Structured logging uses `ILogger<T>` with correlation ID, guild ID, channel ID, and user ID in every log scope.

---

## 10. Reliability

### 10.1 Gateway Reconnection

Discord.Net's `DiscordSocketClient` handles Gateway reconnection with built-in exponential backoff. The `DiscordGatewayService` monitors the `Disconnected` event and logs close codes. If the close code indicates a non-recoverable error (e.g., 4004 Authentication Failed, 4014 Disallowed Intents), the service logs a critical error and stops -- it does not retry indefinitely against a permanent failure.

### 10.2 Inbound Deduplication

Two-layer deduplication:
1. **IDeduplicationService** (in-memory/cache): Fast-path suppression of duplicate interaction IDs within the TTL window.
2. **IDiscordInteractionStore** (database): UNIQUE constraint on `InteractionId` prevents duplicate processing across restarts and multi-instance deployments.

### 10.3 Outbound Durability

Durable outbound pipeline:
1. Messages are persisted to the `OutboundMessage` table before being processed.
2. `OutboundQueueProcessor` dequeues, sends via `IMessageSender`, and marks sent/failed.
3. Failed messages retry with exponential backoff up to `MaxAttempts` (default 5).
4. Exhausted messages are dead-lettered with full error history.

### 10.4 Rate-Limit-Aware Batching

For 100+ active agents, Low-severity status updates are the highest-volume message type. The batching strategy:
1. `OutboundQueueProcessor` checks pending Low-severity count before dequeuing Low messages.
2. When count exceeds the batching threshold (default 50), the processor collects up to 10 Low-severity messages and passes them to `DiscordMessageSender` as a batch.
3. The sender combines them into a single embed with an agent status table.
4. This reduces per-minute API calls for status updates, staying within Discord's rate limits.

Critical and High severity messages are never batched -- they are sent individually and immediately.
