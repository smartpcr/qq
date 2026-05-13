# E2E Test Scenarios — Microsoft Teams Messenger Support

**Story:** `qq:MICROSOFT-TEAMS-MESS`
**Version:** 1.43

---

## Feature: Personal Chat— Agent Task Creation

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
    And the event contains canonical envelope fields plus typed payload (per architecture.md §3.1):
      | Field               | Value                                          |
      | EventType           | AgentTaskRequest                               |
      | CorrelationId       | <non-empty UUID>                               |
      | Messenger           | Teams                                          |
      | ExternalUserId      | <alice's AadObjectId>                          |
      | Payload.TaskId      | <newly generated>                              |
      | Payload.Body        | create e2e test scenarios for update service   |
    And an immutable audit record is persisted for the command

  Scenario: User creates a task via team channel mention
    Given user "bob@contoso.com" is in team channel "#ops-swarm"
    And the bot is installed in the team
    And user "bob@contoso.com" belongs to tenant "contoso-tenant-id"
    And IIdentityResolver maps bob's AadObjectId to an internal user identity
    And user "bob@contoso.com" has RBAC role "operator"
    When the user sends "@AgentBot agent ask design persistence layer" in the channel
    Then the bot strips the @mention and parses "agent ask design persistence layer"
    And the bot validates bob's tenant via TenantValidationMiddleware
    And IIdentityResolver.ResolveAsync maps bob's Entra identity to an internal user
    And IUserAuthorizationService.AuthorizeAsync confirms bob's RBAC role
    And a MessengerEvent of type "AgentTaskRequest" is enqueued
    And the event contains canonical envelope fields plus typed payload:
      | Field               | Value                                          |
      | EventType           | AgentTaskRequest                               |
      | ExternalUserId      | <bob's AadObjectId>                            |
      | Payload.Body        | design persistence layer                       |
    And the bot replies in a thread under the original message
    And an immutable audit record is persisted for the command

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
      | Field           | Value                                      |
      | QuestionId      | Q-501                                      |
      | AgentId         | arch-agent-03                              |
      | TaskId          | TASK-77                                    |
      | TenantId        | contoso-tenant-id                          |
      | TargetUserId    | <alice's internal user ID>                 |
      | TargetChannelId | <null>                                     |
      | Title           | Database migration strategy                |
      | Body            | Should we use blue-green or rolling deploy? |
      | Severity        | High                                       |
      | AllowedActions  | Approve, Reject, Need more info            |
      | ExpiresAt       | <now + 24 hours>                           |
      | CorrelationId   | <UUID>                                     |
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
    When the agent publishes an AgentQuestion with:
      | Field           | Value                          |
      | QuestionId      | Q-502                          |
      | AgentId         | release-agent-01               |
      | TaskId          | TASK-88                        |
      | TenantId        | contoso-tenant-id              |
      | TargetUserId    | <null>                         |
      | TargetChannelId | <channel ID for #release-gates>|
      | Title           | Release gate approval          |
      | AllowedActions  | Approve, Reject                |
      | CorrelationId   | <UUID>                         |
    Then the bot resolves TargetChannelId to the stored conversation reference via IConversationReferenceStore
    And the bot sends an Adaptive Card to the channel using the stored conversation reference
    And the card is threaded under the original task context if one exists

  Scenario: Proactive message delivery after service restart
    Given the AgentSwarm.Messaging.Worker host was restarted
    And conversation references were persisted before the restart
    When agent "test-agent-02" publishes an AgentQuestion with TargetUserId set to alice's internal user ID
    Then the ProactiveNotifier resolves TargetUserId to alice's conversation reference via IConversationReferenceStore
    And the Adaptive Card is delivered successfully to the user
    And connector recovery completes within 30 seconds

  Scenario: Proactive message to user who uninstalled the bot (known uninstall — inactive reference pre-check)
    Given user "charlie@contoso.com" had the bot installed previously
    And the bot received an installationUpdate activity with action "remove" for "charlie@contoso.com"
    And the conversation reference for "charlie@contoso.com" was marked inactive (per tech-spec §4.2 Identity & Security)
    When agent "planner-agent-01" publishes an AgentQuestion with TargetUserId set to charlie's internal user ID
    Then the ProactiveNotifier resolves TargetUserId via IConversationReferenceStore and finds the reference is inactive
    And no proactive message attempt is made to Bot Framework
    And the notification is moved to the dead-letter queue
    And an audit record is persisted indicating the reference is inactive due to uninstall

  Scenario: Proactive message fails with stale reference — 403 (user removed from tenant)
    Given user "dave@contoso.com" had the bot installed previously
    And a conversation reference exists for "dave@contoso.com" and is still marked active
    But user "dave@contoso.com" was removed from the tenant without the bot receiving an uninstall event
    When agent "planner-agent-01" publishes an AgentQuestion with TargetUserId set to dave's internal user ID
    Then the ProactiveNotifier resolves TargetUserId via IConversationReferenceStore, finds the reference is active, and attempts delivery
    And the Bot Framework returns HTTP 403/Forbidden
    And the notification is moved to the dead-letter queue with reason "stale_reference"
    And the stored conversation reference is marked as stale (inactive)
    And a "teams.proactive.reference.stale" metric is emitted (per tech-spec §5.1, R-2)
    And an audit record is persisted indicating delivery failure due to stale reference

  Scenario: Proactive message fails with stale reference — 404 (team deleted or activity not found)
    Given team "eng-team" had the bot installed and a conversation reference exists for channel "#eng-ops"
    And the conversation reference for "#eng-ops" is still marked active
    But the team "eng-team" was deleted from Teams without the bot receiving an uninstall event
    When agent "planner-agent-01" publishes an AgentQuestion targeting the "#eng-ops" channel
    Then the ProactiveNotifier resolves the channel to its stored conversation reference, finds it active, and attempts delivery
    And the Bot Framework returns HTTP 404/NotFound (resource no longer exists)
    And the notification is moved to the dead-letter queue with reason "stale_reference"
    And the stored conversation reference is marked as stale (inactive)
    And a "teams.proactive.reference.stale" metric is emitted (per tech-spec §5.1, R-2)
    And an audit record is persisted indicating delivery failure due to deleted team/resource not found
    And no retries are scheduled for this permanently-failed reference
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
    Then CardActionHandler queries IAgentQuestionStore.GetByIdAsync("Q-601") and confirms Status == "Open"
    And CardActionHandler atomically transitions AgentQuestion.Status from "Open" to "Resolved" via IAgentQuestionStore.TryUpdateStatusAsync("Q-601", "Open", "Resolved", ct) (architecture.md §4.11 line 766 — compare-and-set; returns false if Status was not "Open", first-writer-wins)
    And a HumanDecisionEvent is created:
      | Field             | Value            |
      | QuestionId        | Q-601            |
      | ActionValue       | approve          |
      | Comment           | <null>           |
      | Messenger         | Teams            |
      | ExternalUserId    | <alice's AadObjectId> |
      | ExternalMessageId | <Teams activity ID> |
      | CorrelationId     | <original UUID>  |
    And the event is wrapped in a MessengerEvent with EventType "Decision" (per architecture.md §3.1 DecisionEvent subtype):
      | Field          | Value                      |
      | EventType      | Decision                   |
      | CorrelationId  | <original UUID>            |
      | Messenger      | Teams                      |
      | ExternalUserId | <alice's AadObjectId>      |
      | Payload        | <the HumanDecisionEvent above> |
    And the MessengerEvent/Decision is enqueued to the inbound queue via IInboundEventPublisher.PublishAsync
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
    And the event is wrapped in a MessengerEvent with EventType "Decision" (per architecture.md §3.1 DecisionEvent subtype) carrying the HumanDecisionEvent as typed payload
    And the MessengerEvent/Decision is enqueued to the inbound queue via IInboundEventPublisher.PublishAsync
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

  Scenario: User approves via text command in personal chat (single pending question in conversation)
    Given agent "release-agent-01" previously sent an Adaptive Card for question "Q-701"
    And question "Q-701" is the only AgentQuestion with Status == "Open" and ConversationId matching the current personal-chat conversation
    And the card is pending in user "alice@contoso.com"'s personal chat
    When user "alice@contoso.com" sends "approve" in personal chat
    Then ApproveCommandHandler calls IAgentQuestionStore.GetOpenByConversationAsync(conversationId) to retrieve ALL open questions in the current conversation (per implementation-plan.md §1.2 line 39 and §3.2 lines 187-188)
    And the returned list contains exactly one AgentQuestion ("Q-701"), so "Q-701" is resolved without disambiguation
    And ApproveCommandHandler transitions AgentQuestion "Q-701" Status from "Open" to "Resolved" via IAgentQuestionStore.TryUpdateStatusAsync("Q-701", "Open", "Resolved", ct) (architecture.md §4.11 line 766 — compare-and-set; returns false if Status was not "Open", first-writer-wins)
    And a MessengerEvent of type "Command" is enqueued with canonical envelope plus typed payload:
      | Field                   | Value                 |
      | EventType               | Command               |
      | CorrelationId           | <non-empty UUID>      |
      | ExternalUserId          | <alice's AadObjectId> |
      | Payload.CommandName     | approve               |
      | Payload.QuestionId      | Q-701                 |
    And a HumanDecisionEvent is created with ActionValue "approve"
    And the original Adaptive Card is updated to show "Approved by alice@contoso.com"
    And the card action buttons are disabled (card replaced with read-only version)
    And an immutable audit record is persisted with EventType "CardActionReceived"

  Scenario: User rejects via text command in personal chat (single pending question in conversation)
    Given agent "release-agent-01" previously sent an Adaptive Card for question "Q-702"
    And question "Q-702" has AllowedAction "Reject" with RequiresComment = false
    And question "Q-702" is the only AgentQuestion with Status == "Open" and ConversationId matching the current personal-chat conversation
    And the card is pending in user "alice@contoso.com"'s personal chat
    When user "alice@contoso.com" sends "reject" in personal chat
    Then RejectCommandHandler calls IAgentQuestionStore.GetOpenByConversationAsync(conversationId) to retrieve ALL open questions in the current conversation
    And the returned list contains exactly one AgentQuestion ("Q-702"), so "Q-702" is resolved without disambiguation
    And RejectCommandHandler transitions AgentQuestion "Q-702" Status from "Open" to "Resolved" via IAgentQuestionStore.TryUpdateStatusAsync("Q-702", "Open", "Resolved", ct) (architecture.md §4.11 line 766 — compare-and-set; returns false if Status was not "Open", first-writer-wins)
    And a MessengerEvent of type "Command" is enqueued with canonical envelope plus typed payload:
      | Field                   | Value                 |
      | EventType               | Command               |
      | CorrelationId           | <non-empty UUID>      |
      | ExternalUserId          | <alice's AadObjectId> |
      | Payload.CommandName     | reject                |
      | Payload.QuestionId      | Q-702                 |
    And a HumanDecisionEvent is created with ActionValue "reject" and Comment = null
    And the original Adaptive Card is updated to show "Rejected by alice@contoso.com"
    And the card action buttons are disabled (card replaced with read-only version)
    And an immutable audit record is persisted with EventType "CardActionReceived"
    # Note: Q-702 has RequiresComment = false, so no comment prompt is shown.
    # Contrast with the Adaptive Card rejection scenario for Q-602 (above),
    # where RequiresComment = true triggers an input field for the rejection reason.

  Scenario: User sends bare approve with multiple pending questions — disambiguation card returned
    Given agent "release-agent-01" sent an Adaptive Card for question "Q-801" (Status = "Open", CreatedAt = T1)
    And agent "planner-agent-02" sent an Adaptive Card for question "Q-802" (Status = "Open", CreatedAt = T2, where T2 > T1)
    And both questions have ConversationId matching the current personal-chat conversation
    When user "alice@contoso.com" sends "approve" in personal chat
    Then ApproveCommandHandler detects multiple open questions in the conversation (per implementation-plan.md §3.2 line 187 — "if zero or more than one are found, the handler returns a disambiguation card")
    And the handler does NOT resolve either Q-801 or Q-802
    And the bot returns a disambiguation card listing all open questions in the conversation:
      | QuestionId | Title                           | CreatedAt |
      | Q-802      | <question title>                | T2        |
      | Q-801      | <question title>                | T1        |
    And the disambiguation card includes action buttons for each question with explicit questionId (e.g., "approve Q-801", "approve Q-802")
    And both Q-801 and Q-802 remain with Status "Open"
    And an immutable audit record is persisted with EventType "CommandReceived" and Outcome "Disambiguation"
    # Design decision: bare approve/reject with multiple open questions returns a disambiguation
    # card per implementation-plan.md §3.2 line 187. The user must either tap a specific action
    # on the disambiguation card or use explicit syntax (e.g., "approve Q-801") to resolve
    # a specific question. This prevents accidental resolution of the wrong question.

  Scenario: User rejects via text command when only one question is pending in conversation (auto-resolved without disambiguation)
    Given agent "release-agent-01" sent an Adaptive Card for question "Q-803" (Status = "Open")
    And question "Q-803" is the only AgentQuestion with Status == "Open" and ConversationId matching the current conversation
    When user "alice@contoso.com" sends "reject" in personal chat
    Then RejectCommandHandler calls IAgentQuestionStore.GetOpenByConversationAsync(conversationId) to retrieve ALL open questions in the current conversation
    And the returned list contains exactly one AgentQuestion ("Q-803"), so it is resolved without disambiguation
    And RejectCommandHandler transitions AgentQuestion "Q-803" Status from "Open" to "Resolved" via IAgentQuestionStore.TryUpdateStatusAsync("Q-803", "Open", "Resolved", ct) (architecture.md §4.11 line 766 — compare-and-set; returns false if Status was not "Open", first-writer-wins)
    And a HumanDecisionEvent is created with ActionValue "reject" and QuestionId "Q-803"
    And an immutable audit record is persisted with EventType "CardActionReceived"
    # Note: When exactly one question is pending in the conversation, the bot
    # resolves it unambiguously — no disambiguation prompt is needed.

  Scenario: Cross-Conversation Consent attack is prevented (bare approve in personal chat does not resolve channel question)
    Given agent "release-agent-01" sent an Adaptive Card for question "Q-810" (Status = "Open") in channel "#ops-swarm" (conversationId = "channel-conv-1")
    And no questions are pending in user "alice@contoso.com"'s personal-chat conversation (conversationId = "personal-conv-alice")
    When user "alice@contoso.com" sends "approve" in personal chat
    Then ApproveCommandHandler calls IAgentQuestionStore.GetOpenByConversationAsync("personal-conv-alice")
    And the returned list is empty (zero open questions in the personal-chat conversation)
    And the bot replies: "No pending questions in this conversation."
    And question "Q-810" in channel "#ops-swarm" remains with Status "Open"
    And no HumanDecisionEvent is created
    # Note: This scenario explicitly validates Cross-Conversation Consent
    # prevention: bare approve/reject only resolves questions whose ConversationId
    # matches the current conversation. A question pending in a different
    # conversation (channel or another personal chat) is never resolved by a
    # bare command in an unrelated conversation.
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
    And question "Q-701" was previously resolved with decision "Approved" by user "alice@contoso.com"
    When the bot attempts to update the card via UpdateActivityAsync
    Then the Bot Framework returns an error
    And the failure is logged with the CorrelationId
    And the bot sends a new read-only replacement card to the user instead:
      | Field         | Constraint                                                    |
      | Status        | Shows the original decision outcome (e.g., "Approved")        |
      | DecidedBy     | alice@contoso.com                                             |
      | DecidedAt     | <ISO 8601 timestamp of the original decision>                 |
      | ActionButtons | None — the replacement card MUST NOT contain any action buttons |
    And the new replacement card's activity ID is recorded in ICardStateStore for question "Q-701" (replacing the stale "act-901")
    And ICardStateStore.UpdateStatusAsync is called to mark the card state as Answered (preserving the original decision outcome)
    And the outbound retry policy does not infinitely retry the stale update
