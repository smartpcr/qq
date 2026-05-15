# Architecture: Slack Messenger Support (qq-SLACK-MESSENGER-SUPP)

## 1. Overview

This document defines the component architecture, data model, interfaces, and
end-to-end sequence flows for integrating Slack as a messenger channel into the
agent swarm platform. The swarm contains 100+ autonomous agents performing
software-factory tasks (planning, coding, testing, releasing, incident
response). Human operators interact with agents exclusively through messenger
applications.

**Scope.** This story covers the Slack-specific connector layer:

- Receiving inbound Slack events (Events API, Socket Mode, slash commands,
  app mentions, interactive payloads).
- Dispatching outbound messages via the Slack Web API.
- Mapping Slack interactions to the shared `HumanDecisionEvent` model.
- Threading every agent task into a dedicated Slack conversation thread.
- Idempotency, authorization, audit, and observability for the Slack channel.

**Out of scope.** Platform-agnostic abstractions (`IMessengerConnector`,
`AgentQuestion`, `HumanDecisionEvent`) are proposed in the target project
`AgentSwarm.Messaging.Abstractions` (not yet created in the repo) and are not
re-specified here except by reference. Persistence infrastructure (outbox
engine, DLQ store) is proposed in `AgentSwarm.Messaging.Persistence`. The
orchestrator and agent runtime are upstream consumers.

> **Repo status.** As of this writing the repository contains no `src/`
> directory, no `.csproj` files, and no implementation code. All project and
> namespace references in this document (e.g., `AgentSwarm.Messaging.Slack`,
> `AgentSwarm.Messaging.Core`) describe the **target solution structure** to be
> created during implementation.

**Library choice.** SlackNet is the preferred C# client library (see story
description). Fallback: Slack.NetStandard or direct `HttpClient` calls against
the Slack Web API.

---

## 2. Component Inventory

Each component below maps to a class (or small set of classes) inside the
proposed `AgentSwarm.Messaging.Slack` project (to be created). Components in
the proposed `Abstractions`, `Core`, or `Persistence` projects are referenced
but not owned by this story.

### 2.1 SlackConnector

| Attribute | Value |
|---|---|
| Project | `AgentSwarm.Messaging.Slack` (proposed -- not yet in repo) |
| Implements | `IMessengerConnector` |
| Responsibility | Top-level facade that wires inbound transport, command dispatch, interaction handling, outbound dispatch, thread management, and audit into a single connector lifecycle. Registered in DI as the Slack implementation of `IMessengerConnector`. |

The connector delegates to the components below; it does not contain business
logic itself.

### 2.2 Inbound Transport Layer

Two concrete receivers share a common internal interface so the rest of the
pipeline is transport-agnostic.

#### 2.2.1 ISlackInboundTransport

```csharp
internal interface ISlackInboundTransport
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
```

Each transport converts Slack-native payloads into a normalized
`SlackInboundEnvelope` and enqueues it into the durable inbound queue.

#### 2.2.2 SlackEventsApiReceiver

| Attribute | Value |
|---|---|
| Transport | HTTP POST endpoints (ASP.NET Core) |
| Responsibility | Handles Events API callbacks (including `app_mention` events), the `url_verification` challenge, slash commands, and interactive payloads. Validates request signature via `SlackSignatureValidator`. Immediately returns HTTP 200 to satisfy the 3-second ACK requirement, then enqueues the event envelope for async processing. For slash commands that require a modal (`review`, `escalate`), the receiver uses a **synchronous fast-path**: it validates signature, runs authorization and idempotency checks inline, then calls `views.open` via `SlackDirectApiClient` before returning the HTTP response, because the `trigger_id` expires within 3 seconds (see section 5.3). |

Endpoint routes:

- `POST /api/slack/events` -- Events API subscription (receives `app_mention`, `message`, and other event types)
- `POST /api/slack/commands` -- Slash command payloads
- `POST /api/slack/interactions` -- Interactive component payloads (buttons, modals)

#### 2.2.3 SlackSocketModeReceiver

| Attribute | Value |
|---|---|
| Transport | WebSocket (Slack Socket Mode) |
| Responsibility | Maintains a persistent WebSocket connection to Slack. Receives events without a public HTTP endpoint. Sends envelope ACKs over the socket. Intended for development and environments without a public ingress. |

Reconnection uses exponential backoff with jitter (initial 1 s, max 30 s).

### 2.3 SlackSignatureValidator

| Attribute | Value |
|---|---|
| Responsibility | ASP.NET Core middleware (or action filter) that verifies the `X-Slack-Signature` header against the workspace signing secret. Rejects requests with invalid or missing signatures with HTTP 401. Rejected requests are logged to audit. |

