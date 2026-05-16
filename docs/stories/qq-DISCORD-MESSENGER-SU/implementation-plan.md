---
title: "Discord Messenger Support"
storyId: "qq:DISCORD-MESSENGER-SU"
---

# Phase 1: Solution Scaffolding and Shared Abstractions

## Dependencies
- _none -- start phase_

## Stage 1.1: Solution and Project Structure

### Implementation Steps
- [ ] Create solution file `AgentSwarm.Messaging.sln` at repository root with solution folders `src/` and `tests/`
- [ ] Add `AgentSwarm.Messaging.Abstractions` class library project targeting .NET 8 under `src/AgentSwarm.Messaging.Abstractions/`
- [ ] Add `AgentSwarm.Messaging.Core` class library project under `src/AgentSwarm.Messaging.Core/` with project reference to Abstractions
- [ ] Add `AgentSwarm.Messaging.Discord` class library project under `src/AgentSwarm.Messaging.Discord/` with NuGet reference to `Discord.Net` (3.x) and project reference to Core
- [ ] Add `AgentSwarm.Messaging.Persistence` class library project under `src/AgentSwarm.Messaging.Persistence/` with NuGet references to `Microsoft.EntityFrameworkCore.Sqlite` and project reference to Abstractions
- [ ] Add `AgentSwarm.Messaging.Worker` worker service project under `src/AgentSwarm.Messaging.Worker/` with project references to Discord, Core, and Persistence
- [ ] Add `AgentSwarm.Messaging.Tests` xUnit test project under `tests/AgentSwarm.Messaging.Tests/` with references to all src projects plus `Moq`, `FluentAssertions`, `Microsoft.EntityFrameworkCore.InMemory`
- [ ] Add `Directory.Build.props` at repo root with shared properties: `TreatWarningsAsErrors=true`, `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`
- [ ] Verify `dotnet build AgentSwarm.Messaging.sln` succeeds with zero errors and zero warnings

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Solution builds clean -- Given all projects are created with correct references, When `dotnet build` is run, Then build succeeds with zero errors and zero warnings
- [ ] Scenario: Project reference graph is correct -- Given the solution file, When project references are inspected, Then Discord references Core, Core references Abstractions, Persistence references Abstractions, Worker references Discord and Core and Persistence, Tests references all src projects

## Stage 1.2: Shared Data Models

### Implementation Steps
- [ ] Create `MessageSeverity` enum in Abstractions with values: `Critical`, `High`, `Normal`, `Low` (used for priority queuing; name aligns with architecture.md Section 3.1)
- [ ] Create `ChannelPurpose` enum in Abstractions with values: `Control`, `Alert`, `Workstream` (used in GuildBinding to route messages; see architecture.md Section 3.1)
- [ ] Create `HumanAction` record in Abstractions with properties: `ActionId` (string), `Label` (string), `Value` (string), `RequiresComment` (bool) -- per architecture.md Section 3.1; Discord renders Label as button text or select menu option label
- [ ] Create `AgentQuestion` record in Abstractions with properties: `QuestionId` (string, max 30 ASCII chars), `AgentId` (string), `TaskId` (string), `Title` (string), `Body` (string), `Severity` (MessageSeverity), `AllowedActions` (HumanAction[]), `ExpiresAt` (DateTimeOffset), `CorrelationId` (string) -- per architecture.md Section 3.1
- [ ] Create `AgentQuestionEnvelope` record in Abstractions wrapping AgentQuestion with `ProposedDefaultActionId` (string?) and `RoutingMetadata` (Dictionary of string to string) -- for Discord, RoutingMetadata carries DiscordChannelId and optional DiscordThreadId
- [ ] Create `HumanDecisionEvent` record in Abstractions with properties: `Messenger` (string, always "Discord"), `ExternalUserId` (string), `ExternalMessageId` (string), `QuestionId` (string), `SelectedActionId` (string), `ActionValue` (string), `CorrelationId` (string), `Timestamp` (DateTimeOffset)
- [ ] Create `MessengerMessage` record and `MessengerEvent` record in Abstractions as shared inbound/outbound envelope types referenced by IMessengerConnector
- [ ] Create `SwarmCommand` record in Abstractions with properties: `CommandId` (Guid), `CommandType` (string), `AgentTarget` (string), `Arguments` (IReadOnlyDictionary of string to string), `CorrelationId` (string), `Timestamp` (DateTimeOffset)

### Dependencies
- phase-solution-scaffolding-and-shared-abstractions/stage-solution-and-project-structure

### Test Scenarios
- [ ] Scenario: AgentQuestion round-trip serialization -- Given an AgentQuestion with all fields populated including AllowedActions array of HumanAction, When serialized to JSON and deserialized, Then all property values are preserved including ActionId, Label, Value, and RequiresComment
- [ ] Scenario: MessageSeverity ordering -- Given MessageSeverity values, When sorted ascending by integer value, Then order is Critical then High then Normal then Low
- [ ] Scenario: QuestionId constraint -- Given a QuestionId string longer than 30 characters or containing non-ASCII characters, When validated, Then validation fails with a descriptive error

## Stage 1.3: Messenger Abstraction Interfaces

