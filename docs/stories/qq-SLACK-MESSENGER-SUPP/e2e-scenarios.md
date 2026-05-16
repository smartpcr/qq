# E2E Scenarios: Slack Messenger Support (qq-SLACK-MESSENGER-SUPP)

This document defines Gherkin-style feature scenarios for the Slack Messenger
Support story. Scenarios cover the six slash commands (`/agent ask`, `status`,
`approve`, `reject`, `review`, `escalate`), app mention invocation, agent
questions with Block Kit buttons, modal flows, threading, idempotency, security,
audit, reliability, and performance.

Component names, data model entities, and sequence flows referenced here align
with the sibling architecture.md and tech-spec.md documents. The shared data
types (`AgentQuestion`, `HumanAction`, `HumanDecisionEvent`, `MessengerMessage`)
are defined in `AgentSwarm.Messaging.Abstractions` (see architecture.md
section 3.6).

---

## Feature 1: Slash Command -- /agent ask

Covers story acceptance criterion AC-1: "User can invoke `/agent ask generate
implementation plan for persistence failover`."

### Scenario 1.1: Happy path -- user creates a new agent task

```gherkin
Feature: /agent ask slash command

  Scenario: User creates a new agent task via /agent ask
    Given a registered SlackWorkspaceConfig with team_id "T0123ABCD"
      And channel "C-ENG-OPS" is in allowed_channel_ids
      And user "U-ALICE" belongs to an allowed user group
      And the Slack request carries a valid HMAC SHA-256 signature
    When user "U-ALICE" types "/agent ask generate implementation plan for persistence failover" in channel "C-ENG-OPS"
    Then SlackEventsApiReceiver returns HTTP 200 within 3 seconds
      And SlackSignatureValidator accepts the request signature
      And SlackAuthorizationFilter passes workspace, channel, and user-group checks
      And SlackIdempotencyGuard records a new SlackInboundRequestRecord with source_type "command"
      And SlackCommandHandler parses sub-command "ask" with prompt "generate implementation plan for persistence failover"
      And the orchestrator receives a create-task request with the prompt text
      And a CorrelationId is assigned to the task
```

### Scenario 1.2: Happy path -- agent thread is created for the new task

```gherkin
  Scenario: Agent creates a Slack thread for the new task
    Given the orchestrator has created task "TASK-101" from an "/agent ask" command
      And task "TASK-101" has CorrelationId "corr-abc-123"
    When SlackOutboundDispatcher processes the task-created event
    Then SlackThreadManager calls chat.postMessage in the default channel
      And the root message contains the task summary and CorrelationId
      And the returned message timestamp is stored in SlackThreadMapping as thread_ts
      And SlackThreadMapping row has task_id "TASK-101", team_id, channel_id, and agent_id populated
      And SlackAuditLogger persists an outbound entry with direction "outbound" and request_type "message_send"
```

### Scenario 1.3: Happy path -- subsequent status updates are threaded replies

```gherkin
  Scenario: Agent status updates appear as threaded replies
    Given task "TASK-101" has an existing SlackThreadMapping with thread_ts "1716000000.000100"
    When the orchestrator emits a status-update MessengerMessage for task "TASK-101"
    Then SlackOutboundDispatcher posts a threaded reply using thread_ts "1716000000.000100"
      And the reply contains the agent's progress text rendered as Block Kit section blocks
      And SlackAuditLogger records the outbound exchange with the thread_ts
```

### Scenario 1.4: Missing prompt text

```gherkin
  Scenario: User provides no prompt text to /agent ask
    Given a valid, authorized Slack request
    When user types "/agent ask" with no additional text
    Then SlackCommandHandler returns an ephemeral error message "Please provide a prompt for the agent task."
      And no task is created in the orchestrator
      And SlackAuditLogger records an inbound entry with outcome "error"
```

---

## Feature 2: Slash Command -- /agent status

### Scenario 2.1: Happy path -- query specific task status

```gherkin
Feature: /agent status slash command

  Scenario: User queries a specific task status
    Given a valid, authorized Slack request
      And task "TASK-101" exists with an active SlackThreadMapping
    When user types "/agent status TASK-101" in an authorized channel
    Then SlackEventsApiReceiver returns HTTP 200 within 3 seconds
      And SlackCommandHandler parses sub-command "status" with argument "TASK-101"
      And the orchestrator returns the current status of task "TASK-101"
      And a threaded reply is posted in the task's thread with the status summary
      And SlackAuditLogger records the exchange
```

### Scenario 2.2: Happy path -- query swarm-wide status (no task-id)

```gherkin
  Scenario: User queries swarm-wide status without a task ID
    Given a valid, authorized Slack request
    When user types "/agent status" with no arguments
    Then SlackCommandHandler requests a swarm-level summary from the orchestrator
      And an ephemeral message is returned to the user with an overview of active agents and tasks
```

### Scenario 2.3: Task not found

```gherkin
  Scenario: User queries status for a non-existent task
    Given a valid, authorized Slack request
    When user types "/agent status TASK-NONEXISTENT"
    Then SlackCommandHandler returns an ephemeral error "Task TASK-NONEXISTENT not found."
      And SlackAuditLogger records the exchange with outcome "error"
