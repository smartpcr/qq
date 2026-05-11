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
- [ ] Create `AgentQuestion` record in Abstractions with properties: `QuestionId`, `AgentId`, `TaskId`, `Title`, `Body`, `Severity`, `AllowedActions` (list of `HumanAction`), `ExpiresAt`, `DefaultAction`, `CorrelationId`
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
- [ ] Scenario: Serialization round-trip — Given an `AgentQuestion` with all fields populated, When serialized to JSON and deserialized back, Then all field values match the original

## Stage 1.3: Connector Interface and Service Contracts

### Implementation Steps
- [ ] Create `IMessengerConnector` interface in Abstractions with methods: `SendMessageAsync(MessengerMessage, CancellationToken)`, `SendQuestionAsync(AgentQuestion, CancellationToken)`, `ReceiveAsync(CancellationToken)` returning `IReadOnlyList<MessengerEvent>`
- [ ] Create `ICommandRouter` interface in Abstractions with method: `RouteAsync(MessengerEvent, CancellationToken)` returning `CommandResult`
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
- [ ] Create `IAlertService` interface in Abstractions with method: `SendAlertAsync(string subject, string detail, CancellationToken)` — used to notify operators via a secondary channel when dead-letter events occur or critical failures are detected

### Dependencies
- phase-messaging-abstractions-and-solution-scaffold/stage-shared-data-models

### Test Scenarios
- [ ] Scenario: Interface contracts compile — Given all interfaces are defined in Abstractions, When the project is built, Then it compiles with zero errors
- [ ] Scenario: Mock connector satisfies interface — Given a Moq mock of `IMessengerConnector`, When `SendMessageAsync` is invoked, Then the mock records the call without error
- [ ] Scenario: Pipeline interface mockable — Given a Moq mock of `ITelegramUpdatePipeline`, When `ProcessAsync` is invoked with a `MessengerEvent`, Then the mock records the call and returns a `PipelineResult`

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

## Stage 2.2: Webhook Receiver Endpoint

### Implementation Steps
- [ ] Create ASP.NET Core minimal API endpoint `POST /api/telegram/webhook` in the Worker project that receives Telegram `Update` JSON payloads
- [ ] Implement `TelegramWebhookSecretFilter` that validates the `X-Telegram-Bot-Api-Secret-Token` header against the configured `SecretToken`; reject with 403 if mismatch
- [ ] Deserialize the incoming `Update` using `Telegram.Bot` serialization and convert to the internal `MessengerEvent` model via a `TelegramUpdateMapper` class
- [ ] Implement idempotency check: store processed `Update.Id` values in a time-bounded concurrent dictionary (or persistence layer); skip duplicate updates and return 200
- [ ] On successful processing, return HTTP 200 immediately; pass the `MessengerEvent` to `ITelegramUpdatePipeline.ProcessAsync` (interface defined in Abstractions, implementation provided via DI from Stage 2.5) for async command routing
- [ ] Add webhook registration logic in `IHostedService` startup: call `SetWebhookAsync` with the configured URL, secret token, and allowed update types

### Dependencies
- phase-telegram-bot-integration/stage-telegram-bot-client-wrapper
- phase-telegram-bot-integration/stage-inbound-update-pipeline

### Test Scenarios
- [ ] Scenario: Valid webhook accepted — Given a well-formed Telegram Update JSON, When POSTed to `/api/telegram/webhook` with correct secret header, Then response is HTTP 200
- [ ] Scenario: Invalid secret rejected — Given a Telegram Update JSON, When POSTed with an incorrect `X-Telegram-Bot-Api-Secret-Token`, Then response is HTTP 403
- [ ] Scenario: Duplicate update ignored — Given the same `Update.Id` is received twice, When both are POSTed, Then only the first triggers downstream processing

## Stage 2.3: Long Polling Receiver for Development

### Implementation Steps
- [ ] Create `TelegramPollingService` as a `BackgroundService` in the Telegram project that calls `GetUpdatesAsync` in a loop when `TelegramOptions.UsePolling` is `true`
- [ ] Map each received `Update` to `MessengerEvent` using the shared `TelegramUpdateMapper` and pass to `ITelegramUpdatePipeline.ProcessAsync` (interface defined in Abstractions, implementation provided via DI)
- [ ] Implement graceful shutdown: respect `CancellationToken`, log final offset, and cleanly stop polling
- [ ] Ensure polling and webhook modes are mutually exclusive at startup via a guard in the DI registration