### Implementation Steps
- [ ] Create `IMessengerConnector` interface in Abstractions with methods: `SendMessageAsync(MessengerMessage message, CancellationToken ct)`, `SendQuestionAsync(AgentQuestionEnvelope envelope, CancellationToken ct)`, `ReceiveAsync(CancellationToken ct)` returning `IReadOnlyList<MessengerEvent>` -- per architecture.md Section 4.1
- [ ] Create `OutboundMessage` record in Abstractions with properties: `MessageId` (Guid), `IdempotencyKey` (string), `ChatId` (long, Discord channel snowflake cast to long), `Severity` (MessageSeverity), `Status` (OutboundMessageStatus enum: Pending/Sending/Sent/Failed/DeadLettered), `SourceType` (enum: Question/Alert/StatusUpdate/CommandAck), `Payload` (string), `SourceEnvelopeJson` (string?), `SourceId` (string?), `AttemptCount` (int), `MaxAttempts` (int, default 5), `NextRetryAt` (DateTimeOffset?), `PlatformMessageId` (long?), `CorrelationId` (string), `CreatedAt` (DateTimeOffset), `SentAt` (DateTimeOffset?), `ErrorDetail` (string?)
- [ ] Create `IOutboundQueue` interface in Abstractions with methods: `EnqueueAsync(OutboundMessage, CancellationToken)`, `DequeueAsync(CancellationToken)` returning OutboundMessage or null, `MarkSentAsync(Guid messageId, long platformMessageId, CancellationToken)`, `MarkFailedAsync(Guid messageId, string error, CancellationToken)`, `DeadLetterAsync(Guid messageId, CancellationToken)` -- per architecture.md Section 4.4
- [ ] Create `IMessageSender` interface in Core with methods: `SendTextAsync(long channelId, string text, CancellationToken)` and `SendQuestionAsync(long channelId, AgentQuestionEnvelope envelope, CancellationToken)` both returning `SendResult` -- per architecture.md Section 4.9
- [ ] Create `IAuditLogger` interface in Abstractions with methods: `LogAsync(AuditEntry entry, CancellationToken)` and `LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken)` -- per architecture.md Section 4.10; AuditEntry includes Platform, ExternalUserId, MessageId, Details JSON
- [ ] Create `IDeduplicationService` interface in Abstractions with methods: `TryReserveAsync(string eventId)`, `IsProcessedAsync(string eventId)`, `MarkProcessedAsync(string eventId)` -- three-method contract per architecture.md Section 4.11
- [ ] Create `IPendingQuestionStore` interface in Abstractions with methods: `StoreAsync`, `GetAsync(string questionId)`, `MarkAnsweredAsync`, `MarkAwaitingCommentAsync`, `RecordSelectionAsync`, `GetExpiredAsync` -- per architecture.md Section 4.7
- [ ] Create `ISwarmCommandBus` interface in Abstractions with methods: `PublishCommandAsync(SwarmCommand, CancellationToken)`, `PublishHumanDecisionAsync(HumanDecisionEvent, CancellationToken)`, `QueryStatusAsync(SwarmStatusQuery, CancellationToken)`, `QueryAgentsAsync(SwarmAgentsQuery, CancellationToken)`, `SubscribeAsync(string tenantId, CancellationToken)` returning IAsyncEnumerable of SwarmEvent -- per architecture.md Section 4.6
- [ ] Create `SendResult` record in Core with properties: `Success` (bool), `PlatformMessageId` (long?), `ErrorMessage` (string?) -- return type for IMessageSender methods per architecture.md Section 4.9
- [ ] Create `AuthorizationResult` record in Core with properties: `IsAllowed` (bool), `DenialReason` (string?), `ResolvedBinding` (GuildBinding?) -- return type for IUserAuthorizationService per architecture.md Section 4.5
- [ ] Create `IUserAuthorizationService` interface in Core with method: `AuthorizeAsync(string externalUserId, string chatId, string commandName, CancellationToken)` returning `AuthorizationResult` -- per architecture.md Section 4.5; Discord-specific implementation resolves GuildBinding and validates roles
- [ ] Create ISwarmCommandBus supporting DTOs in Abstractions: `SwarmStatusQuery` (TenantId, AgentId filter), `SwarmAgentsQuery` (TenantId, RoleFilter), `SwarmStatusSummary` (TotalAgents, ActiveTasks, BlockedCount), `AgentInfo` (AgentId, Role, CurrentTask, ConfidenceScore, BlockingQuestion), `SwarmEvent` (EventType, AgentId, Payload, CorrelationId, Timestamp) -- all referenced by ISwarmCommandBus methods
- [ ] Add batch support methods to `IOutboundQueue`: `CountPendingAsync(MessageSeverity severity, CancellationToken)` returning int, and `DequeueBatchAsync(MessageSeverity severity, int maxCount, CancellationToken)` returning `IReadOnlyList<OutboundMessage>` -- required for low-priority batching per architecture.md Section 10.4
- [ ] Add batch send method to `IMessageSender`: `SendBatchAsync(long channelId, IReadOnlyList<OutboundMessage> messages, CancellationToken)` returning `SendResult` -- combines multiple Low-severity status updates into a single summary embed per architecture.md Section 10.4

### Dependencies
- phase-solution-scaffolding-and-shared-abstractions/stage-shared-data-models

### Test Scenarios
- [ ] Scenario: IMessengerConnector contract compiles -- Given a mock implementation of IMessengerConnector, When SendQuestionAsync is called with an AgentQuestionEnvelope, Then the call compiles and completes without error
- [ ] Scenario: OutboundMessage IdempotencyKey derivation -- Given a Question-type OutboundMessage with AgentId "build-agent-3" and QuestionId "Q-42", When IdempotencyKey is computed, Then it equals "q:build-agent-3:Q-42"

# Phase 2: Persistence and Data Access

## Dependencies
- phase-solution-scaffolding-and-shared-abstractions

## Stage 2.1: Database Context and Entity Configuration

