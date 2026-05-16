# Technical Specification -- Discord Messenger Support (qq-DISCORD-MESSENGER-SU)

## 1. Problem Statement

The agent swarm -- 100+ autonomous agents performing product planning, architecture, coding, testing, release orchestration, incident response, and operational remediation -- currently has no Discord-based human interface. Engineering operators need to start tasks, answer blocking questions, approve or reject actions, query agent status, and receive alerts through team-style Discord channels on desktop and mobile.

Discord is architecturally distinct from the other supported messenger platforms (Telegram, Slack, Microsoft Teams) in two critical ways:

1. **Dual-transport model.** Inbound bot events arrive through the Discord Gateway WebSocket connection, where the bot declares Gateway Intents for the event groups it needs. Outbound messages use the Discord REST API. This is unlike Telegram's webhook/polling model or Slack's Events API -- the Gateway is a persistent, stateful WebSocket session that requires lifecycle management, session resumption, and reconnection with backoff.

2. **Interaction-acknowledgement deadline.** Discord mandates that bot interactions (slash commands, button clicks, select menu selections) be acknowledged within 3 seconds. Failure to acknowledge causes the interaction to fail visibly for the user. This deadline constrains the inbound processing pipeline: the durable persist and a fast in-memory authorization check must both complete within 3 seconds, followed by either an immediate ephemeral rejection (if unauthorized) or a `DeferAsync()` call (if authorized) that enables asynchronous command processing via deferred follow-up responses.

The Discord connector must slot into the shared `IMessengerConnector` abstraction defined in `AgentSwarm.Messaging.Abstractions` (see architecture.md Section 4.1) and satisfy the requirements enumerated in the story description and the epic-level brief (`.forge-attachments/agent_swarm_messenger_user_stories.md`, Story ID MSG-DC-001).

### 1.1 Key Acceptance Criteria (from story description)

| ID | Criterion |
|---|---|
| AC-1 | User can run `/agent ask architect design update-service cache strategy` |
| AC-2 | Agent can post a question with Approve, Reject, Need more info, and Delegate buttons |
| AC-3 | Bot reconnects automatically after Gateway disconnect |
| AC-4 | Duplicate interaction IDs are ignored |
| AC-5 | High-priority alerts are delivered before low-priority progress spam |
| AC-6 | Unauthorized Discord users cannot issue agent-control commands |

---

## 2. In-Scope

The following capabilities are in scope for this story. Each item maps to a specific requirement area from the story description.

### 2.1 Protocol

- Discord Gateway WebSocket connection for receiving inbound bot events (slash commands, button clicks, select menu selections, modal submissions).
- Discord REST API for all outbound message delivery (embeds, components, thread replies, message edits).
- Gateway Intent declaration: `Guilds`, `GuildMessages`, `MessageContent`, `GuildMessageReactions`. The `GuildMessageReactions` intent is declared solely to match the `DiscordOptions.GatewayIntents` configuration defined in architecture.md Section 2.2. No reaction events are subscribed to, processed, or acted upon in this story; reactions are explicitly excluded as input (see Section 3, "Message reactions as input").
- Gateway session resumption on reconnect (replay missed events from last sequence number).
- Automatic reconnection with exponential backoff on Gateway disconnect.

### 2.2 C# Library

- Discord.Net (`Discord.Net.WebSocket` for `DiscordSocketClient`, `Discord.Net.Rest` for REST API access).
- .NET 8+ target framework, consistent with the epic-level mandate.
- All Discord API access goes through Discord.Net -- no raw WebSocket or HTTP calls.

### 2.3 Interaction Model

- Slash commands via Discord's application command system, registered as guild commands for instant propagation.
- Buttons rendered as Discord message components (action rows with up to 5 buttons per row).
- Select menus used when more than 5 actions are available for a single question.
- Modal dialogs for collecting comment text when `HumanAction.RequiresComment = true`.
- Threaded replies for per-task conversation isolation in Workstream channels.
- Rich embeds with color-coded sidebars (severity), author fields (agent identity), and structured fields (task, confidence, status).

