---
title: "Telegram Messenger Support"
storyId: "qq-TELEGRAM-MESSENGER-S"
---

# Phase 1: Messaging Abstractions and Solution Scaffold

## Dependencies
- _none — start phase_

## Stage 1.1: Solution and Project Structure

### Implementation Steps
- [ ] Create .NET 8 solution file `AgentSwarm.Messaging.sln` at repo root with project folders for `src/` and `tests/`
- [ ] Create class library project `src/AgentSwarm.Messaging.Abstractions/AgentSwarm.Messaging.Abstractions.csproj` targeting `net8.0` with no external dependencies
- [ ] Create class library project `src/AgentSwarm.Messaging.Core/AgentSwarm.Messaging.Core.csproj` targeting `net8.0` with a project reference to Abstractions
- [ ] Create class library project `src/AgentSwarm.Messaging.Telegram/AgentSwarm.Messaging.Telegram.csproj` targeting `net8.0` with project references to Abstractions and Core, and NuGet reference to `Telegram.Bot`
- [ ] Create class library project `src/AgentSwarm.Messaging.Persistence/AgentSwarm.Messaging.Persistence.csproj` targeting `net8.0` with project reference to Abstractions
- [ ] Create worker service project `src/AgentSwarm.Messaging.Worker/AgentSwarm.Messaging.Worker.csproj` targeting `net8.0` with references to Core, Telegram, and Persistence
- [ ] Create xUnit test project `tests/AgentSwarm.Messaging.Tests/AgentSwarm.Messaging.Tests.csproj` with references to all src projects and NuGet references to `xUnit`, `Moq`, `FluentAssertions`
- [ ] Add `Directory.Build.props` at repo root with shared properties: `LangVersion=latest`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`
- [ ] Add `.editorconfig` at repo root enforcing C# coding conventions consistent with .NET defaults
- [ ] Verify solution builds with `dotnet build` and all projects restore successfully

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Solution builds cleanly — Given all projects are created, When `dotnet build AgentSwarm.Messaging.sln` is run, Then exit code is 0 and no warnings are emitted
- [ ] Scenario: Project references resolve — Given Telegram project references Abstractions and Core, When the solution is restored, Then all inter-project references resolve without errors

## Stage 1.2: Shared Data Models

### Implementation Steps
- [ ] Create `MessengerMessage` record in Abstractions with properties: `MessageId`, `CorrelationId`, `AgentId`, `TaskId`, `ConversationId`, `Timestamp`, `Text`, `Severity`, `Metadata` dictionary
- [ ] Create `AgentQuestion` record in Abstractions with properties: `QuestionId`, `AgentId`, `TaskId`, `Title`, `Body`, `Severity`, `AllowedActions` (list of `HumanAction`), `ExpiresAt`, `CorrelationId` — the shared model does **not** include a `DefaultAction` property; per e2e-scenarios.md (lines 57–62), the proposed default action is provided as sidecar metadata via `ProposedDefaultActionId` in the agent/command context envelope (see `AgentQuestionEnvelope` below), enabling connectors to handle defaults without coupling the shared model to timeout behavior
- [ ] Create `AgentQuestionEnvelope` record in Abstractions with properties: `Question` (`AgentQuestion`), `ProposedDefaultActionId` (nullable `string` — the `ActionId` of the proposed default action from `AllowedActions`; carried as routing/context metadata alongside the `AgentQuestion`; when the question times out, the connector reads this from `PendingQuestionRecord.DefaultActionId` and applies the action automatically; when `null`, the question expires with `ActionValue = "__timeout__"`), `RoutingMetadata` (dictionary of string key-value pairs for extensible context)
- [ ] Create `HumanAction` record in Abstractions with properties: `ActionId`, `Label`, `Value`, `RequiresComment`
- [ ] Create `HumanDecisionEvent` record in Abstractions with properties: `QuestionId`, `ActionValue`, `Comment`, `Messenger`, `ExternalUserId`, `ExternalMessageId`, `ReceivedAt`, `CorrelationId`
- [ ] Create `MessengerEvent` record in Abstractions representing inbound events with properties: `EventId`, `EventType` enum, `RawCommand`, `UserId`, `ChatId`, `Timestamp`, `CorrelationId`, `Payload`
- [ ] Create `EventType` enum in Abstractions: `Command`, `CallbackResponse`, `TextReply`, `Unknown`
- [ ] Create `MessageSeverity` enum in Abstractions: `Info`, `Warning`, `Error`, `Critical`

### Dependencies
- phase-messaging-abstractions-and-solution-scaffold/stage-solution-and-project-structure

### Test Scenarios
- [ ] Scenario: Records are immutable — Given an `AgentQuestion` instance, When a property mutation is attempted, Then the compiler rejects it (record semantics)
- [ ] Scenario: CorrelationId is required — Given a `MessengerMessage` constructor call with null `CorrelationId`, When instantiated, Then an `ArgumentNullException` is thrown via guard clause
- [ ] Scenario: Serialization round-trip — Given an `AgentQuestionEnvelope` wrapping an `AgentQuestion` with all fields populated and `ProposedDefaultActionId` set, When serialized to JSON and deserialized back, Then all field values match the original including the sidecar metadata

## Stage 1.3: Connector Interface and Service Contracts

### Implementation Steps
- [ ] Create `IMessengerConnector` interface in Abstractions with methods: `SendMessageAsync(MessengerMessage, CancellationToken)`, `SendQuestionAsync(AgentQuestionEnvelope, CancellationToken)` — accepts the full envelope so the connector can read `ProposedDefaultActionId` sidecar metadata and `RoutingMetadata` for `TelegramChatId`, `ReceiveAsync(CancellationToken)` returning `IReadOnlyList<MessengerEvent>`
- [ ] Create `ICommandRouter` interface in Abstractions with method: `RouteAsync(ParsedCommand, AuthorizedOperator, CancellationToken)` returning `CommandResult` — the pipeline (Stage 2.2) parses and authorizes first, then passes the resolved command and identity to the router
- [ ] Create `IPendingQuestionStore` interface in Abstractions with methods: `StoreAsync(AgentQuestionEnvelope envelope, long telegramChatId, long telegramMessageId, CancellationToken)` — accepts the full envelope so the store can extract `Question` fields, `ProposedDefaultActionId` for denormalization into `PendingQuestionRecord.DefaultActionId`, and `TelegramChatId` for timeout message edits; `GetAsync(string questionId, CancellationToken)`, `GetByTelegramMessageIdAsync(long telegramMessageId, CancellationToken)`, `MarkAnsweredAsync(string questionId, CancellationToken)`, `MarkAwaitingCommentAsync(string questionId, CancellationToken)`, `GetExpiredAsync(CancellationToken)` — defined here so Stage 2.2 can reference it for `TextReply` routing; concrete implementation in Stage 3.5
- [ ] Create `CommandResult` record in Abstractions with properties: `Success`, `ResponseText`, `CorrelationId`, `ErrorCode`
- [ ] Create `IUserAuthorizationService` interface in Abstractions with method: `AuthorizeAsync(string externalUserId, string chatId, CancellationToken)` returning `AuthorizationResult`
- [ ] Create `AuthorizationResult` record with properties: `IsAuthorized`, `OperatorId`, `TenantId`, `WorkspaceId`, `DenialReason`
- [ ] Create `AuthorizedOperator` record in Abstractions with properties: `OperatorId`, `TenantId`, `WorkspaceId`, `Roles` (list of string), `TelegramUserId`, `TelegramChatId` — represents a resolved authorized identity passed to command handlers
- [ ] Create `IAuditLogger` interface in Abstractions with method: `LogAsync(AuditEntry, CancellationToken)`
- [ ] Create `AuditEntry` record with properties: `EntryId`, `MessageId`, `UserId`, `AgentId`, `Action`, `Timestamp`, `CorrelationId`, `Details`
- [ ] Create `ITelegramUpdatePipeline` interface in Abstractions with method: `ProcessAsync(MessengerEvent, CancellationToken)` returning `PipelineResult` — defines the inbound processing chain so receivers (webhook, polling) can reference it without depending on the implementation
- [ ] Create `PipelineResult` record in Abstractions with properties: `Handled` (bool), `ResponseText`, `CorrelationId`
- [ ] Create `ICommandParser` interface in Abstractions with method: `Parse(string messageText)` returning `ParsedCommand` — abstracts command parsing so the pipeline does not depend on concrete parser implementations
- [ ] Create `ParsedCommand` record in Abstractions with properties: `CommandName`, `Arguments`, `RawText`, `IsValid`, `ValidationError`
- [ ] Create `ICallbackHandler` interface in Abstractions with method: `HandleAsync(MessengerEvent, CancellationToken)` returning `CommandResult` — abstracts callback query processing for inline button presses
- [ ] Create `IDeduplicationService` interface in Abstractions with methods: `IsProcessedAsync(string eventId, CancellationToken)` returning bool, and `MarkProcessedAsync(string eventId, CancellationToken)` — abstracts inbound event deduplication
- [ ] Create `ISwarmCommandBus` interface in Abstractions with methods: `PublishCommandAsync(SwarmCommand, CancellationToken)`, `QueryStatusAsync(CancellationToken)`, `QueryAgentsAsync(CancellationToken)` — port to the agent swarm orchestrator (transport is out of scope for this story, per architecture.md)
- [ ] Create `SwarmCommand` record in Abstractions with properties: `CommandType`, `TaskId`, `OperatorId`, `Payload`, `CorrelationId`
- [ ] Create `IOperatorRegistry` interface in Core (per architecture.md §4.3) with methods: `GetByTelegramUserAsync(long telegramUserId, long chatId, CancellationToken)` returning `OperatorBinding?`, `GetAllBindingsAsync(long telegramUserId, CancellationToken)` returning `IReadOnlyList<OperatorBinding>`, `GetByAliasAsync(string operatorAlias, CancellationToken)` returning `OperatorBinding?`, `RegisterAsync(long telegramUserId, long chatId, string tenantId, string workspaceId, CancellationToken)`, `IsAuthorizedAsync(long telegramUserId, long chatId, CancellationToken)` returning `bool` — used by Stage 3.2 (`HandoffCommandHandler` alias resolution), Stage 3.4 (concrete implementation), and Stage 5.2 (runtime authorization)
- [ ] Create `OperatorBinding` record in Core with properties: `Id` (Guid), `TelegramUserId` (long), `TelegramChatId` (long), `ChatType` (enum: `Private`, `Group`, `Supergroup`), `OperatorAlias` (string), `TenantId`, `WorkspaceId`, `Roles` (list of string), `RegisteredAt` (DateTimeOffset), `IsActive` (bool) — per architecture.md §3.1

### Dependencies
- phase-messaging-abstractions-and-solution-scaffold/stage-shared-data-models

### Test Scenarios
- [ ] Scenario: Interface contracts compile — Given all interfaces are defined in Abstractions, When the project is built, Then it compiles with zero errors
- [ ] Scenario: Mock connector satisfies interface — Given a Moq mock of `IMessengerConnector`, When `SendMessageAsync` is invoked, Then the mock records the call without error
- [ ] Scenario: Pipeline interface mockable — Given a Moq mock of `ITelegramUpdatePipeline`, When `ProcessAsync` is invoked with a `MessengerEvent`, Then the mock records the call and returns a `PipelineResult`

## Stage 1.4: Outbound Sender and Alert Contracts

### Implementation Steps
- [ ] Create `IMessageSender` interface in Core with methods: `SendTextAsync(long chatId, string text, CancellationToken)`, `SendQuestionAsync(long chatId, AgentQuestionEnvelope envelope, CancellationToken)` — the platform-agnostic outbound sending contract that `TelegramMessageSender` (Stage 2.3) implements; uses `AgentQuestionEnvelope` so the sender can read `ProposedDefaultActionId` from sidecar metadata and denormalize it into `PendingQuestionRecord.DefaultActionId`; used by `OutboundQueueProcessor` (Stage 4.1) to send messages without depending on the Telegram project directly
- [ ] Create `IAlertService` interface in Abstractions with method: `SendAlertAsync(string subject, string detail, CancellationToken)` — used to notify operators via a secondary channel when dead-letter events occur or critical failures are detected

### Dependencies
- phase-messaging-abstractions-and-solution-scaffold/stage-connector-interface-and-service-contracts

### Test Scenarios
- [ ] Scenario: IMessageSender accepts envelope — Given a Moq mock of `IMessageSender`, When `SendQuestionAsync` is invoked with an `AgentQuestionEnvelope` containing `ProposedDefaultActionId`, Then the mock records the call and envelope properties are accessible
- [ ] Scenario: IAlertService mockable — Given a Moq mock of `IAlertService`, When `SendAlertAsync` is invoked with a subject and detail, Then the mock records the call without error

# Phase 2: Telegram Bot Integration

## Dependencies
- phase-messaging-abstractions-and-solution-scaffold

## Stage 2.1: Telegram Bot Client Wrapper

### Implementation Steps
- [ ] Create `TelegramBotClientFactory` in the Telegram project that reads bot token from `IConfiguration` (supports Key Vault, environment variable, or user secrets) and returns a configured `ITelegramBotClient` instance
- [ ] Create `TelegramOptions` configuration POCO bound from `appsettings.json` section `Telegram` with properties: `BotToken` (never logged), `WebhookUrl`, `UsePolling` (bool), `AllowedUserIds` (list), `SecretToken` for webhook validation
- [ ] Register `TelegramOptions` via `IOptions<TelegramOptions>` pattern in a `TelegramServiceCollectionExtensions.AddTelegram(this IServiceCollection, IConfiguration)` extension method
- [ ] Implement token-redaction logic in `TelegramOptions` so `ToString()` never exposes the token, and add a custom `IValidateOptions<TelegramOptions>` that rejects empty/null tokens at startup

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Missing token fails fast — Given `TelegramOptions` with an empty `BotToken`, When the host starts, Then startup fails with a descriptive `OptionsValidationException`
- [ ] Scenario: Token not logged — Given `TelegramOptions` with a valid token, When `ToString()` is called, Then the output contains `[REDACTED]` instead of the actual token value

## Stage 2.2: Inbound Update Pipeline

### Implementation Steps
- [ ] Implement `TelegramUpdatePipeline` (the concrete class implementing `ITelegramUpdatePipeline` defined in Stage 1.3) in the Telegram project; inject all dependencies as interfaces: `IDeduplicationService`, `IUserAuthorizationService`, `ICommandParser`, `ICommandRouter`, `ICallbackHandler`
- [ ] Compose the pipeline as a sequential chain: deduplication check (via `IDeduplicationService`) → allowlist gate (via `IUserAuthorizationService`, producing `AuthorizedOperator`) → command parsing (via `ICommandParser`, producing `ParsedCommand`) → routing by `EventType`: `Command` events pass `ParsedCommand` and `AuthorizedOperator` to `ICommandRouter.RouteAsync`, `CallbackResponse` events go to `ICallbackHandler`, `TextReply` events check for pending `RequiresComment` prompts (via `IPendingQuestionStore`, defined in Stage 1.3 Abstractions) before falling through to default handling
- [ ] Provide stub/no-op implementations of `ICommandParser`, `ICommandRouter`, `ICallbackHandler`, `IDeduplicationService`, and `IPendingQuestionStore` in the Telegram project for initial compilation and testing; concrete implementations are registered in Phase 3 (command processing) and Phase 4 (deduplication)
- [ ] Emit structured log entries at each pipeline stage with `CorrelationId`, `EventId`, and stage name for end-to-end traceability
- [ ] Return a `PipelineResult` (defined in Abstractions) to callers (webhook endpoint and polling service)

### Dependencies
- phase-telegram-bot-integration/stage-telegram-bot-client-wrapper

### Test Scenarios
- [ ] Scenario: Pipeline routes command — Given a `MessengerEvent` with `EventType=Command` and text `/status`, When processed through the pipeline, Then `CommandRouter` is invoked and a response is returned
- [ ] Scenario: Pipeline routes callback to stub — Given a `MessengerEvent` with `EventType=CallbackResponse`, When processed, Then the injected `ICallbackHandler` mock/stub is invoked with the event (real `CallbackQueryHandler` emitting `HumanDecisionEvent` is provided by Stage 3.3; this test verifies routing only)
- [ ] Scenario: Pipeline rejects unauthorized — Given a `MessengerEvent` from a user not in the allowlist, When processed, Then the pipeline short-circuits with a denial response and no command handler is invoked

## Stage 2.3: Outbound Message Sender

### Implementation Steps
- [ ] Create `TelegramMessageSender` implementing `IMessageSender` (defined in Stage 1.4 Core) with methods: `SendTextAsync(chatId, text, ct)`, `SendQuestionAsync(chatId, AgentQuestionEnvelope, ct)`
- [ ] Implement `SendQuestionAsync` to render `AgentQuestionEnvelope` as a rich Telegram message: extract the `AgentQuestion` from the envelope; include `Title`, `Body` (full context), `Severity` badge, `ExpiresAt` timeout countdown; render `AllowedActions` as Telegram `InlineKeyboardMarkup` buttons with callback data encoding `QuestionId:ActionId`; read `AgentQuestionEnvelope.ProposedDefaultActionId` from the sidecar metadata (per e2e-scenarios.md lines 57–76) and, when present, display the proposed default in the message body (e.g., "Default action if no response: Approve") and denormalize the `ActionId` into `PendingQuestionRecord.DefaultActionId` (Stage 3.5) for efficient timeout polling
- [ ] When building the inline keyboard, write each `HumanAction` to `IDistributedCache` keyed by `QuestionId:ActionId` with expiry set to `AgentQuestion.ExpiresAt` (per architecture.md §5.2 and tech-spec D-3); this enables `CallbackQueryHandler` and `QuestionTimeoutService` to resolve the full `HumanAction` from the short `ActionId` in callback data
- [ ] For actions with `RequiresComment = true`, append "(reply required)" to the button label so the operator knows a follow-up text reply is expected after tapping
- [ ] Format outbound messages with Markdown V2 parse mode; include `CorrelationId` as a footer or hidden tag for traceability
- [ ] Implement Telegram API rate-limit handling: detect `429 Too Many Requests`, extract `RetryAfter`, and back off accordingly
- [ ] Implement a proactive dual token-bucket rate limiter (per architecture.md §10.4) with two layers: a global bucket limiting sends to `Telegram:RateLimits:GlobalPerSecond` (default 30 msg/s) across all chats, and per-chat buckets limiting each individual chat to `Telegram:RateLimits:PerChatPerMinute` (default 20 msg/min); create `RateLimitOptions` configuration POCO bound from `Telegram:RateLimits`; workers acquire tokens before sending and block-wait when exhausted, preventing 429 responses proactively
- [ ] Add message-ID tracking: after successful send, persist the Telegram message ID mapped to `CorrelationId` for reply correlation

### Dependencies
- phase-telegram-bot-integration/stage-telegram-bot-client-wrapper

### Test Scenarios
- [ ] Scenario: Question renders buttons — Given an `AgentQuestion` with three `HumanAction` items, When `SendQuestionAsync` is called, Then the constructed `InlineKeyboardMarkup` contains exactly three buttons with correct labels
- [ ] Scenario: Question body includes full context — Given an `AgentQuestionEnvelope` with `Severity=Critical`, `ExpiresAt` in 30 minutes, and `ProposedDefaultActionId` set to an `ActionId` whose label is "skip", When `SendQuestionAsync` is called, Then the message body contains the severity badge, timeout information, default action label ("Default action if no response: skip"), and full question `Body` text
- [ ] Scenario: HumanAction cached on keyboard build — Given an `AgentQuestion` with two `AllowedActions`, When inline keyboard buttons are rendered, Then two `IDistributedCache` entries are written keyed by `QuestionId:ActionId` containing the full `HumanAction` with expiry matching `ExpiresAt`
- [ ] Scenario: Rate limit handled gracefully — Given the Telegram API returns HTTP 429 with `RetryAfter=5`, When the sender encounters it, Then it waits at least 5 seconds before retrying and does not throw
- [ ] Scenario: CorrelationId in message — Given a `MessengerMessage` with a specific `CorrelationId`, When sent, Then the outbound message body contains the correlation ID
- [ ] Scenario: RequiresComment action labeled — Given an `AgentQuestion` with one action having `RequiresComment=true`, When rendered, Then that button label includes a "(reply required)" indicator
- [ ] Scenario: Proactive rate limiter throttles — Given the global token bucket is exhausted (30 tokens consumed within 1 second), When a new send is attempted, Then the sender blocks until a token is available rather than issuing a request that would be 429'd

## Stage 2.4: Webhook Receiver Endpoint

### Implementation Steps
- [ ] Create ASP.NET Core minimal API endpoint `POST /api/telegram/webhook` in the Worker project that receives Telegram `Update` JSON payloads
- [ ] Implement `TelegramWebhookSecretFilter` that validates the `X-Telegram-Bot-Api-Secret-Token` header against the configured `SecretToken`; reject with 403 if mismatch
- [ ] Deserialize the incoming `Update` using `Telegram.Bot` serialization and convert to the internal `MessengerEvent` model via a `TelegramUpdateMapper` class
- [ ] Persist an `InboundUpdate` durable record (per architecture.md §3.1 lines 126-134 and §5.1 line 370) **before** returning HTTP 200: insert into the `inbound_updates` table with fields `UpdateId` (PK, Telegram's `update_id`), `RawPayload` (full serialized `Update` JSON), `ReceivedAt`, `ProcessedAt` (null initially), and `IdempotencyStatus` (set to `Received`); if the `UNIQUE` constraint on `UpdateId` fails, the update is a duplicate — return 200 immediately without further processing; this eliminates the command-loss window: if the process crashes after Telegram receives 200, the `InboundUpdate` record (with full `RawPayload`) is already persisted for recovery
- [ ] Create `InboundUpdate` entity in Persistence with fields: `UpdateId` (long, PK), `RawPayload` (string), `ReceivedAt` (DateTimeOffset), `ProcessedAt` (DateTimeOffset?), `IdempotencyStatus` (enum: `Received`, `Processing`, `Completed`, `Failed`); add EF Core configuration with a `UNIQUE` constraint on `UpdateId`
- [ ] Create `IInboundUpdateStore` interface in Abstractions with methods: `PersistAsync(InboundUpdate, CancellationToken)` returning `bool` (false if duplicate), `MarkProcessingAsync(long updateId, CancellationToken)`, `MarkCompletedAsync(long updateId, CancellationToken)`, `MarkFailedAsync(long updateId, CancellationToken)`, `GetUnprocessedAsync(CancellationToken)` returning `IReadOnlyList<InboundUpdate>` (records with `IdempotencyStatus = Received` or `Processing`)
- [ ] Implement `PersistentInboundUpdateStore` in Persistence backed by EF Core (SQLite for dev, PostgreSQL or SQL Server for production), using the shared `MessagingDbContext` (see Stage 6.3 for connection configuration)
- [ ] After persisting the `InboundUpdate` record, return HTTP 200 and pass the `MessengerEvent` to `ITelegramUpdatePipeline.ProcessAsync` (interface defined in Abstractions, concrete implementation from Stage 2.2 provided via DI) for async command routing; upon completion, transition `IdempotencyStatus` to `Completed` (or `Failed` on error)
- [ ] Implement `InboundRecoverySweep` as a startup `IHostedService` that runs once on Worker startup: queries `IInboundUpdateStore.GetUnprocessedAsync()` for records with `IdempotencyStatus = Received` or `Processing`, deserializes each `RawPayload` back into a Telegram `Update`, maps to `MessengerEvent`, and re-feeds into `ITelegramUpdatePipeline.ProcessAsync` for idempotent re-processing; log recovered records at `Warning` level
- [ ] Add webhook registration logic in `IHostedService` startup: call `SetWebhookAsync` with the configured URL, secret token, and allowed update types

### Dependencies
- phase-telegram-bot-integration/stage-telegram-bot-client-wrapper
- phase-telegram-bot-integration/stage-inbound-update-pipeline

### Test Scenarios
- [ ] Scenario: Valid webhook accepted — Given a well-formed Telegram Update JSON, When POSTed to `/api/telegram/webhook` with correct secret header, Then an `InboundUpdate` record is persisted with `IdempotencyStatus=Received` and `RawPayload` containing the full Update JSON, and response is HTTP 200
- [ ] Scenario: Invalid secret rejected — Given a Telegram Update JSON, When POSTed with an incorrect `X-Telegram-Bot-Api-Secret-Token`, Then response is HTTP 403 and no `InboundUpdate` record is created
- [ ] Scenario: Duplicate update ignored — Given the same `Update.Id` is received twice, When both are POSTed, Then only the first creates an `InboundUpdate` record and triggers downstream processing; the second returns 200 with no new record
- [ ] Scenario: Crash recovery on restart — Given an `InboundUpdate` record exists with `IdempotencyStatus=Received` (simulating a crash after 200 was returned but before processing completed), When the Worker restarts, Then `InboundRecoverySweep` deserializes the `RawPayload`, re-feeds it into the pipeline, and the record transitions to `Completed`

## Stage 2.5: Long Polling Receiver for Development

### Implementation Steps
- [ ] Create `TelegramPollingService` as a `BackgroundService` in the Telegram project that calls `GetUpdatesAsync` in a loop when `TelegramOptions.UsePolling` is `true`
- [ ] Map each received `Update` to `MessengerEvent` using the shared `TelegramUpdateMapper` and pass to `ITelegramUpdatePipeline.ProcessAsync` (interface defined in Abstractions, concrete implementation from Stage 2.2 provided via DI)
- [ ] Implement graceful shutdown: respect `CancellationToken`, log final offset, and cleanly stop polling
- [ ] Ensure polling and webhook modes are mutually exclusive at startup via a guard in the DI registration

### Dependencies
- phase-telegram-bot-integration/stage-telegram-bot-client-wrapper
- phase-telegram-bot-integration/stage-inbound-update-pipeline

### Test Scenarios
- [ ] Scenario: Polling receives updates — Given polling mode is enabled and the bot has pending updates, When the polling loop executes, Then updates are mapped and enqueued
- [ ] Scenario: Mutual exclusion enforced — Given both `UsePolling=true` and `WebhookUrl` is set, When the host starts, Then startup fails with a configuration error explaining the conflict

# Phase 3: Command Processing and Agent Routing

## Dependencies
- phase-telegram-bot-integration

## Stage 3.1: Command Parser

### Implementation Steps
- [ ] Create `TelegramCommandParser` implementing `ICommandParser` (defined in Stage 1.3) in the Telegram project that extracts command name and arguments from Telegram message text (e.g., `/ask build release notes for Solution12` → command=`ask`, args=`build release notes for Solution12`)
- [ ] Handle bot-mention syntax (`/ask@BotName`) by stripping the `@BotName` suffix
- [ ] Support all required commands: `/start`, `/status`, `/agents`, `/ask`, `/approve`, `/reject`, `/handoff`, `/pause`, `/resume`
- [ ] Handle edge cases: empty arguments where required (e.g., `/ask` with no text), unknown commands, and non-command messages

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Parse standard command — Given message text `/ask build release notes`, When parsed, Then `CommandName` is `ask` and `Arguments` is `build release notes`
- [ ] Scenario: Strip bot mention — Given message text `/status@MyBot`, When parsed, Then `CommandName` is `status` with no leftover `@MyBot`
- [ ] Scenario: Empty argument rejected — Given message text `/ask` with no further text, When parsed, Then `IsValid` is false and `ValidationError` explains that `/ask` requires a task description

## Stage 3.2: Command Router and Handlers

### Implementation Steps
- [ ] Create `CommandRouter` implementing `ICommandRouter` in Core that receives `ParsedCommand` and `AuthorizedOperator`, dispatches to registered `ICommandHandler` instances via a dictionary keyed by command name
- [ ] Define `ICommandHandler` interface in Core with method `HandleAsync(ParsedCommand, AuthorizedOperator, CancellationToken)` returning `CommandResult`; `AuthorizedOperator` (defined in Stage 1.3) provides the resolved operator identity for authorization and audit
- [ ] Implement `StartCommandHandler` — registers user, responds with welcome and available commands
- [ ] Implement `StatusCommandHandler` — queries swarm orchestrator via `ISwarmCommandBus.QueryStatusAsync` (defined in Stage 1.3) for current status summary, returns formatted text
- [ ] Implement `AgentsCommandHandler` — queries active agents list via `ISwarmCommandBus.QueryAgentsAsync`, returns formatted agent roster
- [ ] Implement `AskCommandHandler` — creates a work item in the swarm orchestrator via `ISwarmCommandBus.PublishCommandAsync` from the argument text, returns confirmation with task ID and correlation ID
- [ ] Implement `ApproveCommandHandler` and `RejectCommandHandler` — resolve a pending `AgentQuestion` by emitting a `HumanDecisionEvent` with the appropriate action value
- [ ] Implement `PauseCommandHandler` and `ResumeCommandHandler` — send pause/resume signals to the target agent via `ISwarmCommandBus.PublishCommandAsync`
- [ ] Implement `HandoffCommandHandler` with **full oversight transfer** (per architecture.md §5.5 "Full oversight transfer (Decided)" and tech-spec.md D-4): (1) validates `/handoff TASK-ID @operator-alias` syntax (two arguments: task ID and operator alias); if invalid (zero or one argument), returns usage help "Usage: `/handoff TASK-ID @operator-alias`"; (2) validates that the task exists and the requesting operator currently has oversight; if task not found, replies "❌ Task TASK-ID not found"; (3) resolves the target operator via `IOperatorRegistry.GetByAliasAsync`; if not found, replies "❌ Operator @operator-alias is not registered"; (4) creates or updates a `TaskOversight` record mapping the task to the target operator; (5) notifies both operators — the sender receives "✅ Oversight of TASK-ID transferred to @operator-alias", the target receives a handoff notification with task context; (6) persists an audit record with task ID, source operator, target operator, timestamp, and `CorrelationId` for traceability
- [ ] Create `TaskOversight` entity in the Persistence project (per architecture.md §5.5) with fields: `TaskId` (PK, string), `OperatorBindingId` (FK to `OperatorBinding.Id`, Guid), `AssignedAt` (DateTimeOffset), `AssignedBy` (string — the operator who initiated the handoff), `CorrelationId` (string); create EF Core `TaskOversightConfiguration` with table `task_oversights`, add migration, and register in `MessagingDbContext`; add indexes on `OperatorBindingId` for operator-scoped queries and on `TaskId` for task lookup
- [ ] Create `ITaskOversightRepository` interface in Core with methods: `GetByTaskIdAsync(string taskId, CancellationToken)` returning `TaskOversight?`, `UpsertAsync(TaskOversight, CancellationToken)`, `GetByOperatorAsync(Guid operatorBindingId, CancellationToken)` returning `IReadOnlyList<TaskOversight>`; implement `PersistentTaskOversightRepository` in Persistence backed by EF Core

### Dependencies
- phase-command-processing-and-agent-routing/stage-command-parser

### Test Scenarios
- [ ] Scenario: Ask creates work item — Given an authorized user sends `/ask build release notes for Solution12`, When the `AskCommandHandler` processes it, Then a work item is created and the response includes a task ID
- [ ] Scenario: Unknown command rejected — Given a `ParsedCommand` with `CommandName` = `foo`, When routed, Then the result has `Success=false` and a helpful error message listing valid commands
- [ ] Scenario: Approve emits decision event — Given a pending question with `QuestionId=Q1`, When `/approve Q1` is processed, Then a `HumanDecisionEvent` is emitted with `ActionValue=approve` and the correct `CorrelationId`
- [ ] Scenario: Handoff transfers oversight — Given an authorized user "operator-1" has oversight of task "TASK-099" and "operator-2" (alias "@operator-2") is registered, When `HandoffCommandHandler` processes `/handoff TASK-099 @operator-2`, Then a `TaskOversight` record maps "TASK-099" to "operator-2", "operator-1" receives "✅ Oversight of TASK-099 transferred to @operator-2", "operator-2" receives a handoff notification with task context, and an audit record is persisted with task ID, source operator, target operator, timestamp, and CorrelationId
- [ ] Scenario: Handoff with nonexistent task is rejected — Given task "NONEXISTENT" does not exist, When `HandoffCommandHandler` processes `/handoff NONEXISTENT @operator-2`, Then the bot replies "❌ Task NONEXISTENT not found" and no `TaskOversight` record is created
- [ ] Scenario: Handoff with unregistered target is rejected — Given alias "@unknown-user" is not in the OperatorRegistry, When `HandoffCommandHandler` processes `/handoff TASK-099 @unknown-user`, Then the bot replies "❌ Operator @unknown-user is not registered"
- [ ] Scenario: Handoff with invalid syntax returns usage — Given an authorized user sends `/handoff` with no arguments, When `HandoffCommandHandler` processes it, Then the bot responds with usage help "Usage: `/handoff TASK-ID @operator-alias`"
- [ ] Scenario: Handoff with one argument returns usage — Given an authorized user sends `/handoff TASK-099` with only one argument, When `HandoffCommandHandler` processes it, Then the bot responds with usage help "Usage: `/handoff TASK-ID @operator-alias`"
- [ ] Scenario: Handoff persists TaskOversight — Given an authorized user "operator-1" sends `/handoff TASK-099 @operator-2` and the transfer succeeds, When the `TaskOversight` table is queried for "TASK-099", Then a record exists with `OperatorBindingId` matching "operator-2" and `AssignedBy` matching "operator-1"

## Stage 3.3: Callback Query Handler

### Implementation Steps
- [ ] Create `CallbackQueryHandler` implementing `ICallbackHandler` (defined in Stage 1.3) in the Telegram project that processes Telegram `CallbackQuery` objects from inline button presses
- [ ] Decode callback data format `QuestionId:ActionId` and look up the original `AgentQuestion` from the pending-questions store
- [ ] Emit a `HumanDecisionEvent` with the selected action, user identity, message ID, and correlation ID
- [ ] Answer the callback query via `AnswerCallbackQueryAsync` with a confirmation message (e.g., "✅ Approved")
- [ ] Update the original message to reflect the decision (edit inline keyboard to show selected action, disable further buttons)
- [ ] Implement idempotency: if the same callback has already been processed (same `CallbackQuery.Id`), skip processing and re-answer with the previous result

### Dependencies
- phase-command-processing-and-agent-routing/stage-command-router-and-handlers

### Test Scenarios
- [ ] Scenario: Button press emits decision — Given a callback query with data `Q1:approve`, When processed, Then a `HumanDecisionEvent` is emitted with `QuestionId=Q1` and `ActionValue=approve`
- [ ] Scenario: Duplicate callback ignored — Given callback `CB1` has already been processed, When received again, Then no duplicate `HumanDecisionEvent` is emitted and the user sees the same confirmation
- [ ] Scenario: Original message updated — Given a question message with three action buttons, When one button is pressed, Then the message is edited to show only the selected action and buttons are removed

## Stage 3.4: Operator Identity Mapping

### Implementation Steps
- [ ] Create `OperatorRegistry` implementing `IOperatorRegistry` (defined in Stage 1.3 Core) in Core that maps Telegram `chatId`/`userId` to an internal `AuthorizedOperator` record containing `OperatorId`, `TenantId`, `WorkspaceId`, `Roles`; implements all five interface methods: `GetByTelegramUserAsync`, `GetAllBindingsAsync`, `GetByAliasAsync`, `RegisterAsync`, `IsAuthorizedAsync`
- [ ] Implement persistent storage for `OperatorBinding` records via EF Core in the Persistence project: create `OperatorBindingConfiguration` entity configuration with table `operator_bindings`, columns matching the `OperatorBinding` record (Id, TelegramUserId, TelegramChatId, ChatType, OperatorAlias, TenantId, WorkspaceId, Roles serialized as JSON, RegisteredAt, IsActive); add composite index on `(TelegramUserId, TelegramChatId)` for runtime authorization lookups, unique index on `OperatorAlias` for `/handoff` alias resolution, and index on `TelegramUserId` for `GetAllBindingsAsync`
- [ ] Create EF Core migration `AddOperatorBindings` that creates the `operator_bindings` table with the above schema and indexes; use SQLite for dev/local and PostgreSQL or SQL Server for production (consistent with the persistence strategy in Stages 4.1, 5.3)
- [ ] Implement `PersistentOperatorRegistry` in the Persistence project that wraps the EF Core `DbContext` and implements `IOperatorRegistry`: `GetByTelegramUserAsync` queries by `(TelegramUserId, TelegramChatId)` with `IsActive=true`; `IsAuthorizedAsync` checks for an active binding matching the `(userId, chatId)` pair; `RegisterAsync` upserts a binding row (insert if absent, update `IsActive=true` and refresh `RegisteredAt` if deactivated); `GetByAliasAsync` queries by `OperatorAlias` with `IsActive=true`
- [ ] Implement `TelegramUserAuthorizationService` implementing `IUserAuthorizationService` that checks the allowlist and returns `AuthorizationResult`; at runtime (non-`/start` commands), delegates to `IOperatorRegistry.IsAuthorizedAsync(userId, chatId)` to enforce persistent binding-based authorization
- [ ] On `/start`, if the Telegram user ID is in the allowlist, register/update the chat-ID-to-operator mapping via `IOperatorRegistry.RegisterAsync` (creating a persistent `OperatorBinding` row); if not, respond with an "unauthorized" message and log the attempt

### Dependencies
- phase-command-processing-and-agent-routing/stage-command-router-and-handlers

### Test Scenarios
- [ ] Scenario: Authorized user mapped — Given Telegram user `12345` is in the allowlist mapped to operator `op-1` in tenant `t-1`, When `/start` is received, Then `AuthorizationResult.IsAuthorized` is true and `OperatorId` is `op-1`
- [ ] Scenario: Unauthorized user rejected — Given Telegram user `99999` is not in the allowlist, When any command is received, Then `AuthorizationResult.IsAuthorized` is false and `DenialReason` is populated
- [ ] Scenario: Binding persists across restart — Given Telegram user `12345` sends `/start` and an `OperatorBinding` row is created, When the service restarts and user `12345` sends `/status` from the same chat, Then `IsAuthorizedAsync` returns true because the binding is persisted in the database
- [ ] Scenario: Alias lookup resolves binding — Given an `OperatorBinding` exists with `OperatorAlias=@operator-1`, When `GetByAliasAsync("@operator-1")` is called, Then the correct `OperatorBinding` is returned with matching `TelegramUserId` and `TelegramChatId`

## Stage 3.5: Pending Question Store and Timeout Service

### Implementation Steps
- [ ] Define `IPendingQuestionStore` concrete registration point — the interface is defined in Stage 1.3 Abstractions; this stage provides the EF Core implementation
- [ ] Create `PendingQuestionRecord` entity with fields: `QuestionId`, `AgentQuestion` (serialized), `TelegramChatId`, `TelegramMessageId`, `StoredAt`, `ExpiresAt`, `DefaultActionId` (nullable `string` — denormalized from `AgentQuestionEnvelope.ProposedDefaultActionId` sidecar metadata at question-send time per e2e-scenarios.md lines 57–76; stored here so that `QuestionTimeoutService` can poll for expired questions and resolve the default via `IDistributedCache` without re-fetching the full envelope), `Status` (enum: `Pending`, `Answered`, `AwaitingComment`, `TimedOut`), `CorrelationId`
- [ ] Register `IDistributedCache` in the DI container (e.g., `AddDistributedMemoryCache()` for dev/local, `AddStackExchangeRedisCache()` for production) — this cache is used by `TelegramMessageSender` (Stage 2.3) to write `QuestionId:ActionId → HumanAction` entries at inline-keyboard build time, and by `CallbackQueryHandler` (Stage 3.3) and `QuestionTimeoutService` (this stage) to resolve full `HumanAction` payloads from short `ActionId` keys
- [ ] Implement `PersistentPendingQuestionStore` implementing `IPendingQuestionStore` (interface from Stage 1.3 Abstractions) backed by the Persistence project (EF Core; SQLite for dev/local, PostgreSQL or SQL Server for production) with indexed lookups by `QuestionId`, `ExpiresAt`, and `DefaultActionId`
- [ ] Integrate store into `TelegramMessageSender.SendQuestionAsync`: after successfully sending a question to Telegram, persist the question with its Telegram message ID for later lookup by `CallbackQueryHandler`
- [ ] Implement `RequiresComment` flow in `CallbackQueryHandler`: when the selected `HumanAction.RequiresComment` is true, set the question status to `AwaitingComment`, send a prompt ("Please reply with your comment"), and defer `HumanDecisionEvent` emission until the operator's text reply arrives via the pipeline's `TextReply` handler
- [ ] Create `QuestionTimeoutService` as a `BackgroundService` that periodically polls `GetExpiredAsync`, and for each expired question: reads `PendingQuestionRecord.DefaultActionId` — if present, resolves the full `HumanAction` from `IDistributedCache` and publishes a `HumanDecisionEvent` with that action's value; if absent (`null`), publishes a `HumanDecisionEvent` with `ActionValue = "__timeout__"`; updates the original Telegram message (via `TelegramMessageId`) to indicate timeout ("⏰ Timed out — default action applied: {action}" or "⏰ Timed out — no default action"); marks the question as `TimedOut`; and writes an audit record

### Dependencies
- phase-command-processing-and-agent-routing/stage-callback-query-handler

### Test Scenarios
- [ ] Scenario: Question stored on send — Given an `AgentQuestion` is sent to Telegram successfully, When `SendQuestionAsync` completes, Then a `PendingQuestionRecord` exists in the store with status `Pending` and the correct `TelegramMessageId`
- [ ] Scenario: Callback resolves pending question — Given a pending question `Q1`, When the callback query handler processes an approve action, Then the question status is updated to `Answered` and a `HumanDecisionEvent` is emitted
- [ ] Scenario: RequiresComment defers decision — Given a pending question with an action having `RequiresComment=true`, When the operator taps that button, Then the bot prompts for a comment and the `HumanDecisionEvent` is not emitted until the operator replies with text
- [ ] Scenario: Timeout applies default action — Given a pending question with `ExpiresAt` in the past and `PendingQuestionRecord.DefaultActionId=skip`, When `QuestionTimeoutService` polls, Then it resolves the `HumanAction` from cache, emits a `HumanDecisionEvent` with `ActionValue=skip`, and updates the Telegram message with "⏰ Timed out — default action applied: skip"
- [ ] Scenario: Timeout without default action — Given a pending question with `ExpiresAt` in the past and `PendingQuestionRecord.DefaultActionId` is null, When `QuestionTimeoutService` polls, Then a `HumanDecisionEvent` is emitted with `ActionValue=__timeout__` and the Telegram message is updated with "⏰ Timed out — no default action"

# Phase 4: Reliability Infrastructure

## Dependencies
- phase-command-processing-and-agent-routing

## Stage 4.1: Durable Outbound Message Queue

### Implementation Steps
- [ ] Define `IOutboundQueue` interface in Core (per architecture.md §4.4) with methods: `EnqueueAsync(OutboundMessage, CancellationToken)`, `DequeueAsync(CancellationToken)` returning the highest-severity pending message (severity-priority order: `Critical` > `High` > `Normal` > `Low`), `MarkSentAsync(Guid messageId, int telegramMessageId, CancellationToken)`, `MarkFailedAsync(Guid messageId, string error, CancellationToken)`, `DeadLetterAsync(Guid messageId, CancellationToken)`
- [ ] Create `OutboundMessage` record matching architecture.md's data model: `Id` (Guid), `IdempotencyKey` (string, derived per `SourceType` — see architecture.md idempotency key derivation table), `ChatId`, `Payload` (serialized MessengerMessage or AgentQuestion), `Severity` (enum: `Critical`, `High`, `Normal`, `Low` — determines priority queue ordering), `SourceType` (enum: `Question`, `Alert`, `StatusUpdate`, `CommandAck` — discriminator for origin type), `SourceId` (string, nullable — `QuestionId` for questions, alert rule ID for alerts, command correlation ID for acks; null only for fire-and-forget status broadcasts), `Status` (enum: `Pending`, `Sending`, `Sent`, `Failed`, `DeadLettered`), `AttemptCount`, `MaxAttempts`, `NextRetryAt`, `CreatedAt`, `SentAt`, `TelegramMessageId`, `CorrelationId`, `ErrorDetail`
- [ ] Add a `UNIQUE` constraint on `IdempotencyKey` in the outbox table so that duplicate enqueue attempts for the same logical message are rejected at the database level
- [ ] Implement `InMemoryOutboundQueue` using a priority-ordered `Channel<OutboundMessage>` (severity-priority dequeue: `Critical` > `High` > `Normal` > `Low`) with bounded capacity for development
- [ ] Implement `PersistentOutboundQueue` backed by EF Core for durable persistence; use SQLite provider for dev/local environments and PostgreSQL or SQL Server for production (the specific production provider is a deployment decision, consistent with architecture.md §11.3); persist messages to an `outbox` table with status tracking; `DequeueAsync` queries `WHERE Status=Pending ORDER BY Severity ASC, CreatedAt ASC` to enforce severity-priority ordering; on `EnqueueAsync`, check `IdempotencyKey` uniqueness before inserting; when queue depth exceeds `OutboundQueue:MaxQueueDepth` (default 5000, per architecture.md §10.4), dead-letter `Low`-severity messages immediately with reason `backpressure:queue_depth_exceeded` and emit a `telegram.queue.backpressure` metric — `Normal`, `High`, and `Critical` messages are always accepted
- [ ] Create `OutboundQueueProcessor` as a `BackgroundService` with configurable concurrency (`OutboundQueue:ProcessorConcurrency`, default 10 workers per architecture.md §10.4); each worker independently dequeues the highest-severity pending message, transitions to `Sending`, sends via `TelegramMessageSender`, and transitions to `Sent` on success or `Failed` on error; under burst conditions (100+ agents), the 10 concurrent workers combined with severity-priority dequeue ensure Critical/High messages reach the Telegram API within the P95 ≤ 2s target — per architecture.md §10.4, this target applies to `telegram.send.latency_ms`, the all-inclusive metric measuring elapsed time from enqueue (`OutboundMessage.CreatedAt`) to Telegram API HTTP 200 for all messages regardless of attempt number or rate-limit holds; under extreme burst conditions, Normal/Low severity messages may queue-delay beyond 2 seconds per the severity-scoped SLO

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Enqueue and dequeue — Given a message is enqueued, When dequeued, Then the message content matches and the queue size decreases by one
- [ ] Scenario: Persistence survives restart — Given a message is enqueued to the persistent queue, When the process restarts, Then the message is still available for dequeue
- [ ] Scenario: Severity-priority dequeue — Given the queue contains a `Low`-severity message enqueued first and a `Critical`-severity message enqueued second, When `DequeueAsync` is called, Then the `Critical` message is returned first
- [ ] Scenario: Backpressure dead-letters low-severity — Given the queue depth exceeds `MaxQueueDepth` (5000), When a `Low`-severity message is enqueued, Then it is dead-lettered immediately with reason `backpressure:queue_depth_exceeded` and a `telegram.queue.backpressure` metric is emitted; when a `Critical`-severity message is enqueued under the same conditions, Then it is accepted normally
- [ ] Scenario: Concurrent processor workers — Given `ProcessorConcurrency=10` and 100 pending messages, When the processor runs, Then up to 10 messages are dequeued and sent concurrently
- [ ] Scenario: Outbound deduplication by idempotency key — Given a message with `IdempotencyKey=agent1:Q1:corr-1` is already enqueued, When a second message with the same `IdempotencyKey` is enqueued, Then the duplicate is rejected and the original message is returned without creating a second outbox entry

## Stage 4.2: Retry Policy and Dead-Letter Queue

### Implementation Steps
- [ ] Create `RetryPolicy` configuration POCO with properties: `MaxAttempts` (default 5, aligned with architecture.md §5.3 `OutboundQueue:MaxRetries` default of 5 and e2e-scenarios.md alignment footer), `InitialDelayMs` (default 2000, aligned with architecture.md `BaseRetryDelaySeconds` default of 2), `BackoffMultiplier` (default 2.0), `MaxDelayMs` (default 30000), `JitterPercent` (default 25, aligned with architecture.md ±25% jitter)
- [ ] Implement exponential backoff with jitter in `OutboundQueueProcessor`: on transient failure, increment `Attempt`, compute next retry time, and re-enqueue
- [ ] Define `IDeadLetterQueue` interface in Core with methods: `SendToDeadLetterAsync(OutboundMessage, FailureReason, CancellationToken)`, `ListAsync(CancellationToken)`
- [ ] Implement `DeadLetterQueue` backed by the same persistence layer (EF Core; SQLite for dev/local, PostgreSQL or SQL Server for production) with `dead_letter_messages` table containing full failure context
- [ ] After `MaxAttempts` exhausted, move message to dead-letter queue and emit an alert event via `IAlertService` (defined in Stage 1.4 Abstractions) to notify operators on a secondary channel
- [ ] Add health check that reports unhealthy if dead-letter queue depth exceeds a configurable threshold

### Dependencies
- phase-reliability-infrastructure/stage-durable-outbound-message-queue

### Test Scenarios
- [ ] Scenario: Retry with backoff — Given a message fails on first attempt, When retried, Then the delay before the second attempt is approximately `InitialDelayMs` (within jitter tolerance)
- [ ] Scenario: Dead-lettered after max attempts — Given `MaxAttempts=5` and a message that always fails, When processed five times, Then the message is in the dead-letter queue and an alert is emitted
- [ ] Scenario: Health check degrades — Given 10 messages in the dead-letter queue and threshold is 5, When health check is queried, Then status is `Unhealthy`

## Stage 4.3: Inbound Deduplication Service

### Implementation Steps
- [ ] Implement `DeduplicationService` (the concrete persistent class implementing `IDeduplicationService` defined in Stage 1.3, **replacing** the in-memory stub registered by Stage 2.2) using a time-bounded sliding-window store: processed event IDs are retained for a configurable TTL (default 1 hour), older entries are evicted
- [ ] For development, back with `ConcurrentDictionary<string, DateTimeOffset>` with a periodic cleanup timer
- [ ] For production, back with a database table `processed_events(event_id TEXT PK, processed_at DATETIME)` via EF Core (SQLite for dev, PostgreSQL or SQL Server for production) with a periodic purge of expired entries
- [ ] Integrate persistent deduplication into the DI container by replacing the Stage 2.2 stub registration of `IDeduplicationService` with the persistent `DeduplicationService`; the webhook endpoint and polling loop already call `IDeduplicationService` (via the pipeline from Stage 2.2), so no additional integration is needed — only the DI registration changes

### Dependencies
- phase-reliability-infrastructure/stage-durable-outbound-message-queue

### Test Scenarios
- [ ] Scenario: First event processed — Given event ID `evt-1` has not been seen, When `IsProcessedAsync` is called, Then it returns false; after `MarkProcessedAsync`, a second call returns true
- [ ] Scenario: Expired events evicted — Given event `evt-old` was processed 2 hours ago and TTL is 1 hour, When cleanup runs, Then `IsProcessedAsync("evt-old")` returns false
- [ ] Scenario: Webhook dedup integration — Given a Telegram Update with `Id=42` is received and processed, When the same `Id=42` arrives again, Then the webhook returns 200 but no downstream processing occurs

# Phase 5: Security and Audit

## Dependencies
- phase-reliability-infrastructure

## Stage 5.1: Secret Management Integration

### Implementation Steps
- [ ] Add Azure Key Vault integration to the Worker project: install `Azure.Extensions.AspNetCore.Configuration.Secrets` NuGet package and configure in `Program.cs` to load secrets when a Key Vault URI is configured
- [ ] Map Key Vault secret `TelegramBotToken` to `Telegram:BotToken` configuration path
- [ ] For local development, support .NET User Secrets as the token source; document the setup procedure in `docs/stories/qq-TELEGRAM-MESSENGER-S/dev-setup.md` as an **implementation artifact and operator/developer setup guide** (not a planning document — the implementation plan remains the sole progress source of truth) with step-by-step instructions for configuring User Secrets, environment variables, and local Telegram Bot API testing
- [ ] Add startup validation that logs (at Warning level) if neither Key Vault nor User Secrets provides the token, then fails with a clear error

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Key Vault token loaded — Given Key Vault contains `TelegramBotToken`, When the Worker starts with Key Vault URI configured, Then `TelegramOptions.BotToken` is populated from Key Vault
- [ ] Scenario: Missing token fails startup — Given no token source is configured, When the Worker starts, Then it exits with a descriptive error within 5 seconds

## Stage 5.2: Chat and User Allowlist Enforcement

### Implementation Steps
- [ ] Normalize allowlist configuration into the existing `TelegramOptions` POCO (defined in Stage 2.1): the `AllowedUserIds` property (already present as `List<long>` on `TelegramOptions`, bound from `Telegram:AllowedUserIds`) serves as the single source of truth for the onboarding allowlist — no separate `AllowlistOptions` POCO is created; this ensures consistent binding across all stages (2.1, 5.2, 6.3) that reference `TelegramOptions.AllowedUserIds`
- [ ] Implement two-tier authorization per architecture.md §7.1: Tier 1 (onboarding) checks `TelegramOptions.AllowedUserIds` via `IOptions<TelegramOptions>` at `/start` time only — if the user's Telegram ID is not in the list, `/start` is rejected and no `OperatorBinding` is created; Tier 2 (runtime) checks `OperatorBinding` records via `IOperatorRegistry.IsAuthorizedAsync(userId, chatId)` on every inbound command — if no matching binding exists for the (userId, chatId) pair, the command is rejected
- [ ] If user is not authorized, respond with a polite denial message, log the attempt at Warning level with user ID and chat ID (but no PII beyond Telegram numeric IDs), and short-circuit processing
- [ ] Support dynamic allowlist reload without restart via `IOptionsMonitor<TelegramOptions>` — changes to `Telegram:AllowedUserIds` in configuration are reflected immediately for `/start` onboarding checks without requiring a service restart

### Dependencies
- phase-security-and-audit/stage-secret-management-integration

### Test Scenarios
- [ ] Scenario: Allowed user passes — Given user ID `12345` has an active `OperatorBinding` for chat ID `67890`, When a command arrives from user `12345` in chat `67890`, Then processing continues normally
- [ ] Scenario: Denied user blocked — Given user ID `99999` has no `OperatorBinding` record, When a command arrives, Then the user receives the denial message and no command handler is invoked
- [ ] Scenario: Dynamic reload — Given user `67890` is added to `Telegram:AllowedUserIds` while the service is running, When `67890` sends `/start`, Then the `OperatorBinding` is created and subsequent commands are accepted without a restart (verified via `IOptionsMonitor<TelegramOptions>` reload)
- [ ] Scenario: Chat authorized through /start — Given user `12345` is in `TelegramOptions.AllowedUserIds` and sends `/start` from chat `55555`, When `12345` later sends `/status` from chat `55555`, Then the command is accepted because the `OperatorBinding` exists for that (userId, chatId) pair

## Stage 5.3: Audit Logging Persistence

### Implementation Steps
- [ ] Create `AuditLogEntry` entity in Persistence with columns: `Id`, `MessageId`, `ExternalUserId`, `AgentId`, `Action`, `Timestamp`, `CorrelationId`, `TenantId`, `Details` (JSON), `Platform` (always "Telegram")
- [ ] Create EF Core `AuditDbContext` with a `AuditLogs` DbSet; configure SQLite provider for dev/local and PostgreSQL or SQL Server for production (consistent with architecture.md persistence strategy)
- [ ] Implement `PersistentAuditLogger` implementing `IAuditLogger` that writes audit entries transactionally
- [ ] Integrate audit logging at command router level: log every inbound command and every outbound decision event with full context
- [ ] Ensure audit records are immutable (no UPDATE/DELETE operations exposed); add a DB migration that creates the table with appropriate indexes on `CorrelationId` and `Timestamp`

### Dependencies
- phase-security-and-audit/stage-chat-and-user-allowlist-enforcement

### Test Scenarios
- [ ] Scenario: Command audited — Given user `12345` sends `/ask build release notes`, When the command is processed, Then an `AuditLogEntry` exists with `Action=ask`, `ExternalUserId=12345`, and a non-null `CorrelationId`
- [ ] Scenario: Decision audited — Given a button press approving question `Q1`, When the callback is processed, Then an `AuditLogEntry` exists with `Action=approve`, `MessageId` matching the callback, and the `AgentId` from the original question
- [ ] Scenario: Audit immutability — Given an audit entry exists, When an attempt is made to modify it via the `IAuditLogger` interface, Then no update method is available (compile-time enforcement)

# Phase 6: Observability and Production Readiness

## Dependencies
- phase-security-and-audit

## Stage 6.1: OpenTelemetry Integration

### Implementation Steps
- [ ] Add NuGet packages `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Exporter.Console` (dev), `OpenTelemetry.Exporter.OpenTelemetryProtocol` (prod)
- [ ] Configure tracing in `Program.cs`: add custom `ActivitySource` named `AgentSwarm.Messaging.Telegram` and instrument all command processing, outbound sends, and queue operations
- [ ] Configure metrics: create custom meters for `telegram.messages.sent`, `telegram.messages.received`, `telegram.commands.processed`, `telegram.errors`, `telegram.queue.depth`, `telegram.dlq.depth`
- [ ] Add structured logging via `ILogger` with consistent property names: `CorrelationId`, `AgentId`, `TelegramUserId`, `CommandName` — ensure bot token is never included in any log scope

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Traces emitted — Given a command is processed end-to-end, When the console exporter is active, Then a trace span with `ActivitySource=AgentSwarm.Messaging.Telegram` is emitted containing `CorrelationId`
- [ ] Scenario: Token excluded from logs — Given structured logging is configured, When all log scopes are inspected during a command flow, Then no log entry contains the bot token string

## Stage 6.2: Health Checks and Liveness

### Implementation Steps
- [ ] Add ASP.NET Core health check endpoint at `/healthz` in the Worker project
- [ ] Implement `TelegramBotHealthCheck` that calls `GetMeAsync` on the bot client and reports healthy if the bot identity is returned within 5 seconds
- [ ] Implement `OutboundQueueHealthCheck` that reports degraded if queue depth exceeds 1000 and unhealthy if dead-letter queue depth exceeds configurable threshold
- [ ] Implement `DatabaseHealthCheck` that verifies the audit database is reachable and migrated
- [ ] Register all health checks in DI and expose at `/healthz` with JSON detail output

### Dependencies
- phase-observability-and-production-readiness/stage-opentelemetry-integration

### Test Scenarios
- [ ] Scenario: Healthy system — Given the bot token is valid, queue is empty, and DB is reachable, When `/healthz` is called, Then HTTP 200 with all checks reporting `Healthy`
- [ ] Scenario: Bot unreachable degrades health — Given the Telegram API is unreachable, When `/healthz` is called, Then the `TelegramBot` check reports `Unhealthy` and overall status is `Unhealthy`

## Stage 6.3: Worker Host Composition and Configuration

### Implementation Steps
- [ ] Wire up `Program.cs` in the Worker project: register all services (Telegram connector, command router, all handlers, outbound queue, deduplication, audit, health checks, OpenTelemetry)
- [ ] Create `appsettings.json` with documented configuration sections: `Telegram` (including `BotToken`, `WebhookUrl`, `UsePolling`, `AllowedUserIds`, `SecretToken`, `RateLimits`), `RetryPolicy`, `OutboundQueue`, `ConnectionStrings:MessagingDb` (shared connection string used by `MessagingDbContext` for all EF Core-backed stores: `InboundUpdate` dedup/recovery from Stage 2.4, outbox from Stage 4.1, dead-letter queue from Stage 4.2, `processed_events` dedup from Stage 4.3, `PendingQuestionRecord` from Stage 3.5, `OperatorBinding` from Stage 3.4, and `TaskOversight` from Stage 3.2; defaults to SQLite `Data Source=messaging.db` for dev/local, swappable to PostgreSQL or SQL Server for production via EF Core provider change), `ConnectionStrings:AuditDb` (separate connection for audit log isolation from Stage 5.3; defaults to SQLite `Data Source=audit.db` for dev/local), `KeyVault:Uri`
- [ ] Create `appsettings.Development.json` with polling mode enabled, console OTel exporter, in-memory queue, and SQLite connection strings: `ConnectionStrings:MessagingDb` = `Data Source=messaging.db` and `ConnectionStrings:AuditDb` = `Data Source=audit.db`
- [ ] Add `Dockerfile` for the Worker project: multi-stage build, `dotnet publish` for linux-x64, expose port 8443 for webhook, configure Kestrel to listen on port 8443 via `ASPNETCORE_URLS=http://+:8443`, health check `HEALTHCHECK CMD curl -f http://localhost:8443/healthz`
- [ ] Add `docker-compose.yml` at repo root with the worker service and optional ngrok sidecar for local webhook testing

