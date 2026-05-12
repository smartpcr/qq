---
title: "Microsoft Teams Messenger Support"
storyId: "qq:MICROSOFT-TEAMS-MESS"
---

# Phase 1: Messaging Abstractions and Data Model

## Dependencies
- _none — start phase_

## Stage 1.1: Core Data Models

### Implementation Steps
- [ ] Create solution file `AgentSwarm.Messaging.sln` and project `AgentSwarm.Messaging.Abstractions` targeting .NET 8.
- [ ] Define `MessengerMessage` record with fields: `MessageId`, `ConversationId`, `AgentId`, `TaskId`, `CorrelationId`, `Body`, `Timestamp`, `Severity`.
- [ ] Define `AgentQuestion` record with fields: `QuestionId`, `AgentId`, `TaskId`, `Title`, `Body`, `Severity`, `AllowedActions` (list of `HumanAction`), `ExpiresAt`, `CorrelationId`.
- [ ] Define `HumanAction` record with fields: `ActionId`, `Label`, `Value`, `RequiresComment`.
- [ ] Define `HumanDecisionEvent` record with fields: `QuestionId`, `ActionValue`, `Comment`, `Messenger`, `ExternalUserId`, `ExternalMessageId`, `ReceivedAt`, `CorrelationId`.
- [ ] Define `MessengerEvent` base record and subtypes: `CommandEvent`, `DecisionEvent`, `TextEvent`.

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Serialize round-trip — Given an `AgentQuestion` with two `HumanAction` items, When serialized to JSON and deserialized, Then all field values match the original.
- [ ] Scenario: Required field validation — Given an `AgentQuestion` with null `QuestionId`, When validated, Then a validation error is returned.
- [ ] Scenario: Immutable records — Given a `HumanDecisionEvent`, When attempting to modify `QuestionId`, Then a compile-time error prevents mutation.

## Stage 1.2: Messenger Connector Interface

### Implementation Steps
- [ ] Define `IMessengerConnector` interface with methods: `SendMessageAsync(MessengerMessage, CancellationToken)`, `SendQuestionAsync(AgentQuestion, CancellationToken)`, `ReceiveAsync(CancellationToken)`.
- [ ] Define `IConversationReferenceStore` interface with methods: `SaveAsync`, `GetAsync`, `GetAllAsync`, `GetByUserIdAsync`, `GetByChannelIdAsync`, `MarkInactiveAsync`, `DeleteAsync` for storing platform-specific conversation references.
- [ ] Define `IMessageOutbox` interface with methods: `EnqueueAsync`, `DequeueAsync`, `AcknowledgeAsync`, `DeadLetterAsync` for durable outbound messaging.
- [ ] Define `ConnectorOptions` base class with common config: `RetryCount`, `RetryDelayMs`, `MaxConcurrency`, `DeadLetterThreshold`.

### Dependencies
- phase-messaging-abstractions-and-data-model/stage-core-data-models

### Test Scenarios
- [ ] Scenario: Interface contract completeness — Given the `IMessengerConnector` interface, When inspected via reflection, Then it contains exactly `SendMessageAsync`, `SendQuestionAsync`, and `ReceiveAsync` methods.
- [ ] Scenario: Options defaults — Given a default `ConnectorOptions`, When instantiated, Then `RetryCount` is 3 and `RetryDelayMs` is 1000.

## Stage 1.3: Persistence Abstractions

### Implementation Steps
- [ ] Create project `AgentSwarm.Messaging.Persistence` targeting .NET 8.
- [ ] Define `IAuditLogger` interface with method `LogAsync(AuditEntry, CancellationToken)` where `AuditEntry` includes `Timestamp`, `Actor`, `Action`, `Resource`, `CorrelationId`, `Details`.
- [ ] Define `IMessageStore` interface for persisting inbound/outbound messages with methods: `SaveInboundAsync`, `SaveOutboundAsync`, `GetByCorrelationIdAsync`.
- [ ] Implement `AuditEntry` as an immutable record with required fields for enterprise compliance review.

