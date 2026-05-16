# End-to-End Scenarios -- Discord Messenger Support (qq-DISCORD-MESSENGER-SU)

This document defines Gherkin-style feature scenarios for the Discord Messenger
connector. Scenarios are anchored to the acceptance criteria in the story
description and the component model in architecture.md. Each feature maps to a
specific requirement area; edge cases and failure modes are called out inline.

Cross-references:
- architecture.md -- component diagram, data model, sequence flows
- tech-spec.md -- scope boundaries, hard constraints, assumptions
- implementation-plan.md -- phased build order and test stages

---

## Feature 1: Slash Command Execution

Validates that all `/agent` subcommands are received through the Discord
Gateway, routed through the interaction pipeline, and dispatched to the
swarm orchestrator via `ISwarmCommandBus`.

### Scenario 1.1: Create a task with `/agent ask` (AC-1)

```gherkin
Feature: Slash command -- /agent ask

  Scenario: Operator creates a task via /agent ask
    Given the Discord bot is connected to the Gateway
    And a GuildBinding exists for guild "G-1" channel "C-control" with ChannelPurpose "Control"
    And the operator has Discord role "R-ops" which is in AllowedRoleIds
    When the operator sends "/agent ask architect design update-service cache strategy" in channel "C-control"
    Then the DiscordGatewayService persists a DiscordInteractionRecord with InteractionType "SlashCommand"
    And DeferAsync is called within 3 seconds of receiving the interaction
    And AuthorizeAsync confirms guild "G-1", channel "C-control", and role "R-ops"
    And SlashCommandDispatcher publishes a SwarmCommand with CommandType "ask"
      | Field       | Value                                         |
      | AgentTarget | architect                                     |
      | Arguments   | prompt = "design update-service cache strategy"|
    And ISwarmCommandBus.PublishCommandAsync is invoked with a CorrelationId
    And a follow-up embed is posted to channel "C-control" confirming task creation
    And AuditLogger records the command with GuildId, ChannelId, UserId, InteractionId, and CorrelationId
```

### Scenario 1.2: Query swarm-wide status with `/agent status`

```gherkin
Feature: Slash command -- /agent status (swarm-wide)

  Scenario: Operator queries swarm status without agent-id
    Given the Discord bot is connected and authorized
    When the operator sends "/agent status" in the control channel
    Then SlashCommandDispatcher calls ISwarmCommandBus.QueryStatusAsync
    And the follow-up embed contains TotalAgents, ActiveTasks, and BlockedCount
    And AuditLogger records the status query
```

### Scenario 1.3: Query specific agent status with `/agent status <agent-id>`

```gherkin
Feature: Slash command -- /agent status (single agent)

  Scenario: Operator queries a specific agent's status
    Given the Discord bot is connected and authorized
    And agent "build-agent-3" is active with role "Coder" and confidence 80
    When the operator sends "/agent status build-agent-3" in the control channel
    Then SlashCommandDispatcher calls ISwarmCommandBus.QueryAgentsAsync with AgentId filter "build-agent-3"
    And the follow-up embed shows agent name "build-agent-3", role "Coder", current task, and confidence "[####-] 80%"
```

### Scenario 1.4: Approve a pending question with `/agent approve`

```gherkin
Feature: Slash command -- /agent approve

  Scenario: Operator approves a pending question via slash command
    Given a PendingQuestionRecord exists with QuestionId "Q-42" and Status "Pending"
    When the operator sends "/agent approve Q-42" in the control channel
    Then SlashCommandDispatcher resolves the "approve" HumanAction from the pending question
    And ISwarmCommandBus.PublishHumanDecisionAsync is called with ActionValue from the approve action
    And IPendingQuestionStore.MarkAnsweredAsync is called for "Q-42"
    And the original question embed is edited to disable buttons and show "Approved by @operator"
    And AuditLogger records the approval with all Discord IDs and CorrelationId
```

### Scenario 1.5: Reject a pending question with `/agent reject`

```gherkin
Feature: Slash command -- /agent reject

  Scenario: Operator rejects a pending question with a reason
    Given a PendingQuestionRecord exists with QuestionId "Q-42" and Status "Pending"
    When the operator sends "/agent reject Q-42 architecture does not support this approach" in the control channel
    Then SlashCommandDispatcher publishes a HumanDecisionEvent with:
      | Field            | Value   |
      | QuestionId       | Q-42    |
      | SelectedActionId | reject  |
      | ActionValue      | reject  |
      | Messenger        | Discord |
    And the rejection reason "architecture does not support this approach" is stored in the AuditLogEntry Details JSON
    And the question embed is updated to show rejection with the reason
    And AuditLogger records the rejection
```

### Scenario 1.6: Reassign a task with `/agent assign`

```gherkin
Feature: Slash command -- /agent assign

  Scenario: Operator reassigns a task to a different agent
    Given task "T-42" is assigned to agent "coder-1"
    When the operator sends "/agent assign T-42 coder-2" in the control channel
    Then a SwarmCommand is published with CommandType "assign" and Arguments task-id="T-42", agent-id="coder-2"
    And a follow-up embed confirms "Task T-42 reassigned from coder-1 to coder-2"
```

### Scenario 1.7: Pause an agent with `/agent pause`

```gherkin
Feature: Slash command -- /agent pause

  Scenario: Operator pauses an active agent
    Given agent "coder-1" is active
    When the operator sends "/agent pause coder-1" in the control channel
    Then a SwarmCommand is published with CommandType "pause" and AgentTarget "coder-1"
    And a follow-up embed confirms "Agent coder-1 paused"
```

### Scenario 1.8: Resume a paused agent with `/agent resume`

```gherkin
Feature: Slash command -- /agent resume

  Scenario: Operator resumes a paused agent
    Given agent "coder-1" is paused
    When the operator sends "/agent resume coder-1" in the control channel
    Then a SwarmCommand is published with CommandType "resume" and AgentTarget "coder-1"
    And a follow-up embed confirms "Agent coder-1 resumed"
```

---

## Feature 2: Agent Questions and Interactive Components (AC-2)

Validates that agents can post blocking questions with interactive Discord
components (buttons, select menus, modals) and that operator responses are
captured as `HumanDecisionEvent` records.

