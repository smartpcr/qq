# E2E Test Scenarios — Microsoft Teams Messenger Support

**Story:** `qq:MICROSOFT-TEAMS-MESS`
**Version:** 1.1 — Iteration 2

---

## Feature: Personal Chat — Agent Task Creation

Covers the primary interaction where a human creates agent tasks through personal chat with the Teams bot.

```gherkin
Feature: Personal Chat — Agent Task Creation

  Background:
    Given the Teams bot is registered with a valid Bot Framework app registration
    And the bot is installed for user "alice@contoso.com" in tenant "contoso-tenant-id"
    And the user "alice@contoso.com" has RBAC role "operator"
    And the AgentSwarm.Messaging.Worker host service is running
    And a conversation reference is stored for user "alice@contoso.com"

  Scenario: User creates an agent task via personal chat
    Given user "alice@contoso.com" opens a personal chat with the Teams bot
    When the user sends the message "agent ask create e2e test scenarios for update service"
    Then the bot acknowledges receipt within 3 seconds
    And a MessengerEvent of type "AgentTaskRequest" is enqueued to the inbound queue
    And the event contains:
      | Field          | Value                                          |
      | CorrelationId  | <non-empty UUID>                               |
      | ExternalUserId | <alice's AadObjectId>                           |
      | TaskId         | <newly generated>                              |
      | Body           | create e2e test scenarios for update service   |
    And an immutable audit record is persisted for the command

  Scenario: User creates a task via team channel mention
    Given user "bob@contoso.com" is in team channel "#ops-swarm"
    And the bot is installed in the team
    When the user sends "@AgentBot agent ask design persistence layer" in the channel
    Then the bot strips the @mention and parses "agent ask design persistence layer"
    And a MessengerEvent of type "AgentTaskRequest" is enqueued
    And the bot replies in a thread under the original message

  Scenario: User sends an unrecognised command
    Given user "alice@contoso.com" opens a personal chat with the Teams bot
    When the user sends "hello there"
    Then the bot replies with a help card listing available commands:
      | Command       | Description        |
      | agent ask     | Create task        |
      | agent status  | Query status       |
      | approve       | Approve action     |
      | reject        | Reject action      |
      | escalate      | Escalate issue     |
      | pause         | Pause agent        |
      | resume        | Resume agent       |
    And no MessengerEvent is enqueued

  Scenario: User queries agent status
    Given agent "planner-agent-01" is executing task "TASK-42"
    When user "alice@contoso.com" sends "agent status"
    Then the bot replies with an Adaptive Card containing:
      | Field      | Value                |
      | Agent      | planner-agent-01     |
      | Task       | TASK-42              |
      | Status     | In Progress          |
      | StartedAt  | <ISO 8601 timestamp> |
```

---

## Feature: Proactive Messaging — Blocking Questions

Covers the agent-initiated flow where an autonomous agent sends a blocking question to a specific human through Teams.

```gherkin
Feature: Proactive Messaging — Blocking Questions

  Background:
    Given the Teams bot has a stored conversation reference for user "alice@contoso.com"
    And user "alice@contoso.com" has RBAC role "operator"

  Scenario: Agent sends a blocking question to a specific user
    Given agent "arch-agent-03" needs human input for task "TASK-77"
    And the agent publishes an AgentQuestion:
      | Field          | Value                                      |
      | QuestionId     | Q-501                                      |
      | AgentId        | arch-agent-03                              |
      | TaskId         | TASK-77                                    |
      | Title          | Database migration strategy                |
      | Body           | Should we use blue-green or rolling deploy? |
      | Severity       | High                                       |
      | AllowedActions | Approve, Reject, Need more info            |
      | ExpiresAt      | <now + 24 hours>                           |
      | CorrelationId  | <UUID>                                     |
    When the OutboxRetryEngine picks up the question from the outbound queue
    Then the bot sends a proactive Adaptive Card to "alice@contoso.com" using the stored conversation reference
    And the card is delivered within 3 seconds (P95)
    And the Adaptive Card displays the Title and Body
    And the card contains action buttons matching AllowedActions:
      | Button          | ActionValue    |
      | Approve         | approve        |
      | Reject          | reject         |
      | Need more info  | need_more_info |
    And the outbound notification is marked as delivered in the persistence store
    And an audit record is persisted with CorrelationId

  Scenario: Agent sends a blocking question to a team channel
    Given agent "release-agent-01" needs approval for task "TASK-88"
    And the bot has a stored conversation reference for channel "#release-gates"
    When the agent publishes an AgentQuestion targeting channel "#release-gates"
    Then the bot sends an Adaptive Card to the channel using the stored conversation reference
    And the card is threaded under the original task context if one exists

  Scenario: Proactive message delivery after service restart
    Given the AgentSwarm.Messaging.Worker host was restarted
    And conversation references were persisted before the restart
    When agent "test-agent-02" publishes an AgentQuestion for user "alice@contoso.com"
    Then the ProactiveNotifier rehydrates the conversation reference from the ConversationReferenceStore
    And the Adaptive Card is delivered successfully to the user
    And connector recovery completes within 30 seconds

  Scenario: Proactive message to user who uninstalled the bot
    Given user "charlie@contoso.com" had the bot installed previously
    And a conversation reference exists for "charlie@contoso.com"
    But user "charlie@contoso.com" has since uninstalled the Teams bot
    When agent "planner-agent-01" publishes an AgentQuestion for "charlie@contoso.com"
    Then the Bot Framework returns a 403/Forbidden error
    And the notification is moved to the dead-letter queue
    And an audit record is persisted indicating delivery failure
    And the stored conversation reference is marked as stale
```

---

## Feature: Adaptive Card Approvals

Covers human approval and rejection through Adaptive Card action buttons.

```gherkin
Feature: Adaptive Card Approvals

  Background:
    Given user "alice@contoso.com" has RBAC role "approver"
    And agent "release-agent-01" previously sent an Adaptive Card for question "Q-601"
    And the card has action buttons: Approve, Reject

  Scenario: Human approves through Adaptive Card action
    When user "alice@contoso.com" clicks the "Approve" button on the Adaptive Card
    Then a HumanDecisionEvent is created:
      | Field             | Value            |
      | QuestionId        | Q-601            |
      | ActionValue       | approve          |
      | Comment           | <null>           |
      | Messenger         | teams            |
      | ExternalUserId    | alice@contoso.com |
      | ExternalMessageId | <Teams activity ID> |
      | CorrelationId     | <original UUID>  |
    And the event is enqueued to the inbound queue
    And the original Adaptive Card is updated to show "Approved by alice@contoso.com"
    And the card action buttons are disabled (card replaced with read-only version)
    And an immutable audit record is persisted

  Scenario: Human rejects with a required comment
    Given the AgentQuestion for "Q-602" has AllowedAction "Reject" with RequiresComment = true
    When user "alice@contoso.com" clicks "Reject" on the Adaptive Card
    Then the bot presents an input field for the rejection reason
    When the user submits the comment "Insufficient test coverage"
    Then a HumanDecisionEvent is created with:
      | Field       | Value                      |
      | QuestionId  | Q-602                      |
      | ActionValue | reject                     |
      | Comment     | Insufficient test coverage |
    And the original card is updated to show "Rejected by alice@contoso.com: Insufficient test coverage"
    And an immutable audit record is persisted

  Scenario: Adaptive Card action after question has expired
    Given the AgentQuestion "Q-603" has ExpiresAt in the past
    When user "alice@contoso.com" clicks "Approve" on the expired card
    Then the bot replies with an error message: "This question has expired."
    And no HumanDecisionEvent is created
    And the card is updated to show "Expired"

  Scenario: Duplicate Adaptive Card action submission
    Given user "alice@contoso.com" already submitted "Approve" for question "Q-601"
    When user "alice@contoso.com" clicks "Approve" again (e.g., network retry)
    Then the duplicate submission is suppressed by idempotency checks
    And no duplicate HumanDecisionEvent is created
    And the bot responds with the previously recorded decision
```

---

## Feature: Message Update and Delete

Covers updating and deleting previously sent Adaptive Cards.

```gherkin
Feature: Message Update and Delete

  Background:
    Given the bot has a stored conversation reference for user "alice@contoso.com"
    And the bot previously sent Adaptive Card with activity ID "act-901" for question "Q-701"

  Scenario: Bot updates an existing approval card after decision
    When user "alice@contoso.com" approves question "Q-701"
    Then the bot calls UpdateActivityAsync with activity ID "act-901"
    And the card is replaced with a read-only summary card showing:
      | Field     | Value                         |
      | Status    | Approved                      |
      | DecidedBy | alice@contoso.com             |
      | DecidedAt | <ISO 8601 timestamp>          |
    And the updated card no longer contains action buttons

  Scenario: Bot deletes a recalled question card
    Given agent "arch-agent-03" recalls question "Q-701" before a human responds
    When the TeamsMessengerConnector processes the recall event
    Then the bot calls DeleteActivityAsync with activity ID "act-901"
    And the card is removed from the conversation
    And an audit record is persisted for the deletion

  Scenario: Update fails due to expired activity reference
    Given the activity ID "act-901" is no longer valid (e.g., message too old)
    When the bot attempts to update the card
    Then the Bot Framework returns an error
    And the failure is logged with the CorrelationId
    And the bot sends a new replacement card to the user instead
    And the outbound retry policy does not infinitely retry the stale update
```

---

## Feature: Conversation Reference Persistence

Covers the storage and lifecycle of Teams conversation references for proactive messaging.

```gherkin
Feature: Conversation Reference Persistence

  Background:
    Given the Teams bot is registered and running

  Scenario: Conversation reference is stored on first interaction
    Given user "dave@contoso.com" (AadObjectId "aad-obj-dave-001") has never interacted with the bot before
    When user "dave@contoso.com" sends "agent status" in personal chat
    Then a conversation reference is extracted from the incoming Activity
    And the reference is persisted to the durable store keyed by AadObjectId "aad-obj-dave-001" and TenantId "contoso-tenant-id"
    And the reference includes:
      | Field            | Value                  |
      | ServiceUrl       | <Bot Framework URL>    |
      | ConversationId   | <Teams conversation>   |
      | AadObjectId      | aad-obj-dave-001       |
      | TenantId         | contoso-tenant-id      |
      | BotId            | <bot app ID>           |

  Scenario: Conversation reference is updated on subsequent interactions
    Given a conversation reference already exists for user "dave@contoso.com"
    When user "dave@contoso.com" sends a new message from a different Teams client
    And the ServiceUrl has changed
    Then the stored conversation reference is updated with the new ServiceUrl
    And the previous reference is superseded (not deleted, for audit)

  Scenario: Conversation references survive full service restart
    Given conversation references exist for 50 users
    When the AgentSwarm.Messaging.Worker host service is restarted
    Then all 50 conversation references are available from the persistence store
    And proactive messages can be sent to all 50 users without re-interaction

  Scenario: Channel conversation reference is stored on bot install
    Given the bot is installed in team "Engineering" channel "#general"
    When the bot receives an OnTeamsMembersAddedAsync event
    Then a conversation reference is stored for channel "#general"
    And subsequent proactive messages to "#general" use the stored reference
```

---

## Feature: Security — Tenant and User Validation

Covers enforcement of tenant isolation, user identity, and RBAC.

```gherkin
Feature: Security — Tenant and User Validation

  Background:
    Given the Teams bot is configured with allowed tenant IDs: ["contoso-tenant-id"]
    And RBAC roles are configured:
      | Role     | Permissions                                      |
      | operator | agent ask, agent status, pause, resume, escalate |
      | approver | approve, reject                                  |
      | viewer   | agent status                                     |

  Scenario: Message from unauthorized tenant is rejected
    Given user "mallory@evil-corp.com" is in tenant "evil-corp-tenant-id"
    When user "mallory@evil-corp.com" sends "agent ask hack the system" to the bot
    Then the bot validates the incoming Activity's tenant ID
    And the tenant ID "evil-corp-tenant-id" is not in the allowed list
    And the bot returns an Unauthorized response
    And no MessengerEvent is enqueued
    And an audit record is persisted:
      | Field     | Value                       |
      | Event     | UnauthorizedTenantRejected  |
      | TenantId  | evil-corp-tenant-id         |
      | UserId    | mallory@evil-corp.com       |

  Scenario: User without the required RBAC role is denied
    Given user "viewer-only@contoso.com" has RBAC role "viewer"
    When user "viewer-only@contoso.com" sends "approve" in personal chat
    Then the bot checks the user's RBAC permissions
    And the "approve" command requires role "approver"
    And the bot replies: "You do not have permission to perform this action."
    And no HumanDecisionEvent is created
    And an audit record is persisted for the access denial

  Scenario: Proactive message to user without bot installation is pre-checked and rejected
    Given user "no-install@contoso.com" is in tenant "contoso-tenant-id"
    But the Teams bot is not installed for user "no-install@contoso.com"
    And no conversation reference exists in ConversationReferenceStore for "no-install@contoso.com"
    When the ProactiveNotifier attempts to send a proactive message to "no-install@contoso.com"
    Then the ProactiveNotifier detects the missing conversation reference before calling Bot Framework
    And no proactive message attempt is made to Bot Framework
    And the notification is moved directly to the dead-letter queue
    And an audit record is persisted indicating the user has no active installation
    And the stale reference is not retried (per tech-spec §4.3 R-2)

  Scenario: Bot Framework token validation rejects forged activity
    Given an attacker sends a forged HTTP POST to the bot's messaging endpoint
    And the Authorization header contains an invalid JWT token
    When the Bot Framework authentication middleware processes the request
    Then the request is rejected with HTTP 401
    And no processing occurs
    And the attempt is logged in the security audit trail

  Scenario: Multi-tenant isolation — one tenant cannot see another's data
    Given user "alice@contoso.com" in tenant "contoso-tenant-id" has active tasks
    And user "bob@fabrikam.com" in tenant "fabrikam-tenant-id" has active tasks
    And both tenants are in the allowed list
    When user "alice@contoso.com" sends "agent status"
    Then the response includes only tasks scoped to tenant "contoso-tenant-id"
    And no data from tenant "fabrikam-tenant-id" is visible
```

---

## Feature: Reliability — Retry and Recovery

Covers transient failure handling, durable queuing, and connector recovery.

```gherkin
Feature: Reliability — Retry and Recovery

  Background:
    Given the AgentSwarm.Messaging.Worker host service is running
    And the OutboxRetryEngine is configured with retry policy:
      | Setting          | Value              |
      | MaxRetries       | 5                  |
      | InitialBackoff   | 1 second           |
      | BackoffMultiplier| 2                  |
      | MaxBackoff       | 30 seconds         |

  Scenario: Transient Bot Framework failure triggers retry
    Given agent "test-agent-01" publishes an AgentQuestion
    When the bot attempts to send the Adaptive Card
    And the Bot Framework returns HTTP 429 (Too Many Requests)
    Then the notification is retried after the initial backoff
    And the retry count is incremented
    And on the second attempt, the card is delivered successfully
    And the notification is marked as delivered
    And the total retry sequence is logged with CorrelationId

  Scenario: Persistent failure exhausts retries and dead-letters
    Given agent "test-agent-01" publishes an AgentQuestion
    When the bot attempts to send the Adaptive Card
    And the Bot Framework returns HTTP 500 on all 5 retry attempts
    Then the notification is moved to the dead-letter queue after the 5th attempt
    And an alert is raised for the operations team
    And an audit record marks the notification as "DeadLettered"

  Scenario: Outbound notifications survive service crash
    Given 3 notifications are in the durable outbound queue
    When the AgentSwarm.Messaging.Worker process crashes unexpectedly
    And the process is restarted by the orchestrator
    Then the 3 pending notifications are recovered from the durable queue
    And delivery resumes from where it left off
    And no notifications are lost
    And connector recovery completes within 30 seconds

  Scenario: Duplicate inbound webhook is suppressed
    Given user "alice@contoso.com" sends "approve" for question "Q-801"
    And the Bot Framework delivers the activity twice (network retry)
    When the gateway processes both deliveries
    Then the first delivery creates a HumanDecisionEvent
    And the second delivery is detected as a duplicate via the Activity ID
    And no duplicate event is created
    And the duplicate is logged for observability

  Scenario: Inbound queue preserves ordering per conversation
    Given user "alice@contoso.com" sends the following commands in order:
      | Order | Command       |
      | 1     | pause         |
      | 2     | resume        |
      | 3     | agent status  |
    When all three messages are processed
    Then they are dequeued and processed in the order received
    And the agent is first paused, then resumed
    And the status response reflects the resumed state

  Scenario: Adaptive Card update retry on transient failure
    Given user "alice@contoso.com" approves question "Q-901"
    And the bot attempts to update the original card
    When UpdateActivityAsync fails with a transient error
    Then the update is retried according to the retry policy
    And on retry success, the card reflects the approved state
    And the user does not see a duplicate approval confirmation
```

---

## Feature: Compliance — Immutable Audit Trail

Covers the audit logging requirements for enterprise compliance review.

```gherkin
Feature: Compliance — Immutable Audit Trail

  Scenario: All inbound commands are audit-logged
    When user "alice@contoso.com" sends "agent ask create e2e test scenarios for update service"
    Then an immutable audit record is persisted containing:
      | Field             | Value                                              |
      | Timestamp         | <UTC ISO 8601>                                     |
      | EventType         | InboundCommand                                     |
      | Messenger         | teams                                              |
      | TenantId          | contoso-tenant-id                                  |
      | UserId            | alice@contoso.com                                  |
      | Command           | agent ask                                          |
      | Payload           | create e2e test scenarios for update service       |
      | CorrelationId     | <UUID>                                             |
      | ConversationId    | <Teams conversation ID>                            |

  Scenario: All outbound notifications are audit-logged
    When the bot sends an Adaptive Card for question "Q-1001"
    Then an immutable audit record is persisted containing:
      | Field             | Value                     |
      | EventType         | OutboundNotification      |
      | QuestionId        | Q-1001                    |
      | TargetUserId      | alice@contoso.com         |
      | DeliveryStatus    | Delivered                 |
      | ActivityId        | <Teams activity ID>       |
      | CorrelationId     | <UUID>                    |

  Scenario: Approval decisions are audit-logged with full context
    When user "alice@contoso.com" approves question "Q-1001" with comment "LGTM"
    Then an immutable audit record is persisted containing:
      | Field             | Value             |
      | EventType         | HumanDecision     |
      | QuestionId        | Q-1001            |
      | ActionValue       | approve           |
      | Comment           | LGTM              |
      | UserId            | alice@contoso.com |
      | CorrelationId     | <UUID>            |

  Scenario: Security rejections are audit-logged
    When an unauthorized tenant attempts to message the bot
    Then an immutable audit record is persisted with EventType "SecurityRejection"
    And the record includes the rejected tenant ID and user identity

  Scenario: Audit records cannot be modified or deleted
    Given audit records exist for the past 30 days
    When an administrator attempts to modify an existing audit record
    Then the operation is rejected by the persistence layer
    And the record remains unchanged
    And the modification attempt is itself audit-logged

  Scenario: Audit trail supports correlation-based query
    Given 15 audit records exist with CorrelationId "corr-xyz-123"
    When a compliance reviewer queries the audit store by CorrelationId "corr-xyz-123"
    Then all 15 records are returned in chronological order
    And the full lifecycle from command → question → decision → card update is traceable
```

---

## Feature: Performance — Card Delivery SLA

Covers the P95 latency requirement for Adaptive Card delivery.

```gherkin
Feature: Performance — Card Delivery SLA

  Scenario: P95 Adaptive Card delivery under 3 seconds
    Given 100 AgentQuestions are published to the outbound queue in a burst
    When the OutboxRetryEngine processes all 100 notifications
    Then at least 95 of the 100 Adaptive Cards are delivered within 3 seconds of queue pickup
    And the delivery latency for each card is recorded as a metric
    And no cards are lost

  Scenario: Concurrent user interactions do not degrade latency
    Given 50 users are simultaneously interacting with the bot
    When each user sends "agent status"
    Then all 50 responses are returned within 3 seconds
    And the bot does not throttle legitimate requests

  Scenario: Large Adaptive Card renders within SLA
    Given an AgentQuestion has a Body of 2000 characters
    And AllowedActions contains 5 action buttons
    When the bot sends the Adaptive Card
    Then the card is delivered within 3 seconds of queue pickup
    And the card renders correctly in Teams desktop and mobile clients
```

---

## Feature: Escalation, Pause, and Resume Commands

Covers the operational commands for incident management and agent lifecycle.

```gherkin
Feature: Escalation, Pause, and Resume Commands

  Background:
    Given user "alice@contoso.com" has RBAC role "operator"
    And agent "release-agent-01" is executing task "TASK-55"

  Scenario: User escalates an incident
    When user "alice@contoso.com" sends "escalate"
    Then the bot presents a card prompting for:
      | Field       | Type     |
      | Agent       | dropdown |
      | Severity    | dropdown |
      | Description | text     |
    When the user submits the escalation form with:
      | Field       | Value                          |
      | Agent       | release-agent-01               |
      | Severity    | Critical                       |
      | Description | Release pipeline is blocked    |
    Then a MessengerEvent of type "Escalation" is enqueued
    And the event includes CorrelationId, AgentId, and severity
    And an audit record is persisted

  Scenario: User pauses an agent
    When user "alice@contoso.com" sends "pause"
    Then the bot presents a list of active agents
    When the user selects "release-agent-01"
    Then a MessengerEvent of type "PauseAgent" is enqueued for "release-agent-01"
    And the bot confirms: "Agent release-agent-01 has been paused."
    And an audit record is persisted

  Scenario: User resumes a paused agent
    Given agent "release-agent-01" is in state "Paused"
    When user "alice@contoso.com" sends "resume"
    Then the bot presents a list of paused agents
    When the user selects "release-agent-01"
    Then a MessengerEvent of type "ResumeAgent" is enqueued for "release-agent-01"
    And the bot confirms: "Agent release-agent-01 has been resumed."
    And an audit record is persisted

  Scenario: Pause command with no active agents
    Given no agents are currently running
    When user "alice@contoso.com" sends "pause"
    Then the bot replies: "No active agents to pause."
```

---

## Feature: Adaptive Card — Incident Summary and Release Gates

Covers specialized card types for operational workflows.

```gherkin
Feature: Adaptive Card — Incident Summary and Release Gates

  Scenario: Agent sends an incident summary card
    Given agent "incident-agent-01" has completed incident analysis for task "INC-200"
    When the agent publishes an AgentQuestion with Title "Incident Summary"
    And the Severity is "Critical"
    Then the bot sends an Adaptive Card containing:
      | Section          | Content                              |
      | Header           | 🔴 Critical Incident — INC-200       |
      | Root Cause       | <from AgentQuestion.Body>            |
      | Affected Systems | <parsed from Body>                   |
      | Actions          | Acknowledge, Escalate, Need more info|
    And the card is delivered to the configured incident channel

  Scenario: Release gate approval card with multiple approvers
    Given agent "release-agent-01" requires approval from 2 approvers
    And the AgentQuestion is sent to both "alice@contoso.com" and "bob@contoso.com"
    When "alice@contoso.com" clicks "Approve"
    Then alice's card is updated to show "Approved by alice@contoso.com (1/2)"
    And bob's card remains actionable
    When "bob@contoso.com" clicks "Approve"
    Then both cards are updated to show "Fully Approved (2/2)"
    And a HumanDecisionEvent with ActionValue "approve" is created
    And an audit trail records both individual approvals
```

---

## Feature: Bot Installation and Conversation Discovery

Covers the Teams app lifecycle events that trigger conversation reference creation.

```gherkin
Feature: Bot Installation and Conversation Discovery

  Scenario: Bot installed in personal scope
    When user "eve@contoso.com" installs the Teams bot in personal scope
    Then the bot receives an installationUpdate activity
    And a conversation reference is persisted for "eve@contoso.com"
    And the bot sends a welcome Adaptive Card explaining available commands

  Scenario: Bot installed in a team
    When an admin installs the bot in team "Platform Engineering"
    Then the bot receives OnTeamsMembersAddedAsync
    And a conversation reference is stored for the team's general channel
    And the bot posts a welcome message to the channel

  Scenario: Bot uninstalled from personal scope
    Given a conversation reference exists for "eve@contoso.com"
    When user "eve@contoso.com" uninstalls the Teams bot
    Then the bot receives an installationUpdate activity with action "remove"
    And the conversation reference is marked as inactive
    And the reference is retained for audit purposes but not used for proactive messaging

  Scenario: Bot added to a new channel in an existing team
    Given the bot is installed in team "Platform Engineering"
    When the bot is added to channel "#deployments"
    Then a conversation reference is stored for channel "#deployments"
    And the bot can now send proactive messages to "#deployments"
```

---

## Feature: Correlation and Traceability

Covers the end-to-end tracing requirements per FR-004.

```gherkin
Feature: Correlation and Traceability

  Scenario: Full lifecycle traceability from command to completion
    Given user "alice@contoso.com" sends "agent ask create e2e test scenarios for update service"
    And the command is assigned CorrelationId "corr-001"
    When agent "test-agent-01" picks up the task
    And the agent sends a blocking question with CorrelationId "corr-001"
    And user "alice@contoso.com" approves the question
    And the agent completes the task
    Then all MessengerEvents in the chain share CorrelationId "corr-001"
    And the audit trail for "corr-001" shows:
      | Step | EventType             |
      | 1    | InboundCommand        |
      | 2    | OutboundNotification  |
      | 3    | HumanDecision         |
      | 4    | TaskCompletion        |

  Scenario: Every message includes required tracing fields
    When any MessengerEvent is created
    Then it contains all required fields:
      | Field          | Constraint     |
      | CorrelationId  | Non-empty UUID |
      | AgentId        | Non-empty      |
      | TaskId         | Non-empty      |
      | ConversationId | Non-empty      |
      | Timestamp      | UTC ISO 8601   |
```

---

## Feature: Edge Cases and Error Handling

Covers boundary conditions and failure modes that QA should exercise.

```gherkin
Feature: Edge Cases and Error Handling

  Scenario: Bot receives an empty message body
    Given user "alice@contoso.com" sends a message with an empty text body
    When the bot processes the activity
    Then the bot replies with the help card
    And no MessengerEvent is enqueued

  Scenario: Bot receives a message exceeding maximum length
    Given user "alice@contoso.com" sends a message with 10,000 characters
    When the bot processes the activity
    Then the message body is truncated to the configured maximum
    And a warning is logged
    And processing continues with the truncated body

  Scenario: Adaptive Card action payload is malformed
    Given user "alice@contoso.com" submits an Adaptive Card action
    And the action payload is missing the QuestionId field
    When the bot processes the invoke activity
    Then the bot responds with HTTP 200 and an error card: "Invalid action. Please try again."
    And no HumanDecisionEvent is created
    And the malformed payload is logged for debugging

  Scenario: Service URL changes between interactions
    Given the stored conversation reference for "alice@contoso.com" has ServiceUrl "https://smba.trafficmanager.net/us/"
    When alice sends a new message with ServiceUrl "https://smba.trafficmanager.net/eu/"
    Then the conversation reference is updated with the new ServiceUrl
    And subsequent proactive messages use the updated ServiceUrl

  Scenario: Concurrent approvals for the same question from different users
    Given question "Q-999" was sent to both "alice@contoso.com" and "bob@contoso.com"
    When both users click "Approve" within the same second
    Then exactly one HumanDecisionEvent is created (first-writer-wins)
    And the second user sees: "This question has already been decided."
    And both outcomes are audit-logged

  Scenario: Bot Framework token refresh during long-running operation
    Given the bot's authentication token is about to expire
    When the bot sends a proactive message
    Then the Bot Framework SDK automatically refreshes the token
    And the message is delivered without user-visible interruption

  Scenario: Rate limiting from Bot Framework
    Given the bot is sending proactive messages at a high rate
    When the Bot Framework returns HTTP 429 with Retry-After header
    Then the gateway respects the Retry-After interval
    And queued messages are delivered after the rate limit window
    And no messages are lost

  Scenario: User sends command during gateway maintenance window
    Given the Messenger Gateway is temporarily unavailable
    When user "alice@contoso.com" sends "agent status"
    Then the message is queued by the Bot Framework infrastructure
    And when the gateway recovers, the message is processed
    And the user receives a (possibly delayed) response
```