### 2.4 Commands

All commands use the `/agent` command group with subcommands:

| Command | Parameters | Description |
|---|---|---|
| `/agent ask` | `<target-agent> <prompt>` | Create a new task and assign it to an agent. Example: `/agent ask architect design update-service cache strategy` |
| `/agent status` | `[agent-id]` | Query swarm-wide status or a specific agent's status. |
| `/agent approve` | `<question-id>` | Approve a pending agent question. |
| `/agent reject` | `<question-id> [reason]` | Reject a pending agent question with optional reason. |
| `/agent assign` | `<task-id> <agent-id>` | Reassign a task to a different agent. |
| `/agent pause` | `<agent-id>` | Pause an active agent. |
| `/agent resume` | `<agent-id>` | Resume a paused agent. |

### 2.5 Channel Model

- **Control channel** (one per guild): Receives operator commands and agent questions requiring human input.
- **Alert channel** (one per guild): Receives priority alerts (Critical and High severity). Critical alerts include `@here` mentions.
- **Workstream channels** (optional, per workspace): Receive task-specific status updates and threaded conversations. Per-task threads provide isolation.

Channel routing is governed by the `GuildBinding` entity and `IGuildRegistry` interface (see architecture.md Sections 3.1 and 4.3).

### 2.6 Agent Identity Display

Every agent-originated outbound embed includes:

| Field | Rendering | Source |
|---|---|---|
| Agent name | Embed author name | `AgentId` or resolved display name |
| Role | Embed author suffix | Agent role (e.g., "Architect", "Coder", "Tester") |
| Current task | Embed field | Task ID and short description |
| Confidence score | Embed field with visual indicator | Numeric score rendered as progress bar (e.g., `[####-] 80%`) |
| Blocking question | Embed field | Indicator when agent is blocked awaiting human input |

### 2.7 Reliability

- **Durable command inbox:** Every inbound interaction is persisted as a `DiscordInteractionRecord` (including full `RawPayload`) before any acknowledgement is sent. After persist, a fast in-memory authorization check runs against cached `GuildBinding` data (guild/channel/role validation). If unauthorized, the pipeline responds immediately with `RespondAsync(text, ephemeral: true)` -- an ephemeral rejection visible only to the caller -- and the pipeline ends (no `DeferAsync()`). If authorized, the pipeline calls `DeferAsync()` (non-ephemeral) to show a "thinking" indicator, then processes the command asynchronously via deferred follow-up. All three steps (persist, auth check, respond/defer) complete within the 3-second interaction deadline. Crash-safe: the `InteractionRecoverySweep` background service re-processes records with `IdempotencyStatus` in `Received`, `Processing`, or `Failed` (where `AttemptCount < MaxRetries`) on startup and every 60 seconds.
- **Durable outbound outbox:** All outbound messages are persisted in the `OutboundMessage` table before processing. The `OutboundQueueProcessor` dequeues, sends via `DiscordMessageSender`, and marks sent or failed. Failed messages retry with exponential backoff up to `MaxAttempts` (default 5). Exhausted messages are dead-lettered.
- **Gateway reconnection:** Discord.Net's built-in exponential backoff handles reconnection. Non-recoverable close codes (4004 Authentication Failed, 4014 Disallowed Intents) cause the service to stop permanently and not retry. In particular, if `MessageContent` or any other declared intent is not enabled in the Developer Portal, the initial Gateway connection fails with close code 4014 and the bot refuses to start -- there is no reduced-capability or slash-command-only fallback mode (see Section 5.1 and Assumption A-6).
- **Interaction idempotency:** Two-layer deduplication: (1) in-memory `IDeduplicationService` for fast-path suppression within TTL window, (2) UNIQUE constraint on `DiscordInteractionRecord.InteractionId` in the database for cross-restart protection.

### 2.8 Performance

| Metric | Target |
|---|---|
| Concurrent active agents posting status updates | 100+ |
| Gateway reconnect time | < 15 seconds |
| P95 outbound latency (enqueue to REST 200) | < 3 seconds |
| Message loss | 0 tolerated |
| Connector recovery after crash | < 30 seconds |
| Message ordering | Preserved per channel |