### Dependencies
- phase-telegram-bot-integration/stage-webhook-receiver-endpoint
- phase-telegram-bot-integration/stage-inbound-update-pipeline

### Test Scenarios
- [ ] Scenario: Polling receives updates — Given polling mode is enabled and the bot has pending updates, When the polling loop executes, Then updates are mapped and enqueued
- [ ] Scenario: Mutual exclusion enforced — Given both `UsePolling=true` and `WebhookUrl` is set, When the host starts, Then startup fails with a configuration error explaining the conflict

## Stage 2.4: Outbound Message Sender

### Implementation Steps
- [ ] Create `TelegramMessageSender` implementing a `IMessageSender` interface (defined in Core) with methods: `SendTextAsync(chatId, text, parseMode, ct)`, `SendQuestionAsync(chatId, AgentQuestion, ct)`
- [ ] Implement `SendQuestionAsync` to render `AgentQuestion` as a rich Telegram message: include `Title`, `Body` (full context), `Severity` badge, `ExpiresAt` timeout countdown, and `DefaultAction` label in the message body; render `AllowedActions` as Telegram `InlineKeyboardMarkup` buttons with callback data encoding `QuestionId:ActionId`
- [ ] For actions with `RequiresComment = true`, append "(reply required)" to the button label so the operator knows a follow-up text reply is expected after tapping
- [ ] Format outbound messages with Markdown V2 parse mode; include `CorrelationId` as a footer or hidden tag for traceability
- [ ] Implement Telegram API rate-limit handling: detect `429 Too Many Requests`, extract `RetryAfter`, and back off accordingly
- [ ] Implement a proactive dual token-bucket rate limiter (per architecture.md §10.4) with two layers: a global bucket limiting sends to `Telegram:RateLimits:GlobalPerSecond` (default 30 msg/s) across all chats, and per-chat buckets limiting each individual chat to `Telegram:RateLimits:PerChatPerMinute` (default 20 msg/min); create `RateLimitOptions` configuration POCO bound from `Telegram:RateLimits`; workers acquire tokens before sending and block-wait when exhausted, preventing 429 responses proactively
- [ ] Add message-ID tracking: after successful send, persist the Telegram message ID mapped to `CorrelationId` for reply correlation

### Dependencies
- phase-telegram-bot-integration/stage-telegram-bot-client-wrapper

### Test Scenarios
- [ ] Scenario: Question renders buttons — Given an `AgentQuestion` with three `HumanAction` items, When `SendQuestionAsync` is called, Then the constructed `InlineKeyboardMarkup` contains exactly three buttons with correct labels
- [ ] Scenario: Question body includes full context — Given an `AgentQuestion` with `Severity=Critical`, `ExpiresAt` in 30 minutes, and `DefaultAction=skip`, When `SendQuestionAsync` is called, Then the message body contains the severity badge, timeout information, default action label, and full question `Body` text
- [ ] Scenario: Rate limit handled gracefully — Given the Telegram API returns HTTP 429 with `RetryAfter=5`, When the sender encounters it, Then it waits at least 5 seconds before retrying and does not throw
- [ ] Scenario: CorrelationId in message — Given a `MessengerMessage` with a specific `CorrelationId`, When sent, Then the outbound message body contains the correlation ID
- [ ] Scenario: RequiresComment action labeled — Given an `AgentQuestion` with one action having `RequiresComment=true`, When rendered, Then that button label includes a "(reply required)" indicator
- [ ] Scenario: Proactive rate limiter throttles — Given the global token bucket is exhausted (30 tokens consumed within 1 second), When a new send is attempted, Then the sender blocks until a token is available rather than issuing a request that would be 429'd

## Stage 2.5: Inbound Update Pipeline

### Implementation Steps
- [ ] Implement `TelegramUpdatePipeline` (the concrete class implementing `ITelegramUpdatePipeline` defined in Stage 1.3) in the Telegram project; inject all dependencies as interfaces: `IDeduplicationService`, `IUserAuthorizationService`, `ICommandParser`, `ICommandRouter`, `ICallbackHandler`
- [ ] Compose the pipeline as a sequential chain: deduplication check (via `IDeduplicationService`) → allowlist gate (via `IUserAuthorizationService`) → command parsing (via `ICommandParser`) → routing by `EventType`: `Command` events go to `ICommandRouter`, `CallbackResponse` events go to `ICallbackHandler`, `TextReply` events check for pending `RequiresComment` prompts (via `IPendingQuestionStore`) before falling through to default handling
- [ ] Provide stub/no-op implementations of `ICommandParser`, `ICommandRouter`, `ICallbackHandler`, and `IDeduplicationService` in the Telegram project for initial compilation and testing; concrete implementations are registered in Phase 3 (command processing) and Phase 4 (deduplication)
- [ ] Emit structured log entries at each pipeline stage with `CorrelationId`, `EventId`, and stage name for end-to-end traceability
- [ ] Return a `PipelineResult` (defined in Abstractions) to callers (webhook endpoint and polling service)