### Scenario 2.1: Agent posts a question with four action buttons

```gherkin
Feature: Agent question with buttons

  Scenario: Agent posts a question with Approve, Reject, Need more info, and Delegate buttons
    Given the Discord bot is connected
    And the outbound queue processor is running
    When the orchestrator emits an AgentQuestionEnvelope wrapping an AgentQuestion with QuestionId "Q-42"
      | Envelope Field           | Value                                  |
      | Question.AgentId         | build-agent-3                          |
      | Question.TaskId          | T-42                                   |
      | Question.Title           | Cache strategy approval                |
      | Question.Body            | Should we use Redis or Memcached?      |
      | Question.Severity        | High                                   |
      | Question.AllowedActions  | Approve, Reject, Need more info, Delegate |
      | ProposedDefaultActionId  | approve                                |
      | RoutingMetadata          | DiscordChannelId = "C-control"         |
    Then DiscordMessengerConnector.SendQuestionAsync(envelope, ct) enqueues an OutboundMessage with:
      | Field            | Value                                           |
      | SourceType       | Question                                        |
      | Severity         | High                                            |
      | SourceEnvelopeJson | serialized AgentQuestionEnvelope (full envelope) |
      | IdempotencyKey   | q:build-agent-3:Q-42                             |
    And OutboundQueueProcessor dequeues the message and calls DiscordMessageSender.SendQuestionAsync
    And the Discord embed includes:
      | Embed Section   | Content                            |
      | Author          | build-agent-3 (Coder)              |
      | Field: Task     | T-42 - Cache strategy              |
      | Field: Confidence | [####-] 80%                      |
      | Title           | Cache strategy approval             |
      | Description     | Should we use Redis or Memcached?  |
      | Color sidebar   | Orange (High severity)             |
    And the message contains an action row with 4 buttons:
      | Label          | custom_id              | Style   |
      | Approve        | q:Q-42:approve         | Success |
      | Reject         | q:Q-42:reject          | Danger  |
      | Need more info | q:Q-42:need-info       | Primary |
      | Delegate       | q:Q-42:delegate        | Secondary |
    And IPendingQuestionStore.StoreAsync is called with the Discord message ID
    And AuditLogger records the outbound question with CorrelationId
```

### Scenario 2.2: Operator clicks Approve button on a question

```gherkin
Feature: Button interaction -- approve

  Scenario: Operator clicks the Approve button
    Given a pending question "Q-42" exists with 4 action buttons
    When the operator clicks the button with custom_id "q:Q-42:approve"
    Then DiscordGatewayService persists a DiscordInteractionRecord with InteractionType "ButtonClick"
    And DeferAsync is called within 3 seconds
    And ComponentInteractionHandler parses QuestionId "Q-42" and ActionId "approve" from custom_id
    And IPendingQuestionStore.GetAsync("Q-42") returns the pending question
    And HumanAction.Value is resolved for ActionId "approve"
    And ISwarmCommandBus.PublishHumanDecisionAsync is called with:
      | Field             | Value     |
      | QuestionId        | Q-42      |
      | ActionValue       | approve   |
      | Messenger         | Discord   |
      | ExternalUserId    | <operator Discord user ID> |
    And IPendingQuestionStore.MarkAnsweredAsync is called for "Q-42"
    And the original embed is edited to disable all buttons
    And a result line is added: "Approved by @operator at <timestamp>"
    And AuditLogger records the decision with InteractionId and CorrelationId
```

### Scenario 2.3: Question with more than 5 actions uses select menu

> **Platform adaptation:** Buttons encode `ActionId` in `custom_id` as
> `q:{QuestionId}:{ActionId}` (per architecture.md Section 8.3). Discord
> select menus have one `custom_id` per component (not per option), so the
> select menu carries `q:{QuestionId}:select` as its component `custom_id`
> and each option encodes `ActionId` in the option `value` field. The
> `ComponentInteractionHandler` handles both encodings: for buttons it
> parses `ActionId` from `custom_id`; for select menus it parses
> `QuestionId` from `custom_id` and reads `ActionId` from the selected
> option value.

```gherkin
Feature: Select menu for questions with many actions

  Scenario: Agent question with 6 allowed actions renders as a select menu
    Given an AgentQuestionEnvelope wraps an AgentQuestion with 6 AllowedActions
    When DiscordMessageSender renders the question embed
    Then the message uses a select menu component instead of buttons
    And the select menu component has custom_id "q:Q-42:select" (QuestionId in custom_id)
    And each AllowedAction appears as a select menu option with:
      | Option field | Value                                      |
      | label        | HumanAction.Label (display text)           |
      | value        | HumanAction.ActionId (parsed on selection) |
    And the select menu placeholder reads "Choose an action..."
```

### Scenario 2.4: Action with RequiresComment triggers a modal dialog

```gherkin
Feature: Modal for comment-required actions

  Scenario: Operator selects an action that requires a comment
    Given a pending question "Q-55" has AllowedAction "reject" with RequiresComment = true
    When the operator clicks the "Reject" button with custom_id "q:Q-55:reject"
    Then DiscordGatewayService persists the DiscordInteractionRecord
    And ComponentInteractionHandler detects RequiresComment = true for "reject"
    And RespondWithModalAsync is called (this IS the interaction ACK -- DeferAsync is NOT called)
    And a Discord modal dialog is opened with:
      | Field       | Value                          |
      | Title       | Provide rejection reason       |
      | Input label | Comment                        |
      | Style       | Paragraph (multi-line)         |
      | custom_id   | modal:Q-55:reject              |
    And IPendingQuestionStore status transitions to "AwaitingComment" for "Q-55"
    When the operator submits the modal with text "Approach violates the caching policy"
    Then a DiscordInteractionRecord is persisted with InteractionType "ModalSubmit"
    And DeferAsync is called for the modal submit interaction
    And ISwarmCommandBus.PublishHumanDecisionAsync is called with:
      | Field            | Value   |
      | QuestionId       | Q-55    |
      | SelectedActionId | reject  |
      | ActionValue      | reject  |
      | Messenger        | Discord |
    And the modal comment text "Approach violates the caching policy" is stored in AuditLogEntry Details JSON
    And IPendingQuestionStore.MarkAnsweredAsync is called for "Q-55"
    And the question embed is updated to show rejection and the comment text from the audit record
```

