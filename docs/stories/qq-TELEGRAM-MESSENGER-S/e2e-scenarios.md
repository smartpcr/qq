# E2E Scenarios — Telegram Messenger Support

**Story:** `qq:TELEGRAM-MESSENGER-S`
**Version:** v0.12-draft (iteration 6)

---

## Feature: Human Creates Task via /ask Command

Covers AC: _Human can send `/ask build release notes for Solution12` and the swarm creates a work item._

```gherkin
Feature: Task creation through /ask command

  Background:
    Given a Telegram bot is running and connected to the Messenger Gateway
    And bot token is loaded from Key Vault (never logged)
    And user "operator-1" with Telegram user ID "111222333" is in the allowlist
    And user "operator-1" is mapped to tenant "acme" and workspace "factory-1"

  Scenario: Authorized user creates a task with /ask
    Given the swarm orchestrator is healthy
    When user "operator-1" sends "/ask build release notes for Solution12" in chat "998877"
    Then the gateway validates chat ID "998877" and user ID "111222333" against the allowlist
    And a work item is created in the swarm with title "build release notes for Solution12"
    And the work item carries CorrelationId, AgentId (unassigned), TaskId, and Timestamp
    And the bot replies with a confirmation containing the TaskId and CorrelationId
    And an audit record is persisted with message ID, user ID "111222333", timestamp, and CorrelationId

  Scenario: Unauthorized user is rejected
    Given user "stranger" with Telegram user ID "999000111" is NOT in the allowlist
    When user "stranger" sends "/ask build release notes for Solution12" in chat "550011"
    Then the gateway rejects the command before reaching the orchestrator
    And the bot replies with "Unauthorized – contact your administrator"
    And an audit record is persisted with user ID "999000111" and rejection reason

  Scenario: /ask with empty payload
    When user "operator-1" sends "/ask" with no additional text in chat "998877"
    Then the bot replies with usage help: "/ask <task description>"
    And no work item is created
```

---

## Feature: Agent Asks a Blocking Question with Buttons

Covers AC: _Agent can ask a blocking question and Telegram user can answer from mobile._

```gherkin
Feature: Agent blocking question delivered to Telegram

  Background:
    Given user "operator-1" (Telegram user ID "111222333") is registered and authorized
    And agent "arch-agent-7" is working on TaskId "TASK-042"

  Scenario: Agent sends a blocking question with inline buttons
    Given agent "arch-agent-7" publishes an AgentQuestion:
      | Field           | Value                                              |
      | QuestionId      | Q-1001                                             |
      | AgentId         | arch-agent-7                                       |
      | TaskId          | TASK-042                                            |
      | Title           | Database migration strategy                        |
      | Body            | Should we use blue-green or rolling migration?      |
      | Severity        | high                                               |
      | AllowedActions  | [{Label:"Approve", Value:"blue-green"}, {Label:"Reject", Value:"rolling"}, {Label:"Need info", Value:"need-info", RequiresComment:true}] |
      | ProposedDefault | blue-green                                         |
      | ExpiresAt       | 2026-05-11T15:30:00Z                               |
      | CorrelationId   | corr-abc-123                                       |
    When the Messenger Gateway dequeues the question
    Then the bot sends a Telegram message to chat "998877" containing:
      | Element         | Content                                            |
      | Text            | Title, Body, Severity, timeout, and default action |
      | InlineKeyboard  | Three buttons: "Blue-green", "Rolling", "Need info"|
    And the outbound message is logged with CorrelationId "corr-abc-123"

  Scenario: Question includes context, severity, timeout, and proposed default
    Given agent "arch-agent-7" publishes an AgentQuestion with:
      | Field           | Value                                         |
      | Title           | Database migration strategy                    |
      | Body            | Should we use blue-green or rolling migration? |
      | Severity        | critical                                       |
      | ExpiresAt       | 5 minutes from now                             |
      | ProposedDefault | blue-green                                     |
      | AllowedActions  | [{Label:"Approve", Value:"blue-green"}, {Label:"Reject", Value:"rolling"}] |
    When the message is rendered in Telegram
    Then the message body includes the Title "Database migration strategy"
    And the message body includes the Body text "Should we use blue-green or rolling migration?"
    And the message body includes severity badge "🔴 CRITICAL"
    And the message body includes "Timeout: 5 min"
    And the message body includes "Default action if no response: blue-green"

  Scenario: Question timeout expires without human response
    Given agent "arch-agent-7" publishes an AgentQuestion with ExpiresAt 2 minutes from now
    And no human responds within 2 minutes
    When ExpiresAt is reached
    Then the gateway publishes a HumanDecisionEvent with the proposed default ActionValue
    And the Telegram message is updated to show "⏱️ Timed out – default applied"
```