```

---

## Feature 3: Slash Command -- /agent approve

Covers story acceptance criterion AC-3 (CLI path): "Human can answer via button
or modal." Both `/agent approve <question-id>` and Block Kit button clicks are
supported per operator decision OQ-3 (architecture.md section 9).

### Scenario 3.1: Happy path -- approve via CLI argument

```gherkin
Feature: /agent approve slash command

  Scenario: User approves a pending question via slash command
    Given a valid, authorized Slack request
      And the orchestrator has a pending AgentQuestion with QuestionId "Q-200"
      And task "TASK-101" has an active Slack thread
    When user "U-ALICE" types "/agent approve Q-200" in an authorized channel
    Then SlackEventsApiReceiver returns HTTP 200 within 3 seconds
      And SlackCommandHandler parses sub-command "approve" with argument "Q-200"
      And SlackInteractionHandler produces a HumanDecisionEvent with:
        | Field             | Value       |
        | QuestionId        | Q-200       |
        | ActionValue       | approve     |
        | Messenger         | slack       |
        | ExternalUserId    | U-ALICE     |
        | CorrelationId     | (from thread mapping) |
      And the HumanDecisionEvent is published to the orchestrator
      And the original question message in the thread is updated to disable buttons and show "Approved by U-ALICE"
      And SlackAuditLogger records inbound entry with outcome "success"
```

### Scenario 3.2: Question not found

```gherkin
  Scenario: User tries to approve a non-existent question
    Given a valid, authorized Slack request
    When user types "/agent approve Q-NONEXISTENT"
    Then an ephemeral error "Question Q-NONEXISTENT not found." is returned
      And no HumanDecisionEvent is published
```

### Scenario 3.3: Missing question-id argument

```gherkin
  Scenario: User omits question-id from /agent approve
    Given a valid, authorized Slack request
    When user types "/agent approve" with no argument
    Then an ephemeral error "Usage: /agent approve <question-id>" is returned
      And no HumanDecisionEvent is published
```

---

## Feature 4: Slash Command -- /agent reject

### Scenario 4.1: Happy path -- reject via CLI argument

```gherkin
Feature: /agent reject slash command

  Scenario: User rejects a pending question via slash command
    Given a valid, authorized Slack request
      And the orchestrator has a pending AgentQuestion with QuestionId "Q-200"
    When user "U-BOB" types "/agent reject Q-200" in an authorized channel
    Then a HumanDecisionEvent is produced with ActionValue "reject" and ExternalUserId "U-BOB"
      And the HumanDecisionEvent is published to the orchestrator
      And the question message in the thread is updated to show "Rejected by U-BOB"
      And SlackAuditLogger records the exchange with outcome "success"
```

---

## Feature 5: Slash Command -- /agent review (Modal Flow)

Covers the synchronous fast-path described in architecture.md section 5.3.
The `trigger_id` expires in approximately 3 seconds, so the modal must be
opened synchronously within the HTTP request lifecycle.

### Scenario 5.1: Happy path -- review modal opens and submits

```gherkin
Feature: /agent review modal flow

  Scenario: User opens a review modal and submits feedback
    Given a valid, authorized Slack request with a trigger_id
      And task "TASK-42" exists with cached summary data in the orchestrator
    When user "U-ALICE" types "/agent review TASK-42"
    Then SlackSignatureValidator validates the request signature
      And SlackAuthorizationFilter passes all three ACL layers
      And SlackIdempotencyGuard records the command with key "cmd:T0123ABCD:U-ALICE:/agent review:trigger-xyz"
      And SlackCommandHandler detects sub-command "review" requires a modal
      And SlackDirectApiClient calls views.open with trigger_id "trigger-xyz"
      And SlackMessageRenderer.RenderReviewModal produces a modal containing:
        | Element                 | Content                           |
        | Read-only section       | Task summary for TASK-42          |
        | Multi-line text input   | Review comments field             |
        | Select menu             | Verdict: approve / request-changes / reject |
        | Buttons                 | Submit, Cancel                    |
      And SlackEventsApiReceiver returns HTTP 200
      And SlackAuditLogger records an entry with request_type "modal_open"

    When user "U-ALICE" fills in the modal with verdict "approve" and comment "LGTM, ship it"
      And user clicks Submit
    Then Slack sends a view_submission payload to /api/slack/interactions
      And SlackInteractionHandler extracts form values
      And a HumanDecisionEvent is produced with:
        | Field       | Value                |
        | QuestionId  | (from modal metadata)|
        | ActionValue | approve              |
        | Comment     | LGTM, ship it        |
        | Messenger   | slack                |
      And the event is published to the orchestrator
      And a confirmation reply is posted in the task thread
```

### Scenario 5.2: views.open failure due to rate limit

```gherkin
  Scenario: Modal opening fails due to Slack rate limit
    Given a valid, authorized Slack request with a trigger_id
      And the Slack Web API returns HTTP 429 for views.open
    When user types "/agent review TASK-42"
    Then SlackDirectApiClient receives the 429 response
      And an ephemeral error message is returned to the user: "Unable to open review modal. Please try again shortly."
      And the call is NOT retried via the durable outbound queue (trigger_id is expired)
      And SlackAuditLogger records the exchange with outcome "error" and error_detail containing "rate_limited"
```

### Scenario 5.3: Missing task-id argument

```gherkin
  Scenario: User omits task-id from /agent review
    Given a valid, authorized Slack request
    When user types "/agent review" with no argument
    Then an ephemeral error "Usage: /agent review <task-id>" is returned
      And no modal is opened