### Scenario 2.5: Question times out with default action

```gherkin
Feature: Question timeout with default action

  Scenario: Pending question expires and default action is applied
    Given a pending question "Q-60" exists with ExpiresAt 30 minutes from now
    And ProposedDefaultActionId is "approve" with Value "approve"
    When 30 minutes elapse without operator response
    Then QuestionTimeoutService detects "Q-60" via IPendingQuestionStore.GetExpiredAsync
    And ISwarmCommandBus.PublishHumanDecisionAsync is called with:
      | Field            | Value   |
      | QuestionId       | Q-60    |
      | SelectedActionId | approve |
      | ActionValue      | approve |
    And the auto-timeout reason is recorded in AuditLogEntry Details JSON
    And IPendingQuestionStore status transitions to "TimedOut"
    And the question embed is edited to show "Timed out -- default action 'Approve' applied"
    And buttons are disabled on the embed
```

### Scenario 2.6: Question times out with no default action

```gherkin
Feature: Question timeout without default action

  Scenario: Pending question expires with no proposed default
    Given a pending question "Q-61" exists with ExpiresAt 30 minutes from now
    And ProposedDefaultActionId is null
    When 30 minutes elapse without operator response
    Then QuestionTimeoutService detects "Q-61" via IPendingQuestionStore.GetExpiredAsync
    And IPendingQuestionStore status transitions to "TimedOut"
    And the question embed is edited to show "Timed out -- no default action; agent is blocked"
    And buttons are disabled on the embed
    And no HumanDecisionEvent is published
```

---

## Feature 3: Gateway Connection and Reconnection (AC-3)

Validates that the bot connects to the Discord Gateway on startup,
automatically reconnects after disconnects, and handles non-recoverable
close codes.

### Scenario 3.1: Bot connects and registers slash commands on startup

```gherkin
Feature: Gateway startup

  Scenario: Bot connects to Gateway and registers guild commands
    Given DiscordOptions is configured with a valid BotToken, GuildId, and channel IDs
    And the bot application has required guild permissions (Send Messages, Use Slash Commands, Embed Links, Create Public Threads, Send Messages in Threads, Manage Messages)
    And the MessageContent privileged intent is enabled in the Developer Portal
    When the Worker service starts DiscordGatewayService
    Then DiscordSocketClient connects to the Gateway with declared intents (Guilds, GuildMessages, MessageContent, GuildMessageReactions)
    And the /agent command group is registered as a guild command with subcommands: ask, status, approve, reject, assign, pause, resume
    And the discord.gateway.connected gauge is set to 1
    And the service subscribes to InteractionCreated, MessageReceived, Disconnected, and Connected events
```

### Scenario 3.2: Gateway disconnect triggers automatic reconnection

```gherkin
Feature: Gateway reconnection

  Scenario: Bot reconnects after a recoverable Gateway disconnect
    Given the bot is connected to the Gateway
    When Discord sends a close code 4000 (Unknown error)
    Then DiscordGatewayService receives the Disconnected event
    And the close code and reason are logged
    And discord.gateway.connected gauge is set to 0
    And Discord.Net DiscordSocketClient initiates reconnection with exponential backoff (1s, 2s, 4s, 8s)
    And the Gateway session is resumed, replaying missed events from the last sequence number
    And discord.gateway.connected gauge is set to 1
    And discord.gateway.reconnect counter is incremented
    And reconnection completes within 15 seconds
```

### Scenario 3.3: Session cannot resume -- new session established

```gherkin
Feature: Gateway new session after timeout

  Scenario: Gateway session timed out, new session established
    Given the bot was disconnected for longer than the Gateway session timeout
    When Discord.Net attempts session resumption
    And Discord responds with an invalid session indicator
    Then a new Gateway session is established
    And slash commands are re-registered for the guild
    And event subscriptions are re-established
    And InteractionRecoverySweep re-processes any incomplete DiscordInteractionRecords
```

### Scenario 3.4: Non-recoverable close code stops the bot permanently

```gherkin
Feature: Fatal Gateway close codes

  Scenario Outline: Bot stops permanently on fatal close code
    Given the bot is connected to the Gateway
    When Discord sends close code <code> (<description>)
    Then DiscordGatewayService logs a critical error with the close code
    And the service stops permanently without retrying
    And discord.gateway.connected gauge is set to 0
    And no reconnection attempt is made

    Examples:
      | code | description           |
      | 4004 | Authentication Failed |
      | 4014 | Disallowed Intents    |
```

### Scenario 3.5: Outbound messages queue during Gateway disconnect

```gherkin
Feature: Outbound queuing during disconnect

  Scenario: Outbound messages accumulate and drain after reconnect
    Given the bot is disconnected from the Gateway
    And the orchestrator emits 5 agent status updates during the disconnect window
    When OutboundQueueProcessor attempts to send via DiscordMessageSender
    Then REST API calls fail with connection errors
    And failed messages are retried with exponential backoff
    And no messages are dead-lettered during the short disconnect window
    When the Gateway reconnects
    Then pending outbound messages are successfully delivered in severity order
    And all 5 status updates are eventually sent
```

### Scenario 3.6: Bot refuses to start when MessageContent intent is not enabled

```gherkin
Feature: Startup failure on missing privileged intent

  Scenario: Bot fails to start when MessageContent intent is not enabled
    Given DiscordOptions declares GatewayIntents including MessageContent
    And the MessageContent intent is NOT enabled in the Discord Developer Portal
    When the Worker service starts DiscordGatewayService
    Then the Gateway connection is rejected with close code 4014 (Disallowed Intents)
    And the bot logs a critical error explaining the missing intent
    And the service stops permanently without retrying or falling back to slash-command-only mode
```

### Scenario 3.7: Bot refuses to start when guild permissions are missing

```gherkin
Feature: Startup validation of guild permissions

  Scenario: Bot refuses to start when required permissions are absent
    Given the bot application does not have "Send Messages" permission in the target guild
    When the Worker service starts DiscordGatewayService
    Then startup validation detects missing "Send Messages" permission
    And a fatal exception is thrown
    And the service stops permanently without entering a degraded mode
```

