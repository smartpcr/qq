---
title: "Slack Messenger Support"
storyId: "qq-SLACK-MESSENGER-SUPP"
---

# Phase 1: Solution Scaffolding

## Dependencies
- _none -- start phase_

## Stage 1.1: Project Structure and Build Configuration

### Implementation Steps
- [ ] Create `AgentSwarm.Messaging.sln` solution file at the repository root
- [ ] Create `src/AgentSwarm.Messaging.Abstractions/AgentSwarm.Messaging.Abstractions.csproj` class library targeting `net8.0` with nullable enabled and implicit usings
- [ ] Create `src/AgentSwarm.Messaging.Core/AgentSwarm.Messaging.Core.csproj` class library targeting `net8.0` with project reference to Abstractions
- [ ] Create `src/AgentSwarm.Messaging.Persistence/AgentSwarm.Messaging.Persistence.csproj` class library targeting `net8.0` with project references to Abstractions and Core; add `Microsoft.EntityFrameworkCore` and `Microsoft.EntityFrameworkCore.Relational` NuGet packages
- [ ] Create `src/AgentSwarm.Messaging.Slack/AgentSwarm.Messaging.Slack.csproj` class library targeting `net8.0` with project references to Abstractions, Core, and Persistence; add `SlackNet` and `SlackNet.AspNetCore` NuGet packages
- [ ] Create `src/AgentSwarm.Messaging.Worker/AgentSwarm.Messaging.Worker.csproj` ASP.NET Core worker service targeting `net8.0` with project references to Slack and Persistence
- [ ] Create `tests/AgentSwarm.Messaging.Slack.Tests/AgentSwarm.Messaging.Slack.Tests.csproj` xUnit test project targeting `net8.0` with project references to Slack; add `xunit`, `Microsoft.NET.Test.Sdk`, `Moq`, `FluentAssertions` NuGet packages
- [ ] Add `Directory.Build.props` at repo root with shared build settings: `TreatWarningsAsErrors`, `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`
- [ ] Verify solution builds cleanly with `dotnet build AgentSwarm.Messaging.sln`

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Solution builds -- Given all projects are created with correct references, When `dotnet build` is run on the solution, Then the build succeeds with zero errors
- [ ] Scenario: Test runner initializes -- Given the test project references the Slack project, When `dotnet test --list-tests` is run, Then the test runner initializes without error

## Stage 1.2: Prerequisite Abstraction Compile Stubs

### Implementation Steps
- [ ] Add compile-target stub for `IMessengerConnector` interface in the Abstractions project with `SendMessageAsync`, `SendQuestionAsync`, and `ReceiveAsync` method signatures (canonical definitions are owned by the upstream Abstractions story; these stubs unblock Slack project compilation)
- [ ] Add compile-target stubs for shared data records in the Abstractions project: `AgentQuestion`, `HumanAction`, `HumanDecisionEvent`, `MessengerMessage`, `MessengerEvent` base type, and `MessageType` enum, matching the field contracts in architecture.md section 3.6
- [ ] Add a `README.md` to the Abstractions project documenting that all types are compile stubs to be replaced by the canonical Abstractions story deliverables
- [ ] Verify that the Slack project compiles against the stub types with `dotnet build`

### Dependencies
- phase-solution-scaffolding/stage-project-structure-and-build-configuration

### Test Scenarios
- [ ] Scenario: Slack project compiles against stubs -- Given the Abstractions project contains stub types, When `dotnet build` runs for the Slack project, Then the build succeeds with zero errors
- [ ] Scenario: Stub types match upstream contract -- Given stub records for `AgentQuestion` and `HumanDecisionEvent`, When their public property names are compared to architecture.md section 3.6 field list, Then all required fields are present

## Stage 1.3: Slack-Internal Queue and Retry Contracts

### Implementation Steps
- [ ] Define `ISlackInboundQueue` internal interface in the Slack project with `EnqueueAsync(SlackInboundEnvelope)` and `DequeueAsync(CancellationToken)` methods for buffering validated inbound envelopes
- [ ] Define `ISlackOutboundQueue` internal interface in the Slack project with `EnqueueAsync(SlackOutboundEnvelope)` and `DequeueAsync(CancellationToken)` methods for buffering outbound Slack API calls
- [ ] Define `ISlackDeadLetterQueue` internal interface with `EnqueueAsync` and `InspectAsync` for poison messages that exceed retry limits
- [ ] Define `ISlackRetryPolicy` internal interface with `ShouldRetry(int attemptNumber, Exception exception)` and `GetDelay(int attemptNumber)` methods
- [ ] Implement `ChannelBasedSlackQueue<T>` backed by `System.Threading.Channels.Channel<T>` as an in-process queue for development and testing (production swaps in durable queue implementations from the upstream Core project when available)
- [ ] Add XML doc comments on each interface noting that production durable implementations will be provided by `AgentSwarm.Messaging.Core`

### Dependencies
- phase-solution-scaffolding/stage-prerequisite-abstraction-compile-stubs

### Test Scenarios
- [ ] Scenario: Channel queue enqueue-dequeue -- Given a `ChannelBasedSlackQueue<SlackInboundEnvelope>`, When 3 envelopes are enqueued and dequeued, Then envelopes are returned in FIFO order
- [ ] Scenario: Queue respects cancellation -- Given a `ChannelBasedSlackQueue` with no items, When `DequeueAsync` is called with a cancelled token, Then an `OperationCanceledException` is thrown


# Phase 2: Persistence and Data Model

## Dependencies
- phase-solution-scaffolding

## Stage 2.1: Slack Entity Model Definitions