### Implementation Steps
- [ ] Create `DiscordInteractionRecord` entity in Persistence with columns: `InteractionId` (ulong PK, Discord snowflake), `InteractionType` (enum: SlashCommand/ButtonClick/SelectMenu/ModalSubmit), `GuildId` (ulong), `ChannelId` (ulong), `UserId` (ulong), `RawPayload` (string, full serialized interaction JSON), `ReceivedAt` (DateTimeOffset), `ProcessedAt` (DateTimeOffset?), `IdempotencyStatus` (enum: Received/Processing/Completed/Failed), `AttemptCount` (int, default 0), `ErrorDetail` (string?) -- per architecture.md Section 3.1
- [ ] Create `GuildBinding` entity in Persistence with columns: `Id` (Guid PK), `GuildId` (ulong), `ChannelId` (ulong), `ChannelPurpose` (enum: Control/Alert/Workstream), `TenantId` (string), `WorkspaceId` (string), `AllowedRoleIds` (ulong[]), `CommandRestrictions` (Dictionary of string to ulong[], nullable), `RegisteredAt` (DateTimeOffset), `IsActive` (bool) -- with UNIQUE(GuildId, ChannelId, WorkspaceId) and index on (GuildId, ChannelPurpose)
- [ ] Create `OutboundMessage` entity in Persistence mapping all fields from the shared OutboundMessage record including `MessageId` (Guid PK), `IdempotencyKey` (string, unique index), `ChatId` (long), `Severity` (int), `Status` (enum), `SourceType` (enum), `Payload` (string), `SourceEnvelopeJson` (string?), `AttemptCount` (int), `MaxAttempts` (int, default 5), `PlatformMessageId` (long?), `CorrelationId` (string)
- [ ] Create `PendingQuestionRecord` entity in Persistence with columns: `QuestionId` (string PK), `AgentQuestion` (string, JSON), `DiscordChannelId` (ulong), `DiscordMessageId` (ulong), `DiscordThreadId` (ulong?), `DefaultActionId` (string?), `DefaultActionValue` (string?), `ExpiresAt` (DateTimeOffset), `Status` (enum: Pending/Answered/AwaitingComment/TimedOut), `SelectedActionId` (string?), `SelectedActionValue` (string?), `RespondentUserId` (ulong?), `StoredAt` (DateTimeOffset), `CorrelationId` (string) -- with index on (Status, ExpiresAt)
- [ ] Create `AuditLogEntry` entity in Persistence with `Platform` (string), `ExternalUserId` (string), `MessageId` (string), `Details` (string, JSON for Discord-specific GuildId/ChannelId/InteractionId/ThreadId), `Timestamp` (DateTimeOffset), `CorrelationId` (string)
- [ ] Create `DeadLetterMessage` entity in Persistence with columns: `Id` (Guid PK), `OriginalMessageId` (Guid), `ChatId` (long), `Payload` (string), `ErrorReason` (string), `FailedAt` (DateTimeOffset), `AttemptCount` (int)
- [ ] Create `MessagingDbContext` class in Persistence inheriting DbContext with DbSet properties for all entity types and Fluent API configurations in `OnModelCreating`
- [ ] Generate initial EF Core migration `Discord_InitialCreate` via `dotnet ef migrations add Discord_InitialCreate` -- all Discord-specific migrations use the `Discord_` prefix

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: DbContext creates all tables -- Given an InMemory database provider, When EnsureCreated is called on MessagingDbContext, Then all tables are created without errors
- [ ] Scenario: Unique InteractionId constraint enforced -- Given a saved DiscordInteractionRecord with InteractionId 12345, When a second record with InteractionId 12345 is inserted, Then DbUpdateException is thrown
- [ ] Scenario: GuildBinding unique constraint -- Given a GuildBinding for (GuildId=1, ChannelId=2, WorkspaceId="ws-main"), When a duplicate is inserted, Then DbUpdateException is thrown

## Stage 2.2: Repository and Deduplication Services

### Implementation Steps
- [ ] Create `IDiscordInteractionStore` interface in Discord project with methods: `PersistAsync(DiscordInteractionRecord)` returning bool (false if duplicate), `MarkProcessingAsync(ulong interactionId)`, `MarkCompletedAsync(ulong interactionId)`, `MarkFailedAsync(ulong interactionId, string errorDetail)`, `GetRecoverableAsync(int maxRetries, CancellationToken)` -- per architecture.md Section 4.8
- [ ] Implement `PersistentDiscordInteractionStore` class in Persistence backed by MessagingDbContext; PersistAsync uses INSERT with UNIQUE constraint as canonical dedup mechanism
- [ ] Implement `PersistentPendingQuestionStore` class in Persistence implementing IPendingQuestionStore, backed by MessagingDbContext
- [ ] Implement `PersistentAuditLogger` class in Persistence implementing IAuditLogger with Platform="Discord" and Discord-specific IDs stored in Details JSON
- [ ] Implement sliding-window in-memory `IDeduplicationService` backed by ConcurrentDictionary with configurable TTL (default 1 hour) for fast-path duplicate suppression; database UNIQUE constraint on DiscordInteractionRecord provides cross-restart protection as second layer
- [ ] Implement `PersistentOutboundQueue` class in Persistence implementing IOutboundQueue; DequeueAsync returns highest-Severity pending message oldest-first within each severity level; CountPendingAsync filters by severity for batch threshold checks; DequeueBatchAsync collects up to N messages of a given severity

### Dependencies
- phase-persistence-and-data-access/stage-database-context-and-entity-configuration

### Test Scenarios
- [ ] Scenario: PersistAsync returns false for duplicate -- Given a DiscordInteractionRecord already persisted, When PersistAsync is called with the same InteractionId, Then it returns false
- [ ] Scenario: In-memory dedup fast-path -- Given an interaction ID already in the sliding-window cache, When TryReserveAsync is called, Then it returns false without querying the database
- [ ] Scenario: Audit log stores Discord details -- Given an AuditEntry with Platform="Discord" and Details JSON containing GuildId and ChannelId, When LogAsync is called, Then a row is persisted with matching Details

## Stage 2.3: Outbox and Dead Letter Stores

### Implementation Steps
- [ ] Implement severity-ordered dequeue in PersistentOutboundQueue: Critical (0) dequeued before High (1) before Normal (2) before Low (3), oldest-first within each severity
- [ ] Implement MarkSentAsync to update OutboundMessage Status to Sent and store the PlatformMessageId (Discord message snowflake cast to long)
- [ ] Implement MarkFailedAsync to increment AttemptCount, set ErrorDetail, compute NextRetryAt with exponential backoff; when AttemptCount >= MaxAttempts (default 5), call DeadLetterAsync
- [ ] Implement DeadLetterAsync: set OutboundMessage.Status to DeadLettered (the row is retained in the outbound table) AND create a linked `DeadLetterMessage` record via `OriginalMessageId` FK with full error history -- per architecture.md Section 3.2 entity relationship `DeadLetterMessage 1--1 OutboundMessage`
- [ ] Implement IdempotencyKey enforcement: UNIQUE constraint on OutboundMessage.IdempotencyKey prevents duplicate enqueues for the same question/alert/status/ack

### Dependencies
- phase-persistence-and-data-access/stage-database-context-and-entity-configuration

### Test Scenarios
- [ ] Scenario: Priority-ordered dequeue -- Given outbound messages with Critical, Normal, and Low severities enqueued, When DequeueAsync is called repeatedly, Then messages are returned Critical first then Normal then Low
- [ ] Scenario: Dead letter on max attempts -- Given an OutboundMessage that has failed 4 times (AttemptCount=4), When MarkFailedAsync is called a fifth time, Then AttemptCount reaches MaxAttempts (5), DeadLetterAsync is called, OutboundMessage.Status becomes DeadLettered, and a linked DeadLetterMessage record is created