### Dependencies
- phase-messaging-abstractions-and-data-model/stage-core-data-models

### Test Scenarios
- [ ] Scenario: Audit entry immutability — Given an `AuditEntry` record, When created, Then all properties are init-only and cannot be modified.
- [ ] Scenario: Correlation query contract — Given `IMessageStore`, When inspected, Then `GetByCorrelationIdAsync` accepts a `string correlationId` and returns a list of messages.

# Phase 2: Teams Bot Framework Core

## Dependencies
- phase-messaging-abstractions-and-data-model

## Stage 2.1: ASP.NET Core Bot Host

### Implementation Steps
- [ ] Create project `AgentSwarm.Messaging.Teams` with NuGet references: `Microsoft.Bot.Builder` (4.22+), `Microsoft.Bot.Builder.Integration.AspNet.Core` (4.22+), `Microsoft.Bot.Connector.Teams` (4.22+). Note: `Microsoft.Bot.Builder` already includes the `Microsoft.Bot.Builder.Teams` namespace containing `TeamsActivityHandler`; the separate `Microsoft.Bot.Connector.Teams` package provides Teams-specific types such as `TeamsChannelData`, `TeamInfo`, and `TeamsChannelAccount`.
- [ ] Create `TeamsMessagingOptions` configuration class with properties: `MicrosoftAppId`, `MicrosoftAppPassword`, `MicrosoftAppTenantId`, `AllowedTenantIds` (list), `BotEndpoint`.
- [ ] Implement `Startup`/`Program.cs` registering `CloudAdapter` (from `Microsoft.Bot.Builder.Integration.AspNet.Core`) as the bot adapter with middleware pipeline (Telemetry → TenantFilter → RateLimit), `IBot`, health check endpoints (`/health`, `/ready`), and OpenTelemetry tracing.
- [ ] Create `BotController` with POST endpoint at `/api/messages` that delegates to `CloudAdapter.ProcessAsync`.
- [ ] Register `TeamsMessagingOptions` from `appsettings.json` and environment variables using the Options pattern.

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Bot endpoint responds — Given the bot host is running, When a POST is sent to `/api/messages` with a valid Bot Framework activity, Then it returns HTTP 200.
- [ ] Scenario: Health check — Given the bot host is running, When GET `/health` is called, Then it returns HTTP 200 with status `Healthy`.
- [ ] Scenario: Missing config fails startup — Given `MicrosoftAppId` is not configured, When the host starts, Then it throws `OptionsValidationException`.

## Stage 2.2: Teams Activity Handler

### Implementation Steps
- [ ] Create `TeamsSwarmActivityHandler` extending `TeamsActivityHandler` with DI for `IConversationReferenceStore`, `IAuditLogger`, and `ILogger<TeamsSwarmActivityHandler>`.
- [ ] Override `OnMessageActivityAsync` to parse incoming text commands (`agent ask`, `agent status`, `approve`, `reject`, `escalate`, `pause`, `resume`) and route to a command dispatcher.
- [ ] Override `OnTeamsMembersAddedAsync` to capture and persist the conversation reference on bot installation.
- [ ] Override `OnTeamsMembersRemovedAsync` to mark stored conversation references as inactive (retain for audit) on bot uninstall via `IConversationReferenceStore.MarkInactiveAsync`; do not delete references.
- [ ] Override `OnInstallationUpdateActivityAsync` to handle Teams app install/uninstall lifecycle and log audit entries; on uninstall, mark conversation references inactive rather than removing them.
- [ ] Override `OnAdaptiveCardInvokeAsync` to process Adaptive Card `Action.Submit` invoke activities, extract `ActionId` and optional comment from `Activity.Value`, resolve the originating `AgentQuestion` via `QuestionId`, produce a `HumanDecisionEvent`, and return an `AdaptiveCardInvokeResponse`.
- [ ] Implement `OnTurnAsync` to add `CorrelationId` (from activity or new GUID) to the turn context for distributed tracing.