---

## Feature 4: Deduplication and Idempotency (AC-4)

Validates that duplicate Discord interactions are suppressed at both the
in-memory cache layer and the database UNIQUE constraint layer.

### Scenario 4.1: Duplicate interaction suppressed by in-memory cache

```gherkin
Feature: In-memory deduplication

  Scenario: Duplicate interaction ID is ignored via fast-path cache
    Given the bot has already processed interaction ID "I-100"
    And "I-100" is still within the IDeduplicationService TTL window (default 1 hour)
    When a second event with interaction ID "I-100" arrives at DiscordGatewayService
    Then IDeduplicationService.TryReserveAsync returns false
    And the interaction is dropped without persisting a DiscordInteractionRecord
    And discord.interactions.duplicated counter is incremented
    And no DeferAsync or pipeline processing occurs
```

### Scenario 4.2: Duplicate interaction suppressed by database UNIQUE constraint

```gherkin
Feature: Database deduplication

  Scenario: Duplicate interaction ID is caught by UNIQUE constraint after cache miss
    Given the bot was restarted (in-memory cache is empty)
    And a DiscordInteractionRecord with InteractionId "I-100" exists in the database
    When a re-delivered event with interaction ID "I-100" arrives
    Then IDiscordInteractionStore.PersistAsync returns false (UNIQUE constraint violation)
    And the interaction is dropped without calling DeferAsync
    And discord.interactions.duplicated counter is incremented
```

### Scenario 4.3: Outbound message idempotency key prevents duplicate enqueue

```gherkin
Feature: Outbound idempotency

  Scenario: Same question is not enqueued twice
    Given an OutboundMessage with IdempotencyKey "q:build-agent-3:Q-42" is already in the outbound queue
    When DiscordMessengerConnector.SendQuestionAsync is called again with the same AgentQuestionEnvelope
    Then the OutboundMessage UNIQUE constraint on IdempotencyKey rejects the duplicate
    And no second message is enqueued
```

### Scenario 4.4: InteractionRecoverySweep re-processes incomplete interactions

```gherkin
Feature: Interaction recovery sweep

  Scenario: Incomplete interactions are re-processed after crash
    Given a DiscordInteractionRecord exists with IdempotencyStatus "Received" and AttemptCount 0
    And the record was persisted before a process crash (DeferAsync was never called)
    When InteractionRecoverySweep runs on startup
    Then IDiscordInteractionStore.GetRecoverableAsync returns the record
    And the record is re-enqueued into IDiscordInteractionPipeline.ProcessAsync
    And AttemptCount is incremented to 1
    And the interaction is processed normally through the pipeline
```

### Scenario 4.5: Recovery sweep respects max retry count

```gherkin
Feature: Recovery sweep max retries

  Scenario: Interaction exceeding max retries is marked permanently failed
    Given a DiscordInteractionRecord exists with IdempotencyStatus "Failed" and AttemptCount 3
    And the configured max retry count is 3
    When InteractionRecoverySweep runs
    Then the record is NOT re-processed
    And IdempotencyStatus remains "Failed"
    And the record is excluded from GetRecoverableAsync results
```

---

## Feature 5: Priority Queuing and Rate-Limit Handling (AC-5)

Validates that high-priority alerts are delivered before low-priority
status updates, and that Discord REST rate limits are respected.

### Scenario 5.1: Critical alert dequeued before low-priority status updates

```gherkin
Feature: Severity-based priority queuing

  Scenario: Critical alert is delivered before Low-severity status messages
    Given the outbound queue contains:
      | MessageId | Severity | SourceType   | CreatedAt  |
      | M-1       | Low      | StatusUpdate | T+0s       |
      | M-2       | Low      | StatusUpdate | T+1s       |
      | M-3       | Critical | Alert        | T+2s       |
    When OutboundQueueProcessor calls IOutboundQueue.DequeueAsync
    Then M-3 (Critical) is dequeued first despite being enqueued last
    And M-1 (Low) is dequeued after all Critical and High messages are sent
    And M-2 (Low) is dequeued after M-1 (oldest-first within same severity)
```

### Scenario 5.2: Critical alert includes @here mention

```gherkin
Feature: Critical alert with @here mention

  Scenario: Critical alert is routed to alert channel with @here
    Given the guild has an alert channel with ChannelPurpose "Alert"
    When the orchestrator emits an AlertEvent with Severity "Critical"
    Then the alert is routed to the alert channel via IGuildRegistry.GetAlertChannelAsync
    And the Discord message includes an "@here" mention before the embed
    And the embed sidebar color is red
```

### Scenario 5.3: High alert does not include @here mention

```gherkin
Feature: High alert without @here

  Scenario: High-severity alert is delivered without @here
    Given the guild has an alert channel
    When the orchestrator emits an AlertEvent with Severity "High"
    Then the alert is routed to the alert channel
    And the embed sidebar color is orange
    And no @here mention is included
```

### Scenario 5.4: Low-priority status updates are batched when queue depth exceeds threshold

```gherkin
Feature: Low-severity batching

  Scenario: Multiple Low-severity status updates are combined into one embed
    Given the outbound queue contains 55 pending Low-severity StatusUpdate messages
    And the batching threshold is configured at 50
    When OutboundQueueProcessor processes Low-severity messages
    Then IOutboundQueue.CountPendingAsync(Low) returns 55 (above threshold)
    And IOutboundQueue.DequeueBatchAsync(Low, 10) returns up to 10 messages
    And DiscordMessageSender.SendBatchAsync combines them into a single embed with a table layout
    And the batch embed shows agent name, status, and task for each agent
    And only 1 REST API call is made instead of 10
```

### Scenario 5.5: Critical and High messages are never batched

```gherkin
Feature: High-severity messages sent individually

  Scenario: Critical and High messages bypass batching
    Given the outbound queue contains 100 pending Low-severity messages
    And 2 Critical alerts and 3 High alerts are also queued
    When OutboundQueueProcessor processes the queue
    Then each Critical alert is sent as an individual embed
    And each High alert is sent as an individual embed
    And Low-severity messages may be batched
```

### Scenario 5.6: Rate limit hit causes delay, not message loss