---

## Feature: Approval and Rejection via Inline Buttons

Covers AC: _Approval/rejection buttons are converted into strongly typed agent events._

```gherkin
Feature: Strongly typed approval/rejection events from Telegram buttons

  Background:
    Given user "operator-1" (Telegram user ID "111222333") is authorized
    And an AgentQuestion "Q-1001" is displayed in chat "998877" with inline buttons

  Scenario: Human approves via inline button
    # ActionValue carries the domain value from HumanAction.Value, not the verb
    When user "operator-1" taps the "Approve" inline button for question "Q-1001"
    Then a HumanDecisionEvent is published:
      | Field             | Value            |
      | QuestionId        | Q-1001           |
      | ActionValue       | blue-green       |
      | Comment           | null             |
      | Messenger         | Telegram         |
      | ExternalUserId    | 111222333        |
      | ExternalMessageId | <telegram_msg_id>|
      | CorrelationId     | corr-abc-123     |
    And the original Telegram message is edited to show "✅ Approved by operator-1 (blue-green)"
    And an audit record is persisted with all HumanDecisionEvent fields

  Scenario: Human rejects via inline button
    When user "operator-1" taps the "Reject" inline button for question "Q-1001"
    Then a HumanDecisionEvent is published with ActionValue "rolling"
    And the original Telegram message is edited to show "❌ Rejected by operator-1 (rolling)"

  Scenario: Human selects "Need more info" which requires a comment
    Given the "Need info" action has RequiresComment = true
    When user "operator-1" taps the "Need info" inline button
    Then the bot prompts "Please reply with your comment"
    And when user "operator-1" replies with "What is the rollback plan?"
    Then a HumanDecisionEvent is published with ActionValue "need-info" and Comment "What is the rollback plan?"

  Scenario: Button tap from unauthorized user is rejected
    Given user "intruder" (Telegram user ID "666777888") is NOT in the allowlist
    When user "intruder" taps the "Approve" inline button for question "Q-1001" in a 1:1 chat with the bot
    Then no HumanDecisionEvent is published
    And the bot sends a callback answer "Unauthorized"
    And an audit record is persisted with user ID "666777888" and rejection reason
```

---

## Feature: Webhook Deduplication

Covers AC: _Duplicate webhook delivery does not execute the same human command twice._

```gherkin
Feature: Webhook idempotency and deduplication

  Background:
    Given the Telegram webhook endpoint is configured at /api/telegram/webhook
    And the deduplication store is operational

  Scenario: Duplicate webhook delivery is suppressed
    Given user "operator-1" sends "/approve Q-1001" generating Telegram update_id "90001"
    And the gateway processes update_id "90001" and publishes a HumanDecisionEvent
    When Telegram redelivers the same webhook with update_id "90001"
    Then the gateway detects the duplicate via update_id
    And no second HumanDecisionEvent is published
    And the webhook returns HTTP 200 (to prevent further retries)
    And an audit log entry records the suppressed duplicate

  Scenario: Duplicate callback_query is suppressed
    Given user "operator-1" taps "Approve" generating callback_query_id "cb-xyz-1"
    And the gateway processes callback_query_id "cb-xyz-1"
    When the same callback_query_id "cb-xyz-1" is delivered again
    Then no duplicate HumanDecisionEvent is published
    And HTTP 200 is returned

  Scenario: Distinct commands with different update_ids are both processed
    Given user "operator-1" sends "/approve Q-1001" with update_id "90001"
    And user "operator-1" sends "/reject Q-1002" with update_id "90002"
    Then both commands are processed independently
    And two distinct HumanDecisionEvents are published
```

