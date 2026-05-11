# E2E Scenarios — Telegram Messenger Support

**Story:** `qq:TELEGRAM-MESSENGER-S`
**Version:** v0.32-draft (iteration 27)

---

## Feature: Human Creates Task via /ask Command

Covers AC: _Human can send `/ask build release notes for Solution12` and the swarm creates a work item._

```gherkin
Feature: Task creation through /ask command

  Background:
    Given a Telegram bot is running and connected to the Messenger Gateway
    And bot token is loaded from Key Vault (never logged)
    And user "operator-1" with Telegram user ID "111222333" has an active OperatorBinding for chat "998877"
    And user "operator-1" is mapped to tenant "acme" and workspace "factory-1"

  Scenario: Authorized user creates a task with /ask
    Given the swarm orchestrator is healthy
    When user "operator-1" sends "/ask build release notes for Solution12" in chat "998877"
    Then the gateway validates via IOperatorRegistry.IsAuthorizedAsync(userId=111222333, chatId=998877)
    And a work item is created in the swarm with title "build release notes for Solution12"
    And the work item carries CorrelationId, TaskId, and Timestamp
    And the work item's AgentId field is null (no agent is assigned at creation; AgentId is a nullable field on work items, populated only when the orchestrator assigns an agent)
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
    # This document adopts the sidecar envelope model per architecture.md §3.1
    # and implementation-plan.md Stage 1.2: the shared AgentQuestion model does
    # NOT include a DefaultAction property. The proposed default action is carried
    # as ProposedDefaultActionId on the AgentQuestionEnvelope. The connector reads
    # ProposedDefaultActionId from the envelope, denormalizes it into
    # PendingQuestionRecord.DefaultActionId, displays the default in the message
    # body, and applies it on timeout.
    # All sibling documents (tech-spec.md Section 10, architecture.md §3.1,
    # implementation-plan.md Stage 1.2) mark DefaultAction placement as RESOLVED
    # and consistently adopt the sidecar envelope model.
    Given agent "arch-agent-7" publishes an agent question with the following data:
      | Field                    | Value                                              |
      | QuestionId               | Q-1001                                             |
      | AgentId                  | arch-agent-7                                       |
      | TaskId                   | TASK-042                                           |
      | Title                    | Database migration strategy                        |
      | Body                     | Should we use blue-green or rolling migration?      |
      | Severity                 | high                                               |
      | AllowedActions           | [{ActionId:"act-1", Label:"Approve", Value:"approve"}, {ActionId:"act-2", Label:"Reject", Value:"reject"}, {ActionId:"act-3", Label:"Need info", Value:"need-info", RequiresComment:true}] |
      | ExpiresAt                | 2026-05-11T15:30:00Z                               |
      | CorrelationId            | corr-abc-123                                       |
      | ProposedDefaultAction    | act-1 (carried as AgentQuestionEnvelope.ProposedDefaultActionId per architecture.md §3.1) |
    When the Messenger Gateway dequeues the question
    Then the Telegram connector reads ProposedDefaultActionId "act-1" from the AgentQuestionEnvelope and creates a PendingQuestionRecord with DefaultActionId "act-1"
    And the bot sends a Telegram message to chat "998877" containing:
      | Element         | Content                                            |
      | Text            | Title, Body, Severity, timeout, and proposed default action label |
      | InlineKeyboard  | Three buttons: "Approve", "Reject", "Need info"  |
    And the outbound message is logged with CorrelationId "corr-abc-123"

  Scenario: Question includes context, severity, timeout, and proposed default
    Given agent "arch-agent-7" publishes an agent question with:
      | Field                    | Value                                         |
      | Title                    | Database migration strategy                    |
      | Body                     | Should we use blue-green or rolling migration? |
      | Severity                 | critical                                       |
      | ExpiresAt                | 5 minutes from now                             |
      | AllowedActions           | [{ActionId:"act-1", Label:"Approve", Value:"approve"}, {ActionId:"act-2", Label:"Reject", Value:"reject"}] |
      | ProposedDefaultAction    | act-1 (carried as AgentQuestionEnvelope.ProposedDefaultActionId per architecture.md §3.1) |
    When the message is rendered in Telegram
    Then the Telegram connector reads ProposedDefaultActionId "act-1" from the AgentQuestionEnvelope and creates a PendingQuestionRecord with DefaultActionId "act-1"
    And the message body includes the Title "Database migration strategy"
    And the message body includes the Body text "Should we use blue-green or rolling migration?"
    And the message body includes severity badge "🔴 CRITICAL"
    And the message body includes "Timeout: 5 min"
    And the message body includes "Default action if no response: Approve"

  Scenario: Question timeout expires without human response
    Given agent "arch-agent-7" publishes an agent question with ExpiresAt 2 minutes from now
    And the question includes ProposedDefaultActionId "act-1" on the AgentQuestionEnvelope (per architecture.md §3.1)
    And the Telegram connector reads ProposedDefaultActionId "act-1" from the envelope and creates a PendingQuestionRecord with DefaultActionId "act-1"
    And no human responds within 2 minutes
    When ExpiresAt is reached
    Then the QuestionTimeoutService reads PendingQuestionRecord.DefaultActionId "act-1"
    And resolves the corresponding HumanAction from the cache
    And publishes a HumanDecisionEvent with that HumanAction.Value as ActionValue
    And the Telegram message is updated to show "⏱️ Timed out – default applied"

  Scenario: Question timeout with no default action
    Given agent "arch-agent-7" publishes an agent question with ExpiresAt 2 minutes from now
    And the question has no ProposedDefaultActionId on the AgentQuestionEnvelope (null, per architecture.md §3.1)
    And the Telegram connector creates a PendingQuestionRecord with DefaultActionId = null
    And no human responds within 2 minutes
    When ExpiresAt is reached
    Then the QuestionTimeoutService publishes a HumanDecisionEvent with ActionValue "__timeout__"
    And the Telegram message is updated to show "⏰ Timed out — no default action"
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
    # ActionValue carries the HumanAction.Value from AllowedActions (per implementation-plan.md Stage 3.3)
    When user "operator-1" taps the "Approve" inline button for question "Q-1001"
    Then a HumanDecisionEvent is published:
      | Field             | Value            |
      | QuestionId        | Q-1001           |
      | ActionValue       | approve          |
      | Comment           | null             |
      | Messenger         | Telegram         |
      | ExternalUserId    | 111222333        |
      | ExternalMessageId | 48291073         |
      | CorrelationId     | corr-abc-123     |
    And the original Telegram message is edited to show "✅ Approved by operator-1"
    And an audit record is persisted with all HumanDecisionEvent fields

  Scenario: Human rejects via inline button
    When user "operator-1" taps the "Reject" inline button for question "Q-1001"
    Then a HumanDecisionEvent is published with ActionValue "reject"
    And the original Telegram message is edited to show "❌ Rejected by operator-1"

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
    And retry policy is configured: max 5 attempts (architecture.md §5.3 and implementation-plan.md Stage 4.2 are aligned on default 5)
    And exponential backoff with jitter is enabled

  Scenario: Transient Telegram API failure triggers retry
    Given agent "test-agent-3" enqueues an outbound alert message
    And the message is dequeued for delivery
    When the Telegram Bot API returns HTTP 429 (rate limited) on attempt 1
    Then the gateway waits per exponential backoff (respecting Retry-After header)
    And retries delivery (attempt 2)
    When the Telegram Bot API returns HTTP 200 on attempt 2
    Then the message is marked as delivered
    And telegram.send.first_attempt_latency_ms (acceptance gate) is NOT recorded for this message because it did not succeed on first attempt without rate-limiting (per architecture.md §10.4: this metric covers only first-attempt, non-rate-limited successes measured from enqueue instant — OutboundMessage.CreatedAt — to HTTP 200; P95 ≤ 2s acceptance criterion applies to this metric, preserving the story requirement "P95 send latency under 2 seconds after event is queued")
    And telegram.send.all_attempts_latency_ms (all-inclusive) records elapsed time from OutboundMessage.CreatedAt (enqueue) to final HTTP 200, including the rate-limit wait (per architecture.md §8/§10.4: all-inclusive metric covering ALL messages regardless of attempt number or rate-limit holds — used for capacity planning)
    And telegram.send.rate_limited_wait_ms records the time spent waiting during the 429 backoff

  Scenario: Persistent failure dead-letters the message
    Given agent "deploy-agent-9" enqueues an urgent alert message
    When the Telegram Bot API returns HTTP 500 on every attempt up to max 5 attempts
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

  Scenario: P95 send latency under 2 seconds (normal load)
    # Per architecture.md §10.4: telegram.send.first_attempt_latency_ms (acceptance gate)
    # = elapsed time from enqueue instant (OutboundMessage.CreatedAt) to Telegram API HTTP 200,
    # measured ONLY for messages that succeed on first attempt without rate-limit waits —
    # the P95 ≤ 2s acceptance criterion applies to this metric (per operator answer to
    # p95-metric-scope: first-attempt, non-rate-limited only). Measuring from enqueue
    # preserves the story requirement "P95 send latency under 2 seconds after event is queued."
    # telegram.send.all_attempts_latency_ms (all-inclusive) = elapsed time from
    # OutboundMessage.CreatedAt (enqueue) to HTTP 200, measured for ALL messages
    # regardless of attempt number or rate-limit holds — for capacity planning.
    # telegram.send.queue_dwell_ms (diagnostic) = enqueue to dequeue — queue backlog.
    Given 100 outbound messages are enqueued sequentially with mixed severities (25 Critical, 25 High, 25 Normal, 25 Low)
    And the Telegram Bot API responds HTTP 200 with ≤ 50 ms latency on every request
    And no HTTP 429 rate-limit responses occur
    When all 100 messages are dequeued and sent
    Then all 100 messages are delivered successfully on first attempt without rate-limiting
    And telegram.send.first_attempt_latency_ms (acceptance gate) is emitted for every send (per architecture.md §10.4: enqueue instant — OutboundMessage.CreatedAt — to HTTP 200, first-attempt, non-rate-limited sends only; preserves story requirement "after event is queued")
    And telegram.send.all_attempts_latency_ms (all-inclusive) is also emitted for all 100 messages (per architecture.md §8/§10.4: enqueue to HTTP 200, all messages regardless of attempt number or rate-limit holds)
    And telegram.send.queue_dwell_ms (diagnostic) is emitted for all 100 messages (per architecture.md §10.4: enqueue to dequeue — queue backlog monitoring)
    And P95 telegram.send.first_attempt_latency_ms across ALL 100 messages (all severities) is under 2 seconds (per operator answer to p95-metric-scope: the P95 ≤ 2s acceptance criterion applies to first-attempt, non-rate-limited sends only, measured from enqueue instant — OutboundMessage.CreatedAt — to HTTP 200, preserving the story requirement "P95 send latency under 2 seconds after event is queued")
    And P99 telegram.send.first_attempt_latency_ms across ALL 100 messages is under 3 seconds
    And under these test conditions (no retries, no rate-limiting, low queue depth), telegram.send.queue_dwell_ms is negligible and telegram.send.first_attempt_latency_ms closely tracks telegram.send.all_attempts_latency_ms (both measure from enqueue to HTTP 200; the difference is population scope, not measurement points)

  Scenario: Burst of 1000+ agent alerts without message loss
    Given 1000 agents each enqueue one alert message simultaneously
    And the messages have mixed severities: 100 Critical, 200 High, 300 Normal, 400 Low
    When all 1000 messages are processed through the outbound queue
    Then all 1000 messages are eventually delivered or dead-lettered
    And zero messages are lost
    And queue depth is observable via the health check (architecture.md §8: outbound queue depth < threshold)
    And the priority queuing design (architecture.md §5.2: severity-based dispatch ordering) ensures Critical/High messages are dispatched first
    And telegram.send.first_attempt_latency_ms (acceptance gate) is emitted for messages that succeed on first attempt without rate-limiting (per architecture.md §10.4: enqueue instant — OutboundMessage.CreatedAt — to HTTP 200, first-attempt, non-rate-limited sends only; preserves story requirement "after event is queued")
    And telegram.send.all_attempts_latency_ms (all-inclusive) is emitted for all 1000 messages regardless of attempt number or rate-limit holds (per architecture.md §8/§10.4: enqueue to HTTP 200 — capacity planning)
    And telegram.send.queue_dwell_ms (diagnostic) is emitted for all 1000 messages (per architecture.md §10.4: enqueue to dequeue — queue backlog monitoring)
    And P95 telegram.send.first_attempt_latency_ms for Critical and High severity messages is under 2 seconds (per architecture.md §10.4: priority queuing ensures Critical/High are dequeued first with minimal queue dwell, keeping their enqueue-to-200 latency within the 2-second window under burst)
    And Normal and Low severity messages may exceed the 2-second P95 under sustained burst due to accumulated queue dwell (per architecture.md §10.4: "Normal/Low first-attempt sends may exceed 2 s P95 under sustained burst due to accumulated queue dwell")
    And queue dwell time (telegram.send.queue_dwell_ms) increases under burst as expected; because the acceptance metric measures from enqueue, burst-induced queue dwell IS reflected in telegram.send.first_attempt_latency_ms — the P95 target requires that enqueue-to-200 stays under 2s even under burst for qualifying sends
    And time spent waiting during 429 backoff is tracked via telegram.send.rate_limited_wait_ms for operational diagnostics
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
    # Per architecture.md §7.1 and implementation-plan.md Stage 5.2: /start uses
    # Tier 1 onboarding allowlist (Telegram:AllowedUserIds). The tenant/workspace
    # binding is sourced from a pre-configured user-to-tenant mapping
    # (Telegram:UserTenantMappings in app configuration), which maps each allowed
    # Telegram user ID to a tenant and workspace. /start creates an OperatorBinding
    # record via IOperatorRegistry.RegisterAsync(userId, chatId, tenantId, workspaceId).
    # If the user has multiple workspace entries in the mapping, /start creates one
    # OperatorBinding per workspace; subsequent commands trigger workspace disambiguation
    # via inline keyboard (per architecture.md §4.3 multi-workspace disambiguation).
    Given user "new-op" (Telegram user ID "444555666") is in the Telegram:AllowedUserIds allowlist
    And the app configuration maps user "444555666" to tenant "acme", workspace "factory-1" via Telegram:UserTenantMappings
    And user "new-op" has not yet registered (no OperatorBinding exists)
    When user "new-op" sends "/start" in a private chat with chat ID "556677"
    Then the gateway checks Tier 1 authorization: user ID "444555666" is present in Telegram:AllowedUserIds
    And the gateway creates an OperatorBinding record via IOperatorRegistry.RegisterAsync(userId=444555666, chatId=556677, tenantId="acme", workspaceId="factory-1")
    And the bot replies with a welcome message listing available commands
    And an audit record is persisted with user ID "444555666", chat ID "556677", and registration outcome

  Scenario: /status returns swarm summary
    When user "operator-1" sends "/status"
    Then the bot replies with active task count, agent count, and queue depth

  Scenario: /agents lists active agents
    When user "operator-1" sends "/agents"
    Then the bot replies with a formatted list of active agents and their current tasks

  Scenario: /approve with question ID
    Given an open AgentQuestion "Q-2001" exists with AllowedActions [{ActionId:"act-10", Label:"Approve", Value:"approve"}]
    When user "operator-1" sends "/approve Q-2001"
    Then a HumanDecisionEvent with ActionValue "approve" is published for "Q-2001"

  Scenario: /reject with question ID
    Given an open AgentQuestion "Q-2001" exists with AllowedActions [{ActionId:"act-10", Label:"Approve", Value:"approve"}, {ActionId:"act-11", Label:"Reject", Value:"reject"}]
    When user "operator-1" sends "/reject Q-2001"
    Then a HumanDecisionEvent with ActionValue "reject" is published for "Q-2001"

  Scenario: /handoff transfers oversight to another operator
    # architecture.md §5.5: Full oversight transfer (Decided).
    # implementation-plan.md Stage 3.2: HandoffCommandHandler performs full oversight
    # transfer — validates task existence and current oversight, resolves target
    # operator via IOperatorRegistry, creates/updates TaskOversight record, notifies
    # both operators, persists audit record.
    # tech-spec.md D-4: Decided — full transfer with validation, notification, and audit.
    Given user "operator-1" currently has oversight of task "TASK-099"
    And user "operator-2" (alias "@operator-2") is registered in the OperatorRegistry
    When user "operator-1" sends "/handoff TASK-099 @operator-2"
    Then the HandoffCommandHandler validates the syntax (two arguments: task ID and operator alias)
    And the handler validates that task "TASK-099" exists and "operator-1" currently has oversight
    And the handler resolves "@operator-2" via IOperatorRegistry.GetByAliasAsync
    And a TaskOversight record is created or updated mapping "TASK-099" to "operator-2"
    And "operator-1" receives confirmation "✅ Oversight of TASK-099 transferred to @operator-2"
    And "operator-2" receives a handoff notification with task context for "TASK-099"
    And an audit record is persisted with task ID "TASK-099", source operator "operator-1", target operator "operator-2", timestamp, and CorrelationId

  Scenario: /handoff with nonexistent task is rejected
    Given task "NONEXISTENT" does not exist
    When user "operator-1" sends "/handoff NONEXISTENT @operator-2"
    Then the bot replies with "❌ Task NONEXISTENT not found"
    And no TaskOversight record is created or updated
    And no notification is sent to operator-2

  Scenario: /handoff with unregistered target operator is rejected
    Given user "operator-1" currently has oversight of task "TASK-099"
    And alias "@unknown-user" is not registered in the OperatorRegistry
    When user "operator-1" sends "/handoff TASK-099 @unknown-user"
    Then the bot replies with "❌ Operator @unknown-user is not registered"
    And no TaskOversight record is created or updated

  Scenario: /handoff with invalid syntax returns usage help
    When user "operator-1" sends "/handoff" with no arguments
    Then the bot replies with usage help: "Usage: /handoff TASK-ID @operator-alias"
    And an audit record is persisted for the inbound command with user ID, timestamp, command "/handoff", and outcome "invalid_syntax" (per implementation-plan.md Stage 5.3: every inbound command is logged)

  Scenario: /handoff with one argument returns usage help
    When user "operator-1" sends "/handoff TASK-099"
    Then the bot replies with usage help: "Usage: /handoff TASK-ID @operator-alias"
    And an audit record is persisted for the inbound command with user ID, timestamp, command "/handoff TASK-099", and outcome "invalid_syntax"

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

  Scenario: Operator deactivated mid-session via OperatorBinding
    # Per implementation-plan.md Stage 5.2: runtime authorization (Tier 2) checks
    # OperatorBinding records via IOperatorRegistry.IsAuthorizedAsync(userId, chatId),
    # which queries for active bindings (IsActive=true). Removing access mid-session
    # is modeled by setting OperatorBinding.IsActive=false, not by modifying the
    # onboarding allowlist (which only gates /start). This ensures the operator's
    # existing commands are rejected immediately without requiring a /start re-check.
    Given user "operator-1" was previously authorized with an active OperatorBinding (IsActive=true)
    And an administrator sets OperatorBinding.IsActive = false for user "operator-1"
    When user "operator-1" sends "/status"
    Then the gateway calls IOperatorRegistry.IsAuthorizedAsync(userId=111222333, chatId=998877)
    And finds no active OperatorBinding for the (user, chat) pair (IsActive=false)
    And the gateway rejects the command
    And the bot replies with "Unauthorized – your access has been revoked"
    And an audit record is persisted with user ID "111222333" and rejection reason "binding_deactivated"
```