```

---

## Feature 6: Slash Command -- /agent escalate (Modal Flow)

### Scenario 6.1: Happy path -- escalation modal opens and submits

```gherkin
Feature: /agent escalate modal flow

  Scenario: User opens an escalation modal and submits
    Given a valid, authorized Slack request with a trigger_id
      And task "TASK-55" exists
    When user "U-BOB" types "/agent escalate TASK-55"
    Then SlackDirectApiClient calls views.open with the trigger_id
      And SlackMessageRenderer.RenderEscalateModal produces an escalation modal
      And SlackEventsApiReceiver returns HTTP 200
      And SlackAuditLogger records request_type "modal_open"

    When user "U-BOB" fills in escalation details and clicks Submit
    Then a HumanDecisionEvent is produced with ActionValue "escalate" and Comment from the modal
      And the event is published to the orchestrator
      And a confirmation reply is posted in the task thread
```

---

## Feature 7: App Mention Invocation

Covers architecture.md section 5.7. App mentions provide an alternative
invocation surface using the same sub-command set.

### Scenario 7.1: Happy path -- app mention creates a task

```gherkin
Feature: App mention invocation

  Scenario: User creates a task via @AgentBot mention
    Given a registered workspace with team_id "T0123ABCD"
      And channel "C-ENG-OPS" is authorized
      And user "U-ALICE" is in an allowed user group
    When user "U-ALICE" posts "@AgentBot ask design persistence layer" in channel "C-ENG-OPS"
    Then Slack delivers an app_mention event to POST /api/slack/events
      And SlackEventsApiReceiver validates the signature and returns HTTP 200
      And SlackInboundIngestor runs authorization and idempotency checks
      And SlackAppMentionHandler strips the bot user-ID prefix
      And the resulting sub-command "ask" with prompt "design persistence layer" is dispatched
      And the orchestrator creates a new task
      And a threaded reply is posted in the same channel where the mention occurred
      And SlackAuditLogger records the exchange with request_type "app_mention"
```

### Scenario 7.2: App mention with status sub-command

```gherkin
  Scenario: User queries task status via app mention
    Given a valid, authorized app_mention event
    When user posts "@AgentBot status TASK-101"
    Then SlackAppMentionHandler parses sub-command "status" with argument "TASK-101"
      And the task status is returned as a threaded reply
```

### Scenario 7.3: App mention with unrecognized sub-command

```gherkin
  Scenario: User sends unrecognized sub-command via app mention
    Given a valid, authorized app_mention event
    When user posts "@AgentBot deploy production"
    Then SlackAppMentionHandler cannot match "deploy" to a known sub-command
      And a threaded reply is posted: "Unknown command 'deploy'. Available commands: ask, status, approve, reject, review, escalate."
      And SlackAuditLogger records the exchange with outcome "error"
```

---

## Feature 8: Agent Questions with Block Kit Buttons

Covers story acceptance criterion AC-2 ("Agent creates a Slack thread with task
status and follow-up questions") and AC-3 ("Human can answer via button or
modal"). Data model references: `AgentQuestion`, `HumanAction`,
`HumanDecisionEvent` (architecture.md section 3.6).

### Scenario 8.1: Happy path -- agent sends question, human clicks button

```gherkin
Feature: Agent question with Block Kit button response

  Scenario: Agent sends a question and human approves via button
    Given task "TASK-101" has an active Slack thread with thread_ts "1716000000.000100"
      And the orchestrator produces an AgentQuestion:
        | Field          | Value                                  |
        | QuestionId     | Q-300                                  |
        | AgentId        | agent-planner-01                       |
        | TaskId         | TASK-101                               |
        | Title          | Persistence Strategy Decision          |
        | Body           | Should we use event sourcing or CRUD?  |
        | Severity       | high                                   |
        | AllowedActions | [Approve, Reject, Need more info]      |
        | ExpiresAt      | (30 minutes from now)                  |
        | CorrelationId  | corr-abc-123                           |
    When SlackOutboundDispatcher processes the AgentQuestion via SendQuestionAsync
    Then SlackMessageRenderer.RenderQuestion produces Block Kit JSON with:
        | Block             | Content                                    |
        | header block      | "Persistence Strategy Decision"            |
        | section block     | "Should we use event sourcing or CRUD?"    |
        | actions block     | 3 buttons: Approve, Reject, Need more info |
        | context block     | Expiry deadline                            |
      And the message is posted as a threaded reply using thread_ts "1716000000.000100"
      And SlackAuditLogger records an outbound entry with request_type "message_send"

    When user "U-ALICE" clicks the "Approve" button
    Then Slack sends an interactive payload to POST /api/slack/interactions
      And SlackEventsApiReceiver returns HTTP 200 within 3 seconds
      And SlackInteractionHandler builds a HumanDecisionEvent:
        | Field            | Value                    |
        | QuestionId       | Q-300                    |
        | ActionValue      | approve                  |
        | Comment          | (null -- no comment required) |
        | Messenger        | slack                    |
        | ExternalUserId   | U-ALICE                  |
        | ExternalMessageId| (message ts)             |
        | ReceivedAt       | (server receive time)    |
        | CorrelationId    | corr-abc-123             |
      And the HumanDecisionEvent is published to the orchestrator
      And the original question message is updated via chat.update to disable buttons
      And the updated message shows "Approved by U-ALICE"
```

### Scenario 8.2: Button with RequiresComment opens a modal

```gherkin
  Scenario: Button click opens a modal when RequiresComment is true
    Given an AgentQuestion with HumanAction where RequiresComment = true for "Need more info"
      And the question is rendered with Block Kit buttons in a Slack thread
    When user "U-BOB" clicks the "Need more info" button
    Then the button click triggers a modal via views.open (not a direct action callback)
      And the modal contains a free-text input for the comment
    When user "U-BOB" enters "What is the expected write throughput?" and clicks Submit
    Then a HumanDecisionEvent is produced with:
        | Field       | Value                                         |
        | ActionValue | need_more_info                                 |
        | Comment     | What is the expected write throughput?          |
      And the event is published to the orchestrator