---

## Feature: Outbound Message Retry and Dead-Letter

Covers AC: _If Telegram send fails, message is retried and eventually dead-lettered with alert._

```gherkin
Feature: Durable outbound message queue with retry and dead-letter

  Background:
    Given the outbound message queue is operational
    And retry policy is configured: max 5 attempts, exponential backoff (per architecture.md §5.3 OutboundQueue:MaxRetries default of 5)

  Scenario: Transient Telegram API failure triggers retry
    Given agent "test-agent-3" enqueues an outbound alert message
    And the message is dequeued for delivery
    When the Telegram Bot API returns HTTP 429 (rate limited) on attempt 1
    Then the gateway waits per exponential backoff (respecting Retry-After header)
    And retries delivery (attempt 2)
    When the Telegram Bot API returns HTTP 200 on attempt 2
    Then the message is marked as delivered
    # P95 latency target applies to non-rate-limited, first-attempt successes (per architecture.md §8)
    # Backoff wait time is excluded and tracked separately via telegram.send.rate_limited_wait_ms

  Scenario: Persistent failure dead-letters the message
    Given agent "deploy-agent-9" enqueues an urgent alert message
    When the Telegram Bot API returns HTTP 500 on attempts 1 through 5
    Then the message is moved to the dead-letter queue after attempt 5
    And an alert is raised to the operations channel
    And the dead-letter record includes CorrelationId, AgentId, message content, and failure reason

  Scenario: Dead-lettered message can be replayed
    Given a message in the dead-letter queue with CorrelationId "corr-dead-001"
    When an operator triggers replay for "corr-dead-001"
    Then the message is re-enqueued for delivery
    And the retry counter is reset

  Scenario: Duplicate outbound enqueue is suppressed by idempotency key
    Given agent "arch-agent-7" publishes AgentQuestion "Q-3001" with CorrelationId "corr-dup-out-001"
    And the gateway enqueues an outbound Telegram message with idempotency key derived from QuestionId "Q-3001"
    When agent "arch-agent-7" re-publishes AgentQuestion "Q-3001" with the same QuestionId due to a retry
    Then the outbound queue detects the duplicate idempotency key
    And no second outbound message is enqueued
    And the deduplication event is logged with QuestionId "Q-3001" and CorrelationId "corr-dup-out-001"

  Scenario: Duplicate outbound alert enqueue is suppressed
    Given agent "deploy-agent-9" enqueues an alert with idempotency key "alert-deploy-9-evt-500"
    And the alert is already present in the outbound queue
    When the same alert with idempotency key "alert-deploy-9-evt-500" is enqueued again
    Then the duplicate is suppressed
    And the original alert remains in the queue for delivery

  Scenario: P95 send latency under 2 seconds for non-rate-limited delivery
    # Standalone deterministic latency test aligned with architecture.md §10.4
    Given 100 outbound messages are enqueued sequentially
    And the Telegram Bot API responds HTTP 200 with ≤ 50 ms latency on every request
    And no HTTP 429 rate-limit responses occur
    When all 100 messages are dequeued and sent
    Then P95 send latency (measured from enqueue to Telegram API 200 response) is under 2 seconds
    And P99 send latency is under 3 seconds
    And latency is reported via the messenger.outbound.send_latency_ms histogram

  Scenario: Burst of 1000+ agent alerts without message loss
    Given 1000 agents each enqueue one alert message simultaneously
    When all 1000 messages are processed through the outbound queue
    Then all 1000 messages are eventually delivered or dead-lettered
    And zero messages are lost
    And the queue depth metric is observable via OpenTelemetry
    And P95 send latency remains under 2 seconds for the first ~30 messages (per architecture.md §10.4 rate-limit throughput of 30 msg/s)
    And subsequent messages are queue-delayed by Telegram rate limits but not lost
    And time spent waiting during 429 backoff is tracked separately via telegram.send.rate_limited_wait_ms
```

---

## Feature: Correlation and Traceability