---

## Feature: Receive Mode — Webhook vs Long Polling

Covers requirement: _Support webhook in production; allow long polling for local/dev environments._

```gherkin
Feature: Configurable receive mode

  Scenario: Production mode uses webhook
    Given environment is "production"
    And webhook URL is configured as "https://acme-factory1.contoso.io/api/telegram/webhook" (test fixture for tenant "acme", workspace "factory-1")
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

  Scenario: Group-chat command is authorized by OperatorBinding (user, chat) pair
    # Per architecture.md §7.1: runtime authorization checks OperatorBinding records
    # matching BOTH TelegramUserId AND TelegramChatId. Commands in groups are
    # attributed to the sending user_id (per tech-spec.md S-5), but the (user, chat)
    # binding must exist in the OperatorBinding table for authorization to pass.
    Given a Telegram group chat "group-777" contains users "operator-1" (user ID "111222333") and "stranger" (user ID "999000111")
    And user "operator-1" has an OperatorBinding record with TelegramUserId "111222333" and TelegramChatId "group-777" mapped to tenant "acme", workspace "factory-1"
    And user "stranger" does NOT have an OperatorBinding record for TelegramChatId "group-777"
    When user "operator-1" sends "/status" in group chat "group-777"
    Then the gateway calls IOperatorRegistry.IsAuthorizedAsync(userId=111222333, chatId=group-777)
    And finds a matching OperatorBinding for the (user, chat) pair
    And the command is attributed to operator "operator-1" (user_id attribution, not group chat_id)
    And the bot replies with the swarm status
    When user "stranger" sends "/status" in group chat "group-777"
    Then the gateway calls IOperatorRegistry.IsAuthorizedAsync(userId=999000111, chatId=group-777)
    And finds no matching OperatorBinding for the (user, chat) pair
    And the bot replies with "Unauthorized – contact your administrator"
    And an audit record is persisted with user ID "999000111", group chat ID "group-777", and rejection reason

  Scenario: Group-chat inline button tap is authorized by (user, chat) OperatorBinding pair
    # Per architecture.md §7.1 and tech-spec.md S-5: button taps in groups are
    # authorized by the tapping user's (TelegramUserId, TelegramChatId) binding
    # pair via IOperatorRegistry.IsAuthorizedAsync, consistent with the group-chat
    # command scenario above. The OperatorBinding table is the runtime allowlist.
    Given an AgentQuestion "Q-7001" is displayed in group chat "group-777"
    And user "stranger" (user ID "999000111") does NOT have an OperatorBinding record for TelegramChatId "group-777"
    When user "stranger" taps the "Approve" inline button for question "Q-7001" in group chat "group-777"
    Then the gateway calls IOperatorRegistry.IsAuthorizedAsync(userId=999000111, chatId=group-777)
    And finds no matching OperatorBinding for the (user, chat) pair
    And no HumanDecisionEvent is published
    And the bot sends a callback answer "Unauthorized"
    And an audit record is persisted with user ID "999000111", group chat ID "group-777", and rejection reason
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
      | Metric                                   | Type      | Source                                                      |
      | telegram.send.first_attempt_latency_ms   | histogram | architecture.md §10.4: acceptance gate (enqueue instant — OutboundMessage.CreatedAt — to HTTP 200, first-attempt, non-rate-limited — P95 ≤ 2s; preserves story "after event is queued") |
      | telegram.send.all_attempts_latency_ms  | histogram | architecture.md §8/§10.4: all-inclusive (enqueue to HTTP 200, all sends regardless of attempt or rate-limit — capacity planning) |
      | telegram.send.queue_dwell_ms             | histogram | architecture.md §10.4: diagnostic (enqueue to dequeue — queue backlog monitoring) |
      | telegram.send.retry_latency_ms           | histogram | architecture.md §10.4 diagnostic (retried sends)            |
      | telegram.send.rate_limited_wait_ms       | histogram | architecture.md §10.4 rate-limit tracking                   |
      | telegram.updates.received             | counter   | §8 metrics table               |
      | telegram.messages.sent                | counter   | §8 metrics table               |
      | telegram.messages.dead_lettered       | counter   | §8 metrics table               |
      | telegram.messages.backpressure_dlq    | counter   | architecture.md §10.4 backpressure dead-letter (canonical name per operator answer to backpressure-dlq-metric-name; aligned across architecture.md and tech-spec.md HC-5) |
      | telegram.queue.depth                  | gauge     | implementation-plan.md Stage 6.1           |
      | telegram.dlq.depth                    | gauge     | implementation-plan.md Stage 6.1           |
      | telegram.errors                       | counter   | implementation-plan.md Stage 6.1           |
      | telegram.commands.processed           | counter   | §8 metrics table               |
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

_Document generated for story qq:TELEGRAM-MESSENGER-S, iteration 26._
_DefaultAction modeling: This document adopts the sidecar envelope model per architecture.md §3.1 and implementation-plan.md Stage 1.2. Tech-spec.md Section 10 marks DefaultAction placement as **RESOLVED** — all sibling documents consistently use the sidecar approach. The shared `AgentQuestion` model does **not** include a `DefaultAction` property. The proposed default action is carried as `ProposedDefaultActionId` on the `AgentQuestionEnvelope`. The connector reads `ProposedDefaultActionId` from the envelope, denormalizes it into `PendingQuestionRecord.DefaultActionId`, displays the default in the message body, and applies it on timeout. When `null`, the question expires with `ActionValue = "__timeout__"`._
_ActionValue semantics: `/approve` and `/reject` commands emit ActionValue `approve` and `reject` respectively (per implementation-plan.md Stage 3.2). Inline button presses emit the HumanAction.Value from AllowedActions via CallbackQueryHandler (per implementation-plan.md Stage 3.3). All AllowedActions fixtures include ActionId for consistency with the HumanAction model used in callback/default resolution._
_Retry count: architecture.md §5.3 and implementation-plan.md Stage 4.2 are both aligned on `MaxAttempts` / `MaxRetries` default 5. Scenarios assert max 5 attempts accordingly._
_Handoff semantics: architecture.md §5.5 specifies full oversight transfer (Decided). Tech-spec.md D-4 is Decided for full transfer. Implementation-plan.md Stage 3.2 specifies full oversight transfer. Scenarios test the complete flow: task validation, operator resolution via IOperatorRegistry, TaskOversight mutation, dual notification, and audit._
_Metric naming: Per architecture.md §10.4 (canonical source), the latency metric contract is: **`telegram.send.first_attempt_latency_ms`** (acceptance gate) = elapsed time from **enqueue instant** (`OutboundMessage.CreatedAt`) to Telegram Bot API HTTP 200, measured **only** for messages that succeed on their **first delivery attempt** and are **not** waiting behind a 429 rate-limit hold — the **P95 ≤ 2s** acceptance criterion applies to this metric, preserving the story requirement "P95 send latency under 2 seconds **after event is queued**." **`telegram.send.latency_ms`** (primary, all-inclusive) = elapsed time from `OutboundMessage.CreatedAt` (enqueue) to HTTP 200, measured for **all** messages regardless of attempt number or rate-limit holds — for capacity planning and operational dashboards. **`telegram.send.queue_dwell_ms`** (diagnostic) = elapsed time from enqueue to dequeue — queue backlog monitoring. Additional diagnostics: `telegram.send.retry_latency_ms` (retried sends) and `telegram.send.rate_limited_wait_ms` (429 backoff duration). Backpressure dead-letter counter: `telegram.messages.backpressure_dlq` (per architecture.md §10.4 — canonical name; per operator answer to `backpressure-dlq-metric-name`). Queue depth and error counters: `telegram.queue.depth`, `telegram.dlq.depth`, `telegram.errors` (per implementation-plan.md Stage 6.1). **Cross-doc editorial note:** tech-spec.md HC-5 still uses the stale name `telegram.messages.deadlettered_backpressure` instead of the canonical `telegram.messages.backpressure_dlq`; architecture.md §10.4 Metrics row (line 623) describes `telegram.send.first_attempt_latency_ms` as "dequeue to HTTP 200" while §10.4 P95 Metric Definition (line 676) says "enqueue instant" — this e2e document follows the detailed §10.4 definition (enqueue) which aligns with the story text. Both sibling docs need editorial correction to resolve these inconsistencies._
_P95 metric scope: Per operator answer to `p95-metric-scope`, the P95 ≤ 2s acceptance criterion applies to `telegram.send.first_attempt_latency_ms` — first-attempt, non-rate-limited sends only, measured from **enqueue instant** (`OutboundMessage.CreatedAt`) to HTTP 200 (per architecture.md §10.4 P95 Metric Definition, line 676). Measuring from enqueue preserves the story requirement "P95 send latency under 2 seconds **after event is queued**" — the clock starts when the event enters the outbound queue. Under normal operating conditions (no rate-limit hits, low queue depth), queue dwell is negligible (< 50 ms with 10 workers) and the acceptance metric comfortably meets P95 ≤ 2s across all severities. Under burst (1000+ messages), queue dwell increases and IS reflected in the acceptance metric (since it measures from enqueue); the priority queuing design (architecture.md §5.2) ensures Critical/High messages are dequeued first, keeping their enqueue-to-200 latency within the 2-second window. The all-inclusive `telegram.send.latency_ms` (also enqueue to 200, but including retried and rate-limited sends) will show higher values for retried or rate-limited messages — this is expected and tracked for capacity planning. **Note:** architecture.md §10.4 Metrics row (line 623) describes the acceptance gate as "dequeue to HTTP 200," but the detailed P95 Metric Definition in the same section (line 676) says "enqueue instant" — this document follows the detailed definition, which aligns with the story text. Sibling doc editorial correction is noted in the Metric naming footer._
_Group-chat authorization: Per architecture.md §7.1, runtime authorization checks `OperatorBinding` records matching both `TelegramUserId` and `TelegramChatId`. Commands and button taps in groups are authorized by the (user, chat) `OperatorBinding` pair, not by a separate allowlist. Commands are attributed to the sending `user_id` (per tech-spec.md S-5), and the (user, chat) pair must have a matching active `OperatorBinding` record. An unauthorized user in an authorized group is rejected._
_Authorization model: Per architecture.md §7.1 and implementation-plan.md Stage 5.2, authorization uses a two-tier model. Tier 1 (onboarding): `/start` checks `Telegram:AllowedUserIds` — a static allowlist of Telegram user IDs in app configuration. If present, an `OperatorBinding` is created with tenant/workspace sourced from `Telegram:UserTenantMappings` configuration. Tier 2 (runtime): all other commands check `OperatorBinding` records via `IOperatorRegistry.IsAuthorizedAsync(userId, chatId)`. Access revocation mid-session is modeled by setting `OperatorBinding.IsActive=false`, which causes Tier 2 checks to reject commands immediately._
_Audit policy: Per implementation-plan.md Stage 5.3, every inbound command is logged regardless of outcome (success, invalid syntax, unauthorized, etc.). This includes commands like `/handoff` with invalid syntax — an audit record is persisted with the command text, outcome, user ID, and timestamp._