The signing secret is resolved at runtime from the secret provider (see
`SlackWorkspaceConfig.signing_secret_ref` in section 3).

### 2.4 SlackAuthorizationFilter

| Attribute | Value |
|---|---|
| Responsibility | Enforces workspace, channel, and user-group ACLs. Runs after signature validation. Rejects requests from disallowed teams, channels, or users with HTTP 403. Rejected requests are logged to audit with reason. |

Delegates membership resolution to `SlackMembershipResolver`.

### 2.5 SlackMembershipResolver

| Attribute | Value |
|---|---|
| Responsibility | Resolves Slack user group membership by calling `usergroups.users.list` via SlackNet. Caches results with a configurable TTL (default 5 minutes). Used by `SlackAuthorizationFilter` to check whether the requesting user belongs to an allowed user group. |

### 2.6 SlackIdempotencyGuard

| Attribute | Value |
|---|---|
| Responsibility | Prevents duplicate processing of Slack events, commands, and interactions. Checks the inbound idempotency key against `SlackInboundRequestRecord` in the persistence store. If a record exists and processing is complete, the event is silently dropped. If processing is in-progress, the event is deferred. |

Idempotency key derivation is per-surface (see section 3.4).

### 2.7 SlackCommandHandler

| Attribute | Value |
|---|---|
| Responsibility | Dispatches slash commands to typed handler methods. Parses the command text, extracts sub-command and arguments, and routes to the appropriate orchestrator action. |

Supported commands:

| Command | Sub-command | Action |
|---|---|---|
| `/agent` | `ask <prompt>` | Create new agent task |
| `/agent` | `status [task-id]` | Query task / swarm status |
| `/agent` | `approve <question-id>` | Approve a pending question |
| `/agent` | `reject <question-id>` | Reject a pending question |
| `/agent` | `review <task-id>` | Open review modal |
| `/agent` | `escalate <task-id>` | Open escalation modal |

### 2.8 SlackAppMentionHandler

| Attribute | Value |
|---|---|
| Responsibility | Processes `app_mention` events from the Events API. When a user mentions the bot (e.g., `@AgentBot status TASK-42`), this handler parses the mention text, strips the bot user ID prefix, and routes the extracted command to the same dispatch logic used by `SlackCommandHandler`. Replies are posted as threaded messages in the same conversation. |

App mentions provide an alternative to slash commands: users can interact with
the agent bot in any channel where the app is installed by @-mentioning it.
The handler normalizes the mention text into the same sub-command format used
by slash commands (`ask`, `status`, `approve`, `reject`, `review`, `escalate`)
so that downstream processing is unified.

### 2.9 SlackInteractionHandler

| Attribute | Value |
|---|---|
| Responsibility | Processes interactive payloads: Block Kit button clicks and modal view submissions. Maps each interaction to a typed `HumanDecisionEvent` and publishes it to the orchestrator. Updates the originating Slack message to reflect the decision (e.g., disables buttons, appends status). |

Mapping rules:

| Slack interaction | HumanDecisionEvent field mapping |
|---|---|
| Button `action_id` | `ActionValue` = button `value` |
| Modal `view_submission` | `ActionValue` = parsed form values; `Comment` = free-text input |
| `user.id` | `ExternalUserId` |
| `message.ts` or `view.id` | `ExternalMessageId` |
| Server receive time | `ReceivedAt` = `DateTimeOffset.UtcNow` captured at ingest |
| Thread-mapped `correlation_id` | `CorrelationId` |

### 2.10 SlackMessageRenderer

| Attribute | Value |
|---|---|
| Responsibility | Converts `AgentQuestion` and `MessengerMessage` into Slack Block Kit JSON. Produces section blocks for title/body, action blocks for `HumanAction` buttons, and modal views for review/escalate flows. |

Rendering rules:

- `AgentQuestion.Title` -> `header` block.
- `AgentQuestion.Body` -> `section` block with `mrkdwn` text.
- `AgentQuestion.AllowedActions` -> `actions` block with one `button` element
  per `HumanAction`. Button `value` = `HumanAction.Value`.
  `HumanAction.RequiresComment` = true triggers a modal instead of a direct
  button callback.
- `AgentQuestion.Severity` -> emoji prefix and color attachment bar.
- `AgentQuestion.ExpiresAt` -> context block showing deadline.

### 2.11 SlackThreadManager

| Attribute | Value |
|---|---|
| Responsibility | Manages the one-to-one mapping between agent tasks and Slack threads. Creates the root message (thread parent) on first outbound message for a task. Stores the mapping in `SlackThreadMapping`. Retrieves `thread_ts` for subsequent replies. |

Lifecycle:

1. On first message for a `task_id`, posts a root status message to the
   configured channel. Captures `channel_id` + `ts` as `thread_ts`.