### Dependencies
- phase-observability-and-production-readiness/stage-health-checks-and-liveness

### Test Scenarios
- [ ] Scenario: Worker starts in dev mode — Given `appsettings.Development.json` with `UsePolling=true`, When `dotnet run` is executed, Then the worker starts polling without errors and `/healthz` returns 200
- [ ] Scenario: Docker image builds — Given the Dockerfile, When `docker build` is run, Then the image builds successfully and the container starts with `/healthz` responding
- [ ] Scenario: All services resolve — Given the full DI composition in `Program.cs`, When the host is built, Then all registered services resolve without `InvalidOperationException`

# Phase 7: Integration Testing and Acceptance Validation

## Dependencies
- phase-observability-and-production-readiness

## Stage 7.1: Integration Test Infrastructure

### Implementation Steps
- [ ] Create `tests/AgentSwarm.Messaging.IntegrationTests/AgentSwarm.Messaging.IntegrationTests.csproj` with references to Worker, `Microsoft.AspNetCore.Mvc.Testing`, `WireMock.Net`
- [ ] Create `TelegramTestFixture` using `WebApplicationFactory<Program>` that configures the worker with in-memory queue, in-memory dedup, and a WireMock-based Telegram API stub
- [ ] Implement `FakeTelegramApi` using WireMock to simulate `sendMessage`, `answerCallbackQuery`, `editMessageReplyMarkup`, and `getMe` endpoints
- [ ] Create test helper methods: `SimulateWebhookUpdate(Update)`, `SimulateCallbackQuery(CallbackQuery)`, `AssertMessageSent(chatId, textContains)`

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Test fixture starts — Given the `TelegramTestFixture` is instantiated, When the test host starts, Then `/healthz` returns 200 and WireMock is listening
- [ ] Scenario: Fake API records calls — Given `FakeTelegramApi` is configured, When `SendTextAsync` is invoked via the connector, Then WireMock records the `sendMessage` call with correct parameters