# Phase 3: Discord Inbound Pipeline

## Dependencies
- phase-persistence-and-data-access

## Stage 3.1: Gateway Connection and Reconnection

### Implementation Steps
- [ ] Create `DiscordOptions` configuration class in Discord project with properties: `BotToken` (string), `GuildId` (ulong), `ControlChannelId` (ulong), `AlertChannelId` (ulong), `WorkstreamChannelIds` (ulong[]), `GatewayIntents` (GatewayIntents flags, default Guilds|GuildMessages|MessageContent|GuildMessageReactions), `GuildBindings` (GuildBindingConfig[]) -- per architecture.md Section 7.1
- [ ] Create `DiscordGatewayService` as a BackgroundService in Discord project that initializes DiscordSocketClient from Discord.Net with configured GatewayIntents
- [ ] Implement StartAsync: call LoginAsync with TokenType.Bot and configured token, then StartAsync on DiscordSocketClient; register handlers for Connected, Disconnected, InteractionCreated, and Log events; subscribe to SwarmEvents via ISwarmCommandBus.SubscribeAsync for the active tenant
- [ ] Rely on Discord.Net built-in reconnection with exponential backoff; in Disconnected handler, log the close code and reason at Warning level; for non-recoverable close codes (4004 Authentication Failed, 4014 Disallowed Intents), log Critical and stop the service permanently -- do not retry
- [ ] Implement StopAsync: gracefully call LogoutAsync and StopAsync on DiscordSocketClient, then dispose
- [ ] Implement guild-level slash command registration on Ready event: call BulkOverwriteGuildApplicationCommandsAsync with definitions for all 7 subcommands (ask, status, approve, reject, assign, pause, resume) under the `/agent` group -- guild commands for instant propagation
- [ ] Add permission validation on Ready: check bot has required guild permissions (SendMessages, EmbedLinks, UseSlashCommands, CreatePublicThreads, SendMessagesInThreads, ManageMessages); refuse to start if any critical permission is missing per resolved decision OQ-3
- [ ] Create `DiscordInteractionMapper` class in Discord project that maps raw DiscordSocketClient interactions to shared `MessengerEvent` objects for pipeline consumption

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Non-recoverable close code stops service -- Given a Disconnected event with close code 4014 (Disallowed Intents), When the handler executes, Then the service logs Critical and stops permanently without retrying
- [ ] Scenario: Recoverable disconnect triggers auto-reconnect -- Given a Disconnected event with close code 4000 (Unknown Error), When the handler executes, Then Discord.Net auto-reconnects with exponential backoff, the Connected event fires, `discord.gateway.reconnect` counter increments by 1, and slash commands are re-registered if session was not resumed -- per architecture.md Section 5.3 (AC-3)
- [ ] Scenario: Slash commands registered on Ready -- Given the bot connects to a guild, When the Ready event fires, Then BulkOverwriteGuildApplicationCommandsAsync is called with exactly 7 slash command definitions for ask, status, approve, reject, assign, pause, resume
- [ ] Scenario: Missing permissions prevents startup -- Given the bot lacks SendMessages permission, When Ready fires and permissions are checked, Then the service throws a fatal exception and stops

## Stage 3.2: Interaction Routing and Deduplication

### Implementation Steps
- [ ] Create `IDiscordInteractionPipeline` interface in Discord project with method: `ProcessAsync(MessengerEvent messengerEvent, CancellationToken ct)` returning `PipelineResult(bool Handled, string? ResponseText, string CorrelationId)` -- per architecture.md Section 4.2
- [ ] Create `DiscordInteractionRouter` class implementing IDiscordInteractionPipeline that orchestrates the persist-then-defer-then-process flow
- [ ] Implement persist-before-ACK: on InteractionCreated, persist DiscordInteractionRecord (including RawPayload) via IDiscordInteractionStore.PersistAsync BEFORE calling DeferAsync(); if PersistAsync returns false (UNIQUE constraint = duplicate), drop the interaction without acknowledgement
- [ ] Call DeferAsync() immediately after the durable persist, within the 3-second Discord interaction deadline; authorization and command processing proceed asynchronously after the ACK
- [ ] Route SlashCommandInteraction instances to SlashCommandDispatcher and MessageComponentInteraction instances to ComponentInteractionHandler; route ModalSubmit interactions to ComponentInteractionHandler for comment collection
- [ ] Log interaction metadata (InteractionId, Type, UserId, GuildId, ChannelId) at Debug level via ILogger; update DiscordInteractionRecord to MarkCompletedAsync or MarkFailedAsync after processing

### Dependencies
- phase-discord-inbound-pipeline/stage-gateway-connection-and-reconnection

### Test Scenarios
- [ ] Scenario: Duplicate interaction dropped without ACK -- Given a DiscordInteractionRecord already persisted for interaction ID 12345, When InteractionCreated fires with the same ID, Then PersistAsync returns false and DeferAsync is never called
- [ ] Scenario: Persist happens before DeferAsync -- Given an incoming slash command interaction, When the router receives it, Then IDiscordInteractionStore.PersistAsync is called before DeferAsync
- [ ] Scenario: Interaction record status tracked -- Given a new interaction that processes successfully, When complete, Then MarkCompletedAsync is called on the DiscordInteractionRecord

## Stage 3.3: Slash Command Dispatch

### Implementation Steps
- [ ] Create `SlashCommandDispatcher` class in Discord project that reads the subcommand name from interaction data and routes to the matching handler method
- [ ] Implement `/agent ask` handler: extract target-agent and prompt text from options; build SwarmCommand with CommandType="ask"; publish via ISwarmCommandBus.PublishCommandAsync; respond via follow-up embed showing task created confirmation
- [ ] Implement `/agent status` handler: extract optional agent-id filter from options; call ISwarmCommandBus.QueryStatusAsync or QueryAgentsAsync; respond with formatted embed showing agent name, role, task, confidence score, blocking question
- [ ] Implement `/agent approve` handler: extract question-id from options; look up PendingQuestionRecord via IPendingQuestionStore.GetAsync; build HumanDecisionEvent; publish via ISwarmCommandBus.PublishHumanDecisionAsync; respond with confirmation embed
- [ ] Implement `/agent reject` handler: extract question-id and optional reason from options; build HumanDecisionEvent with reject action; publish via ISwarmCommandBus.PublishHumanDecisionAsync; respond with confirmation
- [ ] Implement `/agent assign` handler: extract task-id and agent-id from options; build SwarmCommand with CommandType="assign"; publish via ISwarmCommandBus.PublishCommandAsync; available to all authorized roles per resolved decision OQ-1
- [ ] Implement `/agent pause` and `/agent resume` handlers: extract agent-id; build SwarmCommand with CommandType="pause" or "resume"; publish via ISwarmCommandBus.PublishCommandAsync; respond with ephemeral confirmation
- [ ] All follow-up responses use ModifyOriginalResponseAsync to replace the deferred "thinking" indicator; record the follow-up message ID in IAuditLogger