2. All subsequent messages for that `task_id` are posted as threaded replies
   using the stored `thread_ts`.
3. On connector restart, existing mappings are loaded from persistence --
   threads are not re-created.
4. If the root message is deleted or the channel is archived, the manager logs
   a warning to audit and attempts to create a new thread in the fallback
   channel (configurable).

### 2.12 SlackOutboundDispatcher

| Attribute | Value |
|---|---|
| Responsibility | Sends messages to Slack via the Web API (`chat.postMessage`, `chat.update`, `views.update`). Reads from the durable outbound queue. Handles Slack rate limits (HTTP 429) with `Retry-After` backoff. Failed messages after max retries are moved to the dead-letter queue. |

> **Note on `views.open`.** Modal-opening calls (`views.open`) do NOT pass
> through the durable outbound queue because they require a short-lived
> `trigger_id` (expires in ~3 seconds). Instead, the modal fast-path uses
> `SlackDirectApiClient` (section 2.15) to call `views.open` synchronously
> during request processing. The dispatcher handles all other outbound calls
> including `views.update` for modal modifications after initial open.

Rate-limit strategy:

- On HTTP 429, pause dispatch for the duration in `Retry-After` header.
- Tier-aware: `chat.postMessage` is Tier 2 (roughly 1 req/s per channel);
  `views.update` is Tier 4. (`views.open` is handled by `SlackDirectApiClient`
  using the same shared rate-limiter state.)
- Burst capacity is managed via a token-bucket rate limiter per API method
  tier.

### 2.13 SlackInboundIngestor

| Attribute | Value |
|---|---|
| Responsibility | Receives normalized `SlackInboundEnvelope` from the transport layer, runs it through `SlackSignatureValidator` (if not already validated by middleware), `SlackAuthorizationFilter`, and `SlackIdempotencyGuard`, then dispatches to `SlackCommandHandler`, `SlackAppMentionHandler`, or `SlackInteractionHandler`. Failed processing is retried per the retry policy; poison messages go to the DLQ. |

### 2.14 SlackAuditLogger

| Attribute | Value |
|---|---|
| Responsibility | Persists every inbound and outbound Slack exchange as a `SlackAuditEntry`. Captures both successful operations and rejected/unauthorized requests. Queryable by `correlation_id`, `task_id`, `agent_id`, `team_id`, `channel_id`, `user_id`, and time range. |

### 2.15 SlackDirectApiClient

| Attribute | Value |
|---|---|
| Responsibility | Thin wrapper around SlackNet for Slack Web API calls that must execute synchronously within the HTTP request lifecycle (i.e., cannot be deferred to the durable outbound queue). Used exclusively for `views.open` in the modal fast-path. Applies the same token-bucket rate limiter as `SlackOutboundDispatcher` (shared rate-limit state) and logs every call to `SlackAuditLogger`. Does NOT use the durable outbound queue or retry engine -- if the call fails, the slash command returns an ephemeral error message to the user. |

---

## 3. Data Model

All entities below will be persisted via the proposed
`AgentSwarm.Messaging.Persistence` project.
Primary keys are shown in **bold**.

### 3.1 SlackWorkspaceConfig

Configuration for a registered Slack workspace (one row per workspace).

| Field | Type | Description |
|---|---|---|
| **team_id** | `string` | Slack workspace ID (e.g., `T0123ABCD`) |
| workspace_name | `string` | Human-readable workspace name |
| bot_token_secret_ref | `string` | Secret-provider URI for the bot OAuth token (e.g., `keyvault://slack-bot-token`) |
| signing_secret_ref | `string` | Secret-provider URI for the signing secret |
| app_level_token_ref | `string?` | Secret-provider URI for Socket Mode app-level token (null if Events API only) |
| default_channel_id | `string` | Channel for new agent task threads |
| fallback_channel_id | `string?` | Channel used when default is unavailable |
| allowed_channel_ids | `string[]` | Channels from which commands are accepted |
| allowed_user_group_ids | `string[]` | Slack user group IDs permitted to issue commands |
| enabled | `bool` | Whether this workspace is active |
| created_at | `DateTimeOffset` | Row creation timestamp |
| updated_at | `DateTimeOffset` | Last modification timestamp |

Secrets are never stored directly. `bot_token_secret_ref` and
`signing_secret_ref` are resolved at runtime by the platform secret provider
(Azure Key Vault, Kubernetes secrets, or DPAPI-protected local store).

### 3.2 SlackThreadMapping

Maps each agent task to its Slack conversation thread.