### Dependencies
- phase-telegram-bot-integration/stage-telegram-bot-client-wrapper

### Test Scenarios
- [ ] Scenario: Pipeline routes command — Given a `MessengerEvent` with `EventType=Command` and text `/status`, When processed through the pipeline, Then `CommandRouter` is invoked and a response is returned
- [ ] Scenario: Pipeline routes callback — Given a `MessengerEvent` with `EventType=CallbackResponse`, When processed, Then `CallbackQueryHandler` is invoked and a `HumanDecisionEvent` is emitted
- [ ] Scenario: Pipeline rejects unauthorized — Given a `MessengerEvent` from a user not in the allowlist, When processed, Then the pipeline short-circuits with a denial response and no command handler is invoked

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
- [ ] Create `CommandRouter` implementing `ICommandRouter` in Core that dispatches `ParsedCommand` to registered `ICommandHandler` instances via a dictionary keyed by command name
- [ ] Define `ICommandHandler` interface in Core with method `HandleAsync(ParsedCommand, AuthorizedOperator, CancellationToken)` returning `CommandResult`; `AuthorizedOperator` (defined in Stage 1.3) provides the resolved operator identity for authorization and audit
- [ ] Implement `StartCommandHandler` — registers user, responds with welcome and available commands
- [ ] Implement `StatusCommandHandler` — queries swarm orchestrator via `ISwarmCommandBus.QueryStatusAsync` (defined in Stage 1.3) for current status summary, returns formatted text
- [ ] Implement `AgentsCommandHandler` — queries active agents list via `ISwarmCommandBus.QueryAgentsAsync`, returns formatted agent roster
- [ ] Implement `AskCommandHandler` — creates a work item in the swarm orchestrator via `ISwarmCommandBus.PublishCommandAsync` from the argument text, returns confirmation with task ID and correlation ID
- [ ] Implement `ApproveCommandHandler` and `RejectCommandHandler` — resolve a pending `AgentQuestion` by emitting a `HumanDecisionEvent` with the appropriate action value
- [ ] Implement `PauseCommandHandler` and `ResumeCommandHandler` — send pause/resume signals to the target agent via `ISwarmCommandBus.PublishCommandAsync`
- [ ] Implement `HandoffCommandHandler` — accepts syntax `/handoff TASK-ID @operator-alias`, validates both arguments syntactically (parses task ID and operator alias), but returns a stub response "Handoff is not yet configured — awaiting policy decision (D-4)" without performing any transfer, question reassignment, or agent-team change; persists an audit record with the raw command text, user ID, and CorrelationId for future replay when D-4 is decided (aligns with e2e-scenarios.md which requires a not-yet-configured stub and tech-spec.md D-4 which is still Open)

### Dependencies
- phase-command-processing-and-agent-routing/stage-command-parser

### Test Scenarios
- [ ] Scenario: Ask creates work item — Given an authorized user sends `/ask build release notes for Solution12`, When the `AskCommandHandler` processes it, Then a work item is created and the response includes a task ID
- [ ] Scenario: Unknown command rejected — Given a `ParsedCommand` with `CommandName` = `foo`, When routed, Then the result has `Success=false` and a helpful error message listing valid commands
- [ ] Scenario: Approve emits decision event — Given a pending question with `QuestionId=Q1`, When `/approve Q1` is processed, Then a `HumanDecisionEvent` is emitted with `ActionValue=approve` and the correct `CorrelationId`
- [ ] Scenario: Handoff returns stub — Given an authorized user sends `/handoff TASK-099 @operator-2`, When `HandoffCommandHandler` processes it, Then the syntax is parsed successfully, the bot responds with "Handoff is not yet configured — awaiting policy decision (D-4)", no transfer or reassignment occurs, and an audit record is persisted with the raw command text

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
- [ ] Create `OperatorRegistry` in Core that maps Telegram `chatId`/`userId` to an internal `AuthorizedOperator` record containing `OperatorId`, `TenantId`, `WorkspaceId`, `Roles`
- [ ] Store operator mappings in a persistent store (initially an in-memory dictionary backed by a JSON config file, with interface allowing database swap)
- [ ] Implement `TelegramUserAuthorizationService` implementing `IUserAuthorizationService` that checks the allowlist and returns `AuthorizationResult`
- [ ] On `/start`, if the Telegram user ID is in the allowlist, register/update the chat-ID-to-operator mapping; if not, respond with an "unauthorized" message and log the attempt