Covers AC: _All messages include trace/correlation ID._

```gherkin
Feature: End-to-end correlation and traceability

  Scenario: Inbound command carries correlation ID through the pipeline
    Given user "operator-1" sends "/ask build release notes for Solution12"
    When the gateway receives the webhook
    Then a new CorrelationId is generated (or extracted from headers if present)
    And the CorrelationId is attached to the created work item
    And the CorrelationId is included in the bot's reply message
    And the CorrelationId is recorded in the audit log
    And the CorrelationId is propagated to the OpenTelemetry trace

  Scenario: Outbound agent message preserves original correlation ID
    Given agent "arch-agent-7" sends a question with CorrelationId "corr-abc-123"
    When the gateway delivers the message to Telegram
    Then the delivered Telegram message body includes "corr-abc-123" as a reference tag
    And the outbox record for the outbound message includes CorrelationId "corr-abc-123"
    And the audit record for the outbound message includes CorrelationId "corr-abc-123"

  Scenario: Human response back-links to the original question's correlation ID
    Given AgentQuestion "Q-1001" was delivered with CorrelationId "corr-abc-123"
    When user "operator-1" taps "Approve"
    Then the HumanDecisionEvent carries CorrelationId "corr-abc-123"
    And the audit record links QuestionId, ExternalMessageId, and CorrelationId

  Scenario: Audit record contains all required fields
    When any human response is persisted
    Then the audit record includes:
      | Field           | Source                     |
      | MessageId       | Telegram message_id        |
      | UserId          | Telegram user ID           |
      | AgentId         | From AgentQuestion         |
      | Timestamp       | UTC server time            |
      | CorrelationId   | Propagated from question   |
```

---

## Feature: Bot Command Suite

Covers requirement: _Support /start, /status, /agents, /ask, /approve, /reject, /handoff, /pause, /resume._

```gherkin
Feature: Telegram bot command handling

  Background:
    Given user "operator-1" (Telegram user ID "111222333") is authorized
    And the swarm orchestrator is healthy

  Scenario: /start registers a new user
    Given user "new-op" (Telegram user ID "444555666") is in the allowlist but not yet registered
    When user "new-op" sends "/start" in a private chat
    Then the gateway registers user "new-op" and maps chat ID to tenant/workspace
    And the bot replies with a welcome message and command help

  Scenario: /status returns swarm summary
    When user "operator-1" sends "/status"
    Then the bot replies with active task count, agent count, and queue depth

  Scenario: /agents lists active agents
    When user "operator-1" sends "/agents"
    Then the bot replies with a formatted list of active agents and their current tasks

  Scenario: /approve with question ID
    Given an open AgentQuestion "Q-2001" exists with AllowedActions [{Label:"Approve", Value:"proceed"}]
    When user "operator-1" sends "/approve Q-2001"
    Then a HumanDecisionEvent with ActionValue "proceed" is published for "Q-2001"

  Scenario: /reject with question ID
    Given an open AgentQuestion "Q-2001" exists with AllowedActions [{Label:"Reject", Value:"abort"}]
    When user "operator-1" sends "/reject Q-2001"
    Then a HumanDecisionEvent with ActionValue "abort" is published for "Q-2001"

  Scenario: /handoff transfers human oversight of a task to another operator
    # Fully specified per tech-spec.md D-4 (Decided): /handoff = transfer human oversight
    # NOTE: implementation-plan.md Stage 3.2 references "pending clarification" — that clause
    # is stale; this scenario is the authoritative definition of the handoff flow.
    Given task "TASK-099" exists and user "operator-1" has oversight
    And user "operator-2" with Telegram user ID "222333444" is in the allowlist
    When user "operator-1" sends "/handoff TASK-099 @operator-2"
    Then the HandoffCommandHandler validates TASK-099 exists and @operator-2 is a known operator
    And oversight of TASK-099 is transferred from "operator-1" to "operator-2" in the OperatorRegistry
    And the bot notifies "operator-1": "✅ Oversight of TASK-099 transferred to @operator-2"
    And the bot notifies "operator-2": "📋 You now have oversight of TASK-099 (transferred by @operator-1)"
    And an audit record is persisted with the raw command text, user ID, and CorrelationId

  Scenario: /handoff with invalid task ID is rejected
    When user "operator-1" sends "/handoff NONEXISTENT-TASK @operator-2"
    Then the bot replies with "Task NONEXISTENT-TASK not found"
    And no oversight transfer occurs

  Scenario: /handoff with unknown operator alias is rejected
    Given task "TASK-099" exists and user "operator-1" has oversight
    When user "operator-1" sends "/handoff TASK-099 @unknown-user"
    Then the bot replies with "Operator @unknown-user not found in the allowlist"
    And no oversight transfer occurs

  Scenario: /pause and /resume control agent execution
    When user "operator-1" sends "/pause arch-agent-7"
    Then agent "arch-agent-7" is paused
    And the bot confirms "⏸️ arch-agent-7 paused"
    When user "operator-1" sends "/resume arch-agent-7"
    Then agent "arch-agent-7" is resumed
    And the bot confirms "▶️ arch-agent-7 resumed"

  Scenario: Unknown command returns help
    When user "operator-1" sends "/foobar"
    Then the bot replies with "Unknown command. Use /start for available commands."
```