| Field | Type | Description |
|---|---|---|
| **task_id** | `string` | Agent task identifier |
| team_id | `string` | Slack workspace ID |
| channel_id | `string` | Slack channel ID where the thread lives |
| thread_ts | `string` | Timestamp of the root message (Slack thread parent) |
| correlation_id | `string` | End-to-end trace ID for this task |
| agent_id | `string` | ID of the agent that owns this task |
| created_at | `DateTimeOffset` | When the thread was created |
| last_message_at | `DateTimeOffset` | Timestamp of most recent message in thread |

Unique constraint: `(team_id, channel_id, thread_ts)`.

### 3.3 SlackInboundRequestRecord

Stores processed inbound requests for idempotency enforcement.

| Field | Type | Description |
|---|---|---|
| **idempotency_key** | `string` | Derived key (see section 3.4) |
| source_type | `string` | `event`, `command`, or `interaction` |
| team_id | `string` | Originating workspace |
| channel_id | `string?` | Channel (if applicable) |
| user_id | `string` | Slack user who triggered the request |
| raw_payload_hash | `string` | SHA-256 of the raw payload body |
| processing_status | `string` | `received`, `processing`, `completed`, `failed` |
| first_seen_at | `DateTimeOffset` | When the request was first received |
| completed_at | `DateTimeOffset?` | When processing finished |

### 3.4 Idempotency Key Derivation

Keys are derived per inbound surface to prevent duplicates across all Slack
request types:

| Source type | Key derivation |
|---|---|
| Events API | `event:{event_id}` from the Events API envelope |
| Slash commands | `cmd:{team_id}:{user_id}:{command}:{trigger_id}` -- `trigger_id` is unique per invocation |
| Interactive payloads | `interact:{team_id}:{user_id}:{action_id or view_id}:{trigger_id}` |

The `trigger_id` is assigned by Slack and is unique per user action, making it
the canonical idempotency anchor for commands and interactions. For Events API
retries, Slack sends the `X-Slack-Retry-Num` and `X-Slack-Retry-Reason`
headers; the guard checks `event_id` to detect these retries.

### 3.5 SlackAuditEntry

Immutable audit log for every Slack exchange (inbound and outbound).

| Field | Type | Description |
|---|---|---|
| **id** | `string` (ULID) | Unique entry identifier |
| correlation_id | `string` | End-to-end trace ID |
| agent_id | `string?` | Agent involved (null for inbound human-initiated) |
| task_id | `string?` | Task involved (null if not yet assigned) |
| conversation_id | `string?` | Logical conversation ID (maps to thread) |
| direction | `string` | `inbound` or `outbound` |
| request_type | `string` | `slash_command`, `event`, `app_mention`, `interaction`, `message_send`, `modal_open`, `message_update` |
| team_id | `string` | Slack workspace ID |
| channel_id | `string?` | Slack channel ID |
| thread_ts | `string?` | Slack thread timestamp |
| message_ts | `string?` | Slack message timestamp |
| user_id | `string?` | Slack user ID |
| command_text | `string?` | Raw command or action text |
| response_payload | `string?` | Serialized response sent to Slack (Block Kit JSON) |
| outcome | `string` | `success`, `rejected_auth`, `rejected_signature`, `duplicate`, `error` |
| error_detail | `string?` | Error message if outcome is `error` |
| timestamp | `DateTimeOffset` | When the exchange occurred |

Indexed on: `correlation_id`, `task_id`, `agent_id`, `team_id + channel_id`,
`user_id`, `timestamp`.

### 3.6 Shared Model References

These types are proposed in `AgentSwarm.Messaging.Abstractions` (to be created)
and used throughout the Slack connector:

#### 3.6.1 AgentQuestion

```
AgentQuestion(QuestionId, AgentId, TaskId, Title, Body, Severity,
              AllowedActions, ExpiresAt, CorrelationId)
```

Rendered into Block Kit by `SlackMessageRenderer`. Posted to the task thread by
`SlackOutboundDispatcher`.

#### 3.6.2 HumanAction

```
HumanAction(ActionId, Label, Value, RequiresComment)
```

Each action becomes a Block Kit button. If `RequiresComment` is true, the
button opens a modal with a text input instead of submitting directly.

#### 3.6.3 HumanDecisionEvent

```
HumanDecisionEvent(QuestionId, ActionValue, Comment, Messenger,
                   ExternalUserId, ExternalMessageId, ReceivedAt,
                   CorrelationId)
```

Produced by `SlackInteractionHandler` when a human clicks a button or submits a
modal. `Messenger` is always `"slack"`. Published to the orchestrator.

#### 3.6.4 MessengerMessage

```
MessengerMessage(MessageId, AgentId, TaskId, Content, MessageType,
                 CorrelationId, Timestamp)
```

Generic outbound message from an agent. Rendered into Block Kit by
`SlackMessageRenderer` and posted to the task thread.

---

## 4. Interfaces Between Components