```

### Scenario 8.3: Agent question respects Block Kit 50-block limit

```gherkin
  Scenario: Long agent question body is truncated to respect Block Kit limits
    Given an AgentQuestion with Body text exceeding 3000 characters
    When SlackMessageRenderer.RenderQuestion processes the question
    Then the section block text is truncated to 3000 characters with a truncation indicator
      And the total block count does not exceed 50 blocks
```

---

## Feature 9: Idempotency and Duplicate Suppression

Covers story acceptance criterion AC-4: "Slack event retries do not duplicate
agent tasks." Idempotency key derivation per architecture.md section 3.4.

### Scenario 9.1: Duplicate Events API retry is suppressed

```gherkin
Feature: Idempotency and duplicate suppression

  Scenario: Duplicate Events API callback is silently dropped
    Given an app_mention event with event_id "Ev01ABC123" was already processed
      And SlackInboundRequestRecord exists with idempotency_key "event:Ev01ABC123" and processing_status "completed"
    When Slack retries the same event with X-Slack-Retry-Num header "1"
    Then SlackEventsApiReceiver returns HTTP 200 immediately (to stop further retries)
      And SlackIdempotencyGuard.TryAcquireAsync returns false for key "event:Ev01ABC123"
      And the event is silently dropped -- no duplicate task is created
      And SlackAuditLogger records the request with outcome "duplicate"
```

### Scenario 9.2: Duplicate slash command (double-click) is suppressed

```gherkin
  Scenario: Double-click on slash command does not create duplicate task
    Given user "U-ALICE" invoked "/agent ask build auth module" with trigger_id "trig-001"
      And SlackInboundRequestRecord exists with key "cmd:T0123ABCD:U-ALICE:/agent ask:trig-001"
    When Slack delivers the same command payload again (same trigger_id)
    Then SlackIdempotencyGuard detects the duplicate
      And the command is silently ACKed without creating a second task
      And SlackAuditLogger records outcome "duplicate"
```

### Scenario 9.3: Duplicate interactive payload is suppressed

```gherkin
  Scenario: Duplicate button click does not produce duplicate HumanDecisionEvent
    Given user "U-ALICE" clicked "Approve" on question Q-300 with trigger_id "trig-002"
      And SlackInboundRequestRecord exists with key "interact:T0123ABCD:U-ALICE:approve-Q-300:trig-002"
    When the same interactive payload is delivered again
    Then SlackIdempotencyGuard returns false
      And no duplicate HumanDecisionEvent is published
      And the duplicate is recorded in audit with outcome "duplicate"
```

### Scenario 9.4: In-progress event is deferred (not dropped)

```gherkin
  Scenario: Event retry arrives while original is still processing
    Given an event with event_id "Ev01DEF456" has processing_status "processing"
    When Slack retries the event with X-Slack-Retry-Num "1"
    Then SlackIdempotencyGuard detects the in-progress status
      And the retry is deferred (not dropped and not re-processed)
      And SlackEventsApiReceiver returns HTTP 200
```

---

## Feature 10: Security -- Signature Verification

Covers tech-spec.md section 5.7: "Signature verification is mandatory."

### Scenario 10.1: Valid signature passes verification

```gherkin
Feature: Slack request signature verification

  Scenario: Request with valid HMAC SHA-256 signature is accepted
    Given a Slack request with header X-Slack-Signature computed from the signing secret
      And the request timestamp is within the 5-minute clock-skew tolerance
    When SlackSignatureValidator processes the request
    Then the signature is accepted
      And the request proceeds to authorization
```

### Scenario 10.2: Invalid signature is rejected

```gherkin
  Scenario: Request with invalid signature is rejected with HTTP 401
    Given a Slack request with an incorrect or tampered X-Slack-Signature header
    When SlackSignatureValidator processes the request
    Then the request is rejected with HTTP 401
      And SlackAuditLogger records an entry with outcome "rejected_signature"
      And no further processing occurs
```

### Scenario 10.3: Missing signature header is rejected

```gherkin
  Scenario: Request missing X-Slack-Signature header is rejected
    Given a Slack request with no X-Slack-Signature header
    When SlackSignatureValidator processes the request
    Then the request is rejected with HTTP 401
      And an audit entry is created with outcome "rejected_signature"
```

### Scenario 10.4: Stale request is rejected (replay protection)

```gherkin
  Scenario: Request with timestamp older than 5 minutes is rejected
    Given a Slack request with X-Slack-Request-Timestamp more than 5 minutes old
    When SlackSignatureValidator checks the timestamp
    Then the request is rejected with HTTP 401 (replay attack protection)
      And SlackAuditLogger records the rejection with outcome "rejected_signature"
```

---

## Feature 11: Security -- Authorization (Three-Layer ACL)

Covers story acceptance criterion AC-5: "Unauthorized channels are rejected."
Three-layer model per architecture.md section 7.2.

### Scenario 11.1: Authorized request passes all three layers

```gherkin
Feature: Three-layer authorization

  Scenario: Request from authorized workspace, channel, and user group passes
    Given SlackWorkspaceConfig for team "T0123ABCD" with enabled = true
      And allowed_channel_ids includes "C-ENG-OPS"
      And allowed_user_group_ids includes "S-LEADS"
      And user "U-ALICE" is a member of group "S-LEADS" (per SlackMembershipResolver)
    When user "U-ALICE" sends a command from channel "C-ENG-OPS" in workspace "T0123ABCD"
    Then SlackAuthorizationFilter passes all three layers
      And the request proceeds to idempotency check and command dispatch