### Implementation Steps
- [ ] Define `SlackWorkspaceConfig` entity class in the Slack project with fields per architecture.md section 3.1: `TeamId` (PK), `WorkspaceName`, `BotTokenSecretRef`, `SigningSecretRef`, `AppLevelTokenRef` (nullable), `DefaultChannelId`, `FallbackChannelId` (nullable), `AllowedChannelIds` (string array), `AllowedUserGroupIds` (string array), `Enabled`, `CreatedAt`, `UpdatedAt`
- [ ] Define `SlackThreadMapping` entity class with fields per architecture.md section 3.2: `TaskId` (PK), `TeamId`, `ChannelId`, `ThreadTs`, `CorrelationId`, `AgentId`, `CreatedAt`, `LastMessageAt`; add unique constraint on `(TeamId, ChannelId, ThreadTs)`
- [ ] Define `SlackInboundRequestRecord` entity class with fields per architecture.md section 3.3: `IdempotencyKey` (PK), `SourceType`, `TeamId`, `ChannelId` (nullable), `UserId`, `RawPayloadHash`, `ProcessingStatus`, `FirstSeenAt`, `CompletedAt` (nullable)
- [ ] Define `SlackAuditEntry` entity class with fields per architecture.md section 3.5: `Id` (ULID PK), `CorrelationId`, `AgentId` (nullable), `TaskId` (nullable), `ConversationId` (nullable), `Direction`, `RequestType`, `TeamId`, `ChannelId` (nullable), `ThreadTs` (nullable), `MessageTs` (nullable), `UserId` (nullable), `CommandText` (nullable), `ResponsePayload` (nullable), `Outcome`, `ErrorDetail` (nullable), `Timestamp`
- [ ] Define `SlackConnectorOptions` POCO class with `MaxWorkspaces` (default 15), retry settings, rate-limit tiers, and membership cache TTL
- [ ] Register `SlackConnectorOptions` binding from `IConfiguration` section `"Slack"` via `services.Configure<SlackConnectorOptions>()`

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Entity field completeness -- Given `SlackAuditEntry`, When its public properties are reflected, Then all fields from architecture.md section 3.5 are present (Id, CorrelationId, AgentId, TaskId, ConversationId, Direction, RequestType, TeamId, ChannelId, ThreadTs, MessageTs, UserId, CommandText, ResponsePayload, Outcome, ErrorDetail, Timestamp)
- [ ] Scenario: Options binding -- Given configuration JSON with `Slack:MaxWorkspaces = 10`, When `SlackConnectorOptions` is resolved from DI, Then `MaxWorkspaces` equals 10

## Stage 2.2: Slack Entity Type Configurations and Indexing

### Implementation Steps
- [ ] Create EF Core `IEntityTypeConfiguration<SlackWorkspaceConfig>` class defining column types, primary key on `TeamId`, and value conversion for `AllowedChannelIds` and `AllowedUserGroupIds` string arrays
- [ ] Create EF Core `IEntityTypeConfiguration<SlackThreadMapping>` class with primary key on `TaskId` and unique constraint on `(TeamId, ChannelId, ThreadTs)`
- [ ] Create EF Core `IEntityTypeConfiguration<SlackInboundRequestRecord>` class with primary key on `IdempotencyKey` and index on `FirstSeenAt` for retention cleanup
- [ ] Create EF Core `IEntityTypeConfiguration<SlackAuditEntry>` class with primary key on `Id` and indexes on: `CorrelationId`, `TaskId`, `AgentId`, composite `(TeamId, ChannelId)`, `UserId`, `Timestamp`
- [ ] Create `SlackTestDbContext` test-only `DbContext` subclass in the test project that registers all four Slack entity configurations for isolated schema validation (production uses the upstream `MessagingDbContext` from the Persistence project)
- [ ] Add `Microsoft.EntityFrameworkCore.Sqlite` NuGet package to the test project for integration tests

### Dependencies
- phase-persistence-and-data-model/stage-slack-entity-model-definitions

### Test Scenarios
- [ ] Scenario: Entity configuration applies indexes -- Given `SlackTestDbContext` with entity configurations, When the model is built, Then `SlackAuditEntry` has indexes on `CorrelationId`, `TaskId`, `AgentId`, `UserId`, and `Timestamp`
- [ ] Scenario: Thread mapping unique constraint -- Given a `SlackThreadMapping` with `(TeamId, ChannelId, ThreadTs)` already persisted, When a duplicate `(TeamId, ChannelId, ThreadTs)` is inserted, Then a `DbUpdateException` is thrown
- [ ] Scenario: Workspace config array conversion -- Given a `SlackWorkspaceConfig` with `AllowedChannelIds = ["C1", "C2"]`, When persisted and loaded via `SlackTestDbContext`, Then the array round-trips correctly

## Stage 2.3: Slack Schema Integration Tests

### Implementation Steps
- [ ] Create EF Core migration contribution `AddSlackEntities` that registers all four Slack entity configurations with the upstream `MessagingDbContext` (the migration is added to the shared migration set in the Persistence project when available)
- [ ] Add a schema verification integration test using `SlackTestDbContext` with SQLite in-memory that validates all four tables (`slack_workspace_config`, `slack_thread_mapping`, `slack_inbound_request_record`, `slack_audit_entry`) are created correctly
- [ ] Add a seed data helper method `SlackDbSeeder.SeedTestWorkspace()` that inserts a sample `SlackWorkspaceConfig` for use in downstream integration tests
- [ ] Verify all Slack entity type configurations are auto-discovered by `SlackTestDbContext` via `ApplyConfigurationsFromAssembly`

### Dependencies
- phase-persistence-and-data-model/stage-slack-entity-type-configurations-and-indexing

### Test Scenarios
- [ ] Scenario: Schema creates all tables -- Given a clean SQLite in-memory database, When `SlackTestDbContext.Database.EnsureCreated()` is called, Then tables `slack_workspace_config`, `slack_thread_mapping`, `slack_inbound_request_record`, and `slack_audit_entry` all exist
- [ ] Scenario: Seed workspace loads -- Given a freshly created database, When `SlackDbSeeder.SeedTestWorkspace()` is called, Then a `SlackWorkspaceConfig` row is retrievable by its `TeamId`