### Dependencies
- phase-teams-bot-framework-core/stage-aspnet-core-bot-host

### Test Scenarios
- [ ] Scenario: Command routing — Given a message activity with text `agent ask create e2e test scenarios for update service`, When processed by `OnMessageActivityAsync`, Then the command dispatcher receives an `AskCommand` with prompt `create e2e test scenarios for update service`.
- [ ] Scenario: Bot install captures reference — Given a `MembersAdded` activity where the bot is added, When processed, Then the conversation reference is persisted via `IConversationReferenceStore.SaveAsync`.
- [ ] Scenario: Correlation ID propagation — Given an incoming activity without a `CorrelationId` header, When `OnTurnAsync` runs, Then a new GUID-based `CorrelationId` is attached to the turn context.

## Stage 2.3: Teams Messenger Connector

### Implementation Steps
- [ ] Implement `TeamsMessengerConnector : IMessengerConnector` with constructor injection of `CloudAdapter`, `TeamsMessagingOptions`, `IConversationReferenceStore`.
- [ ] Implement `SendMessageAsync` to send a text message to the stored conversation reference using `adapter.ContinueConversationAsync`.
- [ ] Implement `SendQuestionAsync` to render an `AgentQuestion` as a simple text summary and send it proactively via `ContinueConversationAsync`; Adaptive Card rendering is wired in Phase 3 Stage 3.1 when `AdaptiveCardBuilder` becomes available.
- [ ] Implement `ReceiveAsync` using an in-memory channel (`System.Threading.Channels.Channel<MessengerEvent>`) fed by the activity handler.
- [ ] Wire `TeamsMessengerConnector` into DI as `IMessengerConnector` keyed by `"teams"`.

### Dependencies
- phase-teams-bot-framework-core/stage-teams-activity-handler

### Test Scenarios
- [ ] Scenario: Send message to stored reference — Given a valid conversation reference in the store, When `SendMessageAsync` is called, Then `ContinueConversationAsync` is invoked with the correct reference.
- [ ] Scenario: Receive command event — Given the activity handler processes an `agent status` message, When `ReceiveAsync` is awaited, Then a `CommandEvent` with `Command = "status"` is returned.
- [ ] Scenario: Send with missing reference — Given no conversation reference exists for the target user, When `SendMessageAsync` is called, Then an `InvalidOperationException` is thrown with a descriptive message.

## Stage 2.4: Teams App Manifest