```

### Scenario 11.2: Unregistered workspace is rejected

```gherkin
  Scenario: Request from unregistered workspace is rejected
    Given no SlackWorkspaceConfig exists for team "T-UNKNOWN"
    When a Slack request arrives with team_id "T-UNKNOWN"
    Then SlackAuthorizationFilter rejects the request
      And an ephemeral error is returned: "This workspace is not registered."
      And SlackAuditLogger records outcome "rejected_auth" with team_id "T-UNKNOWN"
```

### Scenario 11.3: Disabled workspace is rejected

```gherkin
  Scenario: Request from disabled workspace is rejected
    Given SlackWorkspaceConfig for team "T-DISABLED" with enabled = false
    When a Slack request arrives with team_id "T-DISABLED"
    Then SlackAuthorizationFilter rejects at the workspace layer
      And an ephemeral error is returned
      And audit records outcome "rejected_auth"
```

### Scenario 11.4: Unauthorized channel is rejected

```gherkin
  Scenario: Request from a channel not in allowed_channel_ids is rejected
    Given SlackWorkspaceConfig with allowed_channel_ids = ["C-ENG-OPS", "C-INCIDENTS"]
    When a command arrives from channel "C-RANDOM" (not in the allowed list)
    Then SlackAuthorizationFilter rejects at the channel layer
      And an ephemeral error is sent to the user: "This channel is not authorized for agent commands."
      And SlackAuditLogger records outcome "rejected_auth" with channel_id "C-RANDOM"
```

### Scenario 11.5: User not in allowed user group is rejected

```gherkin
  Scenario: User not in any allowed user group is rejected
    Given SlackWorkspaceConfig with allowed_user_group_ids = ["S-LEADS", "S-ONCALL"]
      And user "U-INTERN" is not a member of either group (per SlackMembershipResolver)
    When user "U-INTERN" sends "/agent ask do something" from an authorized channel
    Then SlackAuthorizationFilter rejects at the user-group layer
      And an ephemeral error is returned
      And SlackAuditLogger records outcome "rejected_auth" with user_id "U-INTERN"
```

### Scenario 11.6: Membership cache TTL behavior

```gherkin
  Scenario: Recently removed user retains access until cache expires
    Given user "U-REMOVED" was in group "S-LEADS" when cache was last refreshed
      And the membership cache TTL is 5 minutes
      And less than 5 minutes have elapsed since the last cache refresh
    When user "U-REMOVED" sends a command (having been removed from the group in Slack)
    Then SlackMembershipResolver returns cached membership (still authorized)
      And the request is accepted
      And after the cache TTL expires and refreshes, subsequent requests from "U-REMOVED" are rejected
```

---

## Feature 12: Audit Trail and Correlation Queryability

Covers story acceptance criterion AC-6: "Every agent/human exchange is
queryable by correlation ID." SlackAuditEntry defined in architecture.md
section 3.5.

### Scenario 12.1: Full exchange lifecycle is audited

```gherkin
Feature: Audit trail and correlation queryability

  Scenario: Complete task lifecycle produces queryable audit entries
    Given user "U-ALICE" invokes "/agent ask build auth module" (CorrelationId "corr-xyz-001")
      And the orchestrator creates task "TASK-201"
      And the agent posts a thread root message and 2 status updates
      And the agent sends an AgentQuestion with QuestionId "Q-401"
      And user "U-ALICE" clicks "Approve" on Q-401
    When SlackAuditLogger.QueryAsync is called with correlation_id "corr-xyz-001"
    Then the result contains at least 6 SlackAuditEntry records:
        | direction | request_type    | outcome |
        | inbound   | slash_command   | success |
        | outbound  | message_send    | success |
        | outbound  | message_send    | success |
        | outbound  | message_send    | success |
        | outbound  | message_send    | success |
        | inbound   | interaction     | success |
      And every entry has team_id, channel_id, and timestamp populated
      And the inbound slash_command entry has command_text and user_id
      And the interaction entry has user_id "U-ALICE"
```

### Scenario 12.2: Audit entries are queryable by task_id

```gherkin
  Scenario: Audit entries for a task are queryable by task_id
    Given multiple audit entries exist for task "TASK-201"
    When SlackAuditLogger.QueryAsync is called with task_id "TASK-201"
    Then all entries related to task "TASK-201" are returned in chronological order
```

### Scenario 12.3: Audit entries capture rejection details

```gherkin
  Scenario: Rejected requests include rejection details in audit
    Given a command from unauthorized channel "C-RANDOM" was rejected
    When SlackAuditLogger.QueryAsync filters by outcome "rejected_auth"
    Then the entry includes team_id, channel_id "C-RANDOM", user_id, and command_text
```

### Scenario 12.4: Duplicate events are recorded in audit

```gherkin
  Scenario: Duplicate events appear in audit with outcome "duplicate"
    Given 3 Slack retries of event_id "Ev01ABC123" were received and suppressed
    When SlackAuditLogger.QueryAsync filters by outcome "duplicate"
    Then 3 entries exist for the retried event, each with the original event metadata
```

### Scenario 12.5: Audit entries include response payload for outbound messages

```gherkin
  Scenario: Outbound message audit entries include the response payload
    Given an agent question was posted to a Slack thread
    When the corresponding audit entry is queried
    Then the response_payload field contains the serialized Block Kit JSON sent to Slack