### Dependencies
- phase-command-processing-and-agent-routing/stage-command-router-and-handlers

### Test Scenarios
- [ ] Scenario: Authorized user mapped — Given Telegram user `12345` is in the allowlist mapped to operator `op-1` in tenant `t-1`, When `/start` is received, Then `AuthorizationResult.IsAuthorized` is true and `OperatorId` is `op-1`
- [ ] Scenario: Unauthorized user rejected — Given Telegram user `99999` is not in the allowlist, When any command is received, Then `AuthorizationResult.IsAuthorized` is false and `DenialReason` is populated

## Stage 3.5: Pending Question Store and Timeout Service

### Implementation Steps
- [ ] Define `IPendingQuestionStore` interface in Core with methods: `StoreAsync(AgentQuestion, long telegramMessageId, CancellationToken)`, `GetAsync(string questionId, CancellationToken)`, `MarkAnsweredAsync(string questionId, CancellationToken)`, `GetExpiredAsync(CancellationToken)` returning unanswered questions past `ExpiresAt`
- [ ] Create `PendingQuestionRecord` entity with fields: `QuestionId`, `AgentQuestion` (serialized), `TelegramChatId`, `TelegramMessageId`, `StoredAt`, `ExpiresAt`, `Status` (enum: `Pending`, `Answered`, `TimedOut`), `CorrelationId`
- [ ] Implement `PersistentPendingQuestionStore` backed by the Persistence project (EF Core; SQLite for dev/local, PostgreSQL or SQL Server for production) with indexed lookups by `QuestionId` and `ExpiresAt`
- [ ] Integrate store into `TelegramMessageSender.SendQuestionAsync`: after successfully sending a question to Telegram, persist the question with its Telegram message ID for later lookup by `CallbackQueryHandler`
- [ ] Implement `RequiresComment` flow in `CallbackQueryHandler`: when the selected `HumanAction.RequiresComment` is true, set the question status to `AwaitingComment`, send a prompt ("Please reply with your comment"), and defer `HumanDecisionEvent` emission until the operator's text reply arrives via the pipeline's `TextReply` handler
- [ ] Create `QuestionTimeoutService` as a `BackgroundService` that periodically polls `GetExpiredAsync`, and for each expired question: publishes a `HumanDecisionEvent` with `DefaultAction` value, updates the original Telegram message to indicate timeout ("⏰ Timed out — default action applied: {defaultAction}"), marks the question as `TimedOut`, and writes an audit record

### Dependencies
- phase-command-processing-and-agent-routing/stage-callback-query-handler

### Test Scenarios
- [ ] Scenario: Question stored on send — Given an `AgentQuestion` is sent to Telegram successfully, When `SendQuestionAsync` completes, Then a `PendingQuestionRecord` exists in the store with status `Pending` and the correct `TelegramMessageId`
- [ ] Scenario: Callback resolves pending question — Given a pending question `Q1`, When the callback query handler processes an approve action, Then the question status is updated to `Answered` and a `HumanDecisionEvent` is emitted
- [ ] Scenario: RequiresComment defers decision — Given a pending question with an action having `RequiresComment=true`, When the operator taps that button, Then the bot prompts for a comment and the `HumanDecisionEvent` is not emitted until the operator replies with text
- [ ] Scenario: Timeout applies default action — Given a pending question with `ExpiresAt` in the past and `DefaultAction=skip`, When `QuestionTimeoutService` polls, Then a `HumanDecisionEvent` is emitted with `ActionValue=skip` and the Telegram message is updated with the timeout notice

# Phase 4: Reliability Infrastructure

## Dependencies
- phase-command-processing-and-agent-routing

## Stage 4.1: Durable Outbound Message Queue