### Dependencies
- phase-discord-inbound-pipeline/stage-interaction-routing-and-deduplication

### Test Scenarios
- [ ] Scenario: Ask command publishes SwarmCommand -- Given `/agent ask architect design cache strategy`, When dispatched, Then ISwarmCommandBus.PublishCommandAsync receives a SwarmCommand with CommandType="ask" and AgentTarget="architect"
- [ ] Scenario: Approve command publishes HumanDecisionEvent -- Given `/agent approve` with a valid question-id, When dispatched, Then ISwarmCommandBus.PublishHumanDecisionAsync is called with the correct QuestionId and SelectedActionId

## Stage 3.4: Component Interaction Handling

### Implementation Steps
- [ ] Create `ComponentInteractionHandler` class in Discord project that parses custom_id using format `q:{QuestionId}:{ActionId}` to extract QuestionId and ActionId
- [ ] Retrieve PendingQuestionRecord via IPendingQuestionStore.GetAsync(questionId); validate the question exists and Status is Pending
- [ ] For actions where HumanAction.RequiresComment is true, respond with a Discord modal dialog to collect comment text; set PendingQuestionRecord.Status to AwaitingComment via IPendingQuestionStore.MarkAwaitingCommentAsync
- [ ] Handle ModalSubmit interaction: parse the comment text from modal, then proceed with the full HumanDecisionEvent flow using the stored SelectedActionId
- [ ] When AllowedActions count exceeds 5, fall back to select menu rendering instead of buttons (Discord limits action rows to 5 buttons) -- per tech-spec Section 5.1
- [ ] Build HumanDecisionEvent with SelectedActionId, resolved ActionValue from HumanAction.Value, Discord user snowflake as ExternalUserId; call IPendingQuestionStore.RecordSelectionAsync then ISwarmCommandBus.PublishHumanDecisionAsync
- [ ] Update the original message via DiscordMessageSender: disable all buttons/select menus and append a status line showing who took what action and when
- [ ] Log the decision via IAuditLogger.LogHumanResponseAsync with all Discord IDs (GuildId, ChannelId, DiscordMessageId, UserId, InteractionId) in Details JSON

### Dependencies
- phase-discord-inbound-pipeline/stage-interaction-routing-and-deduplication

### Test Scenarios
- [ ] Scenario: Button click resolves pending question -- Given a pending question with QuestionId "Q-42" and user clicks Approve button with custom_id "q:Q-42:approve", When handled, Then HumanDecisionEvent is published and PendingQuestionRecord status changes to Answered
- [ ] Scenario: RequiresComment action shows modal -- Given an action with RequiresComment=true, When the button is clicked, Then a Discord modal dialog is shown and PendingQuestionRecord.Status becomes AwaitingComment
- [ ] Scenario: Expired question click shows error -- Given a PendingQuestionRecord with Status=TimedOut, When a button click arrives for that question, Then the handler responds with ephemeral "This question has timed out" and does not publish a HumanDecisionEvent

# Phase 4: Discord Outbound Pipeline

## Dependencies
- phase-persistence-and-data-access

## Stage 4.1: Message Sender and Thread Management

### Implementation Steps
- [ ] Implement `DiscordMessageSender` class in Discord project implementing `IMessageSender` (from Core); wraps Discord.Net REST client for all outbound sends -- per architecture.md Section 4.9; this class is the sole owner of question rendering
- [ ] Implement SendQuestionAsync: build Discord Embed with author section (agent name, role), fields (task ID, confidence score as ASCII progress bar e.g. "[####-] 80%", blocking question, severity indicator, expiration time, proposed default action); use color-coded sidebar (red=Critical, orange=High, blue=Normal, gray=Low)
- [ ] Implement button rendering: build ComponentBuilder with action buttons using custom_id format `q:{QuestionId}:{ActionId}`; when AllowedActions.Length > 5, use a select menu instead of buttons per Discord's 5-button-per-row limit
- [ ] Implement thread management: create threads for Workstream channel questions using task ID as thread name; set AutoArchiveDuration to 1440 minutes (24 hours, configurable) per resolved decision OQ-2; reuse existing threads by name when posting follow-up messages for the same task
- [ ] Return `SendResult` with `PlatformMessageId` (Discord message snowflake cast to long) from both SendQuestionAsync and SendTextAsync; the caller (OutboundQueueProcessor) is responsible for PendingQuestionRecord persistence via IPendingQuestionStore.StoreAsync -- rendering boundary ends at the REST send
- [ ] Implement SendTextAsync: send pre-rendered embed JSON from OutboundMessage.Payload for non-question messages (alerts, status updates, command acks)
- [ ] For Critical severity alerts routed to alert channel, include @here mention to notify online guild members

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Question embed shows agent identity -- Given an AgentQuestionEnvelope with AgentId="architect", TaskId="T-42", AllowedActions with 4 items, When posted as embed, Then embed contains author name, role, task field, confidence bar, and 4 action buttons
- [ ] Scenario: Thread created with 24h auto-archive -- Given a question routed to a workstream channel, When a thread is created, Then AutoArchiveDuration is 1440 minutes
- [ ] Scenario: Select menu used for more than 5 actions -- Given an AgentQuestion with 7 AllowedActions, When rendered, Then a select menu is used instead of buttons

## Stage 4.2: Outbound Queue and Priority Processing