### 2.9 Rate-Limit Handling

- Read Discord REST API rate limit headers (`X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset`) and proactively delay sends when approaching limits.
- Cooperate with Discord.Net's built-in rate limit handling (automatic request queuing on 429 responses).
- Low-priority status update batching: when outbound queue depth for Low-severity messages exceeds a configurable threshold (default 50), multiple agent status updates are combined into a single embed with a table layout. This reduces API calls from potentially 100+/minute to approximately 10/minute for status updates.
- Critical and High severity messages are never batched -- always sent individually and immediately.

### 2.10 Security

- **Role-based access:** Commands are restricted by Discord role. The `GuildBinding.AllowedRoleIds` field specifies which roles can issue commands. All subcommands -- including `/agent assign` -- are available to all authorized roles by default. `GuildBinding.CommandRestrictions` provides optional per-subcommand role overrides (e.g., restricting `/agent approve` and `/agent reject` to a senior operator role if desired).
- **Guild ID validation:** Only guilds with registered `GuildBinding` entries are authorized.
- **Channel ID validation:** Only channels with active bindings accept commands.
- **Token security:** Bot token stored in Azure Key Vault, DPAPI-protected local storage, or Kubernetes secret. Never logged or included in telemetry.
- **Ephemeral rejections:** Unauthorized command attempts receive an immediate ephemeral response via `RespondAsync(text, ephemeral: true)` -- visible only to the unauthorized user, preventing information leakage. This is possible because the authorization check runs before `DeferAsync()`: the persist-then-auth-then-respond/defer pipeline described in Section 2.7 ensures the ephemeral/non-ephemeral decision is made before any interaction acknowledgement is sent.

### 2.11 Audit

Every command and response is recorded with:

| Audit Field | Source |
|---|---|
| Guild ID | Discord guild snowflake ID |
| Channel ID | Discord channel snowflake ID |
| Message ID | Discord message snowflake ID of the bot's response (follow-up or REST send) |
| User ID | Discord user snowflake ID of the operator |
| Interaction ID | Discord interaction snowflake ID |
| Thread ID | Discord thread snowflake ID (if applicable) |
| Correlation ID | End-to-end trace ID |
| Timestamp | UTC timestamp |

Audit records use the shared `IAuditLogger` interface with `Platform = "Discord"`. Discord-specific IDs are stored in the `Details` JSON field (see architecture.md Section 3.1, AuditLogEntry).

---

## 3. Out-of-Scope

The following items are explicitly excluded from this story.

| Item | Rationale |
|---|---|
| Telegram, Slack, and Microsoft Teams connectors | Separate stories (MSG-TG-001, MSG-SL-001, MSG-MT-001). Discord connector shares abstractions but does not implement other platform logic. |
| Shared abstraction layer implementation | `AgentSwarm.Messaging.Abstractions` and `AgentSwarm.Messaging.Core` interface definitions are proposed by this story and architecture.md, but the concrete shared implementations (e.g., `PersistentOutboundQueue`, `OutboundQueueProcessor`) may be landed by whichever connector story executes first. |
| Agent swarm orchestrator implementation | The `ISwarmCommandBus` interface is consumed by the Discord connector; the orchestrator behind it is a separate system. |
| Discord webhook-only mode | The story mandates full bot/Gateway interaction. Webhook-only posting (without Gateway) is insufficient for receiving slash commands and component interactions. |
| Discord voice channels | No requirement for voice-based interaction with agents. |
| Direct message (DM) bot interactions | The story specifies guild/channel-based interaction. DM support is not required. |
| Multi-guild federation | This story targets exactly one Discord guild. The `GuildBinding` data model can structurally hold multiple entries, but all routing logic, startup validation, and testing assume a single guild binding. Multi-guild routing (cross-guild alert aggregation, guild-to-guild forwarding) is out of scope. |
| Bot presence or activity status | Setting the bot's Discord "Playing" or "Watching" status is not required. |
| Message reactions as input | Only slash commands, buttons, select menus, and modals are supported interaction types. Reactions are not parsed as commands. |
| Discord OAuth2 user authorization flows | Authorization uses Discord role membership within the guild, not OAuth2 flows. |
| Custom emoji management | Agent identity uses text-based progress bars, not custom emoji. |
| Scheduled or cron-triggered commands | Commands are initiated by human operators or agent events, not on a schedule. |