### 4.1 IMessengerConnector (shared abstraction)

```csharp
public interface IMessengerConnector
{
    Task SendMessageAsync(MessengerMessage message, CancellationToken ct);
    Task SendQuestionAsync(AgentQuestion question, CancellationToken ct);
    Task<IReadOnlyList<MessengerEvent>> ReceiveAsync(CancellationToken ct);
}
```

`SlackConnector` implements this interface. `SendMessageAsync` and
`SendQuestionAsync` enqueue work into the durable outbound queue.
`ReceiveAsync` drains processed inbound events from the inbound pipeline.

### 4.2 ISlackInboundTransport (internal)

```csharp
internal interface ISlackInboundTransport
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
```

Implemented by `SlackEventsApiReceiver` and `SlackSocketModeReceiver`.
The active transport is selected by configuration
(`SlackWorkspaceConfig.app_level_token_ref` present = Socket Mode;
absent = Events API).

### 4.3 ISlackThreadManager (internal)

```csharp
internal interface ISlackThreadManager
{
    Task<SlackThreadMapping> GetOrCreateThreadAsync(
        string taskId, string agentId, string correlationId,
        string channelId, CancellationToken ct);

    Task<SlackThreadMapping?> GetThreadAsync(
        string taskId, CancellationToken ct);
}
```

Used by `SlackOutboundDispatcher` to resolve `thread_ts` before posting, and by
`SlackInteractionHandler` to correlate button clicks back to a task.

### 4.4 ISlackIdempotencyGuard (internal)

```csharp
internal interface ISlackIdempotencyGuard
{
    Task<bool> TryAcquireAsync(
        string idempotencyKey, string sourceType,
        CancellationToken ct);

    Task MarkCompletedAsync(
        string idempotencyKey, CancellationToken ct);

    Task MarkFailedAsync(
        string idempotencyKey, CancellationToken ct);
}
```

`TryAcquireAsync` returns `true` if the key is new (processing can proceed) or
`false` if it is a duplicate. Atomicity is enforced at the persistence layer
(database upsert with status check).

### 4.5 ISlackMessageRenderer (internal)

```csharp
internal interface ISlackMessageRenderer
{
    SlackBlockKitPayload RenderQuestion(AgentQuestion question);
    SlackBlockKitPayload RenderMessage(MessengerMessage message);
    SlackModalPayload RenderReviewModal(string taskId, string correlationId);
    SlackModalPayload RenderEscalateModal(string taskId, string correlationId);
}
```

Returns strongly-typed Block Kit payloads that are serialized to JSON by the
outbound dispatcher.

### 4.6 ISlackAuditLogger (internal)

```csharp
internal interface ISlackAuditLogger
{
    Task LogAsync(SlackAuditEntry entry, CancellationToken ct);

    Task<IReadOnlyList<SlackAuditEntry>> QueryAsync(
        SlackAuditQuery query, CancellationToken ct);
}
```

`SlackAuditQuery` supports filtering by `correlation_id`, `task_id`,
`agent_id`, `team_id`, `channel_id`, `user_id`, `direction`, `outcome`, and
time range.

### 4.7 Component Dependency Graph

```
SlackConnector
  |-- ISlackInboundTransport (SlackEventsApiReceiver | SlackSocketModeReceiver)
  |-- SlackInboundIngestor
  |     |-- SlackSignatureValidator
  |     |-- SlackAuthorizationFilter
  |     |     \-- SlackMembershipResolver
  |     |-- ISlackIdempotencyGuard
  |     |-- SlackCommandHandler
  |     |     \-- SlackDirectApiClient (modal fast-path: views.open)
  |     |-- SlackAppMentionHandler
  |     \-- SlackInteractionHandler
  |-- SlackOutboundDispatcher
  |     |-- ISlackThreadManager
  |     |-- ISlackMessageRenderer
  |     \-- Rate limiter (token-bucket per API tier, shared with SlackDirectApiClient)
  |-- SlackDirectApiClient
  |     \-- ISlackAuditLogger
  \-- ISlackAuditLogger
```

External dependencies (proposed projects -- none exist in the repo yet):

- `AgentSwarm.Messaging.Abstractions` -- shared types and `IMessengerConnector`
- `AgentSwarm.Messaging.Core` -- retry engine, outbox/inbox queues, DLQ
- `AgentSwarm.Messaging.Persistence` -- EF Core DbContext, migrations
- `SlackNet` (NuGet package) -- Slack API client

---

## 5. End-to-End Sequence Flows

### 5.1 Slash Command: `/agent ask <prompt>`