### Implementation Steps
- [ ] Create `OutboundQueueProcessor` as a BackgroundService in Worker project (shared component) that calls IOutboundQueue.DequeueAsync in a loop with configurable poll interval (default 1 second)
- [ ] Implement severity-aware processing: DequeueAsync returns highest-Severity pending message, oldest-first within a severity; Critical and High messages are never batched and always sent individually
- [ ] For each dequeued message, dispatch to IMessageSender.SendTextAsync or SendQuestionAsync based on SourceType; pass the channel ID (ChatId cast to ulong) and the Payload or SourceEnvelopeJson
- [ ] On successful send, call IOutboundQueue.MarkSentAsync with the messageId and SendResult.PlatformMessageId; for Question SourceType, call IPendingQuestionStore.StoreAsync with the envelope from SourceEnvelopeJson, channelId, and platformMessageId as the post-send hook -- per architecture.md Section 5.1 sequence (markSent then persist PQ)
- [ ] On failure (Discord API exception or timeout), call IOutboundQueue.MarkFailedAsync; when AttemptCount >= MaxAttempts (default 5), call IOutboundQueue.DeadLetterAsync to move to dead letter store
- [ ] Implement Low-severity batch path: before dequeuing Low messages, call IOutboundQueue.CountPendingAsync(MessageSeverity.Low); when count exceeds batching threshold (default 50), call IOutboundQueue.DequeueBatchAsync(MessageSeverity.Low, maxCount: 10) and pass the batch to IMessageSender.SendBatchAsync which combines them into a single summary embed -- per architecture.md Section 10.4
- [ ] During Gateway disconnect window, REST API failures cause messages to retry with exponential backoff; no messages are lost due to durable outbox pattern

### Dependencies
- phase-discord-outbound-pipeline/stage-message-sender-and-thread-management

### Test Scenarios
- [ ] Scenario: Critical messages sent before low priority -- Given 3 queued messages (Low, Critical, Normal), When processor dequeues, Then Critical message is processed first
- [ ] Scenario: Max retry dead-letters with linked record -- Given an OutboundMessage that fails on every send attempt, When AttemptCount reaches MaxAttempts (5), Then DeadLetterAsync is called, OutboundMessage.Status becomes DeadLettered, and a linked DeadLetterMessage record is created
- [ ] Scenario: PendingQuestionRecord created after question send -- Given a Question-type OutboundMessage, When sent successfully, Then IPendingQuestionStore.StoreAsync is called with the returned Discord message ID

## Stage 4.3: Rate Limit Aware Batching

### Implementation Steps
- [ ] Create `RateLimitTracker` class in Discord project maintaining per-route token bucket state from Discord REST rate limit headers (X-RateLimit-Remaining, X-RateLimit-Reset)
- [ ] Implement `WaitForCapacityAsync(string route, CancellationToken ct)` that delays until the token bucket for the given route has available capacity
- [ ] Integrate RateLimitTracker into DiscordMessageSender: await WaitForCapacityAsync before each REST call, then update bucket state from response headers
- [ ] Implement low-priority batching in DiscordMessageSender.SendBatchAsync: receive up to 10 Low-severity OutboundMessages, combine them into a single Discord embed containing an agent status table (one row per agent with name, task, confidence, status), and send as one REST call -- per architecture.md Section 10.4
- [ ] Layer rate limiting: Discord.Net built-in handler as first layer, RateLimitTracker as second proactive layer for visibility and metrics
- [ ] Emit metrics: increment `discord.ratelimit.hits` counter (tagged by route) on each throttle event and update `discord.outbound.queue_depth` gauge (tagged by severity) on each dequeue cycle -- per architecture.md Section 9

### Dependencies
- phase-discord-outbound-pipeline/stage-outbound-queue-and-priority-processing

### Test Scenarios
- [ ] Scenario: Rate limiter delays when bucket empty -- Given a route with 0 remaining capacity and reset in 2 seconds, When WaitForCapacityAsync is called, Then it delays approximately 2 seconds before returning
- [ ] Scenario: Low priority batching consolidates above threshold -- Given 60 Low-severity status messages in queue (CountPendingAsync returns 60), When OutboundQueueProcessor runs, Then DequeueBatchAsync(Low, 10) is called and SendBatchAsync combines 10 messages into a single summary embed

# Phase 5: Security and Reliability

## Dependencies
- phase-discord-inbound-pipeline

## Stage 5.1: Authorization and Role Enforcement

### Implementation Steps
- [ ] Implement `IUserAuthorizationService` (defined in Core) for Discord: receive externalUserId (Discord user snowflake stringified), chatId (Discord channel snowflake stringified), and commandName (subcommand name) -- per architecture.md Section 4.5
- [ ] Implement Discord-specific authorization logic: (1) resolve GuildBinding via IGuildRegistry.GetBindingAsync(guildId, channelId), (2) validate binding exists and IsActive=true, (3) check if CommandRestrictions has an override for the subcommand name -- if so use those role IDs, otherwise use AllowedRoleIds, (4) validate user has at least one required role
- [ ] Integrate authorization into DiscordInteractionRouter: call IUserAuthorizationService.AuthorizeAsync after DeferAsync; on denial, send ephemeral follow-up via FollowupAsync(text, ephemeral: true), then clean up the non-ephemeral deferred "thinking" indicator via DeleteOriginalResponseAsync -- per tech-spec Section 2.10
- [ ] Log all authorization failures via IAuditLogger.LogAsync with Platform="Discord", ExternalUserId, attempted command, and denial reason in Details JSON
- [ ] Three authorization checks in order: (a) guild ID is registered, (b) channel ID is authorized for the guild, (c) user has a required role; first failing check determines the denial reason -- per architecture.md Section 5.5

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Authorized user proceeds -- Given a user with an allowed role in a registered guild's control channel, When they issue a slash command, Then AuthorizeAsync returns allowed and the command is dispatched
- [ ] Scenario: Unauthorized role denied with ephemeral -- Given a user with no allowed roles, When they issue a slash command, Then an ephemeral follow-up is sent and DeleteOriginalResponseAsync cleans up the deferred indicator
- [ ] Scenario: CommandRestrictions per-subcommand override -- Given a GuildBinding with CommandRestrictions restricting "approve" to role ID 999, When a user without role 999 runs /agent approve, Then authorization is denied even if they have a general AllowedRoleId

## Stage 5.2: Guild Registry and Channel Routing