---

## 4. Non-Goals

Non-goals are things that might seem related but are deliberately not objectives of this story.

1. **Replacing the orchestrator's internal messaging.** The Discord connector is a human interface layer. Agent-to-agent communication within the swarm does not flow through Discord.

2. **Providing a web dashboard.** Discord embeds display agent identity and status, but this is not a substitute for a dedicated monitoring dashboard. The embeds are optimized for mobile/desktop Discord clients, not for analytics.

3. **Real-time streaming of agent logs.** Status updates are periodic summaries, not streaming log output. Log verbosity in Discord would hit rate limits and overwhelm operators.

4. **Guaranteeing exactly-once delivery.** The architecture provides at-least-once delivery with idempotent processing. Exactly-once delivery over an external API without two-phase commit is not achievable (see architecture.md Section 3.1, crash-window analysis for Gap A).

5. **Supporting arbitrary Discord bot features.** The bot implements only the slash commands, components, and embeds specified in the story. General-purpose bot features (moderation, music, games, etc.) are not in scope.

6. **Migrating existing Telegram users to Discord.** Each connector operates independently. Cross-platform conversation continuity is not a goal.

7. **Sub-second outbound latency.** The P95 target is < 3 seconds (per the epic-level FR-007). Optimizing for sub-second delivery is not a priority given the durable-queue architecture and rate-limit constraints.

---

## 5. Hard Constraints

These are non-negotiable technical and operational boundaries.

### 5.1 Discord Platform Constraints