```

---

## Feature 13: Threading and Thread Management

Covers story requirement: "Every agent task should have a Slack thread for
context continuity." SlackThreadManager per architecture.md section 2.11.

### Scenario 13.1: First outbound message creates a thread root

```gherkin
Feature: Thread management

  Scenario: First outbound message for a task creates the thread root
    Given task "TASK-301" has no existing SlackThreadMapping
    When SlackOutboundDispatcher sends the first message for task "TASK-301"
    Then SlackThreadManager calls chat.postMessage to create a root message
      And the root message text is the task summary
      And the returned ts is stored as thread_ts in SlackThreadMapping
      And the SlackThreadMapping row has task_id, team_id, channel_id, correlation_id, and agent_id
```

### Scenario 13.2: Connector restart preserves thread mappings

```gherkin
  Scenario: Thread mappings survive connector restart
    Given task "TASK-301" has a SlackThreadMapping persisted in the database
    When the connector process restarts
    Then SlackThreadManager loads existing mappings from persistence
      And subsequent messages for task "TASK-301" are posted to the same thread_ts
      And no duplicate root message is created
```

### Scenario 13.3: Deleted root message triggers fallback channel

```gherkin
  Scenario: Deleted root message triggers fallback channel
    Given task "TASK-301" has thread_ts "1716000000.000200"
      And the root message has been deleted or the channel is archived
      And SlackWorkspaceConfig has fallback_channel_id "C-FALLBACK"
    When SlackOutboundDispatcher attempts to post a reply to thread_ts "1716000000.000200"
      And the Slack API returns an error indicating the message or channel is not found
    Then SlackThreadManager logs a warning to audit
      And a new root message is created in fallback channel "C-FALLBACK"
      And the SlackThreadMapping is updated with the new channel_id and thread_ts
```

---

## Feature 14: Events API URL Verification

One-time handshake per architecture.md section 5.6.

### Scenario 14.1: URL verification challenge is answered

```gherkin
Feature: Events API URL verification

  Scenario: Slack URL verification handshake succeeds
    Given the Slack app is being configured for the first time
    When Slack sends POST /api/slack/events with body:
      """
      { "type": "url_verification", "challenge": "challenge-token-xyz" }
      """
    Then SlackEventsApiReceiver responds with HTTP 200 and body:
      """
      { "challenge": "challenge-token-xyz" }
      """
      And no further event processing occurs
```

---

## Feature 15: Rate Limit Handling

Token-bucket rate limiter per API method tier (architecture.md section 2.12).

### Scenario 15.1: Outbound rate limit triggers backoff

```gherkin
Feature: Rate limit handling

  Scenario: HTTP 429 response triggers Retry-After backoff
    Given SlackOutboundDispatcher sends a chat.postMessage request
      And the Slack API returns HTTP 429 with Retry-After: 5
    When the dispatcher processes the 429 response
    Then outbound dispatch for that API method tier pauses for 5 seconds
      And the message is re-enqueued in the outbound queue
      And the metric slack.ratelimit.backoff_count is incremented
      And after the backoff period, dispatch resumes and the message is delivered
```

### Scenario 15.2: Rate limit does not cause message loss

```gherkin
  Scenario: No messages are lost during rate-limit backoff
    Given 50 outbound messages are queued for the same channel
      And the Slack API starts returning HTTP 429 after 10 messages
    When the dispatcher processes the remaining 40 messages
    Then all 40 messages are eventually delivered (with backoff delays)
      And the durable outbound queue retains them until delivery succeeds
      And zero messages are moved to the dead-letter queue
```

---

## Feature 16: Dead-Letter Queue and Poison Messages

### Scenario 16.1: Message exceeding max retries goes to DLQ

```gherkin
Feature: Dead-letter queue

  Scenario: Outbound message exceeding max retries is moved to DLQ
    Given SlackOutboundDispatcher attempts to send a chat.postMessage
      And the Slack API returns HTTP 500 on every attempt
      And the max retry count is 5 (default)
    When all 5 retries are exhausted
    Then the message is moved to the dead-letter queue (ISlackDeadLetterQueue)
      And SlackAuditLogger records the failure with outcome "error"
      And the health check DLQ depth metric is updated
```

### Scenario 16.2: DLQ messages are inspectable

```gherkin
  Scenario: Operator can inspect dead-letter queue contents
    Given 3 messages are in the dead-letter queue
    When an operator calls ISlackDeadLetterQueue.InspectAsync
    Then all 3 poison messages are returned with their original payload and failure details
```

---

## Feature 17: Socket Mode Transport

Alternative transport for development and non-public-ingress environments
(architecture.md section 2.2.3).

### Scenario 17.1: Socket Mode connects and receives events

```gherkin
Feature: Socket Mode transport

  Scenario: Socket Mode WebSocket connects and receives events
    Given SlackWorkspaceConfig has app_level_token_ref set (Socket Mode enabled)
    When the connector starts
    Then SlackSocketModeReceiver establishes a WebSocket connection to Slack
      And events received over the WebSocket are ACKed within 5 seconds
      And events are enqueued into the durable inbound queue for processing
```

### Scenario 17.2: Socket Mode reconnects with exponential backoff

```gherkin
  Scenario: Socket Mode reconnects after disconnection
    Given the Socket Mode WebSocket connection drops unexpectedly
    When SlackSocketModeReceiver detects the disconnection
    Then it reconnects using exponential backoff (initial 1s, max 30s) with jitter
      And no events are lost during reconnection (durable inbound queue buffers)
      And the health check reflects transient disconnection status