### Implementation Steps
- [ ] Implement `IGuildRegistry` in Discord project with methods: `GetBindingAsync(ulong guildId, ulong channelId, CancellationToken)` returning GuildBinding or null, `GetAlertChannelAsync(ulong guildId, CancellationToken)` returning the Alert-purpose binding, `GetWorkstreamChannelsAsync(ulong guildId, string workspaceId, CancellationToken)`, `IsAuthorizedChannelAsync(ulong guildId, ulong channelId, CancellationToken)`, `RegisterAsync(GuildBinding, CancellationToken)` -- per architecture.md Section 4.3
- [ ] Implement `GuildRegistry` class that loads GuildBinding entities from database on startup and caches them with configurable TTL (default 5 minutes)
- [ ] Implement channel routing: GetBindingAsync resolves by (guildId, channelId) for authorization; GetAlertChannelAsync finds the ChannelPurpose=Alert binding for priority alert routing; GetWorkstreamChannelsAsync finds all Workstream bindings for a workspace
- [ ] Integrate IGuildRegistry into DiscordMessengerConnector: when enqueuing outbound question or alert messages, resolve the target Discord channel via the registry based on ChannelPurpose
- [ ] Implement configuration seeding: on first startup if no GuildBinding exists for configured GuildId, create bindings from DiscordOptions.GuildBindings configuration array

### Dependencies
- phase-security-and-reliability/stage-authorization-and-role-enforcement

### Test Scenarios
- [ ] Scenario: Control channel binding lookup -- Given a GuildBinding with ChannelPurpose=Control for channelId=111, When GetBindingAsync is called with guildId and channelId=111, Then the Control binding is returned
- [ ] Scenario: Alert channel routing -- Given a GuildBinding with ChannelPurpose=Alert for a guild, When GetAlertChannelAsync is called, Then the alert binding is returned with the correct ChannelId
- [ ] Scenario: IsAuthorizedChannelAsync fast path -- Given a channelId not in any GuildBinding, When IsAuthorizedChannelAsync is called, Then it returns false

## Stage 5.3: Interaction Recovery and Timeout Sweep

### Implementation Steps
- [ ] Create `InteractionRecoverySweep` as a BackgroundService in Discord project running on configurable interval (default 60 seconds); queries IDiscordInteractionStore.GetRecoverableAsync for records with IdempotencyStatus in Received, Processing, or Failed where AttemptCount < MaxRetries (default 3) -- per architecture.md Section 2.2
- [ ] Re-enqueue recoverable interaction records into IDiscordInteractionPipeline.ProcessAsync for reprocessing; increment AttemptCount on each attempt; mark permanently failed when AttemptCount >= MaxRetries
- [ ] Create `QuestionTimeoutService` as a BackgroundService in Worker project that queries IPendingQuestionStore.GetExpiredAsync for questions past ExpiresAt; update Status to TimedOut; edit original Discord message to show "Timed out - no response received" with all buttons/select menus disabled
- [ ] Create `QuestionRecoverySweep` as a BackgroundService in Worker project that backfills missing PendingQuestionRecords for OutboundMessages with SourceType=Question and Status=Sent that lack a corresponding PendingQuestionRecord (Gap B recovery per architecture.md Section 3.1)
- [ ] Log all recovered, expired, and timed-out interactions at Warning level with interaction/question ID and age

### Dependencies
- phase-security-and-reliability/stage-guild-registry-and-channel-routing

### Test Scenarios
- [ ] Scenario: Timed-out question cleaned up -- Given a PendingQuestionRecord with ExpiresAt 5 minutes in the past and Status=Pending, When QuestionTimeoutService runs, Then Status becomes TimedOut and Discord message buttons are disabled
- [ ] Scenario: Stuck interaction reprocessed -- Given a DiscordInteractionRecord with IdempotencyStatus=Received and AttemptCount=0, When InteractionRecoverySweep runs, Then the record is re-enqueued into the pipeline with AttemptCount incremented to 1
- [ ] Scenario: Gap B recovery backfills PendingQuestionRecord -- Given an OutboundMessage with SourceType=Question and Status=Sent but no PendingQuestionRecord exists, When QuestionRecoverySweep runs, Then a PendingQuestionRecord is created from the stored SourceEnvelopeJson and PlatformMessageId

## Stage 5.4: Dead Letter Processing

### Implementation Steps
- [ ] Create `DeadLetterProcessor` class in Discord project with methods: `ReviewAsync()` returning list of dead-lettered OutboundMessages (Status=DeadLettered), `RetryAsync(Guid outboundMessageId)` resetting AttemptCount and Status, `PurgeAsync(DateTimeOffset olderThan)`
- [ ] Implement retry: look up the OutboundMessage by outboundMessageId (Status=DeadLettered); reset AttemptCount=0, Status=Pending, clear ErrorDetail and NextRetryAt; delete the linked DeadLetterMessage record; the message re-enters the outbound queue on next DequeueAsync cycle
- [ ] Implement automatic purge: dead letters older than 7 days are purged by a sweep on each QuestionRecoverySweep cycle
- [ ] Add optional `/agent admin dead-letters` diagnostic slash command restricted to admin roles that lists recent dead-lettered messages with IdempotencyKey, error detail, and CreatedAt

### Dependencies
- phase-security-and-reliability/stage-interaction-recovery-and-timeout-sweep

### Test Scenarios
- [ ] Scenario: Dead letter retry re-enqueues -- Given an OutboundMessage with Status=DeadLettered and AttemptCount=5, When RetryAsync is called, Then AttemptCount resets to 0, Status becomes Pending, and the message re-enters the outbound queue
- [ ] Scenario: Auto purge removes old entries -- Given dead-lettered OutboundMessages older than 7 days, When purge runs, Then those messages are removed from the store

# Phase 6: Observability and Host Integration

## Dependencies
- phase-security-and-reliability

## Stage 6.1: OpenTelemetry Instrumentation