| Constraint | Impact | Mitigation |
|---|---|---|
| **3-second interaction response deadline** | All slash command and component interactions must be acknowledged within 3 seconds or they fail visibly for the user. | Persist the interaction, run a fast in-memory auth check, then branch: unauthorized interactions get an immediate `RespondAsync(ephemeral: true)` rejection; authorized interactions get `DeferAsync()` (non-ephemeral) followed by asynchronous processing and a deferred follow-up (see Section 2.7). |
| **5 buttons per action row** | Discord limits each action row to 5 buttons. Questions with more than 5 allowed actions cannot use buttons alone. | Fall back to a select menu component when `AllowedActions.Count > 5`. |
| **100-character `custom_id` limit** | Component `custom_id` values (buttons, select menus) are limited to 100 characters. | The `q:{QuestionId}:{ActionId}` format fits within 100 chars. `QuestionId` is capped at 30 ASCII characters (shared model constraint). |
| **2000-character message content limit** | Regular message content (non-embed) is limited to 2000 characters. | Use embeds for all agent messages. Embed descriptions support up to 4096 characters; total embed size up to 6000 characters. |
| **REST API rate limits** | Per-route rate limits enforced by Discord. Exceeding limits returns HTTP 429 with `Retry-After`. | Read `X-RateLimit-*` headers proactively. Cooperate with Discord.Net's built-in rate limit handler. Batch low-priority status updates. |
| **Gateway Intents are mandatory** | Since Discord API v9, privileged intents (`MessageContent`, `GuildMembers`) require explicit approval in the Discord Developer Portal for bots in 100+ guilds. For single-guild deployments (this story's scope), privileged intents are enabled by toggling them in the Developer Portal without a formal approval process. | All intents listed in Section 2.1 are declared at Gateway connection time. If any declared privileged intent (e.g., `MessageContent`) is not enabled in the Developer Portal, Discord rejects the connection with close code 4014 (Disallowed Intents). This is fatal: the bot refuses to start and does not retry or fall back to slash-command-only operation (see Section 2.7 and Assumption A-6). |
| **Guild commands vs. global commands** | Global commands take up to 1 hour to propagate; guild commands are instant. | Register all commands as guild commands. |
| **Thread auto-archive** | Discord threads auto-archive after a configurable period. | Set auto-archive duration to 24 hours (configurable), consistent with architecture.md Section 8.5. Archived threads preserve content; operators can unarchive manually. Higher durations (up to 7 days) require guild boost Level 2 or higher. |

### 5.2 Shared Platform Constraints

| Constraint | Source | Impact |
|---|---|---|
| **.NET 8+ required** | Epic-level mandate | All assemblies target `net8.0`. |
| **C# implementation** | Epic-level mandate | No polyglot; entire connector is C#. |
| **`IMessengerConnector` interface conformance** | `AgentSwarm.Messaging.Abstractions` (architecture.md Section 4.1) | Discord connector implements `SendQuestionAsync(AgentQuestionEnvelope envelope, CancellationToken ct)` as the canonical contract defined in architecture.md. The envelope wraps `AgentQuestion` with `RoutingMetadata` and `ProposedDefaultActionId`. The connector stores the full envelope in `SourceEnvelopeJson` for send-time rendering. |
| **Shared data models** | Epic-level brief | `AgentQuestion`, `HumanAction`, `HumanDecisionEvent`, `MessengerEvent`, `OutboundMessage` -- all from `AgentSwarm.Messaging.Abstractions`. |
| **Correlation and traceability** | FR-004 | Every message includes `CorrelationId`, `AgentId`, `TaskId`, `ConversationId`, `Timestamp`. |
| **OpenTelemetry integration** | FR-008 | Metrics, traces, and structured logging via OTel. |
| **`QuestionId` max 30 ASCII characters** | Cross-connector compatibility | Even though Discord's `custom_id` supports 100 chars, `QuestionId` stays at 30 for portability. |

### 5.3 Operational Constraints

| Constraint | Detail |
|---|---|
| **Bot token must never be logged** | Token appears in configuration only via secret store reference. `DiscordOptions.BotToken` is never included in logs, telemetry, or error messages. |
| **Audit immutability** | Audit log entries are append-only. No UPDATE or DELETE on the audit table. |
| **Single-process Gateway session** | Discord enforces one Gateway session per bot token (for non-sharded bots). Only one instance of `DiscordGatewayService` can run concurrently. Multiple worker instances must coordinate (e.g., only one holds the Gateway; others process the outbound queue). |

---

## 6. Identified Risks

### 6.1 Technical Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R-1 | **3-second ACK deadline exceeded under load.** If the durable persist (INSERT into `DiscordInteractionRecord`) takes longer than 3 seconds due to database contention, the interaction fails for the user. | Medium | High | Monitor persist latency. Use a fast, local database for the interaction store. Set an SLO for p99 persist latency < 500ms. If exceeded, consider an in-memory WAL buffer with async flush. |
| R-2 | **Gateway disconnect during high-activity window.** A Gateway disconnect causes a window where inbound interactions are lost (if the session cannot resume). During reconnection, queued outbound messages accumulate and may burst against rate limits when the connection restores. | Medium | Medium | Discord.Net's session resumption replays missed events. Outbound queue processor respects rate limits on burst. Dead-letter prevents infinite retry loops. Monitor `discord.gateway.reconnect` metric for frequency. |
| R-3 | **Discord snowflake ID overflow in `long` cast.** Discord snowflake IDs are `ulong` (unsigned 64-bit). The shared `OutboundMessage.ChatId` field is `long` (signed 64-bit). If Discord issues a snowflake ID where the high bit is set (> `long.MaxValue`), the cast produces a negative value. | Low | High | All currently-issued Discord snowflake IDs fit in `long`. Discord's snowflake epoch started in 2015; the high bit will not be set until approximately year 2084. Document the assumption and add a runtime assertion that warns if a negative cast is detected. |
| R-4 | **Rate limit exhaustion with 100+ agents.** At 100 active agents posting status updates every minute, the bot could generate 100+ REST API calls per minute on a single channel route. Discord's per-route rate limit for channel message sends is typically 5 requests per 5 seconds. | High | High | Low-severity batching (combine multiple status updates into one embed) is the primary mitigation. Architecture.md Section 8.4 specifies batching when queue depth exceeds 50 pending Low-severity messages, reducing calls to approximately 10/minute. Monitor `discord.ratelimit.hits` and tune the batching threshold. |
| R-5 | **Discord.Net library version churn.** Discord.Net is a community-maintained library. Breaking changes in major versions could require significant refactoring. | Medium | Medium | Pin to a specific Discord.Net major version (currently 3.x). Monitor the Discord.Net GitHub repository for deprecation notices. Isolate Discord.Net usage behind the `DiscordMessageSender` and `DiscordGatewayService` wrappers so that a library migration affects only two classes. |
| R-6 | **Thread auto-archive causes context loss.** Discord threads auto-archive after the configured duration (default 24 hours per architecture.md Section 8.5). If a task takes longer than the archive window with idle periods, the thread archives and operators must manually unarchive to continue the conversation. | Medium | Low | Default auto-archive is 24 hours (configurable). The thread content is preserved; only visibility is affected. For longer-running tasks, operators can increase the duration up to 7 days (requires guild boost Level 2 or higher) or manually unarchive. |
| R-7 | **Single Gateway session limits horizontal scaling.** Discord enforces one Gateway session per bot token (non-sharded). Only one process can hold the Gateway connection, limiting inbound throughput to a single instance. | Low | Medium | For the 100-agent scale target, a single Gateway instance is sufficient (Discord Gateway can handle thousands of events/second). If scale increases, use Discord's sharding mechanism (one shard per 2500 guilds). For this story's single-guild scope, sharding is not needed. |

### 6.2 Operational Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R-8 | **Bot token compromise.** If the Discord bot token leaks, an attacker can impersonate the bot and issue commands or read messages in authorized channels. | Low | Critical | Store token in Azure Key Vault / Kubernetes secret. Rotate token immediately on suspected compromise via Discord Developer Portal. Audit all bot actions via `IAuditLogger`. Bot token is never logged (enforced by `DiscordOptions` validation). |
| R-9 | **Guild role misconfiguration.** If `AllowedRoleIds` in `GuildBinding` are set incorrectly, either unauthorized users gain access or legitimate operators are locked out. | Medium | High | Validate `GuildBinding` configuration on startup (check that role IDs exist in the target guild). Provide a `/agent status` self-check that shows the calling user's authorization state. Log all authorization decisions with the specific check that passed or failed. |
| R-10 | **Dead-letter queue growth without operator awareness.** If outbound messages consistently fail (e.g., channel deleted, permissions revoked), the dead-letter queue grows silently. | Medium | Medium | Emit `discord.send.dead_lettered` counter metric. Set an alert threshold (e.g., > 10 dead-lettered messages in 5 minutes). Include dead-letter count in `/agent status` output. |
| R-11 | **Interaction recovery sweep creates duplicate processing.** If `InteractionRecoverySweep` re-processes an interaction that was actually completed but whose status update was lost, the command executes twice. | Low | Medium | The `IDeduplicationService` and `ISwarmCommandBus` both enforce idempotency. The sweep checks `IdempotencyStatus` and only re-processes records in `Received`, `Processing`, or `Failed` state where `AttemptCount < MaxRetries`. The orchestrator is the final idempotency boundary. |

### 6.3 Integration Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R-12 | **Shared abstraction interface instability.** The `IMessengerConnector`, `IOutboundQueue`, `ISwarmCommandBus`, and other shared interfaces are proposed -- they may change as Telegram, Slack, and Teams connectors are developed in parallel. | High | Medium | Isolate Discord-specific logic behind `DiscordMessengerConnector` (which implements `IMessengerConnector`). If the shared interface changes, only the connector adapter class needs updating. Pin interface versions within a sprint; negotiate changes via the shared `AgentSwarm.Messaging.Abstractions` package. |
| R-13 | **Shared persistence schema conflicts.** The `OutboundMessage`, `AuditLogEntry`, and `DeadLetterMessage` tables are shared across connectors. Schema migrations from parallel connector stories could conflict. | Medium | Medium | Use EF Core migrations with explicit migration names prefixed by connector (e.g., `Discord_AddGuildBinding`). Coordinate schema changes through the `AgentSwarm.Messaging.Persistence` package. Discord-specific entities (`GuildBinding`, `DiscordInteractionRecord`) use separate tables with a `Discord_` prefix convention. |
| R-14 | **`AgentQuestionEnvelope` schema evolution.** The `AgentQuestionEnvelope` is the canonical contract per architecture.md Section 4.1. If the envelope schema changes (new routing fields, additional metadata), the Discord connector's `SourceEnvelopeJson` serialization and send-time rendering logic must be updated. | Medium | Low | Isolate envelope deserialization behind `DiscordMessageSender` so schema changes are localized. Version the envelope schema. The core `AgentQuestion` payload inside the envelope is stable per the epic-level FR-001 contract. |

---

## 7. Dependencies

### 7.1 External Dependencies

| Dependency | Version | Purpose |
|---|---|---|
| Discord.Net (`Discord.Net.WebSocket`) | 3.x (latest stable) | Gateway WebSocket client, event handling, slash command registration |
| Discord.Net (`Discord.Net.Rest`) | 3.x (latest stable) | REST API client for sending embeds, components, thread management |
| Microsoft.Extensions.Hosting | 8.x | `BackgroundService` for `DiscordGatewayService`, `OutboundQueueProcessor`, `InteractionRecoverySweep` |
| OpenTelemetry .NET SDK | 1.x | Metrics, tracing, structured logging |
| Entity Framework Core | 8.x | Persistence for `GuildBinding`, `DiscordInteractionRecord`, `PendingQuestionRecord` |

### 7.2 Internal Dependencies

| Dependency | Status | Required For |
|---|---|---|
| `AgentSwarm.Messaging.Abstractions` | Proposed (to be created) | `IMessengerConnector`, `IOutboundQueue`, shared data models |
| `AgentSwarm.Messaging.Core` | Proposed (to be created) | `IMessageSender`, `IUserAuthorizationService` |
| `AgentSwarm.Messaging.Persistence` | Proposed (to be created) | `PersistentOutboundQueue`, `PersistentAuditLogger`, `MessagingDbContext` |
| `AgentSwarm.Messaging.Worker` | Proposed (to be created) | `OutboundQueueProcessor`, `QuestionTimeoutService` host |
| `ISwarmCommandBus` implementation | Separate system | Agent swarm orchestrator integration |

---

## 8. Assumptions

| ID | Assumption | Consequence If Wrong |
|---|---|---|
| A-1 | This story supports exactly one Discord guild. The `GuildBinding` model can structurally hold multiple entries, but all routing, command registration, startup validation, and test scenarios target a single guild. | If multi-guild support is needed later, routing logic, guild-command registration, and permission validation must be extended to iterate over bindings. |
| A-2 | Discord.Net 3.x remains stable and maintained. | A library migration to an alternative (e.g., DSharpPlus, Remora.Discord) would require rewriting `DiscordGatewayService` and `DiscordMessageSender`. |
| A-3 | The shared abstraction interfaces (`IMessengerConnector`, `IOutboundQueue`, etc.) will be defined before or concurrently with this story. | If abstractions are not ready, the Discord connector must define local interfaces and refactor when shared ones land. |
| A-4 | The Discord bot has the `applications.commands` and `bot` scopes, plus `Send Messages`, `Use Slash Commands`, `Embed Links`, `Create Public Threads`, `Send Messages in Threads`, and `Manage Messages` permissions in the target guild. | Missing critical permissions cause the bot to refuse to start (operator decision). Startup validation checks guild permissions and throws a fatal exception if any required permission is absent, preventing operation in a degraded state. |
| A-5 | The persistence layer (database) supports UNIQUE constraints and is accessible with < 500ms p99 latency for single-row INSERTs. | Higher latency risks breaching the 3-second interaction ACK deadline. |
| A-6 | The `MessageContent` privileged intent is enabled for the bot application in the Discord Developer Portal (single-guild deployment, toggle only -- no formal verification program required). | This is a hard prerequisite. All intents listed in Section 2.1 are declared at Gateway connection time. If `MessageContent` is not enabled, Discord rejects the initial connection with close code 4014 (Disallowed Intents). This is fatal: the bot refuses to start, does not retry, and does not fall back to reduced-capability or slash-command-only operation (see Section 2.7 and Section 5.1). The operator must enable the intent in the Developer Portal before the bot can start. |

---

## 9. Resolved Design Decisions

The following questions were raised during iteration 1 and resolved by the operator.

| ID | Question | Resolution |
|---|---|---|
| OQ-1 | Should `/agent assign` be restricted to a specific operator role, or available to all authorized users? | **Available to all authorized roles.** No per-subcommand restriction for `/agent assign`; it follows the standard `GuildBinding.AllowedRoleIds` check. |
| OQ-2 | What is the desired thread auto-archive duration? | **24 hours (configurable).** Consistent with architecture.md Section 8.5. Configurable up to 7 days if the guild has boost Level 2 or higher (operator preference). |
| OQ-3 | Should the bot validate guild permissions on startup and refuse to start if critical permissions are missing? | **Refuse to start.** The bot throws a fatal exception on startup if any required guild permission is absent. No degraded-mode operation. |
| OQ-4 | Should this story adopt `AgentQuestionEnvelope` as the canonical `SendQuestionAsync` signature now? | **Yes -- adopt `AgentQuestionEnvelope` as the canonical signature.** Architecture.md Section 4.1 defines `SendQuestionAsync(AgentQuestionEnvelope, CancellationToken)` as the merged contract. The Discord connector implements this directly; the envelope wraps `AgentQuestion` with routing metadata and proposed default action. |

---

## 10. Glossary

| Term | Definition |
|---|---|
| **Gateway** | Discord's WebSocket API for receiving real-time events (messages, interactions, presence). |
| **Gateway Intent** | Bit flag declaring which event groups the bot wants to receive (e.g., `GuildMessages`, `MessageContent`). |
| **Snowflake ID** | Discord's unique identifier format: a 64-bit unsigned integer encoding a timestamp, worker ID, process ID, and sequence number. |
| **Interaction** | A user action on a Discord application command (slash command) or message component (button, select menu, modal). |
| **Ephemeral message** | A Discord message visible only to the user who triggered the interaction. Used for authorization rejections. |
| **`custom_id`** | A developer-defined string (max 100 characters) attached to message components (buttons, select menus) for identifying which component was interacted with. |
| **Deferred response** | A response pattern where the bot acknowledges an interaction with a "thinking" indicator and sends the full response later. Required when processing takes > 3 seconds. |
| **Guild** | A Discord server. The organizational unit containing channels, roles, and members. |
| **GuildBinding** | The data entity that links a Discord guild/channel to the swarm's tenant, workspace, and authorization model. |
| **Dead-letter** | A message that has exhausted its retry budget and is moved to a separate store for manual investigation. |

---

## 11. Cross-References

| Document | Relation |
|---|---|
| `docs/stories/qq-DISCORD-MESSENGER-SU/architecture.md` | Component design, data model, interfaces, sequence flows. This tech spec defines the problem boundaries; architecture.md defines the solution. |
| `docs/stories/qq-DISCORD-MESSENGER-SU/implementation-plan.md` | Sprint breakdown and task ordering. Downstream plan document regenerated after this tech spec and architecture are finalized. |
| `docs/stories/qq-DISCORD-MESSENGER-SU/e2e-scenarios.md` | End-to-end test scenarios and acceptance verification. Downstream plan document regenerated after this tech spec and architecture are finalized. |
| `.forge-attachments/agent_swarm_messenger_user_stories.md` | Epic-level brief with shared requirements (FR-001 through FR-008), recommended architecture, solution structure, and data models. |