### Implementation Steps
- [ ] Define `IOutboundQueue` interface in Core with methods: `EnqueueAsync(OutboundMessage, CancellationToken)`, `DequeueAsync(CancellationToken)`, `AcknowledgeAsync(messageId, CancellationToken)`, `NackAsync(messageId, CancellationToken)`
- [ ] Create `OutboundMessage` record matching architecture.md's data model: `Id` (Guid), `IdempotencyKey` (string, derived from `AgentId:QuestionId:CorrelationId` to prevent duplicate sends), `ChatId`, `Payload` (serialized MessengerMessage or AgentQuestion), `Status` (enum: `Pending`, `Sending`, `Sent`, `Failed`, `DeadLettered`), `AttemptCount`, `MaxAttempts`, `NextRetryAt`, `CreatedAt`, `SentAt`, `TelegramMessageId`, `CorrelationId`, `ErrorDetail`
- [ ] Add a `UNIQUE` constraint on `IdempotencyKey` in the outbox table so that duplicate enqueue attempts for the same logical message are rejected at the database level
- [ ] Implement `InMemoryOutboundQueue` using `Channel<OutboundMessage>` with bounded capacity and backpressure for development
- [ ] Implement `PersistentOutboundQueue` backed by EF Core for durable persistence; use SQLite provider for dev/local environments and PostgreSQL or SQL Server for production (the specific production provider is a deployment decision, consistent with architecture.md §11.3); persist messages to an `outbox` table with status tracking; on `EnqueueAsync`, check `IdempotencyKey` uniqueness before inserting
- [ ] Create `OutboundQueueProcessor` as a `BackgroundService` that dequeues messages with `Status=Pending`, transitions to `Sending`, sends via `TelegramMessageSender`, and transitions to `Sent` on success or `Failed` on error

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Enqueue and dequeue — Given a message is enqueued, When dequeued, Then the message content matches and the queue size decreases by one
- [ ] Scenario: Persistence survives restart — Given a message is enqueued to the persistent queue, When the process restarts, Then the message is still available for dequeue
- [ ] Scenario: Backpressure applied — Given the in-memory queue is at capacity, When another message is enqueued, Then the call blocks until space is available (does not drop)
- [ ] Scenario: Outbound deduplication by idempotency key — Given a message with `IdempotencyKey=agent1:Q1:corr-1` is already enqueued, When a second message with the same `IdempotencyKey` is enqueued, Then the duplicate is rejected and the original message is returned without creating a second outbox entry

## Stage 4.2: Retry Policy and Dead-Letter Queue

### Implementation Steps
- [ ] Create `RetryPolicy` configuration POCO with properties: `MaxAttempts` (default 3, aligned with architecture.md component table default of 3 and e2e-scenarios.md retry background), `InitialDelayMs` (default 1000), `BackoffMultiplier` (default 2.0), `MaxDelayMs` (default 30000)
- [ ] Implement exponential backoff with jitter in `OutboundQueueProcessor`: on transient failure, increment `Attempt`, compute next retry time, and re-enqueue
- [ ] Define `IDeadLetterQueue` interface in Core with methods: `SendToDeadLetterAsync(OutboundMessage, FailureReason, CancellationToken)`, `ListAsync(CancellationToken)`
- [ ] Implement `DeadLetterQueue` backed by the same persistence layer (EF Core; SQLite for dev/local, PostgreSQL or SQL Server for production) with `dead_letter_messages` table containing full failure context
- [ ] After `MaxAttempts` exhausted, move message to dead-letter queue and emit an alert event via `IAlertService` (defined in Stage 1.3 Abstractions) to notify operators on a secondary channel
- [ ] Add health check that reports unhealthy if dead-letter queue depth exceeds a configurable threshold

### Dependencies
- phase-reliability-infrastructure/stage-durable-outbound-message-queue

### Test Scenarios
- [ ] Scenario: Retry with backoff — Given a message fails on first attempt, When retried, Then the delay before the second attempt is approximately `InitialDelayMs` (within jitter tolerance)
- [ ] Scenario: Dead-lettered after max attempts — Given `MaxAttempts=3` and a message that always fails, When processed three times, Then the message is in the dead-letter queue and an alert is emitted
- [ ] Scenario: Health check degrades — Given 10 messages in the dead-letter queue and threshold is 5, When health check is queried, Then status is `Unhealthy`

## Stage 4.3: Inbound Deduplication Service