```

---

## Feature: Conversation Reference Persistence

Covers the storage and lifecycle of Teams conversation references for proactive messaging.

```gherkin
Feature: Conversation Reference Persistence

  Background:
    Given the Teams bot is registered and running

  Scenario: Conversation reference is refreshed on first command (message path — full identity/RBAC)
    Given user "dave@contoso.com" (AadObjectId "aad-obj-dave-001") installed the bot (install-path reference exists)
    And user "dave@contoso.com" belongs to tenant "contoso-tenant-id" which is in the allowed tenant list
    And AadObjectId "aad-obj-dave-001" is mapped to an internal user via IIdentityResolver
    And the mapped user has RBAC role "viewer" (or above) via IUserAuthorizationService
    When user "dave@contoso.com" sends "agent status" in personal chat
    Then the TenantValidationMiddleware passes the request (tenant is allowed)
    And IIdentityResolver resolves AadObjectId "aad-obj-dave-001" to an internal user identity
    And IUserAuthorizationService confirms the user has the required role for "agent status"
    And only after full identity/RBAC authorization succeeds, the conversation reference is refreshed from the incoming Activity
    And the reference is updated in the durable store keyed by AadObjectId "aad-obj-dave-001" and TenantId "contoso-tenant-id"
    And the reference includes:
      | Field            | Value                  |
      | ServiceUrl       | <Bot Framework URL>    |
      | ConversationId   | <Teams conversation>   |
      | AadObjectId      | aad-obj-dave-001       |
      | TenantId         | contoso-tenant-id      |
      | BotId            | <bot app ID>           |
    # Note: This is the message path (architecture §6.1 step 8). Unlike the install path
    # (§Bot Installation — tenant-validation only), the message path performs full identity
    # resolution and RBAC authorization before refreshing the reference.

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
      | approver | approve, reject, agent status                    |
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
      | Action    | UnauthorizedTenantRejected  |
      | Outcome   | Rejected                    |

  Scenario: User without the required RBAC role is denied
    Given user "viewer-only@contoso.com" has RBAC role "viewer"
    When user "viewer-only@contoso.com" sends "approve" in personal chat
    Then the bot checks the user's RBAC permissions
    And the "approve" command requires role "approver"
    And the bot replies: "You do not have permission to perform this action."
    And no HumanDecisionEvent is created
    And an audit record is persisted:
      | Field     | Value                       |
      | EventType | SecurityRejection           |
      | ActorId   | <viewer-only's AadObjectId> |
      | Action    | InsufficientRoleRejected    |
      | Outcome   | Rejected                    |

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
      | Action    | UnmappedUserRejected        |
      | Outcome   | Rejected                    |

  Scenario: Proactive message to user without bot installation is pre-checked and rejected
    Given an AgentQuestion targets a user with TargetUserId "internal-user-no-install" in TenantId "contoso-tenant-id"
    But the Teams bot is not installed for that user
    And IConversationReferenceStore.GetByInternalUserIdAsync("contoso-tenant-id", "internal-user-no-install") returns null (no conversation reference stored)
    When the ProactiveNotifier resolves the OutboxEntry destination "teams://contoso-tenant-id/user/internal-user-no-install" (per architecture.md §3.1 routing derivation)
    Then the ProactiveNotifier detects the missing conversation reference before calling Bot Framework
    And no proactive message attempt is made to Bot Framework
    And the notification is moved directly to the dead-letter queue
    And an audit record is persisted indicating the user has no active installation:
      | Field        | Value                       |
      | EventType    | ProactiveNotification       |
      | TenantId     | contoso-tenant-id           |
      | TargetUserId | internal-user-no-install    |
      | Outcome      | DeadLettered                |
      | Reason       | NoConversationReference     |

  Scenario: Bot Framework token validation rejects forged activity
    Given an attacker sends a forged HTTP POST to the bot's messaging endpoint
    And the Authorization header contains an invalid JWT token
    When the Bot Framework authentication middleware processes the request
    Then the request is rejected with HTTP 401 by the Bot Framework CloudAdapter authentication pipeline
    And no application code, middleware (including TenantValidationMiddleware), or bot handler runs
    And no MessengerEvent is created
    And no audit entry is emitted because the request never reaches application-level code — rejection occurs in the Bot Framework SDK authentication layer before any bot handler or custom middleware executes (per tech-spec §4.2 Rejection Behavior Matrix, row "Invalid Bot Framework JWT")
    And the HTTP 401 response is logged by the hosting infrastructure (ASP.NET Core request pipeline) but not by IAuditLogger

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

  Scenario: Duplicate inbound webhook is suppressed at middleware level
    Given user "alice@contoso.com" sends "approve" for question "Q-801"
    And the Bot Framework delivers the activity twice (network retry with identical Activity.Id)
    When the first delivery passes through ActivityDeduplicationMiddleware
    Then ActivityDeduplicationMiddleware records the Activity.Id in IActivityIdStore
    And the activity reaches CardActionHandler and creates a HumanDecisionEvent
    When the second delivery arrives with the same Activity.Id
    Then ActivityDeduplicationMiddleware detects the duplicate via IActivityIdStore lookup
    And the middleware short-circuits the request with HTTP 200 before any handler runs
    And no duplicate HumanDecisionEvent is created
    And a deduplication event is logged for observability

  Scenario: Domain-level duplicate card action is suppressed (double-tap)
    Given user "alice@contoso.com" clicks "Approve" twice rapidly for question "Q-801"
    And each click generates a distinct Activity.Id (not a transport retry)
    When both activities pass through ActivityDeduplicationMiddleware (distinct IDs — not suppressed)
    And both reach CardActionHandler
    Then the first activity creates a HumanDecisionEvent for (QuestionId=Q-801, UserId=alice)
    And CardActionHandler records the (QuestionId, UserId) pair in its processed-action set
    And the second activity is rejected as a domain-level duplicate by the (QuestionId, UserId) check
    And no second HumanDecisionEvent is created
    And the domain-level duplicate is logged for observability

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
      | Messenger         | Teams                                              |
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
      | AgentId           | <agent ID>                |
      | TenantId          | contoso-tenant-id         |
      | Action            | send_card                 |
      | TaskId            | <task ID for Q-1001>      |
      | PayloadJson       | {"questionId":"Q-1001","targetAadObjectId":"<alice's AadObjectId>","activityId":"<Teams activity ID>"} |
      | CorrelationId     | <UUID>                    |
      | Outcome           | Success                   |

  Scenario: Approval decisions are audit-logged with full context
    When user "alice@contoso.com" approves question "Q-1001" with comment "LGTM"
    Then an immutable audit record is persisted containing:
      | Field             | Value              |
      | EventType         | CardActionReceived |
      | ActorId           | <alice's AadObjectId> |
      | ActorType         | User               |
      | AgentId           | <agent ID that owns Q-1001> |
      | TenantId          | contoso-tenant-id  |
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

  Scenario: Concurrent user interactions do not degrade card delivery latency (non-normative — aspirational target)
    # Note: tech-spec.md §2.1 line 43 only makes P95 Adaptive Card delivery < 3 seconds a hard
    # requirement; the concurrency target is tentative/unconfirmed (tech-spec.md §7 Assumption 8).
    # This scenario is aspirational and SHOULD NOT be treated as a hard pass/fail gate until the
    # sibling spec confirms a concrete concurrency SLA.
    Given 50 users are simultaneously interacting with the bot
    When each user sends "agent status"
    Then responses SHOULD be returned promptly
    And the bot SHOULD NOT throttle legitimate requests
    And the P95 Adaptive Card delivery SLA (< 3 seconds) for queued cards is not degraded by concurrent text-command load

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

  Scenario: User pauses the agent bound to the current conversation
    Given agent "release-agent-01" is the agent bound to the current conversation (per architecture.md §2.5 line 136: pause targets "the agent bound to the current conversation")
    When user "alice@contoso.com" sends "pause"
    Then the bot resolves the agent bound to the current conversation context
    And a MessengerEvent of type "PauseAgent" is enqueued for "release-agent-01"
    And the bot confirms: "Agent release-agent-01 has been paused."
    And an audit record is persisted

  Scenario: User resumes the agent bound to the current conversation
    Given agent "release-agent-01" is the agent bound to the current conversation
    And agent "release-agent-01" is in state "Paused"
    When user "alice@contoso.com" sends "resume"
    Then the bot resolves the agent bound to the current conversation context
    And a MessengerEvent of type "ResumeAgent" is enqueued for "release-agent-01"
    And the bot confirms: "Agent release-agent-01 has been resumed."
    And an audit record is persisted

  Scenario: Pause command with no agent bound to the current conversation
    Given no agent is bound to the current conversation
    When user "alice@contoso.com" sends "pause"
    Then the bot replies: "No agent is bound to this conversation. Use 'agent status' to check active agents."
