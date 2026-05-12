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
- [ ] Add NuGet package references to `AgentSwarm.Messaging.Persistence.csproj`: `Microsoft.EntityFrameworkCore` (8.x), `Microsoft.EntityFrameworkCore.Sqlite` (8.x for dev/local), `Microsoft.EntityFrameworkCore.Design` (8.x for migrations tooling)
- [ ] Create `MessagingDbContext` class in `AgentSwarm.Messaging.Persistence` inheriting from `DbContext`; constructor accepts `DbContextOptions<MessagingDbContext>`; override `OnModelCreating` to apply entity configurations via `modelBuilder.ApplyConfigurationsFromAssembly(typeof(MessagingDbContext).Assembly)` — this is the shared EF Core context used by all persistence stages (Stage 2.4 `InboundUpdate`, Stage 3.2 `TaskOversight`, Stage 3.4 `OperatorBinding`, Stage 3.5 `PendingQuestionRecord`, Stage 4.1 `OutboundMessage`, Stage 4.2 dead-letter, Stage 4.3 dedup `ProcessedEvent`); entity configurations are added incrementally by each stage and auto-discovered via assembly scanning
- [ ] Create `ServiceCollectionExtensions` class in `AgentSwarm.Messaging.Persistence` with `AddMessagingPersistence(IServiceCollection, IConfiguration)` extension method that registers `MessagingDbContext` with the connection string from `ConnectionStrings:MessagingDb` (defaults to SQLite `Data Source=messaging.db` for dev/local; swappable to PostgreSQL or SQL Server for production by changing the EF Core provider); also calls `EnsureCreated()` or applies pending migrations on startup based on configuration
- [ ] Create initial EF Core migration `InitialCreate` (empty schema — entity configurations will be added by subsequent stages and new migrations generated as needed)
- [ ] Add `.editorconfig` at repo root enforcing C# coding conventions consistent with .NET defaults
- [ ] Verify solution builds with `dotnet build` and all projects restore successfully

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Solution builds cleanly — Given all projects are created, When `dotnet build AgentSwarm.Messaging.sln` is run, Then exit code is 0 and no warnings are emitted
- [ ] Scenario: Project references resolve — Given Telegram project references Abstractions and Core, When the solution is restored, Then all inter-project references resolve without errors
- [ ] Scenario: MessagingDbContext resolves from DI — Given `AddMessagingPersistence` is called with a valid SQLite connection string, When `MessagingDbContext` is resolved from the service provider, Then it is non-null and `Database.CanConnect()` returns true

## Stage 1.2: Shared Data Models