### Implementation Steps
- [ ] Create Teams app manifest `manifest.json` conforming to manifest schema v1.16+ with `$schema`, `manifestVersion`, `id` (bot's AAD app ID), `version`, `name`, `description`, and `developer` fields.
- [ ] Configure bot capability in manifest with `botId` referencing the `MicrosoftAppId`, `scopes` set to `["personal", "team"]`, and `supportsFiles: false`.
- [ ] Add message extension (compose extension) stub in manifest with `type: "action"`, `commandId`, `title`, and `context: ["message", "commandBox"]` to support forwarding message context to agents.
- [ ] Add `validDomains` for the bot endpoint and `webApplicationInfo` with `id` (AAD app ID) and `resource` (API URI) for SSO support.
- [ ] Create `manifest.zip` packaging script that bundles `manifest.json`, `color.png` (192×192), and `outline.png` (32×32) icons for sideloading or admin deployment.

### Dependencies
- phase-teams-bot-framework-core/stage-aspnet-core-bot-host

### Test Scenarios
- [ ] Scenario: Manifest schema validation — Given the generated `manifest.json`, When validated against the Teams manifest schema v1.16, Then no schema errors are reported.
- [ ] Scenario: Required scopes present — Given the manifest, When the `bots[0].scopes` field is inspected, Then it contains both `personal` and `team`.
- [ ] Scenario: Message extension stub present — Given the manifest, When the `composeExtensions` field is inspected, Then it contains at least one action command entry.

# Phase 3: Adaptive Cards and Command Processing

## Dependencies
- phase-teams-bot-framework-core

## Stage 3.1: Adaptive Card Templates

### Implementation Steps
- [ ] Create `AdaptiveCardBuilder` service that renders `AgentQuestion` to an Adaptive Card JSON using the AdaptiveCards NuGet package.
- [ ] Implement approval card template with: title, body text, severity indicator, action buttons generated from `HumanAction` list, optional comment input for actions where `RequiresComment = true`.
- [ ] Implement status summary card template showing: agent ID, task ID, current status, last update timestamp, progress percentage.
- [ ] Implement incident escalation card template with: severity level, affected agents, incident summary, escalate/acknowledge buttons.
- [ ] Implement release gate card template with: gate name, release version, environment, gate conditions checklist, approve/reject/defer buttons, and gate status indicator.
- [ ] Create `CardActionMapper` to map Adaptive Card `Action.Submit` data payloads back to `HumanDecisionEvent` records.

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Approval card rendering — Given an `AgentQuestion` with actions `Approve` and `Reject`, When rendered by `AdaptiveCardBuilder`, Then the resulting JSON contains two `Action.Submit` elements with matching labels.
- [ ] Scenario: Comment input conditional — Given a `HumanAction` with `RequiresComment = true`, When the card is rendered, Then an `Input.Text` field is included adjacent to the action button.
- [ ] Scenario: Card action round-trip — Given an Adaptive Card `Action.Submit` payload with `questionId`, `actionValue`, and `comment`, When mapped by `CardActionMapper`, Then the resulting `HumanDecisionEvent` has all fields correctly populated.

## Stage 3.2: Command Dispatcher

### Implementation Steps
- [ ] Create `ICommandHandler` interface with `Task HandleAsync(CommandContext context, CancellationToken ct)` and a `string CommandName` property.
- [ ] Implement `AskCommandHandler` that creates a new agent task from user input text and sends acknowledgement.
- [ ] Implement `StatusCommandHandler` that queries the agent swarm orchestrator and returns a status summary card.
- [ ] Implement `ApproveCommandHandler` and `RejectCommandHandler` that resolve the referenced `AgentQuestion` and emit a `HumanDecisionEvent`.
- [ ] Implement `EscalateCommandHandler`, `PauseCommandHandler`, and `ResumeCommandHandler` for agent lifecycle management.
- [ ] Create `CommandDispatcher` that parses message text, resolves the matching `ICommandHandler` from DI, and dispatches with structured logging and correlation.

### Dependencies
- phase-adaptive-cards-and-command-processing/stage-adaptive-card-templates

### Test Scenarios
- [ ] Scenario: Ask command parsing — Given message text `agent ask create e2e test scenarios for update service`, When parsed by `CommandDispatcher`, Then `AskCommandHandler` is invoked with prompt `create e2e test scenarios for update service`.
- [ ] Scenario: Unknown command — Given message text `agent deploy`, When parsed, Then the dispatcher returns a help message listing available commands.
- [ ] Scenario: Approve via card action — Given an Adaptive Card submit action with `actionValue = "approve"` and `questionId = "q-123"`, When handled, Then a `HumanDecisionEvent` with `ActionValue = "approve"` and `QuestionId = "q-123"` is emitted.

## Stage 3.3: Card Update and Delete Operations

### Implementation Steps
- [ ] Implement `ICardStateStore` interface to persist mapping of `QuestionId` → Teams `ActivityId` (message ID) and `ConversationId`.
- [ ] Implement card update logic in `TeamsMessengerConnector` using `turnContext.UpdateActivityAsync` to replace an existing approval card with a resolved status card.
- [ ] Implement card delete logic using `turnContext.DeleteActivityAsync` for expired or cancelled questions.
- [ ] Add `ActivityId` capture in `OnMessageActivityAsync` reply flow so sent card IDs are stored in `ICardStateStore`.
- [ ] Add `ActivityId` capture for proactively sent Adaptive Cards: in `SendQuestionAsync` and `ProactiveNotifier`, read the `ResourceResponse.Id` returned by `SendActivityAsync`/`ContinueConversationAsync` and persist it to `ICardStateStore` so that proactive approval cards can be updated or deleted later.

### Dependencies
- phase-adaptive-cards-and-command-processing/stage-command-dispatcher

### Test Scenarios
- [ ] Scenario: Card update after approval — Given an approval card was sent with `ActivityId = "act-1"`, When the user approves, Then the card is updated to show "Approved by {user}" via `UpdateActivityAsync`.
- [ ] Scenario: Card delete on expiry — Given a question with `ExpiresAt` in the past, When the expiry processor runs, Then the card is deleted via `DeleteActivityAsync`.
- [ ] Scenario: State store persistence — Given a card is sent and its `ActivityId` is stored, When the service restarts and the store is queried, Then the `ActivityId` is still retrievable.

# Phase 4: Proactive Messaging and Conversation Reference Management

## Dependencies
- phase-teams-bot-framework-core

## Stage 4.1: Conversation Reference Store Implementation

### Implementation Steps
- [ ] Implement `SqlConversationReferenceStore : IConversationReferenceStore` using Entity Framework Core with a `ConversationReferences` table containing: `Id`, `UserId`, `TenantId`, `ChannelId`, `ServiceUrl`, `ConversationJson` (serialized `ConversationReference`), `CreatedAt`, `UpdatedAt`.
- [ ] Create EF Core migration for the `ConversationReferences` table with indexes on `UserId` and `TenantId`.
- [ ] Implement `GetByUserIdAsync` and `GetByChannelIdAsync` query methods for targeted proactive messaging.
- [ ] Implement reference update logic: if a newer reference arrives for the same user/channel, update in place rather than insert duplicate.

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Save and retrieve — Given a conversation reference for user `user-1` in tenant `tenant-1`, When saved and then retrieved by user ID, Then the deserialized `ConversationReference` matches the original.
- [ ] Scenario: Upsert on duplicate — Given a reference already exists for `user-1`, When a new reference is saved for the same user, Then only one record exists with the updated `ServiceUrl`.
- [ ] Scenario: Multi-tenant isolation — Given references for `tenant-1` and `tenant-2`, When queried for `tenant-1`, Then only `tenant-1` references are returned.

## Stage 4.2: Proactive Notification Service

### Implementation Steps
- [ ] Create `IProactiveNotifier` interface with `SendProactiveAsync(string userId, MessengerMessage message, CancellationToken ct)` and `SendProactiveQuestionAsync(string userId, AgentQuestion question, CancellationToken ct)`.
- [ ] Implement `TeamsProactiveNotifier : IProactiveNotifier` using `CloudAdapter.ContinueConversationAsync` with stored conversation references.
- [ ] Implement conversation reference rehydration: deserialize stored `ConversationReference` JSON and invoke `ContinueConversationAsync` with the app credentials.
- [ ] Add proactive message delivery via direct `ContinueConversationAsync` calls; durable outbox queuing is layered on top in Phase 6 Stage 6.1 when `OutboxRetryEngine` becomes available.
- [ ] Implement notification routing: determine whether to send to personal chat or team channel based on message priority and user preferences.

### Dependencies
- phase-proactive-messaging-and-conversation-reference-management/stage-conversation-reference-store-implementation
- phase-adaptive-cards-and-command-processing/stage-adaptive-card-templates

### Test Scenarios
- [ ] Scenario: Proactive question delivery — Given a stored conversation reference for `user-1`, When `SendProactiveQuestionAsync` is called with an `AgentQuestion`, Then an Adaptive Card is delivered to the user's personal chat using `AdaptiveCardBuilder` to render the card.
- [ ] Scenario: Reference not found — Given no conversation reference exists for `user-2`, When `SendProactiveQuestionAsync` is called, Then the notification is dead-lettered with reason `NoConversationReference`.
- [ ] Scenario: Direct delivery latency — Given a stored conversation reference and a rendered Adaptive Card, When `SendProactiveQuestionAsync` delivers the card directly via `ContinueConversationAsync`, Then delivery completes within P95 < 3 seconds.

# Phase 5: Security, Identity, and Compliance

## Dependencies
- phase-teams-bot-framework-core

## Stage 5.1: Tenant and Identity Validation

### Implementation Steps
- [ ] Create `TenantValidationMiddleware` for the Bot Framework pipeline that rejects activities from tenants not in the `AllowedTenantIds` configuration list.
- [ ] Implement `IUserAuthorizationService` with method `AuthorizeAsync(string tenantId, string userId, string command)` that checks RBAC permissions.
- [ ] Implement `IIdentityResolver` with method `ResolveAsync(string aadObjectId)` that maps the Teams `Activity.From.AadObjectId` (Entra AAD object ID) to an internal user identity record; reject unmapped users with HTTP 200 + Adaptive Card explaining access denial and how to request access (per the two-tier rejection model in `tech-spec.md` §4.2).
- [ ] Create `RbacOptions` configuration class mapping Teams user roles to allowed commands (e.g., `Operator` → all commands, `Approver` → `approve`/`reject`/`agent status`, `Viewer` → `agent status` only).
- [ ] Integrate Entra ID token validation by configuring `BotFrameworkAuthentication` with `AllowedCallers` and tenant restrictions.
- [ ] Add rejection response: unauthorized users receive a polite card explaining they lack access and how to request it.

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Unauthorized tenant rejected — Given `AllowedTenantIds` contains `tenant-A`, When an activity arrives from `tenant-B`, Then HTTP 403 is returned and the activity is not processed.
- [ ] Scenario: RBAC enforcement — Given user `viewer-1` has role `Viewer`, When they send `approve`, Then a rejection message is returned explaining insufficient permissions.
- [ ] Scenario: Authorized user succeeds — Given user `ops-1` has role `Operator`, When they send `agent ask plan migration`, Then the command is dispatched successfully.

## Stage 5.2: Audit Logging Implementation

### Implementation Steps
- [ ] Implement `SqlAuditLogger : IAuditLogger` writing to an append-only `AuditLog` table with columns: `Id`, `Timestamp`, `Actor`, `ActorTenantId`, `Action`, `Resource`, `CorrelationId`, `Details` (JSON), `Checksum` (SHA-256 of row content for tamper detection).
- [ ] Create EF Core migration for the `AuditLog` table with a clustered index on `Timestamp` and non-clustered index on `CorrelationId`.
- [ ] Instrument all command handlers to emit audit entries: log the command, user identity, tenant, timestamp, and outcome.
- [ ] Instrument proactive message sends to emit audit entries: log the target user, message type, correlation ID, and delivery status.
- [ ] Implement `AuditLogQueryService` with methods for compliance review: `GetByDateRangeAsync`, `GetByActorAsync`, `GetByCorrelationIdAsync`.

### Dependencies
- phase-security-identity-and-compliance/stage-tenant-and-identity-validation

### Test Scenarios
- [ ] Scenario: Command audit trail — Given user `ops-1` sends `approve` for question `q-1`, When the command completes, Then an `AuditLog` entry exists with `Action = "approve"`, `Actor = "ops-1"`, and `Resource = "q-1"`.
- [ ] Scenario: Immutability check — Given an audit entry is written, When an UPDATE is attempted on the `AuditLog` table via the query service, Then the operation is rejected or not exposed by the API.
- [ ] Scenario: Checksum integrity — Given an audit entry with known content, When the checksum is recomputed from the stored fields, Then it matches the stored `Checksum` value.

# Phase 6: Reliability and Performance

## Dependencies
- phase-adaptive-cards-and-command-processing
- phase-proactive-messaging-and-conversation-reference-management

## Stage 6.1: Outbox Pattern and Retry Engine

### Implementation Steps
- [ ] Implement `SqlMessageOutbox : IMessageOutbox` with an `OutboxMessages` table containing: `Id`, `Payload` (JSON), `DestinationType` (personal/channel), `DestinationId`, `Status` (Pending/Processing/Sent/Failed/DeadLettered), `RetryCount`, `NextRetryAt`, `CreatedAt`, `LastError`.
- [ ] Create EF Core migration for the `OutboxMessages` table with index on `Status` and `NextRetryAt`.
- [ ] Implement `OutboxRetryEngine` as `BackgroundService` that polls for pending messages, attempts delivery, and updates status.
- [ ] Implement exponential backoff retry: base delay 2s, multiplier 2×, computed delays of 2s, 4s, 8s, 16s with ±25% jitter, max delay cap 60s, up to 4 retries after initial attempt (5 total attempts) per the canonical retry policy in `tech-spec.md` §4.4. Dead-letter after final failed attempt.
- [ ] Implement `Retry-After` header handling: when the Bot Framework returns HTTP 429, parse the `Retry-After` response header and use its value as the minimum delay before the next retry attempt, overriding the computed backoff if `Retry-After` is longer.
- [ ] Implement token-bucket rate limiter in the outbound pipeline to proactively avoid Bot Framework rate limits (default: 50 msgs/sec per bot, configurable).
- [ ] Implement dead-letter handling: messages exceeding retry threshold are moved to `DeadLettered` status with the last error recorded.

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Successful delivery — Given a message is enqueued with status `Pending`, When the outbox processor runs and delivery succeeds, Then the status is updated to `Sent`.
- [ ] Scenario: Transient failure retry — Given delivery fails with a transient `HttpRequestException`, When the outbox processor runs, Then `RetryCount` is incremented and `NextRetryAt` is set with exponential backoff.
- [ ] Scenario: Dead-letter after max retries — Given a message has failed 5 times, When the outbox processor runs, Then the status is set to `DeadLettered` and `LastError` contains the failure reason.

## Stage 6.2: Duplicate Suppression and Idempotency

### Implementation Steps
- [ ] Create `IdempotencyStore` with a `ProcessedMessages` table containing: `MessageId` (unique), `ProcessedAt`, `ExpiresAt`.
- [ ] Implement `IdempotencyMiddleware` for the activity handler pipeline that checks `activity.Id` against `ProcessedMessages` before processing.
- [ ] Add cleanup background job to purge expired entries from `ProcessedMessages` (default TTL: 24 hours).
- [ ] Implement outbound deduplication: `SendMessageAsync` checks if a message with the same `CorrelationId` + `DestinationId` was already sent within a configurable window.

### Dependencies
- phase-reliability-and-performance/stage-outbox-pattern-and-retry-engine

### Test Scenarios
- [ ] Scenario: Duplicate activity suppressed — Given an activity with `Id = "act-1"` was already processed, When the same activity arrives again, Then it is skipped without re-executing the command.
- [ ] Scenario: Non-duplicate processed — Given activity `Id = "act-2"` has not been seen before, When it arrives, Then it is processed normally and recorded in `ProcessedMessages`.
- [ ] Scenario: Expired entries cleaned — Given a processed message entry older than 24 hours, When the cleanup job runs, Then the entry is removed from `ProcessedMessages`.

## Stage 6.3: Performance Monitoring and Health Checks

### Implementation Steps
- [ ] Add OpenTelemetry instrumentation to `TeamsMessengerConnector`: trace spans for `SendMessageAsync`, `SendQuestionAsync`, and `ReceiveAsync` with attributes `correlationId`, `messageType`, `destinationType`.
- [ ] Add custom metrics: `teams.messages.sent` (counter), `teams.messages.received` (counter), `teams.card.delivery.duration_ms` (histogram), `teams.outbox.queue_depth` (gauge).
- [ ] Implement health check for Bot Framework connectivity: verify token endpoint reachability and adapter initialization.
- [ ] Implement health check for conversation reference store: verify database connectivity and reference count.
- [ ] Add structured logging with Serilog enrichers for `CorrelationId`, `TenantId`, `UserId` on every log entry.

### Dependencies
- phase-reliability-and-performance/stage-duplicate-suppression-and-idempotency

### Test Scenarios
- [ ] Scenario: Trace span emitted — Given a message is sent via `SendMessageAsync`, When the operation completes, Then an OpenTelemetry span named `TeamsConnector.SendMessage` is recorded with `correlationId` attribute.
- [ ] Scenario: Delivery histogram — Given 100 messages are sent, When the metrics are queried, Then `teams.card.delivery.duration_ms` has 100 observations with P95 below 3000ms.
- [ ] Scenario: Health check degraded — Given the database is unreachable, When `/health` is called, Then it returns `Degraded` with detail `ConversationReferenceStore: Unhealthy`.

# Phase 7: Integration Testing and End-to-End Validation

## Dependencies
- phase-security-identity-and-compliance
- phase-reliability-and-performance

## Stage 7.1: Unit and Integration Test Suite

### Implementation Steps
- [ ] Create project `AgentSwarm.Messaging.Teams.Tests` with references to `xUnit`, `Moq`, `FluentAssertions`, `Microsoft.Bot.Builder.Testing`.
- [ ] Write unit tests for `TeamsSwarmActivityHandler`: test each command routing path with mocked dependencies.
- [ ] Write unit tests for `AdaptiveCardBuilder`: verify card JSON structure for approval, status, and escalation templates.
- [ ] Write unit tests for `TenantValidationMiddleware`: verify allowed and blocked tenant scenarios.
- [ ] Write integration tests for `SqlConversationReferenceStore` using SQLite in-memory provider.
- [ ] Write integration tests for `SqlMessageOutbox` verifying enqueue, retry, and dead-letter flows.

### Dependencies
- _none — start stage_

### Test Scenarios
- [ ] Scenario: Full command suite — Given each of the 7 commands (`agent ask`, `agent status`, `approve`, `reject`, `escalate`, `pause`, `resume`), When sent as message activities to the handler, Then each routes to its correct handler and returns an appropriate response.
- [ ] Scenario: Card builder output — Given an `AgentQuestion` with 3 actions, When rendered, Then the Adaptive Card JSON validates against the Adaptive Card schema v1.5.
- [ ] Scenario: Store round-trip integration — Given a conversation reference is saved to SQLite, When retrieved after a new `DbContext` is created, Then all fields including serialized JSON match the original.

## Stage 7.2: End-to-End Acceptance Tests

### Implementation Steps
- [ ] Create E2E test harness using Bot Framework `TestAdapter` to simulate Teams channel activities without a live Teams environment.
- [ ] Implement E2E test: user sends `agent ask create e2e test scenarios for update service` → bot acknowledges → agent sends proactive approval question → user approves via card action → card is updated to show approved status.
- [ ] Implement E2E test: bot rejects message from unauthorized tenant with HTTP 403 and no bot response/card (tenant-level rejection per the two-tier model in `tech-spec.md` §4.2).
- [ ] Implement E2E test: service restart → conversation references are reloaded → proactive message succeeds using rehydrated reference.
- [ ] Implement E2E test: duplicate activity delivery is suppressed and command executes only once.
- [ ] Implement E2E test: outbox message fails 5 times → dead-lettered → audit entry recorded.

### Dependencies
- phase-integration-testing-and-end-to-end-validation/stage-unit-and-integration-test-suite

### Test Scenarios
- [ ] Scenario: Happy path E2E — Given a user in an authorized tenant, When they send `agent ask create e2e test scenarios for update service`, Then they receive an acknowledgement card, Then a proactive approval card arrives, Then approving updates the card to show resolution.
- [ ] Scenario: Unauthorized tenant E2E — Given a user in an unauthorized tenant, When they send any command, Then the request is rejected with HTTP 403 at the middleware layer (no bot response or card is sent) and no command is processed.
- [ ] Scenario: Restart resilience E2E — Given conversation references are persisted, When the bot service restarts, Then proactive notifications using stored references are delivered successfully.