## Stage 7.2: End-to-End Acceptance Tests

### Implementation Steps
- [ ] Write test `AC001_AskCreatesWorkItem`: simulate webhook update with `/ask build release notes for Solution12`, verify a work item creation event is emitted with correct task description
- [ ] Write test `AC002_AgentQuestionWithButtons`: enqueue an `AgentQuestion` with three actions, verify the fake Telegram API receives a `sendMessage` call with `InlineKeyboardMarkup` containing three buttons
- [ ] Write test `AC003_ButtonPressEmitsDecisionEvent`: simulate a callback query for an approve button, verify a `HumanDecisionEvent` is emitted with correct `QuestionId`, `ActionValue`, and `CorrelationId`
- [ ] Write test `AC004_DuplicateWebhookIdempotent`: send the same `Update.Id` twice via webhook, verify command handler is invoked exactly once
- [ ] Write test `AC005_FailedSendRetriesAndDeadLetters`: configure WireMock to return 500 for `sendMessage`, enqueue a message, verify retry attempts occur and message is eventually dead-lettered
- [ ] Write test `AC006_AllMessagesHaveCorrelationId`: process multiple commands and questions, inspect all emitted events and outbound messages to verify every one carries a non-null `CorrelationId`
- [ ] Write test `PERF001_P95SendLatencyUnder2Seconds`: enqueue 100 outbound messages, configure WireMock to respond with 200 in under 50ms, measure the elapsed time from `OutboundMessage.CreatedAt` (enqueue instant) to Telegram API HTTP 200 (send-completion) for each message — this is the `telegram.send.latency_ms` all-inclusive metric per architecture.md §10.4, covering all messages regardless of attempt number or rate-limit holds — and assert that the 95th percentile is under 2 seconds
- [ ] Write test `PERF002_BurstFrom100PlusAgents`: simulate 100+ agents each enqueuing one alert message concurrently (1000+ total messages), process all through the outbound queue, and assert that every message reaches either `Sent` or `DeadLettered` status (zero messages lost) and the queue drains completely within a bounded time