### Implementation Steps
- [ ] Create `MessengerMessage` record in Abstractions with properties: `MessageId`, `CorrelationId`, `AgentId`, `TaskId`, `ConversationId`, `Timestamp`, `Text`, `Severity`, `Metadata` dictionary
- [ ] Create `AgentQuestion` record in Abstractions with properties: `QuestionId`, `AgentId`, `TaskId`, `Title`, `Body`, `Severity`, `AllowedActions` (list of `HumanAction`), `ExpiresAt`, `CorrelationId` — the shared model does **not** include a `DefaultAction` property; the proposed default action is carried as sidecar metadata via `ProposedDefaultActionId` in `AgentQuestionEnvelope` (see below); this follows architecture.md §3.1 (lines 167–184) which defines `AgentQuestion` without `DefaultAction` and `AgentQuestionEnvelope` with `ProposedDefaultActionId`; all sibling documents are aligned on the sidecar envelope model: e2e-scenarios.md (lines 57–63, footer line 641) explicitly states "All sibling documents are aligned on the sidecar envelope model" and tests the sidecar approach directly; tech-spec.md §10 alignment table confirms the same
- [ ] Create `AgentQuestionEnvelope` record in Abstractions with properties: `Question` (`AgentQuestion`), `ProposedDefaultActionId` (nullable `string` — the `ActionId` of the proposed default action from `AllowedActions`; carried as routing/context metadata alongside the `AgentQuestion`; when the question times out, the connector reads this from `PendingQuestionRecord.DefaultActionId` and applies the action automatically; when `null`, the question expires with `ActionValue = "__timeout__"`), `RoutingMetadata` (dictionary of string key-value pairs for extensible context)
- [ ] Create `HumanAction` record in Abstractions with properties: `ActionId`, `Label`, `Value`, `RequiresComment`
- [ ] Create `HumanDecisionEvent` record in Abstractions with properties: `QuestionId`, `ActionValue`, `Comment`, `Messenger`, `ExternalUserId`, `ExternalMessageId`, `ReceivedAt`, `CorrelationId`
- [ ] Create `MessengerEvent` record in Abstractions representing inbound events with properties: `EventId`, `EventType` enum, `RawCommand`, `UserId`, `ChatId`, `Timestamp`, `CorrelationId`, `Payload`
- [ ] Create `EventType` enum in Abstractions: `Command`, `CallbackResponse`, `TextReply`, `Unknown`
- [ ] Create `MessageSeverity` enum in Abstractions: `Critical`, `High`, `Normal`, `Low` — canonical severity vocabulary used consistently across all stages (1.2, 4.1), architecture.md §3.1 (`OutboundMessage.Severity`), and e2e-scenarios.md fixtures
- [ ] Create `OutboundMessage` record in Core matching architecture.md §3.1 data model: `MessageId` (Guid — primary key, consistent with architecture.md `OutboundMessage.MessageId`), `IdempotencyKey` (string, derived per `SourceType` — see architecture.md idempotency key derivation table), `ChatId`, `Payload` (serialized MessengerMessage or AgentQuestion), `SourceEnvelopeJson` (string?, serialized original source envelope — populated when `SourceType = Question` with the full `AgentQuestionEnvelope` JSON, or when `SourceType = Alert` with the full `AgentAlertEvent` JSON; used by `QuestionRecoverySweep` to backfill missing `PendingQuestionRecord` rows after a crash between `MarkSentAsync` and `PendingQuestionRecord` persistence — see architecture.md §3.1 Gap B; also preserved in `DeadLetterMessage.SourceEnvelopeJson` for replay; null for `CommandAck` and `StatusUpdate` source types), `Severity` (`MessageSeverity`), `SourceType` (enum: `Question`, `Alert`, `StatusUpdate`, `CommandAck`), `SourceId` (string, nullable), `Status` (enum: `Pending`, `Sending`, `Sent`, `Failed`, `DeadLettered`), `AttemptCount`, `MaxAttempts`, `NextRetryAt`, `CreatedAt`, `SentAt`, `TelegramMessageId`, `CorrelationId`, `ErrorDetail` — defined here in Phase 1 so that `IOutboundQueue` (Stage 1.4) and `TelegramMessengerConnector` (Stage 2.6) can reference it without a forward dependency on Phase 4
- [ ] Create `InboundUpdate` record in Abstractions with fields: `UpdateId` (long, PK), `RawPayload` (string), `ReceivedAt` (DateTimeOffset), `ProcessedAt` (DateTimeOffset?), `IdempotencyStatus` (enum: `Received`, `Processing`, `Completed`, `Failed` — matching architecture.md §3.1 lines 126–134 exactly; no additional terminal statuses), `AttemptCount` (int, default 0 — incremented on each reprocessing attempt by `InboundRecoverySweep`), `ErrorDetail` (string?, stores the latest failure reason for diagnostics) — defined here so `IInboundUpdateStore` (Stage 2.4) can reference it in Abstractions without depending on the Persistence project; EF Core entity configuration is also provided by Stage 2.4; permanently failing updates are identified by `IdempotencyStatus=Failed` with `AttemptCount >= MaxRetries` (configurable via `InboundRecovery:MaxRetries`, default 3) and excluded from recovery sweeps — they remain in `Failed` status for manual investigation and alerting, consistent with architecture.md's four-status model
- [ ] Create `TaskOversight` record in Core with fields: `TaskId` (string, PK), `OperatorBindingId` (Guid), `AssignedAt` (DateTimeOffset), `AssignedBy` (string — the operator who initiated the handoff), `CorrelationId` (string) — defined here so `ITaskOversightRepository` (Stage 1.3) can reference it in Core without depending on Persistence; EF Core entity configuration and migration are provided by Stage 3.2

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
- [ ] Create `PendingQuestion` record in Abstractions as the interface-level DTO with properties: `QuestionId` (string), `AgentId` (string), `Title` (string), `Body` (string), `AllowedActions` (list of `HumanAction`), `DefaultActionId` (string?, from envelope sidecar), `TelegramChatId` (long), `TelegramMessageId` (long), `ExpiresAt` (DateTimeOffset), `CorrelationId` (string), `Status` (enum: `Pending`, `AwaitingComment`, `Answered`, `TimedOut`) — this is the abstraction returned by `IPendingQuestionStore`; the concrete persistence entity `PendingQuestionRecord` (Stage 3.5) maps to/from this DTO
- [ ] Create `IPendingQuestionStore` interface in Abstractions with methods: `StoreAsync(AgentQuestionEnvelope envelope, long telegramChatId, long telegramMessageId, CancellationToken)` — accepts the full envelope so the store can extract `Question` fields, `ProposedDefaultActionId` for denormalization into `PendingQuestion.DefaultActionId`, and `TelegramChatId` for timeout message edits; `GetAsync(string questionId, CancellationToken)` returning `PendingQuestion?`, `GetByTelegramMessageAsync(long telegramChatId, long telegramMessageId, CancellationToken)` returning `PendingQuestion?` (composite key because Telegram `message_id` is only unique within a chat — per architecture.md §4.7), `MarkAnsweredAsync(string questionId, CancellationToken)`, `MarkAwaitingCommentAsync(string questionId, CancellationToken)`, `RecordSelectionAsync(string questionId, string selectedActionId, string selectedActionValue, long respondentUserId, CancellationToken)` — persists the operator's selected action ID, the resolved `HumanAction.Value` (as `selectedActionValue`), and Telegram user ID on the pending question record; called by `CallbackQueryHandler` before `MarkAwaitingCommentAsync` to record which action was tapped, its canonical value, and by whom, enabling the text-reply handler to emit `HumanDecisionEvent.ActionValue` from durable storage without depending on `IDistributedCache`; this method is on the abstraction interface so `CallbackQueryHandler` (in Telegram) does not depend on the Persistence assembly (per architecture.md §4.7), `GetAwaitingCommentAsync(long telegramChatId, long respondentUserId, CancellationToken)` returning `PendingQuestion?` — returns the oldest pending question (by `StoredAt`) with `Status = AwaitingComment` for the given `(TelegramChatId, RespondentUserId)` pair; used by the text-reply handler to match a follow-up comment to the correct pending question (per architecture.md §4.7), `GetExpiredAsync(CancellationToken)` returning `IReadOnlyList<PendingQuestion>` — all query methods return the `PendingQuestion` abstraction DTO (defined above), not the persistence entity; concrete implementation in Stage 3.5 maps between `PendingQuestionRecord` (EF entity) and `PendingQuestion` (DTO)
- [ ] Create `CommandResult` record in Abstractions with properties: `Success`, `ResponseText`, `CorrelationId`, `ErrorCode`
- [ ] Create `IUserAuthorizationService` interface in Abstractions with method: `AuthorizeAsync(string externalUserId, string chatId, string? commandName, CancellationToken)` returning `AuthorizationResult` — accepts an optional `commandName` parameter so the authorization service can distinguish `/start` onboarding (Tier 1: allowlist check, creates `OperatorBinding` if user is in `AllowedUserIds`) from all other commands (Tier 2: requires existing `OperatorBinding` records via `IOperatorRegistry.GetBindingsAsync` returning `IReadOnlyList<OperatorBinding>`, populating `AuthorizationResult.Bindings` for multi-workspace disambiguation per architecture.md §4.3); when `commandName` is `"start"`, the service performs Tier 1 allowlist-based authorization; for all other commands, the service performs Tier 2 binding-based authorization and returns the full binding list
- [ ] Create `AuthorizationResult` record with properties: `IsAuthorized`, `Bindings` (`IReadOnlyList<OperatorBinding>` — all active bindings for the user/chat pair, supporting multi-workspace disambiguation per architecture.md §4.3), `DenialReason`; callers handle cardinality: if exactly one binding, use it directly; if multiple, prompt the operator to disambiguate via inline keyboard (per e2e-scenarios.md multi-workspace flow); create `AuthorizedOperator` record in Abstractions with properties: `OperatorId`, `TenantId`, `WorkspaceId`, `Roles` (list of string), `TelegramUserId`, `TelegramChatId` — represents a resolved authorized identity passed to command handlers after disambiguation
- [ ] Create `IAuditLogger` interface in Abstractions with method: `LogAsync(AuditEntry, CancellationToken)`; create `AuditEntry` record in the same file with properties: `EntryId`, `MessageId`, `UserId`, `AgentId`, `Action`, `Timestamp`, `CorrelationId`, `Details`
- [ ] Create `ITelegramUpdatePipeline` interface in Abstractions with method: `ProcessAsync(MessengerEvent, CancellationToken)` returning `PipelineResult`; create `PipelineResult` record in Abstractions with properties: `Handled` (bool), `ResponseText`, `CorrelationId` — defines the inbound processing chain so receivers (webhook, polling) can reference it without depending on the implementation
- [ ] Create `ICommandParser` interface in Abstractions with method: `Parse(string messageText)` returning `ParsedCommand`; create `ParsedCommand` record in Abstractions with properties: `CommandName`, `Arguments`, `RawText`, `IsValid`, `ValidationError` — abstracts command parsing so the pipeline does not depend on concrete parser implementations
- [ ] Create `ICallbackHandler` interface in Abstractions with method: `HandleAsync(MessengerEvent, CancellationToken)` returning `CommandResult` — abstracts callback query processing for inline button presses
- [ ] Create `IDeduplicationService` interface in Abstractions with methods: `IsProcessedAsync(string eventId, CancellationToken)` returning bool, and `MarkProcessedAsync(string eventId, CancellationToken)` — abstracts inbound event deduplication
- [ ] Create `ITaskOversightRepository` interface in Core with methods: `GetByTaskIdAsync(string taskId, CancellationToken)` returning `TaskOversight?`, `UpsertAsync(TaskOversight, CancellationToken)`, `GetByOperatorAsync(Guid operatorBindingId, CancellationToken)` returning `IReadOnlyList<TaskOversight>` — `TaskOversight` record is defined in Stage 1.2 Core; the concrete `PersistentTaskOversightRepository` implementation is provided by Stage 3.2 in Persistence; defined here so Stage 2.7 (`SwarmEventSubscriptionService`) can resolve the oversighting operator for status-update routing without depending on Phase 3
- [ ] Create `ISwarmCommandBus` interface in Abstractions with methods: `PublishCommandAsync(SwarmCommand, CancellationToken)`, `QueryStatusAsync(SwarmStatusQuery query, CancellationToken)` (where `SwarmStatusQuery` carries `WorkspaceId` and optional `TaskId` per architecture.md §4.6), `QueryAgentsAsync(SwarmAgentsQuery query, CancellationToken)` (where `SwarmAgentsQuery` carries `WorkspaceId` and optional `Filter` per architecture.md §4.6), `SubscribeAsync(string tenantId, CancellationToken)` returning `IAsyncEnumerable<SwarmEvent>` — port to the agent swarm orchestrator (transport is out of scope for this story, per architecture.md §4.6); `SubscribeAsync` enables the connector to receive agent-originated questions, alerts, and status updates for rendering in Telegram
- [ ] Create `SwarmStatusQuery` record in Abstractions with properties: `WorkspaceId` (string, required — scopes the query to the operator's workspace), `TaskId` (string?, optional — when provided, narrows the result to a single task) — per architecture.md §4.6
- [ ] Create `SwarmAgentsQuery` record in Abstractions with properties: `WorkspaceId` (string, required), `Filter` (string?, optional — free-text agent name/role filter) — per architecture.md §4.6
- [ ] Create `SwarmCommand` record in Abstractions with properties: `CommandType`, `TaskId`, `OperatorId`, `Payload`, `CorrelationId`
- [ ] Create `SwarmEvent` abstract record in Abstractions as a discriminated union base with subtypes: `AgentQuestionEvent` (wraps `AgentQuestionEnvelope`), `AgentAlertEvent` (properties: `AlertId`, `AgentId`, `TaskId`, `Title`, `Body`, `Severity`, `WorkspaceId`, `TenantId`, `CorrelationId`, `Timestamp` — all ten fields per architecture.md §4.6; `TaskId` and `WorkspaceId` are required for operator routing in §5.6), `AgentStatusUpdateEvent` (properties: `AgentId`, `TaskId`, `StatusText`, `CorrelationId`) — per architecture.md §4.6, these cover all agent-originated events that the Telegram connector must render
- [ ] Create `OperatorRegistration` sealed record in Core (per architecture.md §4.3 lines 339–348) as the value object accepted by `IOperatorRegistry.RegisterAsync`, carrying all fields required to create an `OperatorBinding`: `TelegramUserId` (long), `TelegramChatId` (long), `ChatType` (enum: `Private`, `Group`, `Supergroup` — derived from `Update.Message.Chat.Type`), `TenantId` (string), `WorkspaceId` (string), `Roles` (string[] — e.g., `["Operator", "Approver"]`), `OperatorAlias` (string — e.g., `"@alice"`) — the `/start` handler constructs this from the Telegram `Update` (for user/chat/type) and the `Telegram:UserTenantMappings` configuration entry (for tenant/workspace/roles/alias) per architecture.md §7.1 lines 636–650
- [ ] Create `IOperatorRegistry` interface in Core (per architecture.md §4.3) with six methods: `GetBindingsAsync(long telegramUserId, long chatId, CancellationToken)` returning `IReadOnlyList<OperatorBinding>` (all active bindings for the user/chat pair — one per workspace; callers handle cardinality: zero = unauthorized, one = use directly, multiple = prompt workspace disambiguation per e2e-scenarios.md multi-workspace flow), `GetAllBindingsAsync(long telegramUserId, CancellationToken)` returning `IReadOnlyList<OperatorBinding>` (all bindings across all chats for admin queries), `GetByAliasAsync(string operatorAlias, string tenantId, CancellationToken)` returning `OperatorBinding?` (tenant-scoped alias resolution per architecture.md lines 116–119: uses the `UNIQUE (OperatorAlias, TenantId)` constraint so `/handoff @alias` in one tenant cannot resolve an operator in a different tenant), `GetByWorkspaceAsync(string workspaceId, CancellationToken)` returning `IReadOnlyList<OperatorBinding>` (all active bindings for a workspace — used by Stage 2.7 alert fallback routing when no `TaskOversight` record exists for an alert's `TaskId`; the first active binding in the workspace receives the alert per architecture.md §5.6), `RegisterAsync(OperatorRegistration registration, CancellationToken)` (accepts the `OperatorRegistration` value object carrying `TelegramUserId`, `TelegramChatId`, `ChatType`, `TenantId`, `WorkspaceId`, `Roles`, and `OperatorAlias` — creates a complete `OperatorBinding` row with all fields populated; per architecture.md §4.3 lines 337–351), `IsAuthorizedAsync(long telegramUserId, long chatId, CancellationToken)` returning `bool` (fast-path: true if at least one active binding exists) — used by Stage 3.2 (`HandoffCommandHandler` alias resolution), Stage 3.4 (concrete implementation), and Stage 5.2 (runtime authorization)
- [ ] Create `OperatorBinding` record in Core with properties: `Id` (Guid), `TelegramUserId` (long), `TelegramChatId` (long), `ChatType` (enum: `Private`, `Group`, `Supergroup`), `OperatorAlias` (string), `TenantId`, `WorkspaceId`, `Roles` (list of string), `RegisteredAt` (DateTimeOffset), `IsActive` (bool) — per architecture.md §3.1

### Dependencies
- phase-messaging-abstractions-and-solution-scaffold/stage-shared-data-models

### Test Scenarios
- [ ] Scenario: Interface contracts compile — Given all interfaces are defined in Abstractions, When the project is built, Then it compiles with zero errors
- [ ] Scenario: Mock connector satisfies interface — Given a Moq mock of `IMessengerConnector`, When `SendMessageAsync` is invoked, Then the mock records the call without error
- [ ] Scenario: Pipeline interface mockable — Given a Moq mock of `ITelegramUpdatePipeline`, When `ProcessAsync` is invoked with a `MessengerEvent`, Then the mock records the call and returns a `PipelineResult`

## Stage 1.4: Outbound Sender and Alert Contracts

### Implementation Steps
- [ ] Create `IMessageSender` interface in Core with methods: `Task<SendResult> SendTextAsync(long chatId, string text, CancellationToken)`, `Task<SendResult> SendQuestionAsync(long chatId, AgentQuestionEnvelope envelope, CancellationToken)` — both methods return `Task<SendResult>` where `SendResult` is a record carrying `TelegramMessageId` (long), defined in Core alongside `IMessageSender` (per architecture.md §4.12); the platform-agnostic outbound sending contract that `TelegramMessageSender` (Stage 2.3) implements; uses `AgentQuestionEnvelope` so the sender can read `ProposedDefaultActionId` from sidecar metadata and display the proposed default in the message body (e.g., "Default action if no response: Approve"); the sender does **not** denormalize or persist `PendingQuestionRecord.DefaultActionId` — that responsibility belongs to `OutboundQueueProcessor` (Stage 4.1) which calls `IPendingQuestionStore.StoreAsync` as a post-send hook per architecture.md §5.2; used by `OutboundQueueProcessor` to send messages without depending on the Telegram project directly
- [ ] Create `IAlertService` interface in Abstractions with method: `SendAlertAsync(string subject, string detail, CancellationToken)` — used to notify operators via a secondary channel when dead-letter events occur or critical failures are detected
- [ ] Create `IOutboundQueue` interface in Abstractions with methods: `EnqueueAsync(OutboundMessage, CancellationToken)`, `DequeueAsync(CancellationToken)` returning the highest-severity pending message, `MarkSentAsync(Guid messageId, long telegramMessageId, CancellationToken)`, `MarkFailedAsync(Guid messageId, string error, CancellationToken)`, `DeadLetterAsync(Guid messageId, CancellationToken)` — per architecture.md §4.4; `messageId` parameter corresponds to `OutboundMessage.MessageId`; `telegramMessageId` is `long` per architecture.md §3.1 canonical type convention; defined here so `TelegramMessengerConnector` (Stage 2.6) can delegate sends without depending on the concrete implementation (Stage 4.1)

### Dependencies
- phase-messaging-abstractions-and-solution-scaffold/stage-connector-interface-and-service-contracts

### Test Scenarios
- [ ] Scenario: IMessageSender accepts envelope — Given a Moq mock of `IMessageSender`, When `SendQuestionAsync` is invoked with an `AgentQuestionEnvelope` containing `ProposedDefaultActionId`, Then the mock records the call and envelope properties are accessible
- [ ] Scenario: IAlertService mockable — Given a Moq mock of `IAlertService`, When `SendAlertAsync` is invoked with a subject and detail, Then the mock records the call without error
- [ ] Scenario: IOutboundQueue mockable — Given a Moq mock of `IOutboundQueue`, When `EnqueueAsync` is invoked with an `OutboundMessage`, Then the mock records the call without error

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
- [ ] Implement the deduplication stage: on entry, call `IDeduplicationService.IsProcessedAsync(event.EventId)` — if already processed, short-circuit with `PipelineResult { Handled = true }` and skip all downstream stages; otherwise proceed with authorization, parsing, and routing; call `IDeduplicationService.MarkProcessedAsync(event.EventId)` only **after** the command handler returns successfully — this ensures a crash between dedup check and handler completion does not mark the event as processed, preserving crash-recovery safety (the webhook recovery sweep in Stage 2.4 will re-deliver unprocessed `InboundUpdate` rows); if the handler throws, the event remains unprocessed and will be retried on the next delivery
- [ ] Implement the parsing and authorization stages: pass `event.RawCommand` to `ICommandParser.Parse` producing `ParsedCommand`; call `IUserAuthorizationService.AuthorizeAsync(userId, chatId, parsedCommand.CommandName, ct)` returning `AuthorizationResult` with `Bindings` list; the `commandName` parameter enables Tier 1 allowlist-based `/start` onboarding versus Tier 2 binding-based authorization for all other commands; if `Bindings.Count == 0`, reject with denial response; if `Bindings.Count == 1`, construct `AuthorizedOperator` directly; if `Bindings.Count > 1`, present workspace disambiguation via inline keyboard per e2e-scenarios.md multi-workspace flow
- [ ] Implement the role enforcement and routing stages: check `AuthorizedOperator.Roles` against command role requirements per architecture.md §9; then route by `EventType`: `Command` events pass `ParsedCommand` and `AuthorizedOperator` to `ICommandRouter.RouteAsync`, `CallbackResponse` events go to `ICallbackHandler`, `TextReply` events check for pending `RequiresComment` prompts (via `IPendingQuestionStore`, defined in Stage 1.3 Abstractions) before falling through to default handling
- [ ] Provide stub/no-op implementations of `ICommandParser`, `ICommandRouter`, `ICallbackHandler`, `IDeduplicationService`, and `IPendingQuestionStore` in the Telegram project for initial compilation and testing; concrete implementations are registered in Phase 3 (command processing) and Phase 4 (deduplication)
- [ ] Emit structured log entries at each pipeline stage with `CorrelationId`, `EventId`, and stage name for end-to-end traceability
- [ ] Return a `PipelineResult` (defined in Abstractions) to callers (webhook endpoint and polling service)

### Dependencies
- phase-telegram-bot-integration/stage-telegram-bot-client-wrapper

### Test Scenarios
- [ ] Scenario: Pipeline routes command — Given a `MessengerEvent` with `EventType=Command` and text `/status`, When processed through the pipeline, Then `CommandRouter` is invoked and a response is returned
- [ ] Scenario: Pipeline routes callback to stub — Given a `MessengerEvent` with `EventType=CallbackResponse`, When processed, Then the injected `ICallbackHandler` mock/stub is invoked with the event (real `CallbackQueryHandler` emitting `HumanDecisionEvent` is provided by Stage 3.3; this test verifies routing only)
- [ ] Scenario: Pipeline rejects unauthorized — Given a `MessengerEvent` from a user not in the allowlist, When processed, Then the pipeline short-circuits with a denial response and no command handler is invoked
- [ ] Scenario: Dedup marks only after handler success — Given a `MessengerEvent` with `EventId=evt-1` that has not been processed, When the command handler throws an exception, Then `MarkProcessedAsync` is NOT called and a subsequent delivery of `evt-1` is processed normally (not short-circuited as duplicate)
- [ ] Scenario: Successful handler marks processed — Given a `MessengerEvent` with `EventId=evt-2`, When the command handler returns successfully, Then `MarkProcessedAsync(evt-2)` is called exactly once and a subsequent delivery of `evt-2` is short-circuited as duplicate

## Stage 2.3: Outbound Message Sender

### Implementation Steps
- [ ] Create `TelegramMessageSender` implementing `IMessageSender` (defined in Stage 1.4 Core) with methods: `Task<SendResult> SendTextAsync(long chatId, string text, CancellationToken ct)`, `Task<SendResult> SendQuestionAsync(long chatId, AgentQuestionEnvelope envelope, CancellationToken ct)` — both return `Task<SendResult>` carrying the Telegram-assigned `message_id` so `OutboundQueueProcessor` can pass it to `MarkSentAsync` and `StoreAsync` (per architecture.md §4.12)
- [ ] Implement `SendQuestionAsync` to render `AgentQuestionEnvelope` as a rich Telegram message: extract the `AgentQuestion` from the envelope; include `Title`, `Body` (full context), `Severity` badge, `ExpiresAt` timeout countdown; render `AllowedActions` as Telegram `InlineKeyboardMarkup` buttons with callback data encoding `QuestionId:ActionId`; read `AgentQuestionEnvelope.ProposedDefaultActionId` from the sidecar metadata (per e2e-scenarios.md lines 57–76) and, when present, display the proposed default in the message body (e.g., "Default action if no response: Approve") — the sender does **not** denormalize or persist `PendingQuestionRecord.DefaultActionId`; that responsibility belongs to `OutboundQueueProcessor` (Stage 4.1) which calls `IPendingQuestionStore.StoreAsync` as a post-send hook, and the store denormalizes `ProposedDefaultActionId` into `PendingQuestionRecord.DefaultActionId` at persistence time (per architecture.md §5.2 invariant 1)
- [ ] When building the inline keyboard, write each `HumanAction` to `IDistributedCache` keyed by `QuestionId:ActionId` with expiry set to `AgentQuestion.ExpiresAt + 5 minutes` (the 5-minute grace window ensures `CallbackQueryHandler` can still resolve the cached `HumanAction` for late button taps near the `ExpiresAt` boundary); this enables `CallbackQueryHandler` (Stage 3.3) to resolve the full `HumanAction` from the short `ActionId` in callback data during interactive button callbacks. **Note:** `QuestionTimeoutService` does **not** depend on the cache — it reads `DefaultActionValue` directly from `PendingQuestionRecord` (per architecture.md §10.3), so no cache grace window is needed for timeout resolution.
- [ ] Add NuGet package reference `Microsoft.Extensions.Caching.Memory` to `AgentSwarm.Messaging.Telegram.csproj`; register `IDistributedCache` via `AddDistributedMemoryCache()` in the Telegram service registration extension method (e.g., `AddTelegram(IServiceCollection, IConfiguration)`) — this is the first stage that writes to the cache; `AddStackExchangeRedisCache()` can replace this in production via configuration; `CallbackQueryHandler` (Stage 3.3) consumes these cache entries at runtime for interactive button callbacks; `QuestionTimeoutService` (Stage 3.5) does **not** consume cache entries — it reads `DefaultActionValue` directly from `PendingQuestionRecord` per architecture.md §10.3
- [ ] For actions with `RequiresComment = true`, append "(reply required)" to the button label so the operator knows a follow-up text reply is expected after tapping
- [ ] Format outbound messages with Markdown V2 parse mode; include `CorrelationId` as a footer or hidden tag for traceability
- [ ] Implement Telegram API rate-limit handling: detect `429 Too Many Requests`, extract `RetryAfter`, and back off accordingly
- [ ] Implement a proactive dual token-bucket rate limiter (per architecture.md §10.4) with two layers: a global bucket limiting sends to `Telegram:RateLimits:GlobalPerSecond` (default 30 msg/s) across all chats, and per-chat buckets limiting each individual chat to `Telegram:RateLimits:PerChatPerMinute` (default 20 msg/min); create `RateLimitOptions` configuration POCO bound from `Telegram:RateLimits`; workers acquire tokens before sending and block-wait when exhausted, preventing 429 responses proactively
- [ ] Add message-ID tracking: after successful send, persist the Telegram message ID mapped to `CorrelationId` for reply correlation
- [ ] Implement message splitting for payloads exceeding Telegram's 4096-character limit (per e2e-scenarios.md "Message exceeds Telegram's 4096 character limit"): split the message body into chunks of ≤ 4096 characters at paragraph or line boundaries where possible; each chunk carries the same `CorrelationId`; chunks are sent in order with sequential Telegram API calls to preserve message ordering in the chat

### Dependencies
- phase-telegram-bot-integration/stage-telegram-bot-client-wrapper

### Test Scenarios
- [ ] Scenario: Question renders buttons — Given an `AgentQuestion` with three `HumanAction` items, When `SendQuestionAsync` is called, Then the constructed `InlineKeyboardMarkup` contains exactly three buttons with correct labels
- [ ] Scenario: Question body includes full context — Given an `AgentQuestionEnvelope` with `Severity=Critical`, `ExpiresAt` in 30 minutes, and `ProposedDefaultActionId` set to an `ActionId` whose label is "skip", When `SendQuestionAsync` is called, Then the message body contains the severity badge, timeout information, default action label ("Default action if no response: skip"), and full question `Body` text
- [ ] Scenario: HumanAction cached on keyboard build — Given an `AgentQuestion` with two `AllowedActions`, When inline keyboard buttons are rendered, Then two `IDistributedCache` entries are written keyed by `QuestionId:ActionId` containing the full `HumanAction` with expiry set to `ExpiresAt + 5 minutes` (grace window for `CallbackQueryHandler` per architecture.md §5.2; `QuestionTimeoutService` does not use the cache — per architecture.md §10.3)
- [ ] Scenario: Rate limit handled gracefully — Given the Telegram API returns HTTP 429 with `RetryAfter=5`, When the sender encounters it, Then it waits at least 5 seconds before retrying and does not throw
- [ ] Scenario: CorrelationId in message — Given a `MessengerMessage` with a specific `CorrelationId`, When sent, Then the outbound message body contains the correlation ID
- [ ] Scenario: RequiresComment action labeled — Given an `AgentQuestion` with one action having `RequiresComment=true`, When rendered, Then that button label includes a "(reply required)" indicator
- [ ] Scenario: Proactive rate limiter throttles — Given the global token bucket is exhausted (30 tokens consumed within 1 second), When a new send is attempted, Then the sender blocks until a token is available rather than issuing a request that would be 429'd
- [ ] Scenario: Long message split into chunks — Given a message body of 6000 characters, When `SendTextAsync` is called, Then the message is split into two chunks of ≤ 4096 characters each, both carrying the same `CorrelationId`, and sent in order

## Stage 2.4: Webhook Receiver Endpoint

### Implementation Steps
- [ ] Create ASP.NET Core minimal API endpoint `POST /api/telegram/webhook` in the Telegram project (per architecture.md §2.2 line 82 and §11.3 line 569: `WebhookController` belongs in `AgentSwarm.Messaging.Telegram`; the Worker project is only the ASP.NET Core host/DI/bootstrap layer and registers the endpoint via `app.MapTelegramWebhook()` extension method) that receives Telegram `Update` JSON payloads
- [ ] Implement `TelegramWebhookSecretFilter` that validates the `X-Telegram-Bot-Api-Secret-Token` header against the configured `SecretToken`; reject with 403 if mismatch
- [ ] Deserialize the incoming `Update` using `Telegram.Bot` serialization and convert to the internal `MessengerEvent` model via a `TelegramUpdateMapper` class
- [ ] Persist an `InboundUpdate` durable record (per architecture.md §3.1 lines 126-134 and §5.1 line 370) **before** returning HTTP 200: insert into the `inbound_updates` table with fields `UpdateId` (PK, Telegram's `update_id`), `RawPayload` (full serialized `Update` JSON), `ReceivedAt`, `ProcessedAt` (null initially), and `IdempotencyStatus` (set to `Received`); if the `UNIQUE` constraint on `UpdateId` fails, the update is a duplicate — return 200 immediately without further processing; this eliminates the command-loss window: if the process crashes after Telegram receives 200, the `InboundUpdate` record (with full `RawPayload`) is already persisted for recovery
- [ ] Create `InboundUpdate` entity configuration in Persistence (the `InboundUpdate` record itself is defined in Stage 1.2 Abstractions so that `IInboundUpdateStore` in Abstractions can reference it without a reverse dependency on Persistence): add EF Core `InboundUpdateConfiguration` mapping to table `inbound_updates` with `UpdateId` as PK and a `UNIQUE` constraint on `UpdateId`; add to `MessagingDbContext`
- [ ] Create `IInboundUpdateStore` interface in Abstractions with methods: `PersistAsync(InboundUpdate, CancellationToken)` returning `bool` (false if duplicate), `MarkProcessingAsync(long updateId, CancellationToken)`, `MarkCompletedAsync(long updateId, CancellationToken)`, `MarkFailedAsync(long updateId, string error, CancellationToken)`, `GetRecoverableAsync(int maxRetries, CancellationToken)` returning `IReadOnlyList<InboundUpdate>` (records with `IdempotencyStatus` in `Received`, `Processing`, or `Failed` where `AttemptCount < maxRetries` — all three statuses represent records that need reprocessing: `Received`/`Processing` from crash recovery, `Failed` from transient handler errors; the `AttemptCount < maxRetries` filter ensures permanently failing updates (those that have exhausted retries) remain in `Failed` status and are excluded from recovery sweeps, consistent with architecture.md §3.1's four-status model of `Received`/`Processing`/`Completed`/`Failed`), `GetExhaustedRetryCountAsync(int maxRetries, CancellationToken)` returning `int` (count of `Failed` records with `AttemptCount >= maxRetries` for health-check alerting) — `InboundUpdate` is defined in Stage 1.2 Abstractions, so this interface has no cross-project dependency
- [ ] Implement `PersistentInboundUpdateStore` in Persistence backed by EF Core (SQLite for dev, PostgreSQL or SQL Server for production), using the shared `MessagingDbContext` (see Stage 6.3 for connection configuration)
- [ ] After persisting the `InboundUpdate` record, return HTTP 200 and pass the `MessengerEvent` to `ITelegramUpdatePipeline.ProcessAsync` (interface defined in Abstractions, concrete implementation from Stage 2.2 provided via DI) for async command routing; upon completion, transition `IdempotencyStatus` to `Completed`; on transient error, transition to `Failed` with the error message stored in a new `ErrorDetail` field — `Failed` records are NOT permanently stranded: `InboundRecoverySweep` reprocesses them on next startup (see below), providing automatic retry for transient handler failures
- [ ] Implement `InboundRecoverySweep` as a `BackgroundService` in the Worker project that runs on startup and then periodically (configurable via `InboundRecovery:SweepIntervalSeconds`, default 60 per architecture.md §7): queries `IInboundUpdateStore.GetRecoverableAsync(maxRetries)` for records with `IdempotencyStatus` in `Received`, `Processing`, or `Failed` where `AttemptCount < MaxRetries` (all three represent incomplete processing — `Received`/`Processing` from crash recovery, `Failed` from transient handler errors; the `AttemptCount` filter ensures permanently failing updates are not retried indefinitely); deserializes each `RawPayload` back into a Telegram `Update`, maps to `MessengerEvent`, resets status to `Processing`, and re-feeds into `ITelegramUpdatePipeline.ProcessAsync` for idempotent re-processing; on success transitions to `Completed`; on repeated failure increments `AttemptCount` and transitions back to `Failed`; when a `Failed` record's `AttemptCount` reaches `InboundRecovery:MaxRetries` (default 3), it stays in `Failed` status (consistent with architecture.md §3.1's four-status enum: `Received`, `Processing`, `Completed`, `Failed`) but is excluded from future recovery sweeps by the `AttemptCount < MaxRetries` filter; logs at `Error` level with the record's `UpdateId` and `ErrorDetail`, and emits a metric `inbound_update_exhausted_retries` for alerting; log recovered records at `Warning` level
- [ ] Add webhook registration logic in `IHostedService` startup: call `SetWebhookAsync` with the configured URL, secret token, and allowed update types

### Dependencies
- phase-telegram-bot-integration/stage-telegram-bot-client-wrapper
- phase-telegram-bot-integration/stage-inbound-update-pipeline

### Test Scenarios
- [ ] Scenario: Valid webhook accepted — Given a well-formed Telegram Update JSON, When POSTed to `/api/telegram/webhook` with correct secret header, Then an `InboundUpdate` record is persisted with `IdempotencyStatus=Received` and `RawPayload` containing the full Update JSON, and response is HTTP 200
- [ ] Scenario: Invalid secret rejected — Given a Telegram Update JSON, When POSTed with an incorrect `X-Telegram-Bot-Api-Secret-Token`, Then response is HTTP 403 and no `InboundUpdate` record is created
- [ ] Scenario: Duplicate update ignored — Given the same `Update.Id` is received twice, When both are POSTed, Then only the first creates an `InboundUpdate` record and triggers downstream processing; the second returns 200 with no new record
- [ ] Scenario: Crash recovery on restart — Given an `InboundUpdate` record exists with `IdempotencyStatus=Received` (simulating a crash after 200 was returned but before processing completed), When the Worker restarts, Then `InboundRecoverySweep` deserializes the `RawPayload`, re-feeds it into the pipeline, and the record transitions to `Completed`
- [ ] Scenario: Failed inbound update retried on sweep — Given an `InboundUpdate` record exists with `IdempotencyStatus=Failed` and `AttemptCount=1` (simulating a transient handler error), When `InboundRecoverySweep` runs its periodic sweep, Then the record is reprocessed via the pipeline; if the handler succeeds, `IdempotencyStatus` transitions to `Completed`; if it fails again, `AttemptCount` is incremented to 2 and `IdempotencyStatus` remains `Failed`
- [ ] Scenario: Permanently failing update excluded from recovery — Given an `InboundUpdate` record with `IdempotencyStatus=Failed` and `AttemptCount` equal to `MaxRetries` (default 3), When `InboundRecoverySweep` runs, Then the record remains in `Failed` status (consistent with architecture.md §3.1 four-status model), is excluded from `GetRecoverableAsync` results by the `AttemptCount < MaxRetries` filter, an error is logged with the `UpdateId` and `ErrorDetail`, and the `inbound_update_exhausted_retries` metric is emitted for alerting

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

## Stage 2.6: Telegram Messenger Connector

### Implementation Steps
- [ ] Create `TelegramMessengerConnector` class in the Telegram project implementing `IMessengerConnector` (defined in Stage 1.3 Abstractions) — per architecture.md §4.1, this is the concrete Telegram implementation of the shared gateway interface
- [ ] Implement `SendMessageAsync(MessengerMessage, CancellationToken)`: convert the `MessengerMessage` to an `OutboundMessage` with appropriate `SourceType` (e.g., `Alert`, `StatusUpdate`, `CommandAck`), derive `IdempotencyKey` per architecture.md §3.1 idempotency key derivation table, set `Severity` from the message, and delegate to `IOutboundQueue.EnqueueAsync` (interface defined in Stage 1.4)
- [ ] Implement `SendQuestionAsync(AgentQuestionEnvelope, CancellationToken)`: convert the envelope to an `OutboundMessage` with `SourceType=Question`, `SourceId=QuestionId`, `IdempotencyKey=q:{AgentId}:{QuestionId}`, read `RoutingMetadata["TelegramChatId"]` for the target chat, and delegate to `IOutboundQueue.EnqueueAsync`
- [ ] Implement `ReceiveAsync(CancellationToken)`: drain processed inbound `MessengerEvent`s from an internal `Channel<MessengerEvent>` that the `TelegramUpdatePipeline` (Stage 2.2) feeds after processing each update; return as `IReadOnlyList<MessengerEvent>`
- [ ] Register `TelegramMessengerConnector` as `IMessengerConnector` in the DI container via `TelegramServiceCollectionExtensions`

### Dependencies
- phase-telegram-bot-integration/stage-inbound-update-pipeline
- phase-telegram-bot-integration/stage-outbound-message-sender

### Test Scenarios
- [ ] Scenario: SendMessageAsync delegates to outbound queue — Given a `MessengerMessage` with `Severity=High`, When `SendMessageAsync` is called on `TelegramMessengerConnector`, Then an `OutboundMessage` is enqueued with matching severity, correct `IdempotencyKey`, and `SourceType=StatusUpdate`
- [ ] Scenario: SendQuestionAsync uses envelope metadata — Given an `AgentQuestionEnvelope` with `RoutingMetadata["TelegramChatId"]="12345"` and `ProposedDefaultActionId="act-1"`, When `SendQuestionAsync` is called, Then the enqueued `OutboundMessage` has `ChatId=12345`, `SourceType=Question`, and `IdempotencyKey=q:{AgentId}:{QuestionId}`
- [ ] Scenario: ReceiveAsync drains processed events — Given two `MessengerEvent`s have been fed into the connector's internal channel by the pipeline, When `ReceiveAsync` is called, Then both events are returned

## Stage 2.7: Swarm Event Ingress Service

### Implementation Steps
- [ ] Create `SwarmEventSubscriptionService` as a `BackgroundService` in the Telegram project that subscribes to agent-originated events via `ISwarmCommandBus.SubscribeAsync(tenantId, CancellationToken)` (defined in Stage 1.3, per architecture.md §4.6)
- [ ] Register `StubOperatorRegistry` (implements `IOperatorRegistry`) in DI with `TryAddSingleton` so it serves as the default until replaced; it returns a configurable set of hardcoded `OperatorBinding` entries loaded from `TelegramOptions.DevOperators` in appsettings; **replacement contract:** Stage 3.4 registers `PersistentOperatorRegistry` with `AddSingleton` which wins over the stub's `TryAddSingleton`; **acceptance-test wiring:** integration tests in Stage 7.2 configure `StubOperatorRegistry` via `WebApplicationFactory.ConfigureServices` with known test bindings so AC001–AC006 run without a database; **production readiness gate:** the Phase 6 DI wiring stage (Stage 6.3) MUST register the concrete `PersistentOperatorRegistry` — a startup health check asserts that the resolved `IOperatorRegistry` is NOT `StubOperatorRegistry` when `ASPNETCORE_ENVIRONMENT` is `Production`
- [ ] Register `StubTaskOversightRepository` (implements `ITaskOversightRepository`) in DI with `TryAddSingleton`; returns `null` for all `GetByTaskIdAsync` lookups, causing status updates to broadcast to all active operators; **replacement contract:** Stage 3.2 registers `PersistentTaskOversightRepository` with `AddSingleton` which wins over the stub's `TryAddSingleton`; **acceptance-test wiring:** integration tests inject the stub so status-update broadcast tests (AC002, AC006) work without persistence; **production readiness gate:** same startup health check in Stage 6.3 asserts that the resolved `ITaskOversightRepository` is NOT `StubTaskOversightRepository` when `ASPNETCORE_ENVIRONMENT` is `Production`
- [ ] On startup, resolve active tenant IDs from `IOperatorRegistry` and call `SubscribeAsync` for each tenant; process the `IAsyncEnumerable<SwarmEvent>` stream as events arrive
- [ ] Route `AgentQuestionEvent` events: extract the `AgentQuestionEnvelope`, resolve the target operator's `TelegramChatId` via `IOperatorRegistry` (or from `RoutingMetadata`), and call `IMessengerConnector.SendQuestionAsync` to deliver the question with inline buttons
- [ ] Route `AgentAlertEvent` events: convert to a `MessengerMessage` with the alert's severity, resolve the target chat via `ITaskOversightRepository.GetByTaskIdAsync(event.TaskId)` → `TaskOversight.OperatorBindingId` → `OperatorBinding.TelegramChatId`; if no `TaskOversight` record exists, fall back to the workspace's default operator (the first active `OperatorBinding` for that `WorkspaceId`, resolved via `IOperatorRegistry.GetByWorkspaceAsync(event.WorkspaceId)` per architecture.md §5.6); call `IMessengerConnector.SendMessageAsync`
- [ ] Route `AgentStatusUpdateEvent` events: convert to a `MessengerMessage` with `Severity=Normal`, resolve the oversighting operator via `ITaskOversightRepository.GetByTaskIdAsync` (returns `null` from stub → broadcast to all active operators; returns `TaskOversight` from concrete impl → route to specific operator), and call `IMessengerConnector.SendMessageAsync`
- [ ] Handle subscription errors with exponential backoff and reconnection; log disconnections at `Warning` level and reconnections at `Information` level
- [ ] Register `StubSwarmCommandBus` (implements `ISwarmCommandBus`) in DI with `TryAddSingleton`; `SubscribeAsync` yields no events (returns an empty `IAsyncEnumerable<SwarmEvent>`); `PublishCommandAsync` logs the command at `Debug` level and returns success; `QueryStatusAsync`/`QueryAgentsAsync` return empty collections; **replacement contract:** the concrete swarm transport adapter (out of scope for this story, per architecture.md §4.6) registers with `AddSingleton` which wins over `TryAddSingleton`; **acceptance-test wiring:** integration tests in Stage 7.2 replace the stub with a `FakeSwarmCommandBus` that exposes a `Channel<SwarmEvent>` writer so tests can inject `AgentQuestionEvent`/`AgentAlertEvent` into the subscription stream and verify end-to-end rendering (AC002, AC003); **production readiness gate:** the Phase 6 startup health check asserts that the resolved `ISwarmCommandBus` is NOT `StubSwarmCommandBus` when `ASPNETCORE_ENVIRONMENT` is `Production`

### Dependencies
- phase-telegram-bot-integration/stage-telegram-messenger-connector
- phase-messaging-abstractions-and-solution-scaffold/stage-connector-interface-and-service-contracts

### Test Scenarios
- [ ] Scenario: Question event routed to Telegram — Given agent "build-agent-1" publishes an `AgentQuestionEvent` with a blocking question, When `SwarmEventSubscriptionService` processes it, Then `IMessengerConnector.SendQuestionAsync` is called with the correct `AgentQuestionEnvelope`
- [ ] Scenario: Alert event routed via task oversight — Given agent "monitor-1" publishes an `AgentAlertEvent` with `Severity=Critical` and `TaskId="TASK-099"`, and a `TaskOversight` record exists for "TASK-099" mapping to operator "op-1", When processed, Then `IMessengerConnector.SendMessageAsync` is called targeting "op-1"'s chat with a `MessengerMessage` containing the alert title and `Severity=Critical`
- [ ] Scenario: Alert event falls back to workspace default operator — Given agent "monitor-2" publishes an `AgentAlertEvent` with `Severity=High`, `TaskId="TASK-100"`, and `WorkspaceId="factory-1"`, and no `TaskOversight` record exists for "TASK-100", When processed, Then the alert is routed to the first active `OperatorBinding` for workspace "factory-1" via `IOperatorRegistry.GetByWorkspaceAsync`
- [ ] Scenario: Subscription reconnects on error — Given the `SubscribeAsync` stream throws a transient exception, When the error occurs, Then the service logs a warning and re-subscribes with backoff

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
- [ ] Implement `AgentsCommandHandler` — queries active agents list via `ISwarmCommandBus.QueryAgentsAsync`, returns formatted agent roster; when the operator has multiple workspace bindings (detected via `IOperatorRegistry.GetAllBindingsAsync(telegramUserId)`), if `/agents` is invoked without a workspace argument, return an inline keyboard listing the operator's workspaces so they can disambiguate (per e2e multi-workspace disambiguation flow); if `/agents WORKSPACE` is invoked with a workspace argument, filter the agent roster to that workspace
- [ ] Implement `AskCommandHandler` — creates a work item in the swarm orchestrator via `ISwarmCommandBus.PublishCommandAsync` from the argument text, returns confirmation with task ID and correlation ID
- [ ] Implement `ApproveCommandHandler` and `RejectCommandHandler` — resolve a pending `AgentQuestion` by emitting a `HumanDecisionEvent` with the appropriate action value
- [ ] Implement `PauseCommandHandler` and `ResumeCommandHandler` — send pause/resume signals to the target agent via `ISwarmCommandBus.PublishCommandAsync`
- [ ] Implement `HandoffCommandHandler` input validation: parse `/handoff TASK-ID @operator-alias` syntax (two arguments: task ID and operator alias); if invalid (zero or one argument), return usage help "Usage: `/handoff TASK-ID @operator-alias`"; validate that the task exists via `ITaskOversightRepository` and the requesting operator currently has oversight; if task not found, reply "❌ Task TASK-ID not found"; resolve the target operator via `IOperatorRegistry.GetByAliasAsync(alias, authorizedOperator.TenantId)` — the tenant ID is obtained from the `AuthorizedOperator` context of the requesting operator, ensuring alias resolution is tenant-scoped per architecture.md lines 116–119 so `/handoff` cannot resolve an operator from a different tenant; if not found, reply "❌ Operator @operator-alias is not registered in this tenant"
- [ ] Implement `HandoffCommandHandler` oversight transfer and notifications (per architecture.md §5.5 "Full oversight transfer (Decided)" and tech-spec.md D-4): create or update a `TaskOversight` record mapping the task to the target operator; notify both operators — the sender receives "✅ Oversight of TASK-ID transferred to @operator-alias", the target receives a handoff notification with task context; persist an audit record with task ID, source operator, target operator, timestamp, and `CorrelationId` for traceability
- [ ] Create `TaskOversight` entity configuration in Persistence (the `TaskOversight` record itself is defined in Stage 1.2 Core so that `ITaskOversightRepository` in Core can reference it without a reverse dependency on Persistence): add EF Core `TaskOversightConfiguration` with table `task_oversights`, add migration, and register in `MessagingDbContext`; add indexes on `OperatorBindingId` for operator-scoped queries and on `TaskId` for task lookup
- [ ] Implement `PersistentTaskOversightRepository` in Persistence implementing `ITaskOversightRepository` (interface defined in Stage 1.3 Core) backed by EF Core

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
- [ ] Scenario: Agents multi-workspace disambiguation — Given operator `op-1` has bindings in workspaces `ws-alpha` and `ws-beta`, When `/agents` is sent without a workspace argument, Then the bot replies with an inline keyboard listing `ws-alpha` and `ws-beta` for the operator to select; when the operator taps `ws-alpha`, Then the agent roster for `ws-alpha` is returned
- [ ] Scenario: Handoff persists TaskOversight — Given an authorized user "operator-1" sends `/handoff TASK-099 @operator-2` and the transfer succeeds, When the `TaskOversight` table is queried for "TASK-099", Then a record exists with `OperatorBindingId` matching "operator-2" and `AssignedBy` matching "operator-1"

## Stage 3.3: Callback Query Handler

### Implementation Steps
- [ ] Create `CallbackQueryHandler` implementing `ICallbackHandler` (defined in Stage 1.3) in the Telegram project that processes Telegram `CallbackQuery` objects from inline button presses
- [ ] Decode callback data format `QuestionId:ActionId` and look up the original `AgentQuestion` from the pending-questions store
- [ ] Emit a `HumanDecisionEvent` with the selected action, user identity, message ID, and correlation ID
- [ ] Answer the callback query via `AnswerCallbackQueryAsync` with a confirmation message (e.g., "✅ Approved")
- [ ] Update the original message to reflect the decision (edit inline keyboard to show selected action, disable further buttons)
- [ ] Implement idempotency: if the same callback has already been processed (same `CallbackQuery.Id`), skip processing and re-answer with the previous result
- [ ] Reject expired callback presses (per e2e-scenarios.md "Callback query answered after question expired"): when the pending question's `ExpiresAt` is in the past, reply with "This question has expired" via `AnswerCallbackQueryAsync` and do **not** publish a `HumanDecisionEvent`
- [ ] Deduplicate concurrent button taps by the same respondent (per e2e-scenarios.md "Concurrent button taps from same user"): use the composite key `QuestionId + respondent TelegramUserId` to ensure only the first tap is processed; if a second tap arrives for the same question from the same user (regardless of which action they selected), answer the callback with "Already responded" and do not emit a duplicate `HumanDecisionEvent`

### Dependencies
- phase-command-processing-and-agent-routing/stage-command-router-and-handlers

### Test Scenarios
- [ ] Scenario: Button press emits decision — Given a callback query with data `Q1:approve`, When processed, Then a `HumanDecisionEvent` is emitted with `QuestionId=Q1` and `ActionValue=approve`
- [ ] Scenario: Duplicate callback ignored — Given callback `CB1` has already been processed, When received again, Then no duplicate `HumanDecisionEvent` is emitted and the user sees the same confirmation
- [ ] Scenario: Original message updated — Given a question message with three action buttons, When one button is pressed, Then the message is edited to show only the selected action and buttons are removed
- [ ] Scenario: Expired callback rejected — Given question `Q-5001` expired 10 minutes ago, When user taps "Approve" on the expired question, Then the bot answers "This question has expired" and no `HumanDecisionEvent` is published
- [ ] Scenario: Concurrent taps deduplicated — Given question `Q-6001` is displayed to user `operator-1`, When user `operator-1` taps "Approve" and "Reject" in rapid succession, Then only the first tap produces a `HumanDecisionEvent` and the second tap receives callback answer "Already responded"

## Stage 3.4: Operator Identity Mapping

### Implementation Steps
- [ ] Create `OperatorBindingConfiguration` entity configuration in the Persistence project with table `operator_bindings`, columns matching the `OperatorBinding` record (Id, TelegramUserId, TelegramChatId, ChatType, OperatorAlias, TenantId, WorkspaceId, Roles serialized as JSON, RegisteredAt, IsActive); add composite index on `(TelegramUserId, TelegramChatId)` for runtime authorization lookups, unique composite index on `(OperatorAlias, TenantId)` for tenant-scoped `/handoff` alias resolution (per architecture.md lines 116–119: ensures alias uniqueness within a tenant so `/handoff @alias` in one tenant cannot resolve an operator in a different tenant; two tenants may independently use the same alias without collision), unique composite index on `(TelegramUserId, TelegramChatId, WorkspaceId)` to prevent duplicate bindings, and index on `TelegramUserId` for `GetAllBindingsAsync` — note: the `IOperatorRegistry` interface is defined in Stage 1.3 Core; the **only** concrete implementation is `PersistentOperatorRegistry` (below); `StubOperatorRegistry` (registered in Stage 2.7 via `TryAddSingleton`) serves as a dev/test stand-in that is automatically superseded when `PersistentOperatorRegistry` is registered with `AddSingleton` in Stage 6.3 DI wiring
- [ ] Create EF Core migration `AddOperatorBindings` that creates the `operator_bindings` table with the above schema and indexes; use SQLite for dev/local and PostgreSQL or SQL Server for production (consistent with the persistence strategy in Stages 4.1, 5.3)
- [ ] Implement `PersistentOperatorRegistry` in the Persistence project that wraps the EF Core `DbContext` and implements `IOperatorRegistry` (all six methods per architecture.md §4.3): `GetBindingsAsync` queries by `(TelegramUserId, TelegramChatId)` with `IsActive=true` and returns `IReadOnlyList<OperatorBinding>` (one row per workspace — supports multi-workspace disambiguation per architecture.md §4.3); `IsAuthorizedAsync` checks for at least one active binding matching the `(userId, chatId)` pair; `RegisterAsync(OperatorRegistration registration)` creates an `OperatorBinding` from the value object's fields (`TelegramUserId`, `TelegramChatId`, `ChatType`, `TenantId`, `WorkspaceId`, `Roles`, `OperatorAlias`) — upserts: insert if absent, update `IsActive=true` and refresh `RegisteredAt`/`Roles`/`OperatorAlias` if deactivated; the `OperatorRegistration` value object (defined in Stage 1.3 Core per architecture.md §4.3 lines 339–348) ensures all required fields are present so `/start` creates complete `OperatorBinding` rows; `GetByAliasAsync(alias, tenantId)` queries by `(OperatorAlias, TenantId)` with `IsActive=true` using the tenant-scoped unique index (per architecture.md lines 116–119); `GetAllBindingsAsync` queries all active bindings for a `TelegramUserId` across all chats; `GetByWorkspaceAsync(workspaceId)` queries all active `OperatorBinding` rows filtered by `WorkspaceId` with `IsActive=true` — used by Stage 2.7 alert fallback routing when no `TaskOversight` record exists for an alert's `TaskId` (per architecture.md §5.6)
- [ ] Implement `TelegramUserAuthorizationService` implementing `IUserAuthorizationService` that checks the allowlist and returns `AuthorizationResult` with the full `Bindings` list populated: at runtime (non-`/start` commands), calls `IOperatorRegistry.GetBindingsAsync(userId, chatId)` to retrieve all active `OperatorBinding` records for the user/chat pair (one per workspace per architecture.md §4.3); sets `AuthorizationResult.IsAuthorized = bindings.Count > 0` and `AuthorizationResult.Bindings = bindings`; callers then handle cardinality: zero bindings = unauthorized, one = use directly to build `AuthorizedOperator`, multiple = prompt workspace disambiguation via inline keyboard (per e2e-scenarios.md multi-workspace flow); on Tier 1 `/start` onboarding, checks the allowlist first, then looks up the user's entry in `Telegram:UserTenantMappings` configuration (per architecture.md §7.1 lines 636–650) to obtain `TenantId`, `WorkspaceId`, `Roles`, and `OperatorAlias`, derives `ChatType` from `Update.Message.Chat.Type`, constructs an `OperatorRegistration` value object, and calls `IOperatorRegistry.RegisterAsync(registration)` to create a complete `OperatorBinding` with all fields populated; returns the newly created binding in `AuthorizationResult.Bindings`
- [ ] On `/start`, if the Telegram user ID is in the allowlist, construct `OperatorRegistration` from the Telegram `Update` (for `TelegramUserId`, `TelegramChatId`, `ChatType`) and the `Telegram:UserTenantMappings` configuration entry (for `TenantId`, `WorkspaceId`, `Roles`, `OperatorAlias` — per architecture.md §7.1 lines 636–650), then call `IOperatorRegistry.RegisterAsync(registration)` to create the `OperatorBinding` with all required fields; if the user is not in the allowlist, respond with an "unauthorized" message and log the attempt; if the mapping contains multiple workspace entries for a user, create one `OperatorBinding` per workspace (subsequent commands trigger workspace disambiguation per §4.3)

### Dependencies
- phase-command-processing-and-agent-routing/stage-command-router-and-handlers

### Test Scenarios
- [ ] Scenario: Authorized user mapped — Given Telegram user `12345` is in the allowlist mapped to operator `op-1` in tenant `t-1`, When `/start` is received, Then `AuthorizationResult.IsAuthorized` is true and `OperatorId` is `op-1`
- [ ] Scenario: Unauthorized user rejected — Given Telegram user `99999` is not in the allowlist, When any command is received, Then `AuthorizationResult.IsAuthorized` is false and `DenialReason` is populated
- [ ] Scenario: Binding persists across restart — Given Telegram user `12345` sends `/start` and an `OperatorBinding` row is created, When the service restarts and user `12345` sends `/status` from the same chat, Then `IsAuthorizedAsync` returns true because the binding is persisted in the database
- [ ] Scenario: Alias lookup resolves binding within tenant — Given an `OperatorBinding` exists with `OperatorAlias=@operator-1` in `TenantId=acme`, When `GetByAliasAsync("@operator-1", "acme")` is called, Then the correct `OperatorBinding` is returned with matching `TelegramUserId` and `TelegramChatId`; when `GetByAliasAsync("@operator-1", "other-tenant")` is called, Then `null` is returned because alias resolution is tenant-scoped per architecture.md lines 116–119
- [ ] Scenario: Multi-workspace bindings returned — Given Telegram user `12345` in chat `67890` has active bindings in workspaces `ws-alpha` and `ws-beta`, When `TelegramUserAuthorizationService.AuthorizeAsync` is called for a non-`/start` command, Then `AuthorizationResult.IsAuthorized` is true and `AuthorizationResult.Bindings` contains both `OperatorBinding` records (one for `ws-alpha`, one for `ws-beta`) so the pipeline can prompt for workspace disambiguation via inline keyboard

## Stage 3.5: Pending Question Store and Timeout Service

### Implementation Steps
- [ ] Define `IPendingQuestionStore` concrete registration point — the interface is defined in Stage 1.3 Abstractions; this stage provides the EF Core implementation
- [ ] Create `PendingQuestionRecord` EF Core entity in `AgentSwarm.Messaging.Persistence` (not in `AgentSwarm.Messaging.Telegram` — the Telegram project depends on Abstractions and Core but not Persistence; placing the entity in Persistence ensures the dependency arrow flows from Persistence → Abstractions, not Telegram → Persistence) with fields: `QuestionId`, `AgentQuestion` (serialized), `TelegramChatId`, `TelegramMessageId`, `StoredAt`, `ExpiresAt`, `DefaultActionId` (nullable `string` — denormalized from `AgentQuestionEnvelope.ProposedDefaultActionId` sidecar metadata at question-send time per e2e-scenarios.md lines 57–76; stored here for display/audit and for `QuestionRecoverySweep` backfill correlation), `DefaultActionValue` (nullable `string` — the `HumanAction.Value` of the default action, resolved and denormalized at send time by `IPendingQuestionStore.StoreAsync`: the store looks up the `HumanAction` in `AgentQuestion.AllowedActions` whose `ActionId` matches `ProposedDefaultActionId` and persists its `Value` here; when present, `QuestionTimeoutService` publishes a `HumanDecisionEvent` with `DefaultActionValue` as the `ActionValue` — consistent with the interactive button-tap path where `HumanAction.Value` is always the canonical `ActionValue`, per architecture.md §3.1 and §10.3; no `IDistributedCache` lookup is required at timeout), `SelectedActionId` (nullable `string`), `SelectedActionValue` (nullable `string` — the `HumanAction.Value` corresponding to `SelectedActionId`, resolved at button-tap time and persisted by `RecordSelectionAsync`), `RespondentUserId` (nullable `long`), `Status` (enum: `Pending`, `Answered`, `AwaitingComment`, `TimedOut`), `CorrelationId`; add EF Core entity configuration `PendingQuestionRecordConfiguration` in Persistence with table name `pending_questions` and indexes on `ExpiresAt` and `TelegramMessageId`
- [ ] Verify `IDistributedCache` is available in the DI container (registered by Stage 2.3 via `AddDistributedMemoryCache()` for dev/local or `AddStackExchangeRedisCache()` for production) — this cache is used by `TelegramMessageSender` (Stage 2.3) to write `QuestionId:ActionId → HumanAction` entries at inline-keyboard build time, and by `CallbackQueryHandler` (Stage 3.3) to resolve full `HumanAction` payloads from short `ActionId` keys during interactive button callbacks (which occur before `ExpiresAt`); `QuestionTimeoutService` does **not** depend on the cache — it reads `DefaultActionValue` directly from `PendingQuestionRecord` (per architecture.md §10.3); no additional registration is needed here since Stage 2.3 already registers it
- [ ] Implement `PersistentPendingQuestionStore` in `AgentSwarm.Messaging.Persistence` implementing `IPendingQuestionStore` (interface from Stage 1.3 Abstractions; returns `PendingQuestion` DTO from Abstractions) backed by EF Core using `PendingQuestionRecord` entity (also in Persistence); the implementation maps between the `PendingQuestionRecord` EF entity and the `PendingQuestion` abstraction DTO at the store boundary, so consumers depend only on Abstractions; uses SQLite for dev/local, PostgreSQL or SQL Server for production; indexed lookups by `QuestionId`, `ExpiresAt`, and `DefaultActionId`
- [ ] Integrate store call site: post-send `PendingQuestionRecord` persistence is handled by `OutboundQueueProcessor` in Stage 4.1 (after `MarkSentAsync` for `SourceType = Question` messages, the processor calls `IPendingQuestionStore.StoreAsync`); `TelegramMessageSender.SendQuestionAsync` in this stage only builds the inline keyboard and enqueues the `OutboundMessage` with `SourceEnvelopeJson` — it does **not** persist the `PendingQuestionRecord` directly
- [ ] Implement `RequiresComment` flow in `CallbackQueryHandler`: when the selected `HumanAction.RequiresComment` is true, set the question status to `AwaitingComment`, send a prompt ("Please reply with your comment"), and defer `HumanDecisionEvent` emission until the operator's text reply arrives via the pipeline's `TextReply` handler
- [ ] Create `QuestionTimeoutService` as a `BackgroundService` that periodically polls `GetExpiredAsync`, and for each expired question: reads `PendingQuestionRecord.DefaultActionValue` (the `HumanAction.Value` of the default action, resolved and denormalized at send time by `IPendingQuestionStore.StoreAsync` — see architecture.md §3.1 `PendingQuestionRecord.DefaultActionValue`) — if present, publishes a `HumanDecisionEvent` with `DefaultActionValue` as the `ActionValue` (consistent with the interactive button-tap path where `HumanAction.Value` is always the canonical `ActionValue`, per architecture.md §10.3; it does **not** look up the full `HumanAction` from `IDistributedCache`, because the cache entry may be evicted when the timeout fires); if absent (`null`), publishes a `HumanDecisionEvent` with `ActionValue = "__timeout__"`; updates the original Telegram message (via `TelegramMessageId`) to indicate timeout ("⏰ Timed out — default action applied: {action}" or "⏰ Timed out — no default action"); marks the question as `TimedOut`; and writes an audit record

### Dependencies
- phase-command-processing-and-agent-routing/stage-callback-query-handler

### Test Scenarios
- [ ] Scenario: Question stored after outbound send — Given an `AgentQuestion` is enqueued as an `OutboundMessage` with `SourceType=Question`, When `OutboundQueueProcessor` sends it successfully and `MarkSentAsync` completes, Then the processor's post-send hook calls `IPendingQuestionStore.StoreAsync` and a `PendingQuestionRecord` exists in the store with status `Pending` and the correct `TelegramMessageId`
- [ ] Scenario: Callback resolves pending question — Given a pending question `Q1`, When the callback query handler processes an approve action, Then the question status is updated to `Answered` and a `HumanDecisionEvent` is emitted
- [ ] Scenario: RequiresComment defers decision — Given a pending question with an action having `RequiresComment=true`, When the operator taps that button, Then the bot prompts for a comment and the `HumanDecisionEvent` is not emitted until the operator replies with text
- [ ] Scenario: Timeout applies default action — Given a pending question with `ExpiresAt` in the past and `PendingQuestionRecord.DefaultActionValue=approve` (resolved from the `HumanAction.Value` where `ActionId` matches `ProposedDefaultActionId`), When `QuestionTimeoutService` polls, Then it reads `DefaultActionValue` directly from `PendingQuestionRecord` (no `IDistributedCache` lookup per architecture.md §10.3), emits a `HumanDecisionEvent` with `ActionValue=approve`, and updates the Telegram message with "⏰ Timed out — default action applied: approve"
- [ ] Scenario: Timeout without default action — Given a pending question with `ExpiresAt` in the past and `PendingQuestionRecord.DefaultActionValue` is null, When `QuestionTimeoutService` polls, Then a `HumanDecisionEvent` is emitted with `ActionValue=__timeout__` and the Telegram message is updated with "⏰ Timed out — no default action"

## Stage 3.6: Question Recovery Sweep

### Implementation Steps
- [ ] Create `QuestionRecoverySweep` as a `BackgroundService` in the Telegram project (per architecture.md §2.2) that runs on startup and then periodically (configurable via `QuestionRecovery:SweepIntervalSeconds`, default 60 per architecture.md §7)
- [ ] On each sweep, query `IOutboundQueue` (or the underlying store) for `OutboundMessage` records with `SourceType = Question` and `Status = Sent` that lack a corresponding `PendingQuestionRecord` (i.e., `IPendingQuestionStore.GetAsync(sourceId)` returns null) — these represent the Gap B crash window described in architecture.md §3.1: the outbound message was successfully sent to Telegram and marked `Sent` (with `TelegramMessageId` populated), but the process crashed before `PendingQuestionRecord` was persisted
- [ ] For each orphaned record, deserialize `OutboundMessage.SourceEnvelopeJson` (added in Stage 1.2) to extract the `AgentQuestionEnvelope` — from the envelope, read `AgentQuestion` fields (`QuestionId`, `ExpiresAt`, `AllowedActions`, etc.) and `ProposedDefaultActionId` for denormalization into the backfilled `PendingQuestionRecord.DefaultActionId`
- [ ] Call `IPendingQuestionStore.StoreAsync` with the reconstructed envelope, the `OutboundMessage.ChatId` as `telegramChatId`, and the `OutboundMessage.TelegramMessageId` as `telegramMessageId` to create the missing `PendingQuestionRecord` — this ensures no operator-visible button message is left untracked
- [ ] Log each backfilled record at `Warning` level with `QuestionId`, `TelegramMessageId`, and `CorrelationId` for traceability
- [ ] Emit a `question_recovery_sweep_backfilled` counter metric for monitoring

### Dependencies
- phase-command-processing-and-agent-routing/stage-pending-question-store-and-timeout-service
- phase-reliability-infrastructure/stage-durable-outbound-message-queue

### Test Scenarios
- [ ] Scenario: Gap B recovery backfills missing PendingQuestionRecord — Given an `OutboundMessage` with `SourceType=Question`, `Status=Sent`, `TelegramMessageId=42`, and `SourceEnvelopeJson` containing a valid `AgentQuestionEnvelope`, and no `PendingQuestionRecord` exists for the question's `QuestionId`, When `QuestionRecoverySweep` runs, Then a `PendingQuestionRecord` is created with `QuestionId`, `TelegramMessageId=42`, `DefaultActionId` from the envelope's `ProposedDefaultActionId`, and `Status=Pending`
- [ ] Scenario: Already-tracked questions are skipped — Given an `OutboundMessage` with `SourceType=Question` and `Status=Sent` AND a `PendingQuestionRecord` already exists for that `QuestionId`, When `QuestionRecoverySweep` runs, Then no duplicate `PendingQuestionRecord` is created
- [ ] Scenario: Sweep runs periodically — Given `QuestionRecovery:SweepIntervalSeconds=60`, When the service starts, Then the sweep executes on startup and then every 60 seconds

# Phase 4: Reliability Infrastructure

## Dependencies
- phase-command-processing-and-agent-routing

## Stage 4.1: Durable Outbound Message Queue

### Implementation Steps
- [ ] Create EF Core entity configuration for `OutboundMessage` (record defined in Stage 1.2 Core) in Persistence: `OutboundMessageConfiguration` mapping to `outbox` table with `MessageId` as PK, status tracking columns, and a `UNIQUE` constraint on `IdempotencyKey`
- [ ] Implement `InMemoryOutboundQueue` using a priority-ordered `Channel<OutboundMessage>` (severity-priority dequeue: `Critical` > `High` > `Normal` > `Low`) with bounded capacity for development
- [ ] Implement `PersistentOutboundQueue` backed by EF Core for durable persistence; use SQLite provider for dev/local environments and PostgreSQL or SQL Server for production (the specific production provider is a deployment decision, consistent with architecture.md §11.3); persist messages to an `outbox` table with status tracking; the `Severity` column is persisted as an `int` with explicit numeric mapping (`Critical=0`, `High=1`, `Normal=2`, `Low=3`) via EF Core value converter so that `ORDER BY Severity ASC` correctly yields `Critical` first, then `High`, then `Normal`, then `Low`; `DequeueAsync` queries `WHERE Status=Pending ORDER BY Severity ASC, CreatedAt ASC` to enforce severity-priority ordering; on `EnqueueAsync`, check `IdempotencyKey` uniqueness before inserting; when queue depth exceeds `OutboundQueue:MaxQueueDepth` (default 5000, per architecture.md §10.4), dead-letter `Low`-severity messages immediately with reason `backpressure:queue_depth_exceeded` and increment the `telegram.messages.backpressure_dlq` counter (canonical backpressure dead-letter metric per architecture.md §10.4) — `Normal`, `High`, and `Critical` messages are always accepted
- [ ] Create `OutboundQueueProcessor` as a `BackgroundService` with configurable concurrency (`OutboundQueue:ProcessorConcurrency`, default 10 workers per architecture.md §10.4); each worker independently dequeues the highest-severity pending message, records `DequeuedAt` timestamp, transitions to `Sending`, sends via `IMessageSender` (the abstraction defined in Core, per architecture.md §4.12 — not the concrete `TelegramMessageSender`, so that the Worker assembly does not depend on `AgentSwarm.Messaging.Telegram`), and transitions to `Sent` on success or `Failed` on error; `IMessageSender.SendTextAsync` / `SendQuestionAsync` return `Task<SendResult>` carrying the `TelegramMessageId` assigned by the Bot API; on success the processor calls `IOutboundQueue.MarkSentAsync(messageId, sendResult.TelegramMessageId)` to persist the Telegram message ID on the `OutboundMessage` record (note: `MarkSentAsync` *consumes* the `TelegramMessageId` — it does not produce it); **post-send question persistence hook:** after `MarkSentAsync` succeeds for messages with `SourceType = Question`, the processor deserializes `OutboundMessage.SourceEnvelopeJson` to recover the `AgentQuestionEnvelope`, then calls `IPendingQuestionStore.StoreAsync(envelope, chatId, sendResult.TelegramMessageId)` using the `TelegramMessageId` from `SendResult` and the `ChatId` from the `OutboundMessage` — this is the concrete component and hook point for step 4 of the crash-window analysis (architecture.md §3.1, §5.2 invariant 1)
- [ ] Implement latency metric emission in `OutboundQueueProcessor` (all metric names, measurement points, and scopes follow architecture.md §10.4 as the canonical source; see cross-document alignment note below): on every successful first-attempt send that did not receive a Telegram 429 response (local token-bucket wait is included in the measurement as it is part of the normal send path), emit `telegram.send.first_attempt_latency_ms` (acceptance gate per architecture.md §10.4: elapsed time from **enqueue instant** — `OutboundMessage.CreatedAt` — to Telegram Bot API HTTP 200 — the **P95 ≤ 2 s** acceptance criterion applies to this metric); on every successful send regardless of attempt number or rate-limit holds, emit `telegram.send.all_attempts_latency_ms` (all-inclusive per architecture.md §10.4: elapsed time from `OutboundMessage.CreatedAt` (enqueue instant) to final HTTP 200, all messages regardless of retries or rate-limit waits — capacity planning); on every dequeue, emit `telegram.send.queue_dwell_ms` (diagnostic per architecture.md §10.4: elapsed time from `OutboundMessage.CreatedAt` (enqueue) to dequeue instant — queue backlog monitoring)
- [ ] **Acceptance target (MUST PASS):** Under normal operating conditions (steady state, queue depth < 100), the P95 of `telegram.send.first_attempt_latency_ms` (enqueue-to-HTTP-200, first-attempt sends excluding Telegram 429 responses; local token-bucket wait included) MUST be ≤ 2 seconds across all severity levels. This is the acceptance gate for the story requirement "P95 send latency under 2 seconds after event is queued." Queue dwell is negligible (< 50 ms with 10 workers) under normal load, so the enqueue-to-200 metric comfortably meets the target. Implementers MUST NOT consider this target met if only dequeue-to-200 is measured — the enqueue instant is the canonical start point per architecture.md §10.4
- [ ] **Degraded burst behavior (documented, not acceptance-gated):** Under sustained burst (100+ agents, 1000+ messages), priority queuing ensures Critical/High messages are dequeued first with minimal queue dwell. When the combined volume of Critical and High messages stays within the ~60-message envelope (per architecture.md §10.4), their enqueue-to-200 `first_attempt_latency_ms` P95 remains bounded; when Critical+High volume exceeds the ~60-message envelope, the 2-second P95 is not guaranteed even for high-priority messages due to queue saturation. Normal/Low severity messages may exceed the 2-second P95 under any sustained burst due to accumulated queue dwell — this is expected degraded behavior per architecture.md §10.4, not a failure of the acceptance gate. The `all_attempts_latency_ms` and `queue_dwell_ms` diagnostics provide visibility into burst-induced latency

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Enqueue and dequeue — Given a message is enqueued, When dequeued, Then the message content matches and the queue size decreases by one
- [ ] Scenario: Persistence survives restart — Given a message is enqueued to the persistent queue, When the process restarts, Then the message is still available for dequeue
- [ ] Scenario: Severity-priority dequeue — Given the queue contains messages enqueued in order: `Low` first, `Normal` second, `High` third, `Critical` fourth, When `DequeueAsync` is called four times, Then messages are returned in severity-priority order: `Critical` (int 0), `High` (int 1), `Normal` (int 2), `Low` (int 3) — verifying the numeric int-backed severity column correctly orders all four levels
- [ ] Scenario: Backpressure dead-letters low-severity — Given the queue depth exceeds `MaxQueueDepth` (5000), When a `Low`-severity message is enqueued, Then it is dead-lettered immediately with reason `backpressure:queue_depth_exceeded` and the `telegram.messages.backpressure_dlq` counter is incremented (canonical metric per architecture.md §10.4); when a `Critical`-severity message is enqueued under the same conditions, Then it is accepted normally
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
- [ ] Implement two-tier authorization per architecture.md §7.1: Tier 1 (onboarding) checks `TelegramOptions.AllowedUserIds` via `IOptions<TelegramOptions>` when `commandName == "start"` — if the user's Telegram ID is not in the list, `/start` is rejected and no `OperatorBinding` is created; Tier 2 (runtime) applies when `commandName` is anything other than `"start"` — calls `IOperatorRegistry.GetBindingsAsync(userId, chatId)` to retrieve all active `OperatorBinding` records (one per workspace per architecture.md §4.3) and populates `AuthorizationResult.Bindings` with the full list; if zero bindings exist, the command is rejected; if one binding exists, the pipeline constructs `AuthorizedOperator` directly; if multiple bindings exist (multi-workspace operator), the pipeline presents an inline keyboard for workspace disambiguation (per e2e-scenarios.md multi-workspace flow); the `commandName` parameter (added to `IUserAuthorizationService.AuthorizeAsync` in Stage 1.3) enables the Tier 1/Tier 2 distinction without requiring separate pipeline branches
- [ ] Implement role enforcement per architecture.md §9: after Tier 2 authorization resolves the `AuthorizedOperator` (which includes `Roles` from `OperatorBinding`), the pipeline checks that the operator holds the required role for role-gated commands — `/approve` and `/reject` require the `Approver` role; `/pause` and `/resume` require the `Operator` role; if the operator lacks the required role, the command is rejected with a "insufficient permissions" reply and an audit log entry at Warning level; `/start`, `/status`, `/agents`, `/ask`, and `/handoff` have no role requirement beyond Tier 2 binding authorization
- [ ] If user is not authorized, respond with a polite denial message, log the attempt at Warning level with user ID and chat ID (but no PII beyond Telegram numeric IDs), and short-circuit processing
- [ ] Support dynamic allowlist reload without restart via `IOptionsMonitor<TelegramOptions>` — changes to `Telegram:AllowedUserIds` in configuration are reflected immediately for `/start` onboarding checks without requiring a service restart

### Dependencies
- phase-security-and-audit/stage-secret-management-integration

### Test Scenarios
- [ ] Scenario: Allowed user passes — Given user ID `12345` has an active `OperatorBinding` for chat ID `67890`, When a command arrives from user `12345` in chat `67890`, Then processing continues normally
- [ ] Scenario: Denied user blocked — Given user ID `99999` has no `OperatorBinding` record, When a command arrives, Then the user receives the denial message and no command handler is invoked
- [ ] Scenario: Dynamic reload — Given user `67890` is added to `Telegram:AllowedUserIds` while the service is running, When `67890` sends `/start`, Then the `OperatorBinding` is created and subsequent commands are accepted without a restart (verified via `IOptionsMonitor<TelegramOptions>` reload)
- [ ] Scenario: Chat authorized through /start — Given user `12345` is in `TelegramOptions.AllowedUserIds` and sends `/start` from chat `55555`, When `12345` later sends `/status` from chat `55555`, Then the command is accepted because the `OperatorBinding` exists for that (userId, chatId) pair
- [ ] Scenario: Approve requires Approver role — Given user `12345` has an active `OperatorBinding` with `Roles=["Operator"]` (no `Approver`), When `/approve` is sent, Then the command is rejected with "insufficient permissions" and an audit entry is logged; given user `12346` has `Roles=["Approver"]`, When `/approve` is sent, Then the command proceeds normally
- [ ] Scenario: Pause requires Operator role — Given user `12345` has an active `OperatorBinding` with `Roles=["Approver"]` (no `Operator`), When `/pause` is sent, Then the command is rejected with "insufficient permissions"; given user `12346` has `Roles=["Operator"]`, When `/pause` is sent, Then the command proceeds normally
- [ ] Scenario: Multi-workspace disambiguation on command — Given user `12345` has active bindings in workspaces `ws-alpha` and `ws-beta` for chat `67890`, When `/status` is sent, Then `AuthorizationResult.Bindings` contains both bindings and the pipeline presents an inline keyboard listing `ws-alpha` and `ws-beta` for the operator to select before routing the command

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
- [ ] Configure metrics: create custom meters for canonical metric names per architecture.md §8 — counters: `telegram.messages.sent`, `telegram.messages.received`, `telegram.messages.dead_lettered`, `telegram.commands.processed`, `telegram.messages.backpressure_dlq`; histograms: `telegram.send.first_attempt_latency_ms` (acceptance gate — enqueue to HTTP 200, first-attempt sends excluding Telegram 429 responses; local token-bucket wait included; P95 ≤ 2 s target), `telegram.send.all_attempts_latency_ms` (capacity planning — all sends regardless of attempt/rate-limit), `telegram.send.queue_dwell_ms` (diagnostic — enqueue to dequeue), `telegram.send.retry_latency_ms` (diagnostic — retried sends), `telegram.send.rate_limited_wait_ms` (diagnostic — Telegram 429 backoff duration)
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
- [ ] Wire up `Program.cs` in the Worker project — Phase 1/2 services: call `services.AddTelegram(configuration)` (from Stage 2.1 extension method) to register `TelegramOptions`, `ITelegramBotClient` factory, `TelegramUpdatePipeline`, `TelegramMessageSender`, `TelegramMessengerConnector`, `TelegramPollingService`/webhook endpoint; call `services.AddCommandProcessing()` to register `CommandRouter`, all `ICommandHandler` implementations, `CallbackQueryHandler`, `TelegramCommandParser`
- [ ] Wire up `Program.cs` — Phase 3/4 services: register `IOutboundQueue` (persistent or in-memory based on environment), `OutboundQueueProcessor`, `RetryPolicy`, `IDeadLetterQueue`, `IDeduplicationService`, `IPendingQuestionStore`, `QuestionTimeoutService`, `SwarmEventSubscriptionService`, `ISwarmCommandBus` (stub), `IOperatorRegistry`, `ITaskOversightRepository`
- [ ] Implement `StubGuardHealthCheck` as a startup `IHealthCheck` that resolves `IOperatorRegistry`, `ITaskOversightRepository`, and `ISwarmCommandBus` from DI and asserts none are stub implementations when `ASPNETCORE_ENVIRONMENT` is `Production`; if any stub is detected, return `HealthCheckResult.Unhealthy("Stub {InterfaceName} detected in Production — register concrete implementation")` — this prevents production deployments from running with stubs while allowing integration tests and dev mode to use them freely
- [ ] Wire up `Program.cs` — Phase 5/6 cross-cutting services: register `IAuditLogger`, `IUserAuthorizationService`, `IAlertService`; configure OpenTelemetry tracing and metrics; register all health checks; map the `/healthz` and `/api/telegram/webhook` endpoints (the webhook endpoint is defined in `AgentSwarm.Messaging.Telegram` and mapped via `app.MapTelegramWebhook()` extension method; the Worker project only hosts it)
- [ ] Create `appsettings.json` with documented configuration sections: `Telegram` (including `BotToken`, `WebhookUrl`, `UsePolling`, `AllowedUserIds`, `SecretToken`, `RateLimits`, and `UserTenantMappings` — the mapping of allowlisted Telegram user IDs to their tenant/workspace/roles/alias per architecture.md §7.1 lines 636–650; each user ID key maps to a **JSON array** of workspace entry objects, e.g., `"12345": [{ "TenantId": "acme", "WorkspaceId": "factory-1", "Roles": ["Operator", "Approver"], "OperatorAlias": "@alice" }]` — a single-workspace user has a one-element array, a multi-workspace user has multiple elements; the `/start` handler iterates the array and calls `IOperatorRegistry.RegisterAsync` once per entry, creating one `OperatorBinding` per workspace per architecture.md §7.1; this configuration is consumed by `TelegramUserAuthorizationService` at `/start` time to construct `OperatorRegistration` value objects with complete field data for creating `OperatorBinding` rows), `RetryPolicy`, `OutboundQueue`, `ConnectionStrings:MessagingDb` (shared connection string used by `MessagingDbContext` for all EF Core-backed stores: `InboundUpdate` dedup/recovery from Stage 2.4, outbox from Stage 4.1, dead-letter queue from Stage 4.2, `processed_events` dedup from Stage 4.3, `PendingQuestionRecord` from Stage 3.5, `OperatorBinding` from Stage 3.4, and `TaskOversight` from Stage 3.2; defaults to SQLite `Data Source=messaging.db` for dev/local, swappable to PostgreSQL or SQL Server for production via EF Core provider change), `ConnectionStrings:AuditDb` (separate connection for audit log isolation from Stage 5.3; defaults to SQLite `Data Source=audit.db` for dev/local), `KeyVault:Uri`
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
- [ ] Write test `PERF001_P95SendLatencyUnder2Seconds`: enqueue 100 outbound messages, configure WireMock to respond with 200 in under 50ms (ensuring all succeed on first attempt without Telegram 429 responses); the `OutboundQueueProcessor` emits `telegram.send.first_attempt_latency_ms` as elapsed time from **enqueue instant** (`OutboundMessage.CreatedAt`) to Telegram API HTTP 200 (per architecture.md §10.4: enqueue-to-HTTP-200, first-attempt sends excluding Telegram 429 responses; local token-bucket wait included — the P95 ≤ 2s acceptance gate); assert that `telegram.send.first_attempt_latency_ms` is emitted for every send with P95 ≤ 2000ms; assert that `telegram.send.all_attempts_latency_ms` (all-inclusive: enqueue-to-HTTP-200 per architecture.md §10.4) is also emitted; assert that `telegram.send.queue_dwell_ms` (diagnostic: enqueue-to-dequeue per architecture.md §10.4) is emitted; under these test conditions (low queue depth, no retries, no rate-limiting), `first_attempt_latency_ms` ≈ `all_attempts_latency_ms` since every message succeeds on first attempt
- [ ] Write test `PERF002_BurstFrom100PlusAgents`: simulate 100+ agents each enqueuing 10 alert messages concurrently (1000+ total messages), process all through the outbound queue, and assert that every message reaches either `Sent` or `DeadLettered` status (zero messages lost) and the queue drains completely within a bounded time

### Dependencies
- phase-integration-testing-and-acceptance-validation/stage-integration-test-infrastructure

### Test Scenarios
- [ ] Scenario: All acceptance tests pass — Given the full integration test suite, When `dotnet test` is run, Then all eight AC and PERF tests pass
- [ ] Scenario: Tests are deterministic — Given the integration tests, When run three times consecutively, Then all pass every time with no flaky failures
- [ ] Scenario: P95 latency measured accurately — Given the PERF001 test, When 100 messages are sent through the outbound pipeline (all succeeding on first attempt without Telegram 429 responses), Then `telegram.send.first_attempt_latency_ms` (acceptance gate per architecture.md §10.4: measured from **enqueue instant** — `OutboundMessage.CreatedAt` — to HTTP 200, first-attempt sends excluding Telegram 429 responses; local token-bucket wait included) is emitted for every send with P95 ≤ 2000ms; `telegram.send.all_attempts_latency_ms` (all-inclusive per architecture.md §10.4: enqueue-to-HTTP-200, all sends regardless of attempt or rate-limit) is also emitted; `telegram.send.queue_dwell_ms` (diagnostic per architecture.md §10.4: enqueue-to-dequeue) is also emitted; under low-queue-depth test conditions, all three metrics are approximately equal since every message succeeds on first attempt with negligible queue dwell

> **Metric definitions.** This plan uses architecture.md §10.4 as the canonical source. `telegram.send.first_attempt_latency_ms` = enqueue (`OutboundMessage.CreatedAt`) to HTTP 200, first-attempt sends excluding Telegram 429 responses; local token-bucket wait (proactive rate limiting) is included (acceptance gate, P95 ≤ 2s). `telegram.send.all_attempts_latency_ms` = enqueue to HTTP 200, all sends regardless of attempt count or rate-limiting (capacity planning). `telegram.send.queue_dwell_ms` = enqueue to dequeue (diagnostic). Implementers use these names and measurement points.