```gherkin
Feature: Rate limit handling

  Scenario: Discord REST rate limit hit triggers backoff without losing messages
    Given DiscordMessageSender is sending messages to channel "C-control"
    When Discord returns HTTP 429 with Retry-After header of 5 seconds
    Then DiscordMessageSender cooperates with Discord.Net built-in rate limit handler
    And outbound sends to channel "C-control" are delayed for 5 seconds
    And discord.ratelimit.hits counter is incremented with route tag
    And no messages are dropped or dead-lettered due to rate limiting
    And messages resume sending after the Retry-After window expires
```

### Scenario 5.7: Proactive rate limit avoidance

```gherkin
Feature: Proactive rate limit reading

  Scenario: Sender reads X-RateLimit headers and delays proactively
    Given DiscordMessageSender has sent messages to a channel route
    And the REST response includes X-RateLimit-Remaining = 1 and X-RateLimit-Reset = 3 seconds
    When another outbound message targets the same route
    Then the sender delays the send until the rate limit window resets
    And the send completes without hitting HTTP 429
```

### Scenario 5.8: Message ordering preserved per channel

```gherkin
Feature: Per-channel message ordering

  Scenario: Messages to the same channel are delivered in severity-then-FIFO order
    Given 3 Normal-severity messages are enqueued for channel "C-control" at T+0, T+1, T+2
    When all 3 are dequeued and sent
    Then they are delivered to Discord in the order T+0, T+1, T+2
    And no reordering occurs within the same severity
```

---

## Feature 6: Security and Authorization (AC-6)

Validates that only authorized Discord users in authorized guilds and
channels can issue agent-control commands. Rejections are ephemeral.

### Scenario 6.1: Unauthorized user without required role

```gherkin
Feature: Role-based authorization

  Scenario: User without an authorized role receives ephemeral rejection
    Given a GuildBinding exists for guild "G-1" channel "C-control" with AllowedRoleIds ["R-ops", "R-lead"]
    And user "U-visitor" has roles ["R-member"] (none in AllowedRoleIds)
    When "U-visitor" sends "/agent ask coder-1 build the widget" in channel "C-control"
    Then DiscordGatewayService persists the DiscordInteractionRecord
    And DeferAsync is called (ACK within 3 seconds)
    And IUserAuthorizationService.AuthorizeAsync returns IsAllowed = false with DenialReason "User lacks required role"
    And an ephemeral follow-up is sent via FollowupAsync(text, ephemeral: true)
    And the message reads "You do not have permission to execute this command."
    And the non-ephemeral deferred "thinking" indicator is deleted via DeleteOriginalResponseAsync
    And discord.interactions.rejected counter is incremented
    And AuditLogger records the rejected attempt with UserId, DenialReason, and CorrelationId
```

### Scenario 6.2: Command from an unregistered guild

```gherkin
Feature: Guild authorization

  Scenario: Command from an unregistered guild is rejected
    Given no GuildBinding exists for guild "G-unknown"
    When a user sends "/agent status" from guild "G-unknown"
    Then IUserAuthorizationService.AuthorizeAsync returns IsAllowed = false with DenialReason "Guild not registered"
    And an ephemeral rejection is sent to the user
    And AuditLogger records the rejection
```

### Scenario 6.3: Command from an unauthorized channel

```gherkin
Feature: Channel authorization

  Scenario: Command from a non-bound channel is rejected
    Given a GuildBinding exists for guild "G-1" channel "C-control" only
    And no binding exists for channel "C-random" in guild "G-1"
    When a user sends "/agent ask coder-1 do something" in channel "C-random"
    Then IUserAuthorizationService.AuthorizeAsync returns IsAllowed = false with DenialReason "Channel not authorized"
    And an ephemeral rejection is sent
```

### Scenario 6.4: Per-command role restriction via CommandRestrictions

```gherkin
Feature: Per-subcommand role overrides

  Scenario: Operator with base role cannot approve when CommandRestrictions restricts it
    Given a GuildBinding has AllowedRoleIds ["R-ops", "R-lead"]
    And CommandRestrictions has "approve" mapped to ["R-lead"] only
    And user "U-ops" has role "R-ops" but not "R-lead"
    When "U-ops" sends "/agent approve Q-42" in the control channel
    Then AuthorizeAsync checks CommandRestrictions["approve"] and finds "R-ops" is not in ["R-lead"]
    And IsAllowed = false with DenialReason "User lacks required role for subcommand 'approve'"
    And an ephemeral rejection is sent

  Scenario: Operator with senior role can approve
    Given the same CommandRestrictions configuration
    And user "U-lead" has role "R-lead"
    When "U-lead" sends "/agent approve Q-42" in the control channel
    Then AuthorizeAsync passes the CommandRestrictions check
    And the command is processed normally
```

### Scenario 6.5: Ephemeral rejection is visible only to the unauthorized user

```gherkin
Feature: Ephemeral rejection visibility

  Scenario: Other users do not see the rejection message
    Given user "U-visitor" is unauthorized
    When "U-visitor" sends "/agent ask coder-1 build something" in the control channel
    Then the rejection follow-up is marked ephemeral: true
    And only "U-visitor" can see the rejection message
    And the deferred "thinking" indicator (non-ephemeral) is deleted via DeleteOriginalResponseAsync
    And other channel members see no trace of the failed command
```

### Scenario 6.6: Bot token is never logged

```gherkin
Feature: Token security

  Scenario: Bot token does not appear in logs or telemetry
    Given DiscordOptions.BotToken is loaded from a secret store
    When the bot starts, connects, reconnects, or encounters errors
    Then no log entry, telemetry event, or error message contains the BotToken value
    And DiscordOptions validation rejects configurations that embed the token in non-secret fields
```

---

## Feature 7: Channel Model and Routing

Validates that the control channel, alert channel, and workstream channels
receive the correct message types, and that threads provide per-task
conversation isolation.

### Scenario 7.1: Operator commands are accepted only in the control channel