```
Human                  Slack Platform         SlackEventsApiReceiver    SlackInboundIngestor    SlackCommandHandler     Orchestrator            SlackOutboundDispatcher   Slack Platform
  |                         |                         |                         |                         |                         |                         |                         |
  |-- /agent ask "plan" --->|                         |                         |                         |                         |                         |                         |
  |                         |-- POST /api/slack/commands ->|                         |                         |                         |                         |                         |
  |                         |                         |-- validate signature --->|                         |                         |                         |                         |
  |                         |                         |<-- HTTP 200 (ack) ------>|                         |                         |                         |                         |
  |                         |                         |   (within 3 seconds)     |                         |                         |                         |                         |
  |                         |                         |                         |-- check authz ---------->|                         |                         |                         |
  |                         |                         |                         |-- check idempotency ---->|                         |                         |                         |
  |                         |                         |                         |-- dispatch command ----->|                         |                         |                         |
  |                         |                         |                         |                         |-- parse "ask" + prompt  |                         |                         |
  |                         |                         |                         |                         |-- create task --------->|                         |                         |
  |                         |                         |                         |                         |                         |-- emit task created --->|                         |
  |                         |                         |                         |                         |                         |                         |-- create thread root -->|
  |                         |                         |                         |                         |                         |                         |   (chat.postMessage)    |
  |                         |                         |                         |                         |                         |                         |<-- ts (thread_ts) ------|
  |                         |                         |                         |                         |                         |                         |-- store thread mapping  |
  |<-- thread root msg -----|<------------------------|-------------------------|-------------------------|-------------------------|-------------------------|-------------------------|
  |                         |                         |                         |                         |                         |                         |                         |
  |                         |                         |                         |                         |                         |-- status update ------->|                         |
  |                         |                         |                         |                         |                         |                         |-- reply in thread ----->|
  |<-- status update -------|<------------------------|-------------------------|-------------------------|-------------------------|-------------------------|-------------------------|
```

Steps:

1. Human types `/agent ask generate implementation plan for persistence
   failover` in a Slack channel.
2. Slack sends POST to `/api/slack/commands`.
3. `SlackEventsApiReceiver` validates the request signature and returns HTTP
   200 within 3 seconds (Slack requirement). The payload is enqueued for async
   processing.
4. `SlackInboundIngestor` runs authorization (team, channel, user group) and
   idempotency checks.
5. `SlackCommandHandler` parses sub-command `ask` with prompt text. Calls the
   orchestrator to create a new agent task.
6. The orchestrator emits a task-created event. `SlackOutboundDispatcher`
   picks it up, calls `SlackThreadManager.GetOrCreateThreadAsync` to post the
   root message via `chat.postMessage`, and stores the `thread_ts` in
   `SlackThreadMapping`.
7. Subsequent agent progress updates are posted as threaded replies.
8. All exchanges are recorded by `SlackAuditLogger`.

### 5.2 Agent Question with Button Response

```
Orchestrator          SlackOutboundDispatcher   SlackThreadManager   SlackMessageRenderer   Slack Platform          Human
  |                         |                         |                         |                         |                         |
  |-- SendQuestionAsync --->|                         |                         |                         |                         |
  |                         |-- GetThreadAsync ------>|                         |                         |                         |
  |                         |<-- thread_ts -----------|                         |                         |                         |
  |                         |-- RenderQuestion ------>|                         |                         |                         |
  |                         |                         |                         |-- Block Kit JSON ------>|                         |
  |                         |-- chat.postMessage ---->|                         |                         |                         |
  |                         |   (thread_ts, blocks)   |                         |                         |-- question + buttons -->|
  |                         |                         |                         |                         |                         |
  |                         |                         |                         |                         |<-- clicks "Approve" ----|
  |                         |                         |                         |                         |                         |
```

```
Slack Platform         SlackEventsApiReceiver    SlackInboundIngestor     SlackInteractionHandler   Orchestrator
  |                         |                         |                         |                         |
  |-- POST /api/slack/interactions -->|                         |                         |                         |
  |                         |-- HTTP 200 (ack) ------>|                         |                         |
  |                         |-- enqueue ------------->|                         |                         |
  |                         |                         |-- idempotency check --->|                         |
  |                         |                         |-- dispatch interaction >|                         |
  |                         |                         |                         |-- build HumanDecisionEvent
  |                         |                         |                         |   QuestionId = from action block_id
  |                         |                         |                         |   ActionValue = "approve"
  |                         |                         |                         |   Messenger = "slack"
  |                         |                         |                         |   ExternalUserId = user.id
  |                         |                         |                         |   CorrelationId = from thread mapping
  |                         |                         |                         |-- publish event ------->|
  |                         |                         |                         |-- update message ------>| (disable buttons, show decision)
```

Steps:

1. Agent produces an `AgentQuestion` with `AllowedActions` = [Approve, Reject,
   Need more info].