### Implementation Steps
- [ ] Implement `DeduplicationService` (the concrete class implementing `IDeduplicationService` defined in Stage 1.3) using a time-bounded sliding-window store: processed event IDs are retained for a configurable TTL (default 1 hour), older entries are evicted
- [ ] For development, back with `ConcurrentDictionary<string, DateTimeOffset>` with a periodic cleanup timer
- [ ] For production, back with a database table `processed_events(event_id TEXT PK, processed_at DATETIME)` via EF Core (SQLite for dev, PostgreSQL or SQL Server for production) with a periodic purge of expired entries
- [ ] Integrate deduplication into the webhook endpoint and polling loop: before processing any `MessengerEvent`, check `IsProcessedAsync`; if true, skip silently

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
- [ ] For local development, support .NET User Secrets as the token source; document the setup procedure in `docs/stories/qq-TELEGRAM-MESSENGER-S/dev-setup.md` with step-by-step instructions for configuring User Secrets and environment variables
- [ ] Add startup validation that logs (at Warning level) if neither Key Vault nor User Secrets provides the token, then fails with a clear error

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Key Vault token loaded — Given Key Vault contains `TelegramBotToken`, When the Worker starts with Key Vault URI configured, Then `TelegramOptions.BotToken` is populated from Key Vault
- [ ] Scenario: Missing token fails startup — Given no token source is configured, When the Worker starts, Then it exits with a descriptive error within 5 seconds

## Stage 5.2: Chat and User Allowlist Enforcement

### Implementation Steps
- [ ] Create `AllowlistOptions` POCO bound from configuration section `Security:Allowlist` with properties: `AllowedTelegramUserIds` (list of long), `AllowedChatIds` (list of long), `DefaultDenyMessage`
- [ ] Implement allowlist check as middleware in the webhook pipeline: before any command processing, validate the incoming user ID and chat ID against the allowlist
- [ ] If user is not authorized, respond with a polite denial message, log the attempt at Warning level with user ID and chat ID (but no PII beyond Telegram numeric IDs), and short-circuit processing
- [ ] Support dynamic allowlist reload without restart via `IOptionsMonitor<AllowlistOptions>`

### Dependencies
- phase-security-and-audit/stage-secret-management-integration

### Test Scenarios
- [ ] Scenario: Allowed user passes — Given user ID `12345` is in `AllowedTelegramUserIds`, When a command arrives from user `12345`, Then processing continues normally
- [ ] Scenario: Denied user blocked — Given user ID `99999` is not in the allowlist, When a command arrives, Then the user receives the denial message and no command handler is invoked
- [ ] Scenario: Dynamic reload — Given user `67890` is added to the allowlist config while the service is running, When `67890` sends a command, Then it is accepted without a restart

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
- [ ] Create `appsettings.json` with documented configuration sections: `Telegram`, `Security:Allowlist`, `RetryPolicy`, `Queue`, `ConnectionStrings:AuditDb`, `KeyVault:Uri`
- [ ] Create `appsettings.Development.json` with polling mode enabled, console OTel exporter, and in-memory queue
- [ ] Add `Dockerfile` for the Worker project: multi-stage build, `dotnet publish` for linux-x64, expose port 8443 for webhook, health check `HEALTHCHECK CMD curl -f http://localhost:8080/healthz`
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
- [ ] Write test `PERF001_P95SendLatencyUnder2Seconds`: enqueue 100 outbound messages, configure WireMock to respond with 200 in under 50ms, measure the elapsed time from enqueue to send-completion for each message, and assert that the 95th percentile is under 2 seconds
- [ ] Write test `PERF002_BurstFrom100PlusAgents`: simulate 100+ agents each enqueuing one alert message concurrently (1000+ total messages), process all through the outbound queue, and assert that every message reaches either `Sent` or `DeadLettered` status (zero messages lost) and the queue drains completely within a bounded time

### Dependencies
- phase-integration-testing-and-acceptance-validation/stage-integration-test-infrastructure

### Test Scenarios
- [ ] Scenario: All acceptance tests pass — Given the full integration test suite, When `dotnet test` is run, Then all eight AC and PERF tests pass
- [ ] Scenario: Tests are deterministic — Given the integration tests, When run three times consecutively, Then all pass every time with no flaky failures
- [ ] Scenario: P95 latency measured accurately — Given the PERF001 test, When 100 messages are sent through the outbound pipeline, Then a P95 histogram is computed and the assertion threshold is 2000ms