# Phase 3: Security Pipeline

## Dependencies
- phase-persistence-and-data-model

## Stage 3.1: Request Signature Validation

### Implementation Steps
- [ ] Create `SlackSignatureValidator` as ASP.NET Core middleware in the Slack project that reads `X-Slack-Signature` and `X-Slack-Request-Timestamp` headers from inbound HTTP requests
- [ ] Implement HMAC SHA-256 verification: compute `v0:{timestamp}:{body}` hash with the workspace signing secret and compare to the provided signature
- [ ] Reject requests with missing or invalid signatures by returning HTTP 401 and logging an audit entry with `outcome = rejected_signature`
- [ ] Reject requests where the timestamp is older than 5 minutes (clock-skew tolerance) to prevent replay attacks
- [ ] Resolve the signing secret at runtime from the secret provider via `SlackWorkspaceConfig.SigningSecretRef` (use `ISecretProvider` interface defined in this step)
- [ ] Define `ISecretProvider` interface in Core with `GetSecretAsync(string secretRef, CancellationToken)` returning `string`; implement `InMemorySecretProvider` stub for testing

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Valid signature passes -- Given a request with a correctly computed HMAC SHA-256 signature and timestamp within 5 minutes, When `SlackSignatureValidator` processes the request, Then the request proceeds to the next middleware
- [ ] Scenario: Invalid signature rejected -- Given a request with a tampered signature, When `SlackSignatureValidator` processes the request, Then HTTP 401 is returned
- [ ] Scenario: Stale timestamp rejected -- Given a request with a timestamp older than 5 minutes, When `SlackSignatureValidator` processes the request, Then HTTP 401 is returned

## Stage 3.2: Authorization Filter and Membership Resolution

### Implementation Steps
- [ ] Create `SlackAuthorizationFilter` implementing ASP.NET Core `IAsyncActionFilter` that enforces the three-layer ACL: workspace (`team_id`), channel (`channel_id`), and user group membership
- [ ] Implement workspace check: verify `team_id` from the Slack payload matches a registered `SlackWorkspaceConfig` with `Enabled = true`
- [ ] Implement channel check: verify `channel_id` is in the workspace's `AllowedChannelIds` list
- [ ] Implement user-group check via `SlackMembershipResolver`: verify the requesting user belongs to at least one group in `AllowedUserGroupIds`
- [ ] Create `SlackMembershipResolver` class that calls Slack `usergroups.users.list` via SlackNet, caching results with configurable TTL (default 5 minutes per `SlackConnectorOptions`)
- [ ] Return an ephemeral Slack error message for rejected requests (Slack endpoints must return HTTP 200; rejection is communicated in the response body as an ephemeral message); log audit entry with `outcome = rejected_auth` including `team_id`, `channel_id`, and `user_id`

### Dependencies
- phase-security-pipeline/stage-request-signature-validation

### Test Scenarios
- [ ] Scenario: Authorized request passes -- Given a request from an allowed workspace, allowed channel, and a user in an allowed group, When `SlackAuthorizationFilter` runs, Then the request proceeds
- [ ] Scenario: Unknown workspace rejected -- Given a request with a `team_id` not in `SlackWorkspaceConfig`, When `SlackAuthorizationFilter` runs, Then an ephemeral error message is returned with audit entry `outcome = rejected_auth`
- [ ] Scenario: Disallowed channel rejected -- Given a valid workspace but `channel_id` not in `AllowedChannelIds`, When `SlackAuthorizationFilter` runs, Then an ephemeral error message is returned
- [ ] Scenario: Membership cache respects TTL -- Given `SlackMembershipResolver` with a 5-minute TTL, When the same user group is queried twice within TTL, Then the Slack API is called only once

## Stage 3.3: Secret Provider Integration

### Implementation Steps
- [ ] Implement `EnvironmentSecretProvider` that resolves secrets from environment variables (for development and CI)
- [ ] Define `SecretProviderOptions` POCO with `ProviderType` enum (`Environment`, `KeyVault`, `Kubernetes`) for configuration-driven selection
- [ ] Implement `CompositeSecretProvider` that selects the concrete provider based on `SecretProviderOptions.ProviderType`
- [ ] Register `ISecretProvider` in DI via `ServiceCollectionExtensions.AddSecretProvider(IConfiguration)` that reads `SecretProvider` config section
- [ ] Implement secret caching in `CompositeSecretProvider` with configurable refresh interval (default 1 hour per architecture.md section 7.3)
- [ ] Ensure secrets are never logged: apply `[LogPropertyIgnore]` attribute or equivalent scrubbing to secret fields

### Dependencies
- phase-security-pipeline/stage-request-signature-validation

### Test Scenarios
- [ ] Scenario: Environment secret resolution -- Given `EnvironmentSecretProvider` and an environment variable `SLACK_BOT_TOKEN=xoxb-test`, When `GetSecretAsync("env://SLACK_BOT_TOKEN")` is called, Then the returned value is `xoxb-test`
- [ ] Scenario: Secret caching avoids repeated lookups -- Given `CompositeSecretProvider` with 1-hour refresh, When the same secret ref is resolved twice within 1 hour, Then the underlying provider is called only once
- [ ] Scenario: Missing secret throws descriptive error -- Given a secret ref that does not resolve, When `GetSecretAsync` is called, Then a `SecretNotFoundException` is thrown with the ref in the message


# Phase 4: Inbound Transport

## Dependencies
- phase-security-pipeline

## Stage 4.1: Events API HTTP Endpoints