```

### Scenario 17.3: Transport selection is per-workspace

```gherkin
  Scenario: Transport selection depends on app_level_token_ref
    Given workspace "T-WS1" has app_level_token_ref set (Socket Mode)
      And workspace "T-WS2" has app_level_token_ref as null (Events API)
    When the connector initializes both workspaces
    Then workspace "T-WS1" uses SlackSocketModeReceiver
      And workspace "T-WS2" uses SlackEventsApiReceiver
```

---

## Feature 18: Performance -- 3-Second ACK Deadline

Covers tech-spec.md section 5.2: "3-second ACK deadline."

### Scenario 18.1: Slash command ACK within 3 seconds (async path)

```gherkin
Feature: 3-second ACK deadline

  Scenario: Async slash command is ACKed within 3 seconds
    Given a valid "/agent ask generate plan" command arrives
    When SlackEventsApiReceiver processes the request
    Then HTTP 200 is returned within 3 seconds
      And all command processing (auth, dispatch, orchestrator call) happens asynchronously after the ACK
```

### Scenario 18.2: Modal command ACK within 3 seconds (synchronous fast-path)

```gherkin
  Scenario: Modal slash command completes fast-path within 3 seconds
    Given a valid "/agent review TASK-42" command arrives
    When SlackEventsApiReceiver runs the synchronous fast-path:
      | Step                    | Constraint             |
      | Signature validation    | In-memory              |
      | Authorization           | Cached membership      |
      | Idempotency check       | Connection-pooled DB   |
      | views.open              | SlackDirectApiClient   |
    Then all four steps and the HTTP 200 response complete within 3 seconds
```

### Scenario 18.3: Interactive payload ACK within 3 seconds

```gherkin
  Scenario: Button click interactive payload is ACKed within 3 seconds
    Given a user clicks a Block Kit button in a Slack thread
    When the interactive payload arrives at /api/slack/interactions
    Then SlackEventsApiReceiver returns HTTP 200 within 3 seconds
      And the HumanDecisionEvent construction and publishing happen asynchronously
```

---

## Feature 19: Connector Recovery

### Scenario 19.1: Connector recovers within 30 seconds

```gherkin
Feature: Connector recovery

  Scenario: Connector restarts and recovers within 30 seconds
    Given the connector process was terminated
      And the database contains active SlackThreadMapping and SlackWorkspaceConfig rows
    When the connector process restarts
    Then all workspace configurations are loaded from the database
      And all active thread mappings are loaded
      And Socket Mode WebSocket connections are re-established for applicable workspaces
      And the connector is fully operational within 30 seconds
      And outbound messages queued before the crash are drained from the durable outbound queue
```

### Scenario 19.2: Durable queues survive restart

```gherkin
  Scenario: Messages in durable queues are not lost on restart
    Given 5 outbound messages were in the durable outbound queue when the connector crashed
    When the connector restarts
    Then all 5 messages are delivered to Slack from the recovered queue
      And no messages are duplicated or lost
```

---

## Feature 20: Observability

### Scenario 20.1: OpenTelemetry traces span the inbound pipeline

```gherkin
Feature: Observability

  Scenario: Inbound command produces a complete OpenTelemetry trace
    Given the OTel ActivitySource "AgentSwarm.Messaging.Slack" is configured
    When a slash command is processed end-to-end
    Then the trace includes spans for:
      | Span                 |
      | inbound_receive      |
      | signature_validation |
      | authorization        |
      | idempotency_check    |
      | command_dispatch     |
      And each span carries correlation_id, task_id, agent_id, team_id, and channel_id as attributes
```

### Scenario 20.2: Health checks report component status

```gherkin
  Scenario: ASP.NET Core health checks report operational status
    Given the connector is running normally
    When the health check endpoint is queried
    Then the response includes:
      | Check                | Status  |
      | Slack API connectivity | Healthy |
      | Outbound queue depth   | Healthy |
      | DLQ depth              | Healthy |
```

### Scenario 20.3: Metrics are emitted for key operations

```gherkin
  Scenario: Key operational metrics are emitted
    Given the connector processes inbound and outbound messages
    Then the following metrics are emitted via System.Diagnostics.Metrics:
      | Metric name                        | Type      |
      | slack.inbound.count                | Counter   |
      | slack.outbound.count               | Counter   |
      | slack.outbound.latency_ms          | Histogram |
      | slack.idempotency.duplicate_count  | Counter   |
      | slack.auth.rejected_count          | Counter   |
      | slack.ratelimit.backoff_count      | Counter   |
```

---

## Feature 21: Data Retention Cleanup

Per tech-spec.md section 2.7: 30-day retention.

### Scenario 21.1: Records older than 30 days are purged

```gherkin
Feature: Data retention cleanup

  Scenario: Background job purges records older than 30 days
    Given SlackAuditEntry rows exist with timestamp older than 30 days
      And SlackInboundRequestRecord rows exist with first_seen_at older than 30 days
    When the retention cleanup job runs
    Then all SlackAuditEntry rows older than 30 days are deleted
      And all SlackInboundRequestRecord rows older than 30 days are deleted
      And records newer than 30 days are retained
```

### Scenario 21.2: Thread mappings for completed tasks are retained

```gherkin
  Scenario: Thread mappings are not purged by the retention job
    Given SlackThreadMapping rows exist for completed tasks older than 30 days
    When the retention cleanup job runs
    Then SlackThreadMapping rows are NOT deleted (they support long-running thread references)