2. `SlackOutboundDispatcher` resolves the thread, renders Block Kit via
   `SlackMessageRenderer`, and posts to the thread.
3. Human clicks the "Approve" button in Slack.
4. Slack sends an interactive payload to `/api/slack/interactions`.
5. Receiver ACKs immediately (HTTP 200).
6. `SlackInteractionHandler` maps the button click to a `HumanDecisionEvent`:
   - `QuestionId` is extracted from the button's `block_id` (encoded during
     rendering).
   - `ActionValue` = the button's `value` field.
   - `CorrelationId` is resolved from `SlackThreadMapping` via the message's
     `thread_ts`.
7. The `HumanDecisionEvent` is published to the orchestrator.
8. The original message is updated (`chat.update`) to disable the buttons and
   show the decision.

### 5.3 Modal Flow: `/agent review <task-id>`

> **Trigger-ID constraint.** Slack's `trigger_id` (included in every slash
> command and interactive payload) expires approximately 3 seconds after
> issuance. Because `views.open` requires a valid `trigger_id`, modal-opening
> commands (`review`, `escalate`) follow a **synchronous fast-path** that
> performs signature validation, authorization, and idempotency checks inline
> (not deferred to async processing), then calls `views.open` via
> `SlackDirectApiClient` before returning the HTTP response.

Steps:

1. Human types `/agent review TASK-42`.
2. `SlackEventsApiReceiver` validates the request signature via
   `SlackSignatureValidator`. If invalid, HTTP 401 is returned and an audit
   entry with `outcome = rejected_signature` is logged. Processing stops.
3. The receiver runs `SlackAuthorizationFilter` synchronously: workspace
   (`team_id`), channel (`channel_id`), and user-group membership are checked
   against `SlackWorkspaceConfig`. If any layer fails, an ephemeral error
   message is returned to the user and an audit entry with `outcome =
   rejected_auth` is logged. Processing stops.
4. The receiver runs `SlackIdempotencyGuard.TryAcquireAsync` with key
   `cmd:{team_id}:{user_id}:{command}:{trigger_id}`. If this is a duplicate,
   the request is silently ACKed and an audit entry with `outcome = duplicate`
   is logged. Processing stops.
5. The receiver detects that sub-command `review` requires a modal. It
   synchronously calls `SlackCommandHandler`, which invokes `views.open` via
   `SlackDirectApiClient` (not the durable outbound queue) with the
   `trigger_id` from the slash-command payload, passing a modal rendered by
   `SlackMessageRenderer.RenderReviewModal`. The `SlackDirectApiClient` logs
   an audit entry with `request_type = modal_open`. The modal contains:
   - Read-only section showing task summary (fetched from orchestrator cache).
   - Multi-line text input for review comments.
   - Select menu for verdict (approve / request-changes / reject).
   - Submit and cancel buttons.
6. The receiver returns HTTP 200 to Slack (ACK).
7. Human fills in the modal and clicks Submit.
8. Slack sends a `view_submission` payload to `/api/slack/interactions`.
9. `SlackInteractionHandler` extracts form values, builds a
   `HumanDecisionEvent` with `ActionValue` = selected verdict and `Comment` =
   review text.
10. Event is published; the originating thread receives a confirmation reply.

The same fast-path applies to `/agent escalate`, which also opens a modal.
If `views.open` fails (e.g., rate limit or network error), the command handler
returns an ephemeral error to the user; the call is not retried via the
outbound queue because the `trigger_id` is already expired.

### 5.4 Duplicate Event Handling (Idempotency)

Steps:

1. Slack retries an Events API callback (e.g., due to network timeout).
   The retry carries the same `event_id` and `X-Slack-Retry-Num` header.
2. `SlackEventsApiReceiver` ACKs immediately (HTTP 200) to stop further
   retries.
3. `SlackInboundIngestor` calls `SlackIdempotencyGuard.TryAcquireAsync` with
   key `event:{event_id}`.
4. The guard finds an existing `SlackInboundRequestRecord` with
   `processing_status = completed`.
5. The event is silently dropped. No duplicate task or decision is created.
6. The drop is recorded in audit with `outcome = duplicate`.

For slash commands, the `trigger_id` ensures that even if a user double-clicks,
only the first invocation is processed. For interactions, the combination of
`trigger_id` + `action_id` prevents duplicate decision events.

### 5.5 Unauthorized Channel Rejection

Steps:

1. A user in an unauthorized channel types `/agent ask do something`.
2. Slack sends the command payload.
3. `SlackEventsApiReceiver` validates the signature (passes) and ACKs.
4. `SlackInboundIngestor` runs `SlackAuthorizationFilter`.
5. The filter checks `channel_id` against
   `SlackWorkspaceConfig.allowed_channel_ids` -- not found.