### Implementation Steps
- [ ] Create `SlackEventsController` ASP.NET Core controller in the Slack project with route `POST /api/slack/events` that handles Events API callbacks
- [ ] Implement `url_verification` challenge handshake: detect `type = url_verification` and return `{ "challenge": "<token>" }` with HTTP 200
- [ ] Create `SlackCommandsController` with route `POST /api/slack/commands` that receives slash command payloads and immediately returns HTTP 200 (async processing deferred)
- [ ] Create `SlackInteractionsController` with route `POST /api/slack/interactions` that receives interactive payloads (button clicks, modal submissions) and immediately returns HTTP 200
- [ ] Define `SlackInboundEnvelope` internal record to normalize all inbound payload types (event, command, interaction) into a common envelope carrying `IdempotencyKey`, `SourceType`, `TeamId`, `ChannelId`, `UserId`, `RawPayload`, `TriggerId` (nullable), and `ReceivedAt`
- [ ] Wire `SlackSignatureValidator` middleware to all three Slack endpoint routes
- [ ] Enqueue normalized `SlackInboundEnvelope` to `ISlackInboundQueue` after ACK for async processing
- [ ] Implement modal fast-path detection in `SlackCommandsController`: for sub-commands `review` and `escalate`, run auth + idempotency + `views.open` synchronously before returning HTTP 200 (per architecture.md section 2.2.2 and tech-spec section 5.2)

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: URL verification handshake -- Given a POST to `/api/slack/events` with `type = url_verification` and `challenge = abc123`, When the controller processes it, Then the response body is `{ "challenge": "abc123" }` with HTTP 200
- [ ] Scenario: Slash command ACK within deadline -- Given a valid `/agent ask` slash command payload, When posted to `/api/slack/commands`, Then HTTP 200 is returned and the envelope is enqueued for async processing
- [ ] Scenario: Interactive payload ACK -- Given a valid Block Kit button click payload, When posted to `/api/slack/interactions`, Then HTTP 200 is returned within the 3-second deadline

## Stage 4.2: Socket Mode WebSocket Receiver

### Implementation Steps
- [ ] Create `SlackSocketModeReceiver` implementing `ISlackInboundTransport` with `StartAsync` and `StopAsync` lifecycle methods
- [ ] Establish WebSocket connection to Slack using the app-level token resolved from `SlackWorkspaceConfig.AppLevelTokenRef` via `ISecretProvider`
- [ ] Implement envelope acknowledgment: send ACK response over the WebSocket within 5 seconds of receiving each event envelope
- [ ] Normalize received Socket Mode payloads into `SlackInboundEnvelope` and enqueue to `ISlackInboundQueue`
- [ ] Implement reconnection with exponential backoff (initial 1s, max 30s) and jitter on WebSocket disconnection
- [ ] Implement graceful shutdown: on `StopAsync`, close the WebSocket connection and drain pending envelopes
- [ ] Select transport per workspace based on `SlackWorkspaceConfig.AppLevelTokenRef`: present = Socket Mode, absent = Events API (per architecture.md section 4.2 and tech-spec section 2.1)

### Dependencies
- phase-inbound-transport/stage-events-api-http-endpoints

### Test Scenarios
- [ ] Scenario: Socket Mode connects and ACKs -- Given a mock WebSocket server sending an event envelope, When `SlackSocketModeReceiver` receives it, Then an ACK is sent within 5 seconds and the envelope is enqueued
- [ ] Scenario: Reconnection on disconnect -- Given an active Socket Mode connection, When the WebSocket disconnects unexpectedly, Then the receiver reconnects with exponential backoff
- [ ] Scenario: Transport selection by config -- Given a workspace with `AppLevelTokenRef = null`, When the connector starts, Then Events API transport is used for that workspace

## Stage 4.3: Inbound Ingestor and Deduplication

### Implementation Steps
- [ ] Create `SlackInboundIngestor` as a `BackgroundService` that continuously drains `ISlackInboundQueue` and dispatches envelopes through the processing pipeline
- [ ] Create `SlackIdempotencyGuard` implementing `ISlackIdempotencyGuard` with `TryAcquireAsync`, `MarkCompletedAsync`, and `MarkFailedAsync` backed by `SlackInboundRequestRecord` persistence
- [ ] Implement idempotency key derivation per architecture.md section 3.4: `event:{event_id}` for Events API, `cmd:{team_id}:{user_id}:{command}:{trigger_id}` for slash commands, `interact:{team_id}:{user_id}:{action_id or view_id}:{trigger_id}` for interactions
- [ ] Implement processing pipeline order: authorization check -> idempotency check -> dispatch to appropriate handler (`SlackCommandHandler`, `SlackAppMentionHandler`, or `SlackInteractionHandler`); authorization runs first so unauthorized requests are rejected before acquiring idempotency records (per architecture.md sections 574-575)
- [ ] Route `SlackInboundEnvelope` by `SourceType`: `command` to `SlackCommandHandler`, `event` (with `app_mention` subtype) to `SlackAppMentionHandler`, `interaction` to `SlackInteractionHandler`
- [ ] On processing failure, apply retry policy via `ISlackRetryPolicy`; after max retries, move envelope to `ISlackDeadLetterQueue`
- [ ] Log duplicate events to audit with `outcome = duplicate`

### Dependencies
- phase-inbound-transport/stage-events-api-http-endpoints

### Test Scenarios
- [ ] Scenario: First event processes normally -- Given a new `SlackInboundEnvelope` with a unique idempotency key, When the ingestor processes it, Then the envelope is dispatched to the correct handler and the idempotency record shows `processing_status = completed`
- [ ] Scenario: Duplicate event is dropped -- Given an envelope with an idempotency key already marked `completed`, When the ingestor processes it, Then the envelope is silently dropped and audit records `outcome = duplicate`
- [ ] Scenario: Failed processing retries -- Given an envelope whose handler throws a transient exception, When the ingestor retries per the retry policy, Then the envelope is retried up to max attempts before being moved to the DLQ


# Phase 5: Command and Interaction Processing

## Dependencies
- phase-inbound-transport

## Stage 5.1: Slash Command Dispatch