```gherkin
Feature: Control channel routing

  Scenario: Slash commands are accepted in the control channel
    Given a GuildBinding with ChannelPurpose "Control" for channel "C-control"
    And the operator has an authorized role
    When the operator sends "/agent ask architect design cache" in "C-control"
    Then AuthorizeAsync resolves the GuildBinding for "C-control" with ChannelPurpose "Control"
    And the command is processed normally through the interaction pipeline

  Scenario: Slash commands in a workstream channel are rejected
    Given a GuildBinding with ChannelPurpose "Workstream" for channel "C-ws-1"
    And the operator has an authorized role
    When the operator sends "/agent ask coder-1 build widget" in "C-ws-1"
    Then the interaction pipeline rejects the command because ChannelPurpose is not "Control"
    And an ephemeral follow-up states "Commands are only accepted in the control channel"
    And the command is NOT dispatched to ISwarmCommandBus
```

> **Note:** Per tech-spec Section 2.5, the Control channel receives operator
> commands and agent questions. Workstream channels receive task-specific
> status updates and threaded conversations only. The Alert channel receives
> priority alerts.

### Scenario 7.2: Alerts are routed to the alert channel

```gherkin
Feature: Alert channel routing

  Scenario: High and Critical alerts go to the alert channel
    Given a GuildBinding with ChannelPurpose "Alert" for channel "C-alert"
    When the orchestrator emits an AlertEvent with Severity "Critical"
    Then DiscordConnector resolves the alert channel via IGuildRegistry.GetAlertChannelAsync("G-1")
    And the OutboundMessage targets ChatId = "C-alert"
    And the alert embed is delivered to "C-alert"
```

### Scenario 7.3: Workstream updates are routed to per-task threads

```gherkin
Feature: Workstream thread routing

  Scenario: Agent status update creates or reuses a task thread
    Given a GuildBinding with ChannelPurpose "Workstream" for channel "C-ws-1"
    And no existing thread for TaskId "T-42" in "C-ws-1"
    When the orchestrator emits a StatusUpdate for agent "coder-1" on TaskId "T-42"
    Then DiscordMessageSender creates a new thread in "C-ws-1" named "T-42"
    And the status update is posted inside the thread
    When a subsequent update for TaskId "T-42" arrives
    Then the existing thread is reused (no new thread created)
```

### Scenario 7.4: Thread auto-archive after configured duration

```gherkin
Feature: Thread auto-archive

  Scenario: Thread auto-archive is set to 24 hours by default
    When DiscordMessageSender creates a thread for TaskId "T-42"
    Then the thread's auto-archive duration is set to 24 hours
    And after 24 hours of inactivity the thread is archived by Discord
    And thread content is preserved (not deleted)
    And operators can manually unarchive the thread to continue the conversation
```

---

## Feature 8: Audit and Traceability

Validates that every command, response, and decision is recorded in the
audit log with Discord-specific identifiers.

### Scenario 8.1: Slash command audit trail

```gherkin
Feature: Command audit logging

  Scenario: Every slash command is recorded with full Discord metadata
    When the operator sends "/agent ask architect design cache" in guild "G-1" channel "C-control"
    Then IAuditLogger.LogAsync is called with an AuditEntry containing:
      | Field          | Value                                |
      | Platform       | Discord                              |
      | ExternalUserId | <operator Discord user ID as string> |
      | MessageId      | <follow-up Discord message ID as string> |
      | CorrelationId  | <generated correlation ID>           |
    And AuditEntry.Details JSON contains:
      | Key            | Value                               |
      | GuildId        | G-1                                 |
      | ChannelId      | C-control                           |
      | InteractionId  | <interaction snowflake ID>          |
    And the audit record is immutable (append-only, no UPDATE or DELETE)
```

### Scenario 8.2: Human decision audit trail

```gherkin
Feature: Decision audit logging

  Scenario: Button click decision is recorded with all Discord IDs
    When the operator clicks "Approve" on question "Q-42"
    Then IAuditLogger.LogHumanResponseAsync is called with a HumanResponseAuditEntry containing:
      | Field           | Value                                             |
      | Platform        | Discord                                           |
      | ExternalUserId  | <operator Discord user ID as string>              |
      | MessageId       | <Discord message snowflake ID of bot follow-up>   |
      | CorrelationId   | <question correlation ID>                         |
    And AuditEntry.Details JSON includes:
      | Key            | Value                                              |
      | GuildId        | <guild snowflake ID>                               |
      | ChannelId      | <channel snowflake ID>                             |
      | InteractionId  | <interaction snowflake ID of the button click>     |
      | ThreadId       | <thread snowflake ID, if applicable>               |
```

### Scenario 8.3: CorrelationId propagated end-to-end

```gherkin
Feature: End-to-end correlation

  Scenario: CorrelationId links command to response to decision
    Given the operator sends "/agent ask architect design cache"
    And the system generates CorrelationId "trace-abc-123"
    When the command creates task "T-42"
    And agent "architect" posts a question "Q-42" with CorrelationId "trace-abc-123"
    And the operator approves via button click
    Then all audit entries for this flow share CorrelationId "trace-abc-123":
      - The /agent ask command audit entry
      - The outbound question message audit entry
      - The button click decision audit entry
      - The HumanDecisionEvent published to ISwarmCommandBus
```

### Scenario 8.4: Authorization rejection is audited

```gherkin
Feature: Rejection audit

  Scenario: Unauthorized command attempt is recorded in audit log
    Given user "U-visitor" is not authorized
    When "U-visitor" sends "/agent pause coder-1"
    Then AuditLogger records the rejected command with:
      | Field          | Value                            |
      | Platform       | Discord                          |
      | ExternalUserId | U-visitor                        |
      | Details.GuildId | G-1                             |
      | DenialReason   | User lacks required role         |
```

---

## Feature 9: Outbound Durability and Dead-Letter Queue

Validates the durable outbound pipeline, retry logic, and dead-letter
handling.

### Scenario 9.1: Failed outbound message retries with exponential backoff

```gherkin
Feature: Outbound retry

  Scenario: Transient REST failure triggers retry with backoff
    Given an OutboundMessage "M-1" is dequeued with AttemptCount 0 and MaxAttempts 5
    When DiscordMessageSender.SendTextAsync returns a transient error (e.g., HTTP 500)
    Then IOutboundQueue.MarkFailedAsync is called with the error detail
    And AttemptCount is incremented to 1
    And NextRetryAt is set using exponential backoff (e.g., 2^1 = 2 seconds)
    And the message remains in the queue for retry
    And discord.send.retry_count counter is incremented
```