```

---

## Feature: Adaptive Card — Incident Summary and Release Gates

Covers specialized card types for operational workflows.

```gherkin
Feature: Adaptive Card — Incident Summary and Release Gates

  Scenario: Agent sends an incident summary card
    Given agent "incident-agent-01" has completed incident analysis for task "INC-200"
    When the agent publishes an IncidentSummary (per architecture.md §3.3 IncidentSummary model) with:
      | Field         | Value                                  |
      | IncidentId    | INC-200                                |
      | TaskId        | TASK-INC-200                           |
      | AgentId       | incident-agent-01                      |
      | Severity      | Critical                               |
      | Title         | Database connection pool exhaustion     |
      | Description   | Connection pool saturated at 100/100 active connections causing cascading timeouts in order-service and payment-service |
      | OccurredAt    | <ISO 8601 UTC timestamp>               |
      | CorrelationId | <non-empty UUID>                       |
    Then the bot renders an Adaptive Card via IAdaptiveCardRenderer.RenderIncidentCard (per architecture.md §3.3):
      | Section          | Content                                     |
      | Header           | 🔴 Critical Incident — INC-200              |
      | Title            | Database connection pool exhaustion          |
      | Description      | <from IncidentSummary.Description>           |
      | Severity         | Critical                                     |
      | OccurredAt       | <from IncidentSummary.OccurredAt>            |
      | Actions          | Acknowledge, Escalate, Need more info        |
    And the card is delivered to the configured incident channel

  Scenario: Release gate approval with multiple approvers (separate questions per approver)
    Given agent "release-agent-01" requires approval from 2 approvers for a release gate
    And the orchestrator creates two separate AgentQuestion records:
      | QuestionId | TargetUserId                     | Status |
      | Q-gate-a   | <alice's internal user ID>       | Open   |
      | Q-gate-b   | <bob's internal user ID>         | Open   |
    And each AgentQuestion follows the standard single-decision lifecycle (first-writer-wins per architecture §6.3.1)
    And the orchestrator's release-gate workflow tracks the approval threshold (2 required) at the workflow layer
    When "alice@contoso.com" clicks "Approve" on her card (Q-gate-a)
    Then Q-gate-a transitions from Open to Resolved (standard CardActionHandler flow)
    And a HumanDecisionEvent with ActionValue "approve" is created for alice
    And alice's card is updated to show "Approved by alice@contoso.com"
    And the orchestrator records 1 of 2 approvals — threshold not yet met
    When "bob@contoso.com" clicks "Approve" on his card (Q-gate-b)
    Then Q-gate-b transitions from Open to Resolved (standard CardActionHandler flow)
    And a HumanDecisionEvent with ActionValue "approve" is created for bob
    And bob's card is updated to show "Approved by bob@contoso.com"
    And the orchestrator records 2 of 2 approvals — threshold met
    And the release gate transitions to approved state
    And an audit trail records both individual approvals as separate CardActionReceived events