### Dependencies
- phase-integration-testing-and-acceptance-validation/stage-integration-test-infrastructure

### Test Scenarios
- [ ] Scenario: All acceptance tests pass — Given the full integration test suite, When `dotnet test` is run, Then all eight AC and PERF tests pass
- [ ] Scenario: Tests are deterministic — Given the integration tests, When run three times consecutively, Then all pass every time with no flaky failures
- [ ] Scenario: P95 latency measured accurately — Given the PERF001 test, When 100 messages are sent through the outbound pipeline, Then a P95 histogram is computed using the `telegram.send.latency_ms` all-inclusive metric (per architecture.md §10.4) and the assertion threshold is 2000ms

---

## Cross-Document Alignment Notes

| Topic | This document's position | Sibling document status |
|-------|-------------------------|------------------------|
| **DefaultAction model** | Stage 1.2 defines `AgentQuestion` **without** a `DefaultAction` property. The proposed default action is carried as sidecar metadata via `ProposedDefaultActionId` in `AgentQuestionEnvelope` (Stage 1.2). This aligns with architecture.md §3.1 (lines 167–184) and e2e-scenarios.md (lines 57–63, 613). The connector reads `ProposedDefaultActionId` from the envelope, denormalizes it into `PendingQuestionRecord.DefaultActionId` (Stage 3.5), and applies it on timeout. | **tech-spec.md** Section 10 alignment table (line 160) incorrectly states that this plan's Stage 1.2 "defines `AgentQuestion` with `DefaultAction` as a first-class property." This is a stale cross-reference — this plan has used the sidecar-metadata approach since iteration 12. tech-spec.md should update its alignment table column for implementation-plan.md to reflect the sidecar approach. **architecture.md** has an internal inconsistency: §3.1 specifies sidecar-metadata (no `DefaultAction` on `AgentQuestion`), while §5.3 (line 424) describes `DefaultAction` as a first-class property. This plan follows §3.1 as authoritative. |
| **Data-flow for default actions** | `IMessengerConnector.SendQuestionAsync` accepts `AgentQuestionEnvelope` (not bare `AgentQuestion`) so the connector has access to `ProposedDefaultActionId` and `RoutingMetadata`. `IPendingQuestionStore.StoreAsync` accepts `AgentQuestionEnvelope`, `telegramChatId`, and `telegramMessageId` so the store can denormalize `ProposedDefaultActionId` into `PendingQuestionRecord.DefaultActionId` and persist `TelegramChatId` for timeout message edits. This closes the data-flow gap between Stage 1.2 (envelope definition), Stage 1.3 (interface contracts), Stage 2.3 (outbound sender), and Stage 3.5 (pending question store/timeout). | Consistent with e2e-scenarios.md lines 64–77 (envelope fixture) and architecture.md §3.1. |
| **P95 latency scope** | The P95 ≤ 2s acceptance criterion applies to `telegram.send.latency_ms`, the all-inclusive metric measuring elapsed time from `OutboundMessage.CreatedAt` (enqueue instant) to Telegram API HTTP 200, for all messages regardless of attempt number or rate-limit holds, per architecture.md §10.4 (lines 662–666). No provisional scoping remains. | Consistent with architecture.md §10.4 and e2e-scenarios.md line 618. tech-spec.md HC-4 previously noted the scope as pending confirmation; this plan adopts the architecture.md definition as decided. |
| **Stage ordering** | Phase 2 stages are ordered: 2.1 Bot Client Wrapper → 2.2 Inbound Update Pipeline → 2.3 Outbound Message Sender → 2.4 Webhook Receiver → 2.5 Long Polling Receiver. The pipeline (2.2) appears before both receivers (2.4, 2.5), so all dependency references point to upstream stage anchors. | No cross-doc impact; stage ordering is internal to implementation-plan.md. |