```

---

## Feature 22: Multi-Workspace Support

Covers architecture.md section 3.1 and `MaxWorkspaces` config.

### Scenario 22.1: Commands from multiple workspaces are handled independently

```gherkin
Feature: Multi-workspace support

  Scenario: Two workspaces operate independently
    Given SlackWorkspaceConfig entries for team "T-WS1" and team "T-WS2"
      And each workspace has its own allowed channels and user groups
    When user "U-1" in workspace "T-WS1" types "/agent ask plan auth"
      And user "U-2" in workspace "T-WS2" types "/agent ask plan billing"
    Then two independent tasks are created with separate CorrelationIds
      And each task's Slack thread is created in its respective workspace's default channel
      And audit entries correctly reflect the respective team_id for each exchange
```

### Scenario 22.2: Exceeding MaxWorkspaces limit is prevented

```gherkin
  Scenario: Workspace registration beyond MaxWorkspaces is rejected
    Given SlackConnectorOptions.MaxWorkspaces = 15
      And 15 workspaces are already registered
    When an attempt is made to register a 16th workspace
    Then the registration is rejected with an appropriate error
```

---

## Feature 23: Error Handling Edge Cases

### Scenario 23.1: Orchestrator unavailable during task creation

```gherkin
Feature: Error handling edge cases

  Scenario: Orchestrator is temporarily unavailable
    Given a valid "/agent ask plan something" command is processed
      And the orchestrator service is temporarily unreachable
    When SlackCommandHandler calls the orchestrator
    Then the command is retried per the retry policy (exponential backoff, max 5 attempts)
      And if all retries fail, an ephemeral error is returned to the user
      And the message is moved to the DLQ
      And SlackAuditLogger records outcome "error" with error_detail
```

### Scenario 23.2: Malformed slash command text

```gherkin
  Scenario: Unrecognized sub-command in /agent
    Given a valid, authorized Slack request
    When user types "/agent deploy now"
    Then SlackCommandHandler does not match "deploy" to any known sub-command
      And an ephemeral error is returned: "Unknown sub-command 'deploy'. Available: ask, status, approve, reject, review, escalate."
      And SlackAuditLogger records the exchange with outcome "error"
```

### Scenario 23.3: Concurrent approve and reject on the same question

```gherkin
  Scenario: Two users click different buttons on the same question simultaneously
    Given AgentQuestion Q-500 is displayed with Approve and Reject buttons
    When user "U-ALICE" clicks "Approve" at roughly the same time user "U-BOB" clicks "Reject"
    Then the first interaction to pass SlackIdempotencyGuard is processed
      And the second interaction is treated as a duplicate (same question, different action)
        Or the orchestrator receives both decisions and applies its own conflict resolution
      And both interactions are recorded in the audit trail
```

---

## Feature 24: Secret Management

### Scenario 24.1: Bot token is resolved from secret provider

```gherkin
Feature: Secret management

  Scenario: Bot token is resolved at runtime from the secret provider
    Given SlackWorkspaceConfig.bot_token_secret_ref = "keyvault://slack-bot-token"
    When the connector starts and initializes the Slack API client
    Then the bot token is resolved from Azure Key Vault (or configured provider)
      And the resolved token is held in memory only -- never logged or persisted
      And outbound Slack API calls use the resolved token for authentication
```

### Scenario 24.2: Signing secret rotation with dual-secret validation

```gherkin
  Scenario: Signing secret rotation accepts old and new secrets during grace period
    Given the signing secret is being rotated
      And SlackSignatureValidator supports dual-secret validation
    When a request arrives signed with the old signing secret
    Then the request is accepted during the configured grace period
      And requests signed with the new secret are also accepted
      And after the grace period, only the new secret is accepted
```

---

## Scenario Cross-Reference to Acceptance Criteria

| Acceptance Criterion | Primary Scenarios |
|---|---|
| AC-1: User can invoke `/agent ask generate implementation plan for persistence failover` | 1.1, 1.2, 1.3 |
| AC-2: Agent creates a Slack thread with task status and follow-up questions | 1.2, 1.3, 8.1, 13.1 |
| AC-3: Human can answer via button or modal | 3.1, 5.1, 8.1, 8.2 |
| AC-4: Slack event retries do not duplicate agent tasks | 9.1, 9.2, 9.3, 9.4 |
| AC-5: Unauthorized channels are rejected | 11.4, 11.2, 11.3, 11.5 |
| AC-6: Every agent/human exchange is queryable by correlation ID | 12.1, 12.2, 12.3, 12.4, 12.5 |

---

## Scenario Cross-Reference to Story Requirements

| Requirement Area | Scenarios |
|---|---|
| Protocol (Events API / Socket Mode / Web API) | 1.1, 7.1, 14.1, 17.1, 17.3 |
| C# library (SlackNet preferred) | (implementation detail; validated via build) |
| App features (slash commands, app mentions, modals, Block Kit, threads) | 1.1, 5.1, 6.1, 7.1, 8.1, 13.1 |
| Commands (/agent ask, status, approve, reject, review, escalate) | 1.1, 2.1, 3.1, 4.1, 5.1, 6.1 |
| Threading (every task has a Slack thread) | 1.2, 13.1, 13.2, 13.3 |
| Human response (buttons and modals map to HumanDecisionEvent) | 3.1, 5.1, 8.1, 8.2 |
| Reliability (idempotency key from Slack event ID) | 9.1, 9.2, 9.3, 9.4 |
| Performance (ACK quickly, process async) | 18.1, 18.2, 18.3 |
| Security (verify signatures, restrict workspace/channel/user) | 10.1-10.4, 11.1-11.6, 24.1, 24.2 |
| Audit (persist team ID, channel ID, thread_ts, user ID, command, response) | 12.1-12.5, 21.1 |