```

---

## Feature: Bot Installation and Conversation Discovery

Covers the Teams app lifecycle events that trigger conversation reference creation.

```gherkin
Feature: Bot Installation and Conversation Discovery

  Scenario: Bot installed in personal scope (install path — tenant-validation only)
    When user "eve@contoso.com" installs the Teams bot in personal scope
    Then the bot receives an installationUpdate activity
    And the TenantValidationMiddleware validates that eve's tenant is in AllowedTenantIds
    And a conversation reference is persisted for "eve@contoso.com" after tenant validation only
    And no identity resolution (IIdentityResolver) or RBAC check is performed on the install path
    And the bot sends a welcome Adaptive Card explaining available commands
    # Note: Install-path references prove the app is installed (per architecture §6.1 step 8).
    # Full identity/RBAC authorization occurs on the message path when the user sends a command,
    # at which point the reference is refreshed with updated ServiceUrl/ConversationId.

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

  Scenario: Teams app installation blocked by Entra admin-consent / app installation policy
    Given the organization's Entra ID tenant "restricted-tenant-id" has a Teams app installation policy that blocks sideloaded or unapproved apps
    And the AgentSwarm Teams bot app has NOT been approved in the Teams admin center for "restricted-tenant-id"
    When user "frank@restricted-org.com" attempts to install the Teams bot
    Then the Teams client rejects the installation per the tenant's app installation policy
    And no installationUpdate activity is sent to the bot endpoint
    And no conversation reference is created for "frank@restricted-org.com"
    And the user sees a Teams-native error indicating the app is blocked by admin policy

  Scenario: Teams app installation succeeds after admin grants consent and tenant is allow-listed
    Given the organization's Entra ID tenant "restricted-tenant-id" previously blocked the app
    When the Teams admin approves the AgentSwarm bot in the Teams admin center and grants admin consent for the required Entra app registration (per tech-spec §5.1, R-5)
    And the system operator adds "restricted-tenant-id" to the AllowedTenantIds configuration (per tech-spec §4.2 — every inbound Activity tenant must be in the allow-list)
    And user "frank@restricted-org.com" installs the Teams bot in personal scope
    Then the bot receives an installationUpdate activity
    And the TenantValidationMiddleware accepts the activity because "restricted-tenant-id" is now in AllowedTenantIds
    And a conversation reference is persisted for "frank@restricted-org.com"
    And user "frank@restricted-org.com" can now send commands and receive proactive messages

  Scenario: Teams app approved by admin but tenant NOT in AllowedTenantIds — installationUpdate is rejected
    Given the organization's Entra ID tenant "approved-but-unlisted-tenant-id" has granted admin consent for the bot
    But "approved-but-unlisted-tenant-id" has NOT been added to AllowedTenantIds
    When user "eve@approved-but-unlisted.com" installs the bot in personal scope
    Then the bot receives an installationUpdate activity
    And the TenantValidationMiddleware rejects the installationUpdate activity because the tenant is not in AllowedTenantIds
    And no conversation reference is created for "eve@approved-but-unlisted.com"
    And the bot returns a 403 response to the installationUpdate activity
    And an audit record is persisted with EventType "SecurityRejection" containing TenantId "approved-but-unlisted-tenant-id"
    # Note: Since the installationUpdate itself is rejected at the middleware layer,
    # no conversation reference is persisted — the install path (architecture §6.1 step 8)
    # only persists a reference AFTER tenant validation passes. Consequently, no
    # subsequent commands or proactive messages are possible for this tenant.

  Scenario: Commands from an approved-but-unlisted tenant are also rejected
    Given the organization's Entra ID tenant "approved-but-unlisted-tenant-id" has granted admin consent for the bot
    But "approved-but-unlisted-tenant-id" has NOT been added to AllowedTenantIds
    And the bot has no conversation reference for "eve@approved-but-unlisted.com" (install was rejected)
    When user "eve@approved-but-unlisted.com" somehow sends "agent status" (e.g., via a Teams deep link or cached app state)
    Then the TenantValidationMiddleware rejects the inbound Activity because the tenant is not in AllowedTenantIds
    And the bot returns a 403 response
    And an audit record is persisted with EventType "SecurityRejection"

  Scenario: Proactive message to user whose tenant revoked app consent
    Given user "grace@revoked-org.com" had the bot installed and a conversation reference exists
    When the tenant admin revokes app consent in the Teams admin center
    And agent "test-agent-01" publishes an AgentQuestion with TargetUserId set to grace's internal user ID
    Then the ProactiveNotifier resolves TargetUserId via IConversationReferenceStore, finds the reference is active, and attempts delivery
    And the Bot Framework returns an error (HTTP 403) because the app is no longer consented
    And the notification is moved to the dead-letter queue with reason "app_consent_revoked"
    And the stored conversation reference is marked as stale
    And an audit record is persisted indicating delivery failure due to revoked app consent
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
    And the custom TeamsSwarmActivityHandler.OnTeamsMessagingExtensionSubmitActionAsync delegates to MessageExtensionHandler
    And MessageExtensionHandler extracts the source message context and dispatches to CommandParser with the forwarded content
    And a MessengerEvent of type "AgentTaskRequest" is enqueued with canonical envelope plus typed payload:
      | Field               | Value                                                                             |
      | EventType           | AgentTaskRequest                                                                  |
      | CorrelationId       | <non-empty UUID>                                                                  |
      | ExternalUserId      | <alice's AadObjectId>                                                             |
      | Source              | MessageAction                                                                     |
      | Payload.Body        | The deployment pipeline for service-xyz is failing with timeout errors on stage 3. |
    And the bot returns a MessagingExtensionActionResponse containing a task-submitted confirmation Adaptive Card to the invoking user (per architecture.md §2.15 — message extensions return a confirmation card response, not a channel thread reply)
    And an immutable audit record is persisted with EventType "MessageActionReceived" (message actions log as MessageActionReceived per tech-spec §4.3 Canonical Audit Record Schema — the canonical audit set contains exactly seven values; MessageActionReceived is a dedicated audit event type distinct from CommandReceived because message-action submissions arrive through the composeExtension/submitAction invoke mechanism rather than direct text commands)

  Scenario: Message action uses direct submit (fetchTask false) to forward a message
    Given user "alice@contoso.com" selects a message and invokes the "Forward to Agent" action
    And the Teams app manifest sets fetchTask to false for this action command
    When the bot receives the composeExtension/submitAction invoke directly (no prior fetchTask round-trip)
    Then the bot extracts the message payload from MessagingExtensionAction.MessagePayload
    And the bot parses the forwarded message text via CommandParser
    And a MessengerEvent of type "AgentTaskRequest" is enqueued with Source "MessageAction"
    And the bot returns a MessagingExtensionActionResponse with a confirmation Adaptive Card containing the task correlation ID
    And an audit record is persisted with EventType "MessageActionReceived"

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

  # Note: Oversized inbound message scenarios were removed because no sibling
  # plan doc (tech-spec.md, implementation-plan.md, architecture.md) defines
  # an inbound message length limit config key, default, or owner. Including
  # such scenarios would encode an invented product contract that QA cannot
  # validate. If this limit is added to a sibling doc in a future iteration,
  # corresponding E2E scenarios should be added at that time.

  Scenario: Adaptive Card action payload is malformed
    Given user "alice@contoso.com" submits an Adaptive Card action
    And the action payload is missing the QuestionId field
    When the bot processes the invoke activity
    Then the bot responds with HTTP 200 and an error card: "Invalid action. Please try again."
    And no HumanDecisionEvent is created
    And an immutable audit record is persisted with:
      | Field     | Value                          |
      | EventType | Error                          |
      | Action    | MalformedCardAction            |
      | Outcome   | Failed                         |
      | ActorId   | <alice's AadObjectId>          |
    And the malformed payload is logged for debugging

  Scenario: Service URL changes between interactions
    Given the stored conversation reference for "alice@contoso.com" has ServiceUrl "https://smba.trafficmanager.net/us/"
    When alice sends a new message with ServiceUrl "https://smba.trafficmanager.net/eu/"
    Then the conversation reference is updated with the new ServiceUrl
    And subsequent proactive messages use the updated ServiceUrl

  Scenario: Concurrent approvals for the same single-decision channel question (first-writer-wins)
    Given question "Q-999" is a single-decision question (the release gate threshold is 1)
    And question "Q-999" was sent to channel "#ops-swarm" (TargetChannelId = "channel-ops-swarm", TargetUserId = null)
    And both "alice@contoso.com" and "bob@contoso.com" can see and interact with the card in the channel
    When both users click "Approve" within the same second
    Then exactly one IAgentQuestionStore.TryUpdateStatusAsync("Q-999", "Open", "Resolved", ct) call succeeds (first-writer-wins, compare-and-set)
    And exactly one HumanDecisionEvent is created for the winning user
    And the second user's TryUpdateStatusAsync returns false (Status was not "Open" — already Resolved by the first user)
    And the second user sees: "This question has already been decided."
    And both outcomes are audit-logged
    # Note: Q-999 uses TargetChannelId (not TargetUserId) per the AgentQuestion model
    # where exactly one of TargetUserId or TargetChannelId must be non-null
    # (architecture.md §AgentQuestion field table lines 289-290, implementation-plan.md
    # §1.1 line 16). A channel-scoped card is visible to all channel members,
    # creating the concurrent-approval scenario naturally.

  Scenario: Concurrent approvals for a multi-approver release gate (separate questions, both accepted)
    Given the orchestrator created two separate AgentQuestion records for a release gate:
      | QuestionId | TargetUserId                     | Status |
      | Q-gate-x   | <alice's internal user ID>       | Open   |
      | Q-gate-y   | <bob's internal user ID>         | Open   |
    And the release-gate workflow requires 2 approvals at the orchestration layer
    When both users click "Approve" on their respective cards within the same second
    Then Q-gate-x transitions to Resolved (alice's first-writer-wins on her own question)
    And Q-gate-y transitions to Resolved (bob's first-writer-wins on his own question)
    And two separate HumanDecisionEvents are created (one per question)
    And both cards are updated to show individual approval status
    And the orchestrator aggregates both decisions and transitions the release gate to approved
    And both individual approvals are audit-logged

  # Note: Multi-approver release gates are modeled as separate AgentQuestion records
  # — one per approver — each following the standard single-decision first-writer-wins
  # lifecycle (architecture §6.3.1, §6.3 step 5). The orchestrator's workflow layer
  # tracks the approval threshold and aggregates individual decisions. This avoids
  # contradicting the AgentQuestion.Status lifecycle where CardActionHandler atomically
  # transitions Open → Resolved on the first accepted action and rejects subsequent actions.

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

## Document Scope

**Coverage:** Personal chat, channel mention, proactive blocking questions, Adaptive Card approvals/rejections, conversation reference lifecycle (install path and message path), bot installation/uninstall, tenant app-policy enforcement (including allow-list gate), update/delete of sent cards, multi-approver release gates (modeled as separate AgentQuestion records per architecture §6.3.1), RBAC/tenant security, retry/dead-letter, audit trail, performance (P95), message actions (direct submit), edge cases (concurrent approvals, stale references 403 and 404, rate limiting, service restart, empty messages).

---

## Cross-Document Alignment Notes

These notes document how this document resolved known signature differences between sibling plan documents. All scenarios now use consistent contracts.

| Topic | This Document | Sibling Reference | Resolution |
|-------|--------------|-------------------|------------|
| Status transition method | Uses `TryUpdateStatusAsync(questionId, "Open", "Resolved", ct)` — 4-arg compare-and-set returning `Task<bool>`, first-writer-wins semantics (returns `false` if `expectedStatus` does not match current Status) | architecture.md §4.11 line 766 declares `Task<bool> TryUpdateStatusAsync(string questionId, string expectedStatus, string newStatus, CancellationToken ct)`; §6.3 line 957 calls `TryUpdateStatusAsync(questionId, "Open", "Resolved")`; implementation-plan.md §1.2 line 39 declares the same 4-arg `TryUpdateStatusAsync` signature | **Resolved** — this document now uses the architecture.md and implementation-plan.md 4-arg `TryUpdateStatusAsync` compare-and-set signature throughout. All scenario steps pass `expectedStatus = "Open"` as the second argument. |
| Bare approve/reject query method | Uses `GetOpenByConversationAsync(conversationId)` returning `IReadOnlyList<AgentQuestion>` with count-based branching: exactly-one → auto-resolve; zero → "no open questions"; more-than-one → disambiguation card | implementation-plan.md §1.2 line 39 declares `GetOpenByConversationAsync(string conversationId, CancellationToken ct)` returning `IReadOnlyList<AgentQuestion>`; §3.2 line 187 specifies the handler calls this method and branches on list count; §3.2 line 202 has a test expecting disambiguation for multiple open questions | **Resolved** — this document now uses the list-returning `GetOpenByConversationAsync` method in all bare approve/reject scenarios, with explicit count-based branching matching the implementation-plan.md handler contract. |
| Approval event envelope | Card approval/rejection scenarios explicitly assert that the `HumanDecisionEvent` payload is wrapped in a `MessengerEvent` with `EventType = "Decision"` (the `DecisionEvent` subtype) before enqueuing via `IInboundEventPublisher.PublishAsync` | architecture.md §3.1 lines 348-355 defines `DecisionEvent` subtype with `EventType = "Decision"` carrying `HumanDecisionEvent` payload; `implementation-plan.md` §1.1 line 19 defines `DecisionEvent` with `EventType = "Decision"` | **Resolved** — all Adaptive Card action scenarios now explicitly assert the `MessengerEvent`/`Decision` envelope wrapping the `HumanDecisionEvent` payload, preventing QA from implementing a parallel queue vocabulary. |