6. The request is rejected. An ephemeral message is sent to the user:
   "This channel is not authorized for agent commands."
7. Audit entry is created with `outcome = rejected_auth` including `team_id`,
   `channel_id`, and `user_id`.

### 5.6 Events API URL Verification

Steps:

1. During Slack app setup, Slack sends a POST to `/api/slack/events` with
   `{ "type": "url_verification", "challenge": "<token>" }`.
2. `SlackEventsApiReceiver` detects the `url_verification` type.
3. Responds with `{ "challenge": "<token>" }` and HTTP 200.
4. No further processing occurs. This is a one-time handshake.

### 5.7 App Mention: `@AgentBot ask <prompt>`

Steps:

1. A user in an authorized channel posts: `@AgentBot ask design persistence
   layer`.
2. Slack delivers an `app_mention` event to `POST /api/slack/events`.
3. `SlackEventsApiReceiver` validates the signature and ACKs with HTTP 200.
   The event envelope is enqueued for async processing.
4. `SlackInboundIngestor` runs authorization and idempotency checks.
5. `SlackAppMentionHandler` strips the bot user-ID prefix from the mention
   text, yielding `ask design persistence layer`. It parses this as
   sub-command `ask` with prompt text and delegates to the same orchestrator
   path used by slash commands.
6. The orchestrator creates a task. `SlackOutboundDispatcher` posts a threaded
   reply in the same channel/thread where the mention occurred.
7. All exchanges are recorded by `SlackAuditLogger` with `request_type =
   app_mention`.

---

### 6.1 Durable Message Pipeline

The Slack connector uses the shared pipeline infrastructure proposed in
`AgentSwarm.Messaging.Core` (to be created):

| Component | Role |
|---|---|
| Durable inbound queue | Buffers validated inbound envelopes for async processing. Survives connector restart. |
| Durable outbound queue (outbox) | Buffers outbound Slack API calls. Guarantees at-least-once delivery. |
| Retry engine | Retries transient failures (HTTP 5xx, network errors) with exponential backoff. Max retries configurable (default 5). |
| Dead-letter queue (DLQ) | Captures messages that exceed max retries. Operators can inspect and replay. |
| Rate limiter | Token-bucket per Slack API method tier. Prevents 429 responses. |

### 6.2 Performance Targets

| Metric | Target | Mechanism |
|---|---|---|
| Interactive ACK latency | < 3 seconds | Immediate HTTP 200 before processing |
| P95 outbound latency | < 3 seconds | Outbox drain + Slack API call |
| Concurrent threads | 1000+ | Thread mapping is a lightweight DB lookup |
| Message loss | 0 | Durable queues + at-least-once delivery |
| Connector recovery | < 30 seconds | Worker restart loads thread mappings from DB; Socket Mode reconnects with backoff |

### 6.3 Observability

All components emit:

- **OpenTelemetry traces** with `ActivitySource` named
  `AgentSwarm.Messaging.Slack`. Spans cover: inbound receive, signature
  validation, authorization, idempotency check, command dispatch, outbound
  send.
- **Structured logs** via `ILogger<T>` with `correlation_id`, `task_id`,
  `agent_id`, `team_id`, `channel_id` in log scope.
- **Metrics** via `System.Diagnostics.Metrics`:
  `slack.inbound.count`, `slack.outbound.count`, `slack.outbound.latency_ms`,
  `slack.idempotency.duplicate_count`, `slack.auth.rejected_count`,
  `slack.ratelimit.backoff_count`.
- **Health checks** registered with ASP.NET Core health-check middleware:
  Slack API connectivity, outbound queue depth, DLQ depth.

---

## 7. Security

### 7.1 Request Authentication

Every inbound HTTP request is verified using the Slack signing secret (HMAC
SHA-256 of `v0:{timestamp}:{body}`). Requests older than 5 minutes (clock
skew tolerance) are rejected.

### 7.2 Authorization Model

Three-layer ACL evaluated in order:

1. **Workspace** -- `team_id` must match a registered `SlackWorkspaceConfig`
   with `enabled = true`.
2. **Channel** -- `channel_id` must be in `allowed_channel_ids`.
3. **User group** -- user must belong to at least one group in
   `allowed_user_group_ids` (resolved via `SlackMembershipResolver`).

All three layers must pass. Rejection at any layer produces an audit entry.

### 7.3 Secret Management

Bot tokens and signing secrets are stored in a secret provider (Azure Key
Vault, Kubernetes secrets, or DPAPI-protected local store). The
`SlackWorkspaceConfig` stores only a reference URI. Secrets are loaded into
memory at connector startup and refreshed on a configurable interval (default
1 hour). Secrets are never logged or included in audit entries.