---

## Feature: Authentication and Security

Covers requirements: _Validate chat/user allowlist before accepting commands. Store bot token in Key Vault._

```gherkin
Feature: Security enforcement for Telegram connector

  Scenario: Bot token is loaded from secret store
    Given the Telegram connector is starting up
    When it reads configuration
    Then the bot token is fetched from Key Vault (or equivalent secret store)
    And the bot token is never written to logs at any log level
    And the bot token is never included in OpenTelemetry spans or metrics

  Scenario: Webhook request with invalid secret token is rejected
    Given the webhook endpoint is configured with a secret_token for X-Telegram-Bot-Api-Secret-Token validation
    When a request arrives without a valid secret token header
    Then the gateway returns HTTP 403
    And no further processing occurs

  Scenario: Chat ID not in tenant mapping is rejected
    Given chat ID "000111222" is not mapped to any tenant/workspace
    When a message arrives from chat "000111222"
    Then the gateway rejects the message
    And an audit record is persisted with chat ID and rejection reason

  Scenario: User removed from allowlist mid-session
    Given user "operator-1" was previously authorized
    And the allowlist is updated to remove user "operator-1"
    When user "operator-1" sends "/status"
    Then the gateway rejects the command
    And the bot replies with "Unauthorized – your access has been revoked"
```

---

## Feature: Receive Mode — Webhook vs Long Polling

Covers requirement: _Support webhook in production; allow long polling for local/dev environments._

```gherkin
Feature: Configurable receive mode

  Scenario: Production mode uses webhook
    Given environment is "production"
    And webhook URL is configured as "https://gateway.example.com/api/telegram/webhook"
    When the Telegram connector starts
    Then it registers the webhook URL with Telegram Bot API via setWebhook
    And incoming updates are received via HTTPS POST to the webhook endpoint

  Scenario: Development mode uses long polling
    Given environment is "development"
    And no webhook URL is configured
    When the Telegram connector starts
    Then it calls deleteWebhook to clear any existing webhook
    And it begins long polling via getUpdates
    And updates are processed through the same pipeline as webhook mode

  Scenario: Switching from polling to webhook
    Given the connector was running in long-polling mode
    When the configuration is changed to webhook mode and the connector restarts
    Then the connector calls setWebhook
    And long polling is not started
    And no updates are lost during the transition (pending getUpdates are drained)
```

---

## Feature: Agent Routing and Tenant Mapping

Covers requirement: _Map Telegram chat ID to authorized human operator and tenant/workspace._