### Implementation Steps
- [ ] Create `SlackCommandHandler` class that parses the `/agent` slash command text into sub-command and arguments using a simple string parser
- [ ] Implement `ask <prompt>` handler: extract prompt text, call the orchestrator interface to create a new agent task, return task ID for thread creation
- [ ] Implement `status [task-id]` handler: query orchestrator for task or swarm status, return status payload for rendering as an ephemeral or in-thread message
- [ ] Implement `approve <question-id>` handler: validate question-id argument exists, build `HumanDecisionEvent` with `ActionValue = "approve"`, publish to orchestrator
- [ ] Implement `reject <question-id>` handler: validate question-id argument exists, build `HumanDecisionEvent` with `ActionValue = "reject"`, publish to orchestrator
- [ ] Implement `review <task-id>` handler: invoke `SlackDirectApiClient` to call `views.open` with the review modal rendered by `SlackMessageRenderer` (synchronous fast-path per architecture.md section 5.3)
- [ ] Implement `escalate <task-id>` handler: invoke `SlackDirectApiClient` to call `views.open` with the escalation modal (same fast-path as `review`)
- [ ] Define `IAgentTaskService` interface as the orchestrator-facing contract with methods `CreateTaskAsync`, `GetTaskStatusAsync`, `PublishDecisionAsync` to decouple the Slack connector from orchestrator internals
- [ ] Return ephemeral error messages for unrecognized sub-commands or missing arguments

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Ask command creates task -- Given `/agent ask generate implementation plan`, When `SlackCommandHandler` processes it, Then `IAgentTaskService.CreateTaskAsync` is called with the prompt text
- [ ] Scenario: Approve command publishes decision -- Given `/agent approve Q-123`, When `SlackCommandHandler` processes it, Then a `HumanDecisionEvent` with `ActionValue = "approve"` and `QuestionId = "Q-123"` is published
- [ ] Scenario: Unknown sub-command returns error -- Given `/agent unknown`, When `SlackCommandHandler` processes it, Then an ephemeral error message listing valid sub-commands is returned

## Stage 5.2: App Mention Processing