### Implementation Steps
- [ ] Add NuGet references to OpenTelemetry, OpenTelemetry.Extensions.Hosting, and OpenTelemetry.Exporter.Prometheus in the Worker project
- [ ] Create `MessagingMetrics` static class in Core project defining Meter "AgentSwarm.Messaging" with counters: `discord.interactions.received` (tagged by type), `discord.interactions.processed`, `discord.interactions.rejected`, `discord.interactions.duplicated`, `discord.send.retry_count`, `discord.send.dead_lettered`, `discord.ratelimit.hits` (tagged by route), `discord.gateway.reconnect`
- [ ] Define gauges: `discord.gateway.connected` (1 or 0), `discord.outbound.queue_depth` (by severity)
- [ ] Define histogram: `discord.send.first_attempt_latency_ms` for enqueue-to-REST-200 latency
- [ ] Instrument DiscordInteractionRouter: increment received/duplicated/processed counters; instrument authorization path to increment rejected counter
- [ ] Instrument OutboundQueueProcessor: increment retry_count and dead_lettered counters; record first_attempt_latency_ms histogram; update queue_depth gauge per dequeue cycle
- [ ] Add structured logging with ILogger<T> using correlation ID, guild ID, channel ID, and user ID in every log scope -- per architecture.md Section 9
- [ ] Add distributed tracing with Activity spans for interaction processing, command dispatch, and outbound message delivery, linked by CorrelationId

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Interaction counter increments -- Given metrics registered, When a new interaction is processed by the router, Then `discord.interactions.received` counter increases by 1
- [ ] Scenario: Queue depth gauge reflects reality -- Given 5 messages in the outbound queue, When the gauge is observed, Then it reports 5 for the relevant severity

## Stage 6.2: Dependency Injection and Host Wiring

### Implementation Steps
- [ ] Create `DiscordServiceCollectionExtensions` class in Discord project with `AddDiscordMessaging(this IServiceCollection, IConfiguration)` extension method -- per architecture.md Section 6 assembly map
- [ ] Register DiscordSocketClient as singleton, DiscordGatewayService as IHostedService, DiscordInteractionRouter (implementing IDiscordInteractionPipeline) as singleton, DiscordInteractionMapper as singleton
- [ ] Register SlashCommandDispatcher, ComponentInteractionHandler, IUserAuthorizationService implementation, GuildRegistry (implementing IGuildRegistry) as singletons
- [ ] Register DiscordMessageSender (implementing IMessageSender), RateLimitTracker as singletons; register DiscordMessengerConnector (implementing IMessengerConnector) as singleton
- [ ] Register InteractionRecoverySweep, QuestionTimeoutService, QuestionRecoverySweep as IHostedServices; register OutboundQueueProcessor as IHostedService
- [ ] Create `AddMessagingPersistence` extension method in Persistence project registering MessagingDbContext, PersistentOutboundQueue, PersistentAuditLogger, PersistentPendingQuestionStore, PersistentDiscordInteractionStore, and IDeduplicationService (sliding-window in-memory impl)
- [ ] Bind DiscordOptions from configuration section "Discord" using services.Configure; validate BotToken is not null/empty on startup
- [ ] Add health checks: DiscordGatewayHealthCheck (checks ConnectionState == Connected) and DatabaseHealthCheck (checks CanConnectAsync)

### Dependencies
- phase-observability-and-host-integration/stage-opentelemetry-instrumentation

### Test Scenarios
- [ ] Scenario: All services resolve from container -- Given DI container built with AddDiscordMessaging and AddMessagingPersistence, When all registered interfaces are resolved, Then no resolution exceptions are thrown
- [ ] Scenario: Gateway health check reports status -- Given DiscordSocketClient in Connected state, When DiscordGatewayHealthCheck runs, Then it returns Healthy
- [ ] Scenario: Configuration binds to DiscordOptions -- Given appsettings with Discord:BotToken and Discord:GuildId, When DiscordOptions is resolved from DI, Then BotToken and GuildId match configured values

## Stage 6.3: End-to-End Integration Tests

### Implementation Steps
- [ ] Create `DiscordIntegrationTestFixture` in Tests project with in-memory database, mocked DiscordSocketClient via Moq, and fully wired DI container using AddDiscordMessaging and AddMessagingPersistence
- [ ] Write test: `/agent ask architect design cache strategy` flows through persist-then-defer-then-process pipeline, producing a SwarmCommand via PublishCommandAsync and an outbound question message with embed and 4 action buttons
- [ ] Write test: button click with custom_id `q:Q-42:approve` flows through router to component handler, records selection via IPendingQuestionStore.RecordSelectionAsync, and publishes HumanDecisionEvent with correct Discord audit IDs in Details JSON
- [ ] Write test: duplicate interaction ID causes PersistAsync to return false, DeferAsync is never called, and processing is skipped entirely
- [ ] Write test: unauthorized user (wrong role) receives ephemeral follow-up denial, original deferred response is deleted via DeleteOriginalResponseAsync, and an AuditEntry is persisted
- [ ] Write test: outbound message with Critical severity is dequeued and sent before a Normal severity message enqueued earlier
- [ ] Write test: message exceeding MaxAttempts (5) is dead-lettered via DeadLetterAsync -- OutboundMessage.Status becomes DeadLettered, a linked DeadLetterMessage record is created, and discord.send.dead_lettered counter increments
- [ ] Write test: PendingQuestionRecord past ExpiresAt is set to TimedOut by QuestionTimeoutService and Discord message buttons are disabled
- [ ] Write test: 100 concurrent agent status updates are enqueued as Low severity; when CountPendingAsync(Low) exceeds 50, OutboundQueueProcessor calls DequeueBatchAsync and SendBatchAsync to consolidate into summary embeds
- [ ] Write test: question with >5 AllowedActions renders a select menu instead of buttons

### Dependencies
- phase-observability-and-host-integration/stage-dependency-injection-and-host-wiring

### Test Scenarios
- [ ] Scenario: Full ask-to-question flow -- Given a fully wired test fixture, When `/agent ask architect design cache strategy` is simulated, Then SwarmCommand is published to ISwarmCommandBus and an outbound question embed with action buttons is enqueued in IOutboundQueue
- [ ] Scenario: Full approve-to-decision flow -- Given a pending question in the database, When a button click with custom_id "q:Q-42:approve" is simulated, Then HumanDecisionEvent is published with correct ExternalUserId and the PendingQuestionRecord status changes to Answered
- [ ] Scenario: Concurrent agent load handling -- Given 100 agent status updates enqueued as Low severity, When the outbound processor runs to completion, Then CountPendingAsync(Low) triggers batch path, DequeueBatchAsync collects up to 10 at a time, SendBatchAsync sends each batch as a single embed, and all messages are eventually sent