### Scenario 9.2: Exhausted outbound message is dead-lettered

```gherkin
Feature: Dead-letter after retry exhaustion

  Scenario: Message exceeding MaxAttempts is moved to dead-letter queue
    Given an OutboundMessage "M-1" has AttemptCount 4 and MaxAttempts 5
    When the 5th delivery attempt fails
    Then IOutboundQueue.DeadLetterAsync is called for "M-1"
    And a DeadLetterMessage record is created with OriginalMessageId, ErrorReason, and FailedAt
    And OutboundMessage.Status transitions to "DeadLettered"
    And discord.send.dead_lettered counter is incremented
```

### Scenario 9.3: Gap A recovery -- crash between Discord API success and MarkSent

```gherkin
Feature: Gap A crash recovery

  Scenario: Crash after Discord REST success but before MarkSentAsync
    Given OutboundMessage "M-1" has Status "Sending" (dequeued, send in progress)
    And the Discord REST API returned success with PlatformMessageId
    And the process crashes before MarkSentAsync completes
    When the service restarts and OutboundQueueProcessor resumes
    Then "M-1" is found with Status "Sending" and no PlatformMessageId
    And the processor re-sends the message (producing a duplicate Discord message)
    And the duplicate is operationally benign because PendingQuestionRecord is keyed by QuestionId
    And only one pending question record exists for the question
```

### Scenario 9.4: Gap B recovery -- crash between MarkSent and PendingQuestionRecord

```gherkin
Feature: Gap B crash recovery

  Scenario: Crash after MarkSentAsync but before PendingQuestionRecord persistence
    Given OutboundMessage "M-1" has Status "Sent" with PlatformMessageId populated
    And SourceType is "Question" and SourceId is "Q-42"
    And no PendingQuestionRecord exists for "Q-42"
    When the service restarts and QuestionRecoverySweep runs
    Then the sweep finds "M-1" with SourceType=Question, Status=Sent, and no matching PendingQuestionRecord
    And the sweep backfills a PendingQuestionRecord using:
      | Field              | Source                                                   |
      | QuestionId         | M-1.SourceId                                             |
      | DiscordMessageId   | M-1.PlatformMessageId                                    |
      | DiscordChannelId   | M-1.ChatId (cast to ulong)                               |
      | AgentQuestion      | envelope.Question (from deserialized AgentQuestionEnvelope) |
      | DefaultActionId    | envelope.ProposedDefaultActionId                         |
      | DefaultActionValue | resolved from envelope.Question.AllowedActions            |
      | ExpiresAt          | envelope.Question.ExpiresAt                              |
    And SourceEnvelopeJson is deserialized as AgentQuestionEnvelope (not bare AgentQuestion)
    And the question is now correctly tracked for operator button clicks
```

---

## Feature 10: Agent Identity Display

Validates that outbound embeds display agent identity fields as specified
by the story.

### Scenario 10.1: Agent embed includes all identity fields

```gherkin
Feature: Agent identity in embeds

  Scenario: Agent question embed displays name, role, task, confidence, and blocking status
    Given the orchestrator emits an AgentQuestionEnvelope from agent "build-agent-3"
      | Envelope Field          | Value                      |
      | Question.AgentId        | build-agent-3              |
      | Question.TaskId         | T-42                       |
      | ProposedDefaultActionId | approve                    |
    And AgentInfo for "build-agent-3" reports:
      | Field           | Value                      |
      | Role            | Coder                      |
      | ConfidenceScore | 80                         |
      | BlockingQuestion | Q-42                      |
    When DiscordMessageSender renders the question embed
    Then the embed contains:
      | Embed Section        | Value                            |
      | Author name          | build-agent-3                    |
      | Author suffix        | (Coder)                          |
      | Field: Current task  | T-42 - <task description>        |
      | Field: Confidence    | [####-] 80%                      |
      | Field: Status        | Blocked - awaiting input         |
    And the embed uses a color sidebar matching the question Severity
```

### Scenario 10.2: Severity color coding

```gherkin
Feature: Severity-based embed colors

  Scenario Outline: Embed sidebar color matches message severity
    Given an outbound message with Severity "<severity>"
    When DiscordMessageSender renders the embed
    Then the embed sidebar color is "<color>"

    Examples:
      | severity | color  |
      | Critical | Red    |
      | High     | Orange |
      | Normal   | Blue   |
      | Low      | Gray   |
```

---

## Feature 11: Interaction Acknowledgement Timing

Validates the 3-second interaction ACK deadline and the persist-before-defer
invariant.

### Scenario 11.1: Interaction is deferred within 3 seconds

```gherkin
Feature: 3-second ACK deadline

  Scenario: DeferAsync is called within the 3-second Discord deadline
    Given the database supports < 500ms p99 INSERT latency
    When the operator sends any slash command or clicks a button where RequiresComment = false
    Then DiscordGatewayService persists the DiscordInteractionRecord
    And DeferAsync is called immediately after the durable persist
    And the total time from interaction receipt to DeferAsync completion is under 3 seconds
    And the user sees a "thinking..." indicator in Discord while processing continues

  Scenario: Button with RequiresComment uses modal as ACK instead of DeferAsync
    Given the database supports < 500ms p99 INSERT latency
    When the operator clicks a button where the resolved HumanAction has RequiresComment = true
    Then DiscordGatewayService persists the DiscordInteractionRecord
    And RespondWithModalAsync is called instead of DeferAsync (modal response IS the ACK)
    And the total time from interaction receipt to RespondWithModalAsync is under 3 seconds
    And the operator sees a modal dialog (not a "thinking..." indicator)
```

### Scenario 11.2: Persist happens before DeferAsync

```gherkin
Feature: Persist-before-ACK ordering

  Scenario: DiscordInteractionRecord is persisted before acknowledging the interaction
    When the operator sends "/agent ask architect design cache"
    Then DiscordGatewayService first calls IDiscordInteractionStore.PersistAsync
    And only after PersistAsync returns true does DiscordGatewayService call DeferAsync
    And if the process crashes after PersistAsync but before DeferAsync:
      - The interaction record exists in the database
      - InteractionRecoverySweep will re-process it on restart
      - Discord shows the interaction as failed to the user (no ACK received)
      - But the command is not lost
```