### Implementation Steps
- [ ] Create `SlackAppMentionHandler` class that processes `app_mention` events from the Events API
- [ ] Implement bot user ID stripping: remove the `<@BOT_USER_ID>` prefix from the mention text to extract the raw command string
- [ ] Parse the extracted text into the same sub-command format used by slash commands (`ask`, `status`, `approve`, `reject`, `review`, `escalate`)
- [ ] Delegate parsed commands to the same `SlackCommandHandler` dispatch logic to ensure unified processing
- [ ] Post handler responses as threaded replies in the channel where the mention occurred (using the message's `thread_ts` if already in a thread, or creating a new thread if in the main channel)

### Dependencies
- phase-command-and-interaction-processing/stage-slash-command-dispatch

### Test Scenarios
- [ ] Scenario: App mention dispatches to command handler -- Given an `app_mention` event with text `<@U123> ask design persistence layer`, When `SlackAppMentionHandler` processes it, Then the `ask` sub-command is dispatched with prompt `design persistence layer`
- [ ] Scenario: Bot ID stripped correctly -- Given mention text `<@U123BOT> status TASK-42`, When the handler strips the bot prefix, Then the parsed sub-command is `status` with argument `TASK-42`

## Stage 5.3: Interactive Payload Processing

### Implementation Steps
- [ ] Create `SlackInteractionHandler` class that processes Block Kit button clicks and modal `view_submission` payloads
- [ ] Implement button click handling: extract `action_id`, `value`, `user.id`, `message.ts`, and `trigger_id` from the interactive payload; extract `QuestionId` from the button's `block_id` where it was encoded during rendering by `SlackMessageRenderer` (per architecture.md sections 630-634); map to `HumanDecisionEvent` per architecture.md section 2.9 mapping table
- [ ] Implement modal submission handling: extract form values from `view.state.values`, extract `QuestionId` from the modal's `private_metadata` field (set during `views.open`), map selected verdict to `ActionValue` and free-text input to `Comment` in `HumanDecisionEvent`
- [ ] Resolve `CorrelationId` from `SlackThreadMapping` by looking up the `thread_ts` from the button's parent message
- [ ] Populate `HumanDecisionEvent` fields: `Messenger = "slack"`, `ExternalUserId = user.id`, `ExternalMessageId = message.ts or view.id`, `ReceivedAt = DateTimeOffset.UtcNow`
- [ ] Publish the `HumanDecisionEvent` to the orchestrator via `IAgentTaskService.PublishDecisionAsync`
- [ ] After publishing, update the originating Slack message via `chat.update` to disable buttons and display the decision outcome (e.g., "Approved by @user")
- [ ] Handle `RequiresComment` buttons: when a button's associated `HumanAction.RequiresComment = true`, respond with a modal (via `views.open`) containing a text input instead of submitting directly

### Dependencies
- phase-command-and-interaction-processing/stage-slash-command-dispatch

### Test Scenarios
- [ ] Scenario: Button click produces HumanDecisionEvent with QuestionId -- Given a Block Kit button click where `block_id` encodes `QuestionId = Q-99` and `value = approve`, When `SlackInteractionHandler` processes it, Then a `HumanDecisionEvent` with `QuestionId = "Q-99"` and `ActionValue = "approve"` is published and the message buttons are disabled
- [ ] Scenario: Modal submission includes QuestionId and comment -- Given a modal `view_submission` with `private_metadata` encoding `QuestionId = Q-55`, verdict `request-changes`, and comment text `Add error handling`, When the handler processes it, Then `HumanDecisionEvent` has `QuestionId = "Q-55"`, `ActionValue = "request-changes"`, and `Comment = "Add error handling"`
- [ ] Scenario: RequiresComment triggers modal -- Given a button click where the associated `HumanAction.RequiresComment = true`, When `SlackInteractionHandler` processes it, Then a modal with a text input is opened instead of directly submitting


# Phase 6: Outbound Messaging

## Dependencies
- phase-command-and-interaction-processing

## Stage 6.1: Block Kit Message Rendering

### Implementation Steps
- [ ] Create `SlackMessageRenderer` implementing `ISlackMessageRenderer` with methods `RenderQuestion`, `RenderMessage`, `RenderReviewModal`, `RenderEscalateModal`
- [ ] Implement `RenderQuestion(AgentQuestion)`: produce a Block Kit payload with `header` block for `Title`, `section` block with `mrkdwn` for `Body`, `actions` block with one `button` per `HumanAction`, and `context` block showing `ExpiresAt` deadline
- [ ] Map `AgentQuestion.Severity` to emoji prefix and color attachment bar (e.g., critical = red circle, warning = yellow, info = blue)
- [ ] Encode `QuestionId` into each button's `block_id` and `HumanAction.Value` into the button's `value` field for downstream correlation by `SlackInteractionHandler`
- [ ] Implement `RenderMessage(MessengerMessage)`: produce a Block Kit payload with `section` blocks for content, styled by `MessageType` (status update, completion, error)
- [ ] Implement `RenderReviewModal`: produce a Slack modal view with read-only task summary section, multi-line text input for review comments, select menu for verdict (approve / request-changes / reject), and submit/cancel buttons
- [ ] Implement `RenderEscalateModal`: produce a Slack modal view with task context, severity select, escalation reason text input, and submit/cancel buttons
- [ ] Enforce Block Kit size limits: truncate `text` fields at 3000 characters, limit messages to 50 blocks maximum (per tech-spec section 5.2)

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Question renders buttons -- Given an `AgentQuestion` with 3 `AllowedActions`, When `RenderQuestion` is called, Then the output Block Kit payload contains an `actions` block with 3 buttons whose `value` fields match `HumanAction.Value`
- [ ] Scenario: Text truncation at limit -- Given a `MessengerMessage` with `Content` exceeding 3000 characters, When `RenderMessage` is called, Then the output `text` field is truncated to 3000 characters with an ellipsis indicator
- [ ] Scenario: Review modal structure -- Given a call to `RenderReviewModal` with a task ID, When the modal payload is produced, Then it contains a text input block, a select menu with 3 options, and submit/cancel actions

## Stage 6.2: Thread Lifecycle Management

### Implementation Steps
- [ ] Create `SlackThreadManager` implementing `ISlackThreadManager` with `GetOrCreateThreadAsync` and `GetThreadAsync` methods
- [ ] Implement `GetOrCreateThreadAsync`: check `SlackThreadMapping` for existing thread by `taskId`; if not found, post a root status message to the workspace's `DefaultChannelId` via `chat.postMessage`, capture the returned `ts` as `thread_ts`, persist the mapping, and return it
- [ ] Implement `GetThreadAsync`: look up the `SlackThreadMapping` by `taskId` and return it (or null if no thread exists)
- [ ] On connector restart, load existing `SlackThreadMapping` records from the database to resume thread continuity without re-creating threads (per architecture.md section 2.11 lifecycle step 3)
- [ ] Handle deleted root message or archived channel: log a warning to audit and attempt to create a new thread in the `FallbackChannelId` if configured (per architecture.md section 2.11 lifecycle step 4)
- [ ] Update `SlackThreadMapping.LastMessageAt` on every new message posted to the thread

### Dependencies
- phase-outbound-messaging/stage-block-kit-message-rendering

### Test Scenarios
- [ ] Scenario: First message creates thread -- Given no existing `SlackThreadMapping` for task `TASK-1`, When `GetOrCreateThreadAsync` is called, Then a root message is posted via `chat.postMessage`, a `SlackThreadMapping` is persisted with the returned `thread_ts`, and the mapping is returned
- [ ] Scenario: Subsequent messages reuse thread -- Given an existing `SlackThreadMapping` for task `TASK-1`, When `GetOrCreateThreadAsync` is called again, Then no new message is posted and the existing mapping is returned
- [ ] Scenario: Fallback channel on archive -- Given a thread whose channel has been archived and a `FallbackChannelId` is configured, When `GetOrCreateThreadAsync` detects the archive, Then a new thread is created in the fallback channel

## Stage 6.3: Outbound Dispatch and Rate Limiting

### Implementation Steps
- [ ] Create `SlackOutboundDispatcher` as a `BackgroundService` that drains `ISlackOutboundQueue` and sends messages to Slack via the Web API
- [ ] Define `SlackOutboundEnvelope` internal record carrying `TaskId`, `CorrelationId`, `MessageType` (postMessage, update, viewsUpdate), `BlockKitPayload`, and `ThreadTs`
- [ ] Implement `SendMessageAsync` on `SlackConnector`: render the `MessengerMessage` via `ISlackMessageRenderer`, resolve thread via `ISlackThreadManager`, wrap in `SlackOutboundEnvelope`, and enqueue to the outbound queue
- [ ] Implement `SendQuestionAsync` on `SlackConnector`: render the `AgentQuestion` via `ISlackMessageRenderer`, resolve thread, wrap and enqueue
- [ ] Implement token-bucket rate limiter per Slack API method tier: Tier 2 for `chat.postMessage` (~1 req/s/channel), Tier 4 for `views.update` (shared state with `SlackDirectApiClient` per architecture.md section 2.12)
- [ ] Handle HTTP 429 responses: pause dispatch for the `Retry-After` header duration
- [ ] Move messages to `ISlackDeadLetterQueue` after exceeding max retry attempts
- [ ] Log every outbound API call to `SlackAuditLogger` with `direction = outbound`

### Dependencies
- phase-outbound-messaging/stage-thread-lifecycle-management

### Test Scenarios
- [ ] Scenario: Message dispatched to thread -- Given an enqueued `SlackOutboundEnvelope` for task `TASK-1` with an existing thread, When the dispatcher processes it, Then `chat.postMessage` is called with the correct `thread_ts` and Block Kit payload
- [ ] Scenario: Rate limiter throttles burst -- Given 10 outbound messages queued for the same channel in rapid succession, When the dispatcher processes them, Then messages are rate-limited to the Tier 2 rate and no HTTP 429 errors occur
- [ ] Scenario: DLQ on persistent failure -- Given an outbound message that fails with HTTP 500 on all 5 retry attempts, When max retries are exhausted, Then the message is moved to the dead-letter queue

## Stage 6.4: Direct API Client for Modal Fast-Path

### Implementation Steps
- [ ] Create `SlackDirectApiClient` class that wraps SlackNet for synchronous Slack Web API calls within the HTTP request lifecycle
- [ ] Implement `OpenModalAsync(string triggerId, SlackModalPayload modal, CancellationToken)` that calls `views.open` via SlackNet with the provided `trigger_id` and modal view definition
- [ ] Share the token-bucket rate limiter state with `SlackOutboundDispatcher` so that `views.open` calls respect the same per-tier limits
- [ ] Log every `views.open` call to `SlackAuditLogger` with `request_type = modal_open`
- [ ] On `views.open` failure (rate limit, network error, or Slack API error), return an ephemeral error message to the user; do not retry via the outbound queue because the `trigger_id` is already expired (per architecture.md section 2.15); `views.update` for post-open modal modifications is handled by `SlackOutboundDispatcher` through the durable outbound queue, not by this client

### Dependencies
- phase-outbound-messaging/stage-outbound-dispatch-and-rate-limiting

### Test Scenarios
- [ ] Scenario: Modal opens within deadline -- Given a valid `trigger_id` and modal payload, When `OpenModalAsync` is called, Then `views.open` is invoked via SlackNet and an audit entry with `request_type = modal_open` is logged
- [ ] Scenario: Expired trigger returns error -- Given a `trigger_id` that Slack rejects as expired, When `OpenModalAsync` is called, Then an ephemeral error message is returned and no retry is attempted
- [ ] Scenario: Rate limiter shared with dispatcher -- Given concurrent `views.open` and `chat.postMessage` calls, When both use the rate limiter, Then the combined request rate does not exceed tier limits


# Phase 7: Observability and Operations

## Dependencies
- phase-outbound-messaging

## Stage 7.1: Audit Logging and Retention

### Implementation Steps
- [ ] Create `SlackAuditLogger` implementing `ISlackAuditLogger` with `LogAsync` and `QueryAsync` methods backed by EF Core persistence (uses the Slack entity configurations registered with the upstream `MessagingDbContext`)
- [ ] Implement `LogAsync`: persist a `SlackAuditEntry` capturing all required audit fields (team ID, channel ID, thread timestamp, user ID, command text, response payload per story description)
- [ ] Implement `QueryAsync(SlackAuditQuery)`: support filtering by `correlation_id`, `task_id`, `agent_id`, `team_id`, `channel_id`, `user_id`, `direction`, `outcome`, and time range
- [ ] Wire `SlackAuditLogger.LogAsync` calls into every inbound and outbound processing path: signature validation, authorization, idempotency, command dispatch, interaction handling, outbound send, and modal open
- [ ] Create `SlackRetentionCleanupService` as a `BackgroundService` that runs on a configurable schedule (default daily) and purges `SlackAuditEntry` and `SlackInboundRequestRecord` rows older than 30 days (per tech-spec section 2.7)

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Audit entry persisted on command -- Given a valid slash command processed by `SlackCommandHandler`, When processing completes, Then a `SlackAuditEntry` with `direction = inbound`, `request_type = slash_command`, and `outcome = success` is persisted with all mandatory fields populated
- [ ] Scenario: Query by correlation ID -- Given 10 audit entries with varying correlation IDs, When `QueryAsync` filters by a specific `CorrelationId`, Then only entries with that ID are returned
- [ ] Scenario: Retention cleanup purges old records -- Given audit entries with `Timestamp` older than 30 days, When `SlackRetentionCleanupService` runs, Then those entries are deleted and entries newer than 30 days are retained

## Stage 7.2: OpenTelemetry Traces and Metrics

### Implementation Steps
- [ ] Register `ActivitySource` named `AgentSwarm.Messaging.Slack` for distributed tracing in the Slack project's DI registration
- [ ] Add trace spans to key processing paths: inbound receive, signature validation, authorization check, idempotency check, command dispatch, outbound send, and modal open
- [ ] Propagate `correlation_id`, `task_id`, `agent_id`, `team_id`, and `channel_id` as span attributes (baggage) per architecture.md section 6.3
- [ ] Register `System.Diagnostics.Metrics` meter named `AgentSwarm.Messaging.Slack` with counters and histograms: `slack.inbound.count`, `slack.outbound.count`, `slack.outbound.latency_ms`, `slack.idempotency.duplicate_count`, `slack.auth.rejected_count`, `slack.ratelimit.backoff_count`
- [ ] Ensure structured logs via `ILogger<T>` include `correlation_id`, `task_id`, `agent_id`, `team_id`, `channel_id` in the log scope for all Slack components

### Dependencies
- phase-observability-and-operations/stage-audit-logging-and-retention

### Test Scenarios
- [ ] Scenario: Trace spans emitted -- Given a complete slash command processing flow, When an in-memory `ActivityListener` captures activities, Then spans for signature validation, authorization, idempotency, and command dispatch are present with `correlation_id` attribute
- [ ] Scenario: Metrics increment -- Given 5 inbound commands processed, When the `slack.inbound.count` counter is read, Then its value is 5
- [ ] Scenario: Duplicate count metric -- Given 3 duplicate events detected by the idempotency guard, When `slack.idempotency.duplicate_count` is read, Then its value is 3

## Stage 7.3: Health Checks and Diagnostics

### Implementation Steps
- [ ] Register ASP.NET Core health check for Slack API connectivity: call `auth.test` via SlackNet and report `Healthy` or `Unhealthy` based on the response
- [ ] Register health check for outbound queue depth: report `Degraded` if queue depth exceeds a configurable threshold (default 1000)
- [ ] Register health check for DLQ depth: report `Unhealthy` if DLQ depth exceeds a configurable threshold (default 100)
- [ ] Expose health check endpoints at `/health/ready` (includes all checks) and `/health/live` (basic liveness) for Kubernetes probes
- [ ] Add diagnostic logging on connector startup: log active workspaces, transport type per workspace (Events API vs Socket Mode), and rate-limit configuration

### Dependencies
- phase-observability-and-operations/stage-opentelemetry-traces-and-metrics

### Test Scenarios
- [ ] Scenario: Healthy Slack connectivity -- Given a working Slack API connection, When the health check runs, Then it reports `Healthy`
- [ ] Scenario: DLQ depth triggers unhealthy -- Given DLQ depth at 150 (threshold 100), When the health check runs, Then it reports `Unhealthy` with a descriptive message
- [ ] Scenario: Startup diagnostic logging -- Given the connector starts with 2 configured workspaces, When startup completes, Then structured logs include workspace IDs and transport types


# Phase 8: Connector Wiring and Acceptance Validation

## Dependencies
- phase-observability-and-operations

## Stage 8.1: SlackConnector Facade and DI Wiring

### Implementation Steps
- [ ] Create `SlackConnector` class implementing `IMessengerConnector` that composes all internal components: inbound transport, ingestor, outbound dispatcher, thread manager, audit logger
- [ ] Implement `SendMessageAsync`: delegate to `SlackMessageRenderer` + `SlackThreadManager` + outbound queue enqueue
- [ ] Implement `SendQuestionAsync`: delegate to `SlackMessageRenderer` + `SlackThreadManager` + outbound queue enqueue
- [ ] Implement `ReceiveAsync`: drain processed inbound events from the inbound pipeline and return as `IReadOnlyList<MessengerEvent>`
- [ ] Create `ServiceCollectionExtensions.AddSlackMessenger(IConfiguration)` that registers all Slack components in DI: `SlackConnector` as `IMessengerConnector`, all internal handlers, renderers, guards, transports, and the `SlackDirectApiClient`
- [ ] Wire the Worker project `Program.cs` to call `AddSlackMessenger()` and `AddSecretProvider()` for Slack-owned registrations, plus the upstream `AddMessagingCore()` and `AddMessagingPersistence()` DI registrations (provided by the Core and Persistence projects when available)
- [ ] Configure `appsettings.json` with a documented schema for `Slack` configuration section (workspaces, options, secret provider)

### Dependencies
- _none -- start stage_

### Test Scenarios
- [ ] Scenario: Full DI container builds -- Given `AddSlackMessenger` is called with valid configuration, When the DI container is built, Then `IMessengerConnector` resolves to `SlackConnector` and all internal dependencies are satisfied
- [ ] Scenario: SendMessageAsync enqueues -- Given `SlackConnector.SendMessageAsync` is called with a `MessengerMessage`, When the method completes, Then a `SlackOutboundEnvelope` is present in the outbound queue with the rendered Block Kit payload

## Stage 8.2: End-to-End Integration Tests

### Implementation Steps
- [ ] Create `SlackIntegrationTestFixture` that configures `WebApplicationFactory<Program>` with SQLite in-memory database, `ChannelBasedSlackQueue` for inbound/outbound queues, and mock `IAgentTaskService`
- [ ] Create mock Slack API server using ASP.NET Core `TestServer` to simulate Slack Web API responses (`chat.postMessage`, `chat.update`, `views.open`, `auth.test`, `usergroups.users.list`)
- [ ] Write integration test for AC-1: POST `/api/slack/commands` with a valid `/agent ask generate implementation plan for persistence failover` payload; verify `IAgentTaskService.CreateTaskAsync` was called, a thread root message was posted, and a `SlackAuditEntry` was persisted
- [ ] Write integration test for AC-2: simulate orchestrator emitting a status update and an `AgentQuestion`; verify both are posted as threaded replies with correct Block Kit formatting
- [ ] Write integration test for AC-3: simulate a Block Kit button click on an `AgentQuestion` message; verify a `HumanDecisionEvent` is published and the message buttons are updated to disabled state
- [ ] Write integration test for AC-4: send the same event payload twice with identical `event_id`; verify only one `IAgentTaskService` call occurs and the second is recorded as `outcome = duplicate`
- [ ] Write integration test for AC-5: send a slash command from a channel not in `AllowedChannelIds`; verify HTTP 200 ACK is returned to Slack, the request is rejected during async processing with an ephemeral error message to the user, and audit records `outcome = rejected_auth`
- [ ] Write integration test for AC-6: execute a full ask-question-approve flow and verify all exchanges are queryable by `CorrelationId` via `SlackAuditLogger.QueryAsync`

### Dependencies
- phase-connector-wiring-and-acceptance-validation/stage-slackconnector-facade-and-di-wiring

### Test Scenarios
- [ ] Scenario: AC-1 slash command creates task and thread -- Given a signed `/agent ask` payload from an authorized channel, When the full pipeline processes it, Then a task is created via the orchestrator and a Slack thread root message is posted
- [ ] Scenario: AC-3 button click maps to decision -- Given an `AgentQuestion` rendered with buttons in a thread, When a user clicks "Approve", Then a `HumanDecisionEvent` with `ActionValue = "approve"` is published and the originating message is updated
- [ ] Scenario: AC-4 duplicate suppression -- Given two identical Events API payloads with the same `event_id`, When both are processed, Then only one orchestrator call is made and audit shows one `success` and one `duplicate`
- [ ] Scenario: AC-6 correlation ID query -- Given a full command-response-decision exchange, When `SlackAuditLogger.QueryAsync` is called with the `CorrelationId`, Then all related audit entries (inbound command, outbound question, inbound decision) are returned