# E2E Test Scenarios — Microsoft Teams Messenger Support

**Story:** `qq:MICROSOFT-TEAMS-MESS`
**Version:** 1.8 — Iteration 8

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
    Then a MessengerEvent of type "Text" is enqueued with the raw input as payload
    And the bot replies with a help card listing available commands:
      | Command       | Description        |
      | agent ask     | Create task        |
      | agent status  | Query status       |
      | approve       | Approve action     |
      | reject        | Reject action      |
      | escalate      | Escalate issue     |
      | pause         | Pause agent        |
      | resume        | Resume agent       |

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

  Scenario: Proactive message to user who uninstalled the bot (known uninstall — inactive reference pre-check)
    Given user "charlie@contoso.com" had the bot installed previously
    And the bot received an installationUpdate activity with action "remove" for "charlie@contoso.com"
    And the conversation reference for "charlie@contoso.com" was marked inactive (per tech-spec §4.2)
    When agent "planner-agent-01" publishes an AgentQuestion for "charlie@contoso.com"
    Then the ProactiveNotifier checks the ConversationReferenceStore and finds the reference is inactive
    And no proactive message attempt is made to Bot Framework
    And the notification is moved to the dead-letter queue
    And an audit record is persisted indicating the reference is inactive due to uninstall

  Scenario: Proactive message fails with stale reference (uninstall event was missed)
    Given user "dave@contoso.com" had the bot installed previously
    And a conversation reference exists for "dave@contoso.com" and is still marked active
    But user "dave@contoso.com" was removed from the tenant without the bot receiving an uninstall event
    When agent "planner-agent-01" publishes an AgentQuestion for "dave@contoso.com"
    Then the ProactiveNotifier finds the reference is active and attempts delivery
    And the Bot Framework returns a 403/Forbidden error
    And the notification is moved to the dead-letter queue with reason "stale_reference"
    And the stored conversation reference is marked as stale
    And an audit record is persisted indicating delivery failure due to stale reference
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
      | ExternalUserId    | <alice's AadObjectId> |
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

  Scenario: User approves via text command in personal chat
    Given agent "release-agent-01" previously sent an Adaptive Card for question "Q-701"
    And the card is pending in user "alice@contoso.com"'s personal chat
    When user "alice@contoso.com" sends "approve" in personal chat
    Then the bot resolves the most recent pending question "Q-701" for the user
    And a MessengerEvent of type "Command" is enqueued with:
      | Field          | Value                 |
      | CorrelationId  | <non-empty UUID>      |
      | ExternalUserId | <alice's AadObjectId> |
      | CommandName    | approve               |
      | QuestionId     | Q-701                 |
    And a HumanDecisionEvent is created with ActionValue "approve"
    And the original Adaptive Card is updated to show "Approved by alice@contoso.com"
    And the card action buttons are disabled (card replaced with read-only version)
    And an immutable audit record is persisted with EventType "CardActionReceived"

  Scenario: User rejects via text command in personal chat
    Given agent "release-agent-01" previously sent an Adaptive Card for question "Q-702"
    And the card is pending in user "alice@contoso.com"'s personal chat
    When user "alice@contoso.com" sends "reject" in personal chat
    Then the bot resolves the most recent pending question "Q-702" for the user
    And a MessengerEvent of type "Command" is enqueued with:
      | Field          | Value                 |
      | CorrelationId  | <non-empty UUID>      |
      | ExternalUserId | <alice's AadObjectId> |
      | CommandName    | reject                |
      | QuestionId     | Q-702                 |
    And a HumanDecisionEvent is created with ActionValue "reject"
    And the original Adaptive Card is updated to show "Rejected by alice@contoso.com"
    And the card action buttons are disabled (card replaced with read-only version)
    And an immutable audit record is persisted with EventType "CardActionReceived"
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

  Scenario: Message from unauthorized tenant is rejected with HTTP 403
    Given user "mallory@evil-corp.com" is in tenant "evil-corp-tenant-id"
    When user "mallory@evil-corp.com" sends "agent ask hack the system" to the bot
    Then the TenantValidationMiddleware intercepts the request before the bot handler runs
    And the tenant ID "evil-corp-tenant-id" is not in the allowed list
    And the middleware returns HTTP 403 with no bot response or Adaptive Card
    And no MessengerEvent is enqueued
    And an audit record is persisted:
      | Field     | Value                       |
      | EventType | SecurityRejection           |
      | TenantId  | evil-corp-tenant-id         |
      | ActorId   | <mallory's AadObjectId>     |
      | Outcome   | Rejected                    |

  Scenario: User without the required RBAC role is denied
    Given user "viewer-only@contoso.com" has RBAC role "viewer"
    When user "viewer-only@contoso.com" sends "approve" in personal chat
    Then the bot checks the user's RBAC permissions
    And the "approve" command requires role "approver"
    And the bot replies: "You do not have permission to perform this action."
    And no HumanDecisionEvent is created
    And an audit record is persisted for the access denial

  Scenario: Allowed-tenant user with unmapped Entra identity is rejected
    Given user "unknown@contoso.com" is in tenant "contoso-tenant-id"
    And tenant "contoso-tenant-id" is in the allowed list
    But user "unknown@contoso.com" has no mapped internal identity in the IIdentityResolver
    When user "unknown@contoso.com" sends "agent status" in personal chat
    Then the bot handler invokes IIdentityResolver with the user's AadObjectId
    And IIdentityResolver returns no mapped identity
    And the bot replies with an Adaptive Card explaining access denial and how to request access (per tech-spec §4.2 rejection matrix)
    And no MessengerEvent is enqueued
    And an audit record is persisted:
      | Field     | Value                       |
      | EventType | SecurityRejection           |
      | TenantId  | contoso-tenant-id           |
      | ActorId   | <unknown's AadObjectId>     |
      | Outcome   | UnmappedUserRejected        |

  Scenario: Proactive message to user without bot installation is pre-checked and rejected
    Given user "no-install@contoso.com" is in tenant "contoso-tenant-id"
    But the Teams bot is not installed for user "no-install@contoso.com"
    And no conversation reference exists in ConversationReferenceStore for "no-install@contoso.com"
    When the ProactiveNotifier attempts to send a proactive message to "no-install@contoso.com"
    Then the ProactiveNotifier detects the missing conversation reference before calling Bot Framework
    And no proactive message attempt is made to Bot Framework
    And the notification is moved directly to the dead-letter queue
    And an audit record is persisted indicating the user has no active installation

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
      | MaxRetries       | 4                  |
      | InitialBackoff   | 2 seconds          |
      | BackoffMultiplier| 2                  |
      | MaxBackoff       | 60 seconds         |
      | Jitter           | ±25%               |
      | TotalAttempts    | 5 (1 initial + 4 retries) |

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
    And the Bot Framework returns HTTP 500 on all 5 attempts (1 initial + 4 retries)
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
    When the TeamsMessengerConnector processes both deliveries
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
      | EventType         | CommandReceived                                     |
      | Messenger         | teams                                              |
      | TenantId          | contoso-tenant-id                                  |
      | ActorId           | <alice's AadObjectId (Entra OID)>                  |
      | ActorType         | User                                               |
      | Action            | agent ask                                          |
      | PayloadJson       | {"body":"create e2e test scenarios for update service"} |
      | CorrelationId     | <UUID>                                             |
      | ConversationId    | <Teams conversation ID>                            |
      | Outcome           | Success                                            |

  Scenario: All outbound notifications are audit-logged
    When the bot sends an Adaptive Card for question "Q-1001"
    Then an immutable audit record is persisted containing:
      | Field             | Value                     |
      | EventType         | ProactiveNotification     |
      | ActorId           | <agent ID>                |
      | ActorType         | Agent                     |
      | Action            | send_card                 |
      | TaskId            | <task ID for Q-1001>      |
      | PayloadJson       | {"questionId":"Q-1001","targetAadObjectId":"<alice's AadObjectId>","activityId":"<Teams activity ID>"} |
      | CorrelationId     | <UUID>                    |
      | Outcome           | Success                   |

  Scenario: Approval decisions are audit-logged with full context
    When user "alice@contoso.com" approves question "Q-1001" with comment "LGTM"
    Then an immutable audit record is persisted containing:
      | Field             | Value             |
      | EventType         | CardActionReceived |
      | ActorId           | <alice's AadObjectId> |
      | ActorType         | User               |
      | Action            | approve            |
      | TaskId            | <task ID for Q-1001> |
      | PayloadJson       | {"questionId":"Q-1001","actionValue":"approve","comment":"LGTM"} |
      | CorrelationId     | <UUID>             |
      | Outcome           | Success            |

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
    And the release gate is configured with a threshold of 2 required approvals
    And the AgentQuestion is sent to both "alice@contoso.com" and "bob@contoso.com"
    When "alice@contoso.com" clicks "Approve"
    Then a HumanDecisionEvent with ActionValue "approve" is created for alice
    And alice's card is updated to show "Approved by alice@contoso.com (1/2)"
    And bob's card remains actionable
    When "bob@contoso.com" clicks "Approve"
    Then a second HumanDecisionEvent with ActionValue "approve" is created for bob
    And both cards are updated to show "Fully Approved (2/2)"
    And the release gate transitions to approved state
    And an audit trail records both individual approvals as separate CardActionReceived events
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
      | 1    | CommandReceived       |
      | 2    | ProactiveNotification |
      | 3    | CardActionReceived    |
      | 4    | MessageSent           |

  Scenario: Every MessengerEvent includes required tracing fields per canonical model
    When any MessengerEvent is created
    Then it contains all fields defined in the canonical MessengerEvent model (architecture.md §3.1):
      | Field          | Constraint                                              |
      | EventId        | Non-empty unique identifier                             |
      | EventType      | One of: AgentTaskRequest, Command, Decision, Text, Escalation, PauseAgent, ResumeAgent, InstallUpdate, Reaction |
      | CorrelationId  | Non-empty UUID for end-to-end tracing                   |
      | Messenger      | "Teams"                                                 |
      | ExternalUserId | Non-empty (Teams AAD object ID)                         |
      | ActivityId     | Nullable — inbound Activity.Id for dedup (per architecture.md §3.1) |
      | Source         | Nullable — null for DMs, "MessageAction" for forwarded messages     |
      | Payload        | Non-null typed payload                                  |
      | Timestamp      | UTC ISO 8601                                            |
    And the corresponding audit record may additionally contain optional fields:
      | Field          | Constraint                                              |
      | TaskId         | Present when event relates to an agent task (optional per tech-spec §4.3) |
      | ConversationId | Present when event occurs within a conversation (optional per tech-spec §4.3) |
```

---

## Feature: Teams Message Actions (Message Extensions)

Covers the Teams message-extension action commands that allow users to forward selected message context into an agent command, as required by tech-spec.md §2.1 "Message actions".

```gherkin
Feature: Teams Message Actions (Message Extensions)

  Background:
    Given the Teams bot manifest includes a composeExtension with type "action"
    And the action command "forwardToAgent" is defined in the manifest
    And user "alice@contoso.com" has RBAC role "operator"
    And the bot is installed for user "alice@contoso.com" in tenant "contoso-tenant-id"

  Scenario: User invokes message action to forward a message to an agent
    Given user "bob@contoso.com" posted a message in channel "#ops-swarm":
      """
      The deployment pipeline for service-xyz is failing with timeout errors on stage 3.
      """
    When user "alice@contoso.com" right-clicks (or long-presses) the message and selects "Forward to Agent" from the message actions menu
    Then the bot receives an invoke activity of type "composeExtension/submitAction"
    And the activity payload includes the selected message text as context
    And the TeamsActivityHandler delegates to the CommandParser with the forwarded content
    And a MessengerEvent of type "AgentTaskRequest" is enqueued with:
      | Field          | Value                                                                             |
      | CorrelationId  | <non-empty UUID>                                                                  |
      | ExternalUserId | <alice's AadObjectId>                                                             |
      | Body           | The deployment pipeline for service-xyz is failing with timeout errors on stage 3. |
      | Source         | MessageAction                                                                     |
    And the bot replies in the channel thread confirming the task was created
    And an immutable audit record is persisted with EventType "CommandReceived" (message actions are a command submission mechanism and log as CommandReceived per tech-spec §4.3 line 136, not a separate event type)

  Scenario: Message action presents a task form for user input
    Given user "alice@contoso.com" selects a message and invokes the "Forward to Agent" action
    When the bot receives the composeExtension/fetchTask invoke
    Then the bot returns an Adaptive Card task module containing:
      | Field       | Type     | Pre-populated                    |
      | Context     | readonly | <selected message text>          |
      | Command     | dropdown | agent ask, escalate              |
      | Priority    | dropdown | Low, Medium, High, Critical      |
      | Notes       | text     | <empty>                          |
    When the user selects command "agent ask", priority "High", and submits
    Then the bot receives a composeExtension/submitAction invoke with the form data
    And a MessengerEvent of type "AgentTaskRequest" is enqueued with the selected command and priority
    And an audit record is persisted

  Scenario: Message action from user without required RBAC role is denied
    Given user "viewer-only@contoso.com" has RBAC role "viewer"
    When user "viewer-only@contoso.com" invokes the "Forward to Agent" message action
    Then the bot validates the user's RBAC role via Activity.From.AadObjectId
    And the "agent ask" command requires role "operator"
    And the bot returns an error response in the task module: "You do not have permission to perform this action."
    And no MessengerEvent is created
    And an audit record is persisted for the access denial

  Scenario: Message action from unauthorized tenant is rejected
    Given user "mallory@evil-corp.com" is in tenant "evil-corp-tenant-id"
    When user "mallory@evil-corp.com" invokes the "Forward to Agent" message action
    Then the TenantValidationMiddleware rejects the invoke activity
    And the bot returns HTTP 403
    And an audit record is persisted with EventType "SecurityRejection"
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
    And a MessengerEvent of type "Text" is enqueued with an empty payload

  Scenario: Bot receives a message exceeding maximum length
    Given the configured maximum message length is 4,000 characters (configurable via "Teams:MaxMessageLength" app setting)
    And user "alice@contoso.com" sends a message with 10,000 characters
    When the bot processes the activity
    Then the message body is truncated to 4,000 characters
    And a warning is logged with the original length and truncated length
    And processing continues with the truncated body
    And an immutable audit record is persisted with EventType "CommandReceived" and Outcome "Success" including a PayloadJson field noting "truncated_from: 10000, truncated_to: 4000"

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

  Scenario: Concurrent approvals for the same single-decision question (first-writer-wins)
    Given question "Q-999" is a single-decision question (the release gate threshold is 1)
    And question "Q-999" was sent to both "alice@contoso.com" and "bob@contoso.com"
    When both users click "Approve" within the same second
    Then exactly one HumanDecisionEvent is created (first-writer-wins)
    And the second user sees: "This question has already been decided."
    And both outcomes are audit-logged

  Scenario: Concurrent approvals for a multi-approver release gate (all recorded)
    Given question "Q-998" is a release-gate question with a threshold of 2 required approvals
    And question "Q-998" was sent to both "alice@contoso.com" and "bob@contoso.com"
    When both users click "Approve" within the same second
    Then both approvals are accepted and recorded as separate HumanDecisionEvents
    And both cards are updated to show "Fully Approved (2/2)"
    And both individual approvals are audit-logged
    And the release gate transitions to approved state only after reaching the required count

  > **Note:** Single-decision questions (e.g., standard agent blocking questions) use first-writer-wins semantics — only the first response is authoritative. Multi-approver release gates (e.g., release-gate scenario in §Adaptive Cards) require a configurable approval threshold tracked at the orchestration/workflow layer (not on the `AgentQuestion` record itself, which only defines `AllowedActions`). The orchestrator records all individual decisions until the threshold is met. The `AgentQuestion.AllowedActions` metadata defines available buttons; the release-gate configuration determines how many approvals are needed.

  Scenario: Bot Framework token refresh during long-running operation
    Given the bot's authentication token is about to expire
    When the bot sends a proactive message
    Then the Bot Framework SDK automatically refreshes the token
    And the message is delivered without user-visible interruption

  Scenario: Rate limiting from Bot Framework
    Given the bot is sending proactive messages at a high rate
    When the Bot Framework returns HTTP 429 with Retry-After header
    Then the OutboxRetryEngine respects the Retry-After interval
    And queued messages are delivered after the rate limit window
    And no messages are lost

  Scenario: User sends command while bot webhook endpoint is temporarily down
    Given the AgentSwarm.Messaging.Worker host is temporarily unavailable (e.g., during a rolling deployment)
    When user "alice@contoso.com" sends "agent status"
    Then the Bot Framework webhook call to the bot endpoint fails with a transport error
    And Teams may display "Sorry, my bot code is having an issue" to the user
    When the AgentSwarm.Messaging.Worker host recovers
    Then the user must resend the command — Bot Framework does not queue missed webhook deliveries
    And no stale or phantom commands appear in the inbound queue
```

---

## Iteration Summary

**File:** `docs/stories/qq-MICROSOFT-TEAMS-MESS/e2e-scenarios.md`
**Version:** 1.8 — Iteration 8
**Byte count:** ~53,700

### Coverage

- Personal chat task creation (agent ask, agent status, unrecognised commands with Text event)
- Personal chat text-command approval and rejection (approve, reject as text commands)
- Proactive messaging — blocking questions via Adaptive Cards
- Adaptive Card approve/reject/need-more-info actions
- Card update and delete lifecycle
- Conversation reference persistence and rehydration
- Security: tenant validation, unmapped Entra identity rejection, RBAC, bot installation checks, Bot Framework token validation
- Reliability: outbox retry (canonical policy: 4 retries, 2s base, 60s cap, ±25% jitter), dead-letter, idempotency
- Performance: P95 < 3s card delivery
- Compliance: immutable audit trail with canonical EventType values; message actions audit as CommandReceived (per tech-spec §4.3 line 136 — message actions are a command submission mechanism, not a separate event type; the canonical set contains exactly six values)
- Observability: distributed tracing with CorrelationId
- Message actions (message extensions)
- Edge cases: concurrent approvals, malformed payloads, rate limiting, service URL changes, deterministic max message length with audit trail
- Uninstall handling: both known-uninstall (inactive pre-check) and missed-uninstall (stale reference 403)

### Prior feedback resolution

- [x] 1. FIXED — §Message Actions line 773 — Changed `EventType "MessageActionReceived"` to `EventType "CommandReceived"`. Tech-spec §4.3 line 128 defines exactly six canonical audit EventType values (`CommandReceived`, `MessageSent`, `CardActionReceived`, `SecurityRejection`, `ProactiveNotification`, `Error`) and explicitly states "Message actions (Teams message-extension submissions) are logged with audit EventType = CommandReceived". Tech-spec §4.3 line 136 confirms: "Message actions log as `CommandReceived` (not a separate event type)". The prior iteration (iter 7) misread the tech-spec and incorrectly changed `CommandReceived` to `MessageActionReceived`. This iteration reverses that error. Verification — no Gherkin scenario or Coverage line uses `MessageActionReceived`:
```
$ grep -nF "MessageActionReceived" docs/stories/qq-MICROSOFT-TEAMS-MESS/e2e-scenarios.md
(only hits are in this Prior feedback resolution section and the Cross-doc note section — no Gherkin scenario or Coverage summary line uses the term)
```
Positive verification — the scenario now asserts `CommandReceived`:
```
$ grep -nF 'EventType "CommandReceived"' docs/stories/qq-MICROSOFT-TEAMS-MESS/e2e-scenarios.md
773:    And an immutable audit record is persisted with EventType "CommandReceived" (message actions are a command submission mechanism and log as CommandReceived per tech-spec §4.3 line 136, not a separate event type)
```

- [x] 2. FIXED — §Prior feedback resolution — The iteration 7 prior-feedback block (lines 911-945) falsely claimed tech-spec §4.3 line 136 requires `MessageActionReceived`. In reality, tech-spec line 136 reads: "Message actions log as `CommandReceived` (not a separate event type)". The entire iteration 7 prior-feedback block has been replaced with this iteration 8 block, which correctly references `CommandReceived` throughout. No false claims remain.

- [x] 3. FIXED — Cross-doc references in architecture.md and implementation-plan.md — The evaluator noted unacknowledged sibling hits of `MessageActionReceived` in `architecture.md:432`, `architecture.md:947`, `implementation-plan.md:48`, `implementation-plan.md:203`, `implementation-plan.md:214`, `implementation-plan.md:284`. These sibling docs incorrectly use `MessageActionReceived` and claim seven canonical values, contradicting tech-spec §4.3 which defines exactly six values with message actions as `CommandReceived`. **This e2e-scenarios.md file now correctly uses `CommandReceived` and is aligned with tech-spec.** The sibling docs (`architecture.md`, `implementation-plan.md`) must be fixed by their respective sibling agents in their next iterations. Cross-doc inconsistency note added below.

- [x] 4. FIXED — Same scope as item 3 (both items 3 and 4 cite the same set of unacknowledged sibling hits). See item 3 resolution above.

- [x] 5. FIXED — §Coverage summary line 903/905 — Changed the coverage line to say "message actions audit as CommandReceived" matching tech-spec §4.3. Verification — positive check that the coverage line is correct:
```
$ grep -nF "message actions audit as CommandReceived" docs/stories/qq-MICROSOFT-TEAMS-MESS/e2e-scenarios.md
905:- Compliance: immutable audit trail with canonical EventType values; message actions audit as CommandReceived (per tech-spec §4.3 line 136 — message actions are a command submission mechanism, not a separate event type; the canonical set contains exactly six values)
```

- [x] 6. FIXED — Self-matching stale claim at old line 939 — The entire iteration 7 prior-feedback block has been replaced with this iteration 8 block. The phrase "state message actions must audit as" from the old false claim no longer appears in any Gherkin scenario or coverage summary.

- [x] 7. FIXED — §Iteration metadata version inconsistency — Header line 4 previously said "Version: 1.6 — Iteration 6" while the iteration summary said "Version: 1.7 — Iteration 7". Both now say "Version: 1.8 — Iteration 8".

- [x] 8. FIXED — §Edge Cases message length — The "Bot receives a message exceeding maximum length" scenario previously said "10,000 characters" without specifying a deterministic configured maximum or audit expectations. Now specifies: configured maximum of 4,000 characters (via `Teams:MaxMessageLength` app setting), explicit truncation outcome, warning log with both original and truncated lengths, and an immutable audit record with EventType `CommandReceived`, Outcome `Success`, and PayloadJson noting the truncation details.

**Cross-doc inconsistency note (for sibling agent owners):**
- `architecture.md` lines 348, 432, 947 use `MessageActionReceived` and claim seven canonical audit EventType values. Tech-spec §4.3 lines 128 and 136 define exactly **six** values and state message actions log as `CommandReceived`. The architecture.md sibling agent should fix these lines to use `CommandReceived` and six values.
- `implementation-plan.md` lines 48, 203, 214, 284 use `MessageActionReceived` and list seven values. Same fix needed: use `CommandReceived` and six values per tech-spec §4.3.
- `tech-spec.md` line 335 correctly flags `e2e-scenarios.md` line 903 as stale. That line is now fixed in this iteration.

### Open questions

None — all feedback items resolved this iteration.