```gherkin
Feature: Chat-to-operator-to-tenant routing

  Background:
    Given the routing table maps:
      | ChatId  | UserId    | Operator    | Tenant | Workspace  |
      | 998877  | 111222333 | operator-1  | acme   | factory-1  |
      | 887766  | 222333444 | operator-2  | acme   | factory-2  |

  Scenario: Inbound command is routed to the correct tenant/workspace
    When user "operator-1" sends "/ask deploy canary" in chat "998877"
    Then the work item is created in tenant "acme", workspace "factory-1"

  Scenario: Outbound agent message is routed to the correct chat
    Given agent "deploy-agent-9" in tenant "acme", workspace "factory-1" sends a question
    When the gateway resolves the target operator
    Then the question is delivered to chat "998877" (operator-1's chat)

  Scenario: Operator in multiple workspaces receives disambiguation
    Given user "operator-1" is mapped to workspaces "factory-1" and "factory-3"
    When user "operator-1" sends "/agents" without specifying a workspace
    Then the bot replies with an inline keyboard to select a workspace
    And the selected workspace is used for the query
```

---

## Feature: Observability and Health

Covers attachment FR-008: _OpenTelemetry, structured logging, health checks._

```gherkin
Feature: Observability integration

  Scenario: Health check endpoint reports connector status
    Given the Telegram connector is running
    When a health probe calls GET /healthz
    Then the response includes:
      | Check                | Status  |
      | telegram_bot_api     | healthy |
      | outbound_queue       | healthy |
      | deduplication_store  | healthy |
      | secret_store         | healthy |

  Scenario: OpenTelemetry traces span the full message lifecycle
    Given user "operator-1" sends "/ask run smoke tests"
    When the gateway processes the command
    Then an OpenTelemetry trace is created with spans for:
      | Span                        |
      | telegram.webhook.receive    |
      | gateway.auth.validate       |
      | gateway.dedup.check         |
      | orchestrator.workitem.create |
      | telegram.bot.sendMessage    |

  Scenario: Metrics are emitted for queue depth and latency
    Given the outbound queue processes messages
    Then the following metrics are emitted via OpenTelemetry:
      | Metric                              | Type      |
      | messenger.outbound.queue_depth      | gauge     |
      | messenger.outbound.send_latency_ms  | histogram |
      | messenger.inbound.commands_total    | counter   |
      | messenger.outbound.retries_total    | counter   |
      | messenger.outbound.deadletters_total| counter   |
```

---

## Feature: Edge Cases and Error Handling

```gherkin
Feature: Edge cases and error handling

  Scenario: Bot receives an update type it does not handle (e.g., sticker, location)
    Given user "operator-1" sends a sticker in chat "998877"
    When the gateway receives the update
    Then the update is acknowledged with HTTP 200
    And no processing occurs beyond audit logging

  Scenario: Telegram Bot API is unreachable during startup
    Given the Telegram Bot API is unreachable
    When the connector starts
    Then the health check reports "unhealthy"
    And the connector retries connection with backoff
    And no inbound updates are lost (they are queued by Telegram)

  Scenario: Message exceeds Telegram's 4096 character limit
    Given agent "report-agent-5" sends a message body of 6000 characters
    When the gateway prepares the outbound message
    Then the message is split into chunks of ≤ 4096 characters
    And each chunk carries the same CorrelationId
    And chunks are sent in order

  Scenario: Callback query answered after question expired
    Given AgentQuestion "Q-5001" expired 10 minutes ago
    When user "operator-1" taps "Approve" on the expired question
    Then the bot replies "This question has expired"
    And no HumanDecisionEvent is published

  Scenario: Concurrent button taps from same user
    Given AgentQuestion "Q-6001" is displayed to user "operator-1"
    When user "operator-1" taps "Approve" and "Reject" in rapid succession
    Then only the first tap is processed (deduplication by QuestionId + respondent)
    And the second tap receives callback answer "Already responded"
```

---

_Document generated for story qq:TELEGRAM-MESSENGER-S, iteration 6._
_Aligned with sibling tech-spec.md (D-4 /handoff Decided — transfer human oversight to another operator), architecture.md (Messenger="Telegram", /healthz, MaxRetries=5, §10.4 P95 under 2s for non-rate-limited delivery), and implementation-plan.md (Stage 3.2 "pending clarification" on handoff is stale — this document fully specifies the handoff flow)._
_ActionValue semantics: HumanDecisionEvent.ActionValue carries the domain value from HumanAction.Value (e.g., "blue-green", "rolling"), not the approval verb._