### Scenario 11.3: Duplicate interaction detected at persist layer skips DeferAsync

```gherkin
Feature: Duplicate skips ACK

  Scenario: Duplicate interaction is dropped before DeferAsync
    Given interaction "I-200" was already persisted
    When a re-delivered event with interaction ID "I-200" arrives
    Then IDiscordInteractionStore.PersistAsync returns false
    And DeferAsync is NOT called
    And no pipeline processing occurs
    And the duplicate is silently dropped
```

---

## Feature 12: Select Menu Interaction

Validates select menu behavior for questions with many actions. Uses the
platform-adapted encoding described in Scenario 2.3 above: component
`custom_id` = `q:{QuestionId}:select`, option `value` = `ActionId`.

### Scenario 12.1: Operator selects an action from a select menu

```gherkin
Feature: Select menu interaction

  Scenario: Operator selects an action from a dropdown menu
    Given a pending question "Q-70" has 6 AllowedActions rendered as a select menu
    And the select menu component has custom_id "q:Q-70:select"
    When the operator selects the option with value "need-info" from the dropdown
    Then DiscordGatewayService persists a DiscordInteractionRecord with InteractionType "SelectMenu"
    And ComponentInteractionHandler parses QuestionId "Q-70" from the component custom_id
    And ActionId "need-info" is read from the selected option's value field
    And IPendingQuestionStore.GetAsync("Q-70") returns the pending question
    And HumanAction.Value is resolved for ActionId "need-info"
    And ISwarmCommandBus.PublishHumanDecisionAsync is called with:
      | Field            | Value       |
      | QuestionId       | Q-70        |
      | SelectedActionId | need-info   |
      | ActionValue      | need-info   |
      | Messenger        | Discord     |
    And the question embed is updated to show the selection
```

---

## Feature 13: Concurrent Agent Scale

Validates system behavior under the target load of 100+ active agents.

### Scenario 13.1: 100 agents posting status updates concurrently

```gherkin
Feature: 100+ concurrent agent status updates

  Scenario: 100 agents post status updates within a 1-minute window
    Given 100 active agents each emit a StatusUpdate with Severity "Low"
    When all 100 StatusUpdate messages are enqueued within 60 seconds
    Then IOutboundQueue.CountPendingAsync(Low) reaches the batching threshold
    And OutboundQueueProcessor batches Low-severity messages into summary embeds
    And total REST API calls for status updates are reduced to approximately 10/minute
    And no messages are lost or dead-lettered
    And P95 outbound latency (enqueue to REST 200) remains under 3 seconds
    And discord.outbound.queue_depth gauge reflects the queue depth by severity
```

### Scenario 13.2: High-priority alert during high-volume status update burst

```gherkin
Feature: Priority during high load

  Scenario: Critical alert cuts through a burst of status updates
    Given 80 Low-severity status updates are pending in the outbound queue
    When a Critical alert is enqueued
    Then the Critical alert is dequeued and sent before any remaining Low-severity messages
    And the alert is delivered to the alert channel within seconds
    And Low-severity batching continues after the Critical alert is sent
```

---

## Feature 14: Observability

Validates that OTel metrics, structured logging, and health checks are
emitted correctly.

### Scenario 14.1: Metrics are emitted for key operations

```gherkin
Feature: OpenTelemetry metrics

  Scenario: Key counters and gauges are emitted during normal operation
    Given the bot processes 10 slash commands, 2 duplicate interactions, and 1 authorization rejection
    And the bot sends 20 outbound messages with 1 retry and 0 dead-letters
    Then the following metrics are emitted via OpenTelemetry:
      | Metric                               | Value |
      | discord.interactions.received        | 12    |
      | discord.interactions.processed       | 10    |
      | discord.interactions.duplicated      | 2     |
      | discord.interactions.rejected        | 1     |
      | discord.send.retry_count             | 1     |
      | discord.send.dead_lettered           | 0     |
      | discord.gateway.connected (gauge)    | 1     |
```

### Scenario 14.2: Structured logging includes correlation context

```gherkin
Feature: Structured logging

  Scenario: Log entries include CorrelationId, GuildId, ChannelId, and UserId
    When the operator sends a slash command with CorrelationId "trace-xyz"
    Then all log entries for this request include structured fields:
      | Field         | Value      |
      | CorrelationId | trace-xyz  |
      | GuildId       | G-1        |
      | ChannelId     | C-control  |
      | UserId        | <operator> |
```

---

## Cross-Reference to Acceptance Criteria

| Acceptance Criterion | Covered by Scenarios |
|---|---|
| AC-1: `/agent ask architect design update-service cache strategy` | 1.1 |
| AC-2: Agent posts question with Approve, Reject, Need more info, Delegate buttons | 2.1, 2.2 |
| AC-3: Bot reconnects after Gateway disconnect | 3.2, 3.3, 3.5 |
| AC-4: Duplicate interaction IDs are ignored | 4.1, 4.2, 11.3 |
| AC-5: High-priority alerts before low-priority progress | 5.1, 5.2, 5.4, 5.5, 13.2 |
| AC-6: Unauthorized users cannot issue commands | 6.1, 6.2, 6.3, 6.4, 6.5 |

## Cross-Reference to Story Requirements

| Requirement Area | Covered by Features |
|---|---|
| Protocol (Gateway + REST) | 3, 11 |
| C# library (Discord.Net) | 3.1, 3.2 |
| Interaction model (slash, buttons, menus, threads) | 1, 2, 7, 12 |
| Commands (/agent ask/status/approve/reject/assign/pause/resume) | 1 |
| Channel model (control, alert, workstream) | 7 |
| Agent identity (name, role, task, confidence, blocking) | 10 |
| Reliability (durable inbox, outbox, reconnect) | 3, 4, 9 |
| Performance (100 agents, rate-limit batching) | 5, 13 |
| Rate limits (REST rate-limit headers, batching) | 5 |
| Security (role, guild, channel restriction) | 6 |
| Audit (guild/channel/message/user IDs) | 8 |
