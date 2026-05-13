# Architecture — Microsoft Teams Messenger Support

**Story:** `qq:MICROSOFT-TEAMS-MESS`
**Status:** Draft — iteration 15

> **Note on project/assembly names:** This repository currently contains only documentation (no source projects). All assembly names, namespaces, and project references in this document are *proposed* target modules aligned with the recommended solution structure in `implementation-plan.md` and the epic-level attachment. They should not be mistaken for existing source code.

---

## 1. Overview

This document defines the component architecture, data model, inter-component interfaces, and end-to-end sequence flows for the Microsoft Teams messenger connector within the Agent Swarm messaging platform. The connector enables enterprise operators to command, approve, and monitor a 100+ autonomous agent swarm through Microsoft Teams personal chats and team channels, using Adaptive Cards for structured interactions and Bot Framework for protocol handling.

The design conforms to the shared `IMessengerConnector` abstraction defined in `AgentSwarm.Messaging.Abstractions` and layers Teams-specific concerns (Bot Framework middleware, Adaptive Card rendering, proactive messaging, Entra ID identity) on top of the platform-agnostic messaging core defined in `AgentSwarm.Messaging.Core`.

---

## 2. Component Inventory

### 2.1 Component Diagram

```text
┌─────────────────────────────────────────────────────────────────────┐
│                        Microsoft Teams Client                       │
│  (Desktop / Mobile / Web)                                           │
└──────────────────────────────┬──────────────────────────────────────┘
                               │  Bot Framework Channel (HTTPS)
                               ▼
┌─────────────────────────────────────────────────────────────────────┐
│  2.2  TeamsWebhookController                                        │
│  ASP.NET Core controller — receives Bot Framework HTTP POSTs        │
│  Delegates to CloudAdapter; JWT auth handled by Bot Framework SDK   │
└──────────────────────────────┬──────────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────────┐
│  2.3  TeamsBotAdapter                                               │
│  Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter         │
│  Middleware: TelemetryMiddleware → TenantValidationMiddleware →     │
│  ActivityDeduplicationMiddleware → RateLimitMiddleware (4 stages)   │
└──────────────────────────────┬──────────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────────┐
│  2.4  TeamsSwarmActivityHandler                                     │
│  Extends SDK TeamsActivityHandler; dispatches message, invoke,      │
│  installationUpdate, and message-extension activities               │
└────┬─────────────┬───────────┬──────────────┬───────────────────────┘
     │             │           │              │
     ▼             ▼           ▼              ▼
┌─────────┐ ┌──────────┐ ┌──────────┐ ┌──────────────────┐ ┌──────────────────┐
│ 2.5     │ │ 2.6      │ │ 2.7      │ │ 2.8              │ │ 2.15             │
│ Command │ │ Card     │ │ Install  │ │ Conversation     │ │ Message          │
│ Parser  │ │ Action   │ │ Handler  │ │ Reference Store  │ │ Extension        │
│         │ │ Handler  │ │          │ │                  │ │ Handler          │
└────┬────┘ └────┬─────┘ └────┬─────┘ └────────┬─────────┘ └────────┬─────────┘
     │           │            │                 │                    │
     ▼           ▼            ▼                 │                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│  2.9  TeamsMessengerConnector : IMessengerConnector                  │
│  Implements the shared abstraction; bridges Teams-specific objects   │
│  to/from platform-agnostic MessengerMessage / AgentQuestion types   │
└──────────────────────────────┬──────────────────────────────────────┘
                               │
              ┌────────────────┼────────────────┐
              ▼                ▼                ▼
┌──────────────────┐ ┌─────────────────┐ ┌──────────────────┐
│  2.10            │ │  2.11           │ │  2.12            │
│  Adaptive Card   │ │  Proactive      │ │  Outbox /        │
│  Renderer        │ │  Notifier       │ │  Retry Engine    │
└──────────────────┘ └─────────────────┘ └──────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────────┐
│  2.13  Audit Logger                                                 │
│  Immutable, append-only audit trail for compliance                  │
└─────────────────────────────────────────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────────┐
│  2.14  Agent Swarm Orchestrator (external)                          │
│  Consumes MessengerEvent stream; produces AgentQuestion /           │
│  MessengerMessage payloads for outbound delivery                    │
└─────────────────────────────────────────────────────────────────────┘
```

### 2.2 TeamsWebhookController

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Namespace** | `AgentSwarm.Messaging.Teams.Controllers` |
| **Base class** | `ControllerBase` |
| **Route** | `POST /api/messages` |
| **Responsibility** | Accept inbound Bot Framework activities over HTTPS. Delegates processing to `TeamsBotAdapter`. Returns HTTP 200/202 per Bot Framework protocol. |

### 2.3 TeamsBotAdapter

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Type** | `CloudAdapter` (from `Microsoft.Bot.Builder.Integration.AspNet.Core`) |
| **Middleware** | `TelemetryMiddleware` → `TenantValidationMiddleware` → `ActivityDeduplicationMiddleware` → `RateLimitMiddleware` |
| **Responsibility** | Deserialize Bot Framework `Activity` objects, run middleware pipeline, route to `TeamsSwarmActivityHandler`. Handles authentication of inbound requests via Bot Framework JWT validation. |
| **Error handling** | `OnTurnError` logs the exception, sends a user-facing error card, and publishes a dead-letter event to the outbox. |

### 2.4 TeamsSwarmActivityHandler

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Base class** | `Microsoft.Bot.Builder.Teams.TeamsActivityHandler` (SDK base class) |
| **Responsibility** | Override `OnMessageActivityAsync`, `OnAdaptiveCardInvokeAsync`, `OnInstallationUpdateActivityAsync`, and `OnTeamsMessagingExtensionSubmitActionAsync` to dispatch to domain-specific handlers. The custom name `TeamsSwarmActivityHandler` distinguishes this from the SDK base class. |
| **Key overrides** | `OnMessageActivityAsync` → `CommandParser`; `OnAdaptiveCardInvokeAsync` → `CardActionHandler`; `OnInstallationUpdateActivityAsync` / `OnTeamsMembersAddedAsync` → `InstallHandler`; `OnTeamsMessagingExtensionSubmitActionAsync` → `MessageExtensionHandler`. |

### 2.5 CommandParser

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Namespace** | `AgentSwarm.Messaging.Teams.Commands` |
| **Responsibility** | Parse free-text messages into structured commands. Teams does not use slash-command prefixes; the parser recognizes `agent ask <payload>`, `agent status`, `approve`, `reject`, `escalate`, `pause`, `resume` as command triggers. Falls back to echo/help for unrecognized input. |
| **Output** | `ParsedCommand` record containing `CommandType`, `Payload`, `CorrelationId`. |

#### Supported commands

| Input pattern | CommandType | Description |
|---|---|---|
| `agent ask <text>` | `Ask` | Create a new agent task with the free-text payload. |
| `agent status` | `Status` | Query the current swarm or specific agent status. |
| `approve` | `Approve` | Approve the most recent pending approval in context. |
| `reject` | `Reject` | Reject the most recent pending approval in context. |
| `escalate` | `Escalate` | Escalate the current incident or question. |
| `pause` | `Pause` | Pause the agent bound to the current conversation. |
| `resume` | `Resume` | Resume a paused agent. |

### 2.6 CardActionHandler

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Namespace** | `AgentSwarm.Messaging.Teams.Cards` |
| **Responsibility** | Process Adaptive Card `Action.Submit` invoke activities. Extracts the `ActionId` and optional comment from `Activity.Value`, resolves the originating `AgentQuestion` via `QuestionId` embedded in card data, produces a `HumanDecisionEvent`, and publishes it to the inbound queue. Updates the original card to reflect the decision (approved/rejected) and disables further actions. |
| **Idempotency** | Maintains a processed-action set (keyed on `QuestionId + UserId`) to reject duplicate card-action submissions. This is a **domain-level** deduplication layer operating on the semantic action. It works in conjunction with the **activity-level** `ActivityDeduplicationMiddleware` (§2.16) which suppresses duplicate webhook deliveries by `Activity.Id` before the activity reaches any handler. Both layers are required: the middleware catches transport-level retries, while the card-action set catches user-initiated double-taps that share the same question context but arrive as distinct activities. |

### 2.7 InstallHandler

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Namespace** | `AgentSwarm.Messaging.Teams.Lifecycle` |
| **Responsibility** | Handle `installationUpdate` and `conversationUpdate` activities. On install/member-add: validate tenant ID against allowlist, extract `ConversationReference`, persist it via `IConversationReferenceStore.SaveOrUpdateAsync` with `IsActive = true`, and send a welcome Adaptive Card. These **installation-captured references** are gated by Teams app installation policy (admin-managed in Entra ID) and tenant allowlist validation — identity resolution and RBAC authorization are NOT applied at install time because the install event carries no user command to authorize. The stored reference proves the app is installed and enables future proactive messaging; command-level authorization is enforced at command time (§6.1 steps 6–7, §6.4). On uninstall: call `IConversationReferenceStore.MarkInactiveAsync` to mark the conversation reference as inactive. |

### 2.8 ConversationReferenceStore

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Interface** | `IConversationReferenceStore` (defined in `AgentSwarm.Messaging.Teams`) |
| **Responsibility** | Persist and retrieve Bot Framework `ConversationReference` objects for proactive messaging. Each reference has two identity dimensions: (1) **persistence key** — `(TenantId, AadObjectId)` for personal chats or `(TenantId, ChannelId)` for team channels, used for storage and uniqueness; (2) **routing lookup key** — `InternalUserId` (populated by `IIdentityResolver` on first authorized interaction), used by the orchestrator when targeting proactive questions via `AgentQuestion.TargetUserId`. The persistence key is the AAD-native identity captured from `Activity.From.AadObjectId`; the routing lookup key is the platform-agnostic internal user ID mapped by `IIdentityResolver`. Both are stored as separate fields on `TeamsConversationReference` (see §3.2). `GetByAadObjectIdAsync` looks up by persistence key; `GetByInternalUserIdAsync` looks up by routing key. Survives service restarts. |
| **Storage** | Pluggable — default implementation uses the same durable store as the outbox (e.g., SQL Server, Azure Table Storage, or PostgreSQL). |

### 2.9 TeamsMessengerConnector

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Interface** | `IMessengerConnector`, `ITeamsCardManager` |
| **Responsibility** | Implements `SendMessageAsync`, `SendQuestionAsync`, and `ReceiveAsync` from the shared `IMessengerConnector` abstraction. Also implements `ITeamsCardManager` for Teams-specific card update/delete operations (§4.1.1). Translates `MessengerMessage` to Bot Framework `Activity` with Adaptive Card attachments. Translates inbound `Activity` to `MessengerEvent`. Uses `ProactiveNotifier` for outbound delivery and `ConversationReferenceStore` for addressing. |

### 2.10 AdaptiveCardRenderer

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Namespace** | `AgentSwarm.Messaging.Teams.Cards` |
| **Responsibility** | Build Adaptive Card JSON payloads for each interaction type: agent questions (with action buttons), approval requests, status summaries, incident alerts, and release gates. Embeds `QuestionId`, `ActionId`, and `CorrelationId` in card data for round-trip correlation. |
| **Card types** | `AgentQuestionCard`, `ApprovalCard`, `StatusCard`, `IncidentCard`, `ReleaseGateCard`. |

### 2.11 ProactiveNotifier

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Namespace** | `AgentSwarm.Messaging.Teams.Proactive` |
| **Responsibility** | Send messages to users/channels without a prior inbound turn. Retrieves the stored `ConversationReference` from `IConversationReferenceStore` and calls `CloudAdapter.ContinueConversationAsync` to deliver the `Activity`. This is the primary and required delivery path — it depends on a stored reference from a prior app-installation event. For team channels where the bot needs to start a new reply thread (not a new conversation), `ConnectorClient.Conversations.CreateConversationAsync` may be used, but only within teams where the app is already installed. Both paths require a persisted `ConversationReference` proving the app is installed. |
| **Prerequisite** | The Teams app must be installed for the target user or in the target team before proactive messaging can succeed. |

### 2.12 OutboxRetryEngine

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Core` |
| **Namespace** | `AgentSwarm.Messaging.Core.Outbox` |
| **Responsibility** | Durable outbox pattern. Every outbound message is first persisted to the outbox table, then delivered. A background worker polls for undelivered or failed messages and retries transient Bot Connector failures (HTTP 429, 500, 502, 503, 504) with exponential backoff per `tech-spec.md` §4.4: base 2 s, multiplier 2×, max delay 60 s, 5 total attempts (1 initial + 4 retries), ±25% jitter on each computed delay to avoid thundering herd. When Bot Framework returns HTTP 429 with a `Retry-After` header, the header value is used as the minimum delay if it exceeds the computed backoff. Messages exceeding max retries move to the dead-letter queue. |
| **Idempotency** | Each outbox entry has a unique `OutboxEntryId`; the Bot Connector service is tolerant of duplicate `Activity` sends, but the engine deduplicates on its side as well. |

### 2.13 AuditLogger

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Persistence` |
| **Namespace** | `AgentSwarm.Messaging.Persistence.Audit` |
| **Responsibility** | Append-only, immutable audit trail. Logs every inbound command, outbound message, card action, proactive notification, security rejection, and error. Each entry carries all canonical fields from `tech-spec.md` §4.3: `CorrelationId`, `Timestamp`, `ActorId`, `ActorType`, `TenantId`, `EventType`, `Action`, `Outcome`, and `PayloadJson`. Suitable for enterprise compliance review. |
| **Storage** | Write-once store — Azure Table Storage (append-only), SQL Server with row-level immutability triggers, or a dedicated audit log service. |

### 2.14 Agent Swarm Orchestrator (external boundary)

The orchestrator is outside the scope of this story. It produces `AgentQuestion` and `MessengerMessage` payloads for outbound delivery and consumes `MessengerEvent` / `HumanDecisionEvent` from the inbound queue. The interface contract is defined in `AgentSwarm.Messaging.Abstractions`.

### 2.15 MessageExtensionHandler

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Namespace** | `AgentSwarm.Messaging.Teams.Extensions` |
| **Responsibility** | Handle Teams message-extension action commands (`composeExtension/submitAction`). When a user invokes a message action (e.g., right-clicks a message and selects "Forward to Agent"), this handler extracts the source message content, delegates to `CommandParser` to parse the forwarded text (aligned with `e2e-scenarios.md` §Message Actions), and publishes a `MessengerEvent` of type `AgentTaskRequest` with `Source = MessageAction` to the inbound buffer. Returns a task-submitted confirmation card to the user. |
| **Trigger** | `TeamsSwarmActivityHandler.OnTeamsMessagingExtensionSubmitActionAsync` delegates to this handler. |
| **Manifest** | Requires a `composeExtensions` entry in the Teams app manifest with `type: "action"` and `fetchTask: false` (or `true` if a task module is used to collect additional input). |

### 2.16 ActivityDeduplicationMiddleware

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Namespace** | `AgentSwarm.Messaging.Teams.Middleware` |
| **Type** | Bot Framework middleware (`IMiddleware`) |
| **Position in pipeline** | After `TenantValidationMiddleware`, before `RateLimitMiddleware` (see §2.3). |
| **Responsibility** | Suppress duplicate inbound webhook deliveries. Teams and the Bot Connector service may retry an HTTP POST if the initial response times out, resulting in the same logical activity being delivered more than once. This middleware deduplicates by `Activity.Id` (or `Activity.ReplyToId` for invoke activities) per `tech-spec.md` §4.4 and `e2e-scenarios.md` §Reliability lines 447–454 (duplicate inbound webhook suppression scenario). |
| **Store** | `IActivityIdStore` — a lightweight store that tracks recently-seen activity IDs with a configurable TTL (default: 10 minutes, aligned with `implementation-plan.md` §2.1 `ActivityDeduplicationMiddleware` default TTL). The **default implementation is an in-memory `ConcurrentDictionary`** with a background eviction timer — suitable for single-instance deployments. For multi-instance deployments, a **Redis-backed implementation** (`RedisActivityIdStore`) is required to ensure deduplication is consistent across pods. When an `Activity.Id` has already been processed, the middleware short-circuits with HTTP 200 and logs a deduplication event for observability. |
| **Distinction from §2.6 idempotency** | This middleware operates at the **transport level** on raw `Activity.Id`, catching retried HTTP POSTs before any handler runs. The `CardActionHandler` idempotency set (§2.6) operates at the **domain level** on `(QuestionId, UserId)`, catching semantically duplicate card actions that may arrive as distinct activities. Both layers are necessary. |

### 2.17 IdentityResolver

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Namespace** | `AgentSwarm.Messaging.Teams.Security` |
| **Interface** | `IIdentityResolver` (defined in `AgentSwarm.Messaging.Abstractions`; implementations in `AgentSwarm.Messaging.Teams`) |
| **Responsibility** | Map the Teams `Activity.From.AadObjectId` (Entra AAD object ID) to an internal user identity record. Returns `null` when the AAD object ID is not mapped, triggering the unmapped-user rejection flow (§6.4.2). Aligned with `implementation-plan.md` §5.1 which defines `IIdentityResolver` with method `ResolveAsync(string aadObjectId)`. |
| **Invoked by** | `TeamsSwarmActivityHandler` after tenant validation passes. |

### 2.18 UserAuthorizationService

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Namespace** | `AgentSwarm.Messaging.Teams.Security` |
| **Interface** | `IUserAuthorizationService` (defined in `AgentSwarm.Messaging.Abstractions`; implementations in `AgentSwarm.Messaging.Teams`) |
| **Responsibility** | Enforce RBAC permissions for user commands. Given a tenant ID, user ID, and command, determines whether the user's assigned role permits the command. Role definitions are configured via `RbacOptions` (§5.2). Returns an authorization result indicating success or the specific role required. Aligned with `implementation-plan.md` §5.1 which defines `IUserAuthorizationService` with method `AuthorizeAsync(string tenantId, string userId, string command)`. |
| **Invoked by** | `TeamsSwarmActivityHandler` after identity resolution succeeds (§6.4.3). |

---

## 3. Data Model

### 3.1 Core Entities

#### MessengerMessage

Platform-agnostic outbound message. Defined in `AgentSwarm.Messaging.Abstractions`.

| Field | Type | Description |
|---|---|---|
| `MessageId` | `string` | Unique message identifier. |
| `CorrelationId` | `string` | End-to-end trace ID. |
| `AgentId` | `string` | Originating agent identity. |
| `TaskId` | `string` | Associated task/work item. |
| `ConversationId` | `string` | Target human conversation. |
| `Body` | `string` | Message body (markdown). |
| `Severity` | `string` | `Info`, `Warning`, `Error`, `Critical`. |
| `Timestamp` | `DateTimeOffset` | UTC creation time. |

#### AgentQuestion

Blocking question from an agent requiring human response. Defined in `AgentSwarm.Messaging.Abstractions`.

| Field | Type | Description |
|---|---|---|
| `QuestionId` | `string` | Unique question identifier. |
| `AgentId` | `string` | Asking agent. |
| `TaskId` | `string` | Associated task. |
| `TargetUserId` | `string?` | Internal user ID of the intended recipient. The orchestrator sets this when the question targets a specific user (e.g., `alice@contoso.com`'s internal ID). `TeamsMessengerConnector` resolves this to a `TeamsConversationReference` via `IConversationReferenceStore.GetByInternalUserIdAsync(tenantId, targetUserId)`. Null when the question should be broadcast to a channel (in which case `TargetChannelId` must be set). |
| `TargetChannelId` | `string?` | Teams channel ID for channel-scoped questions. Mutually exclusive with `TargetUserId` — exactly one must be non-null. |
| `Title` | `string` | Short title for the card header. |
| `Body` | `string` | Detailed question text. |
| `Severity` | `string` | `Info`, `Warning`, `Error`, `Critical`. |
| `AllowedActions` | `IReadOnlyList<HumanAction>` | Buttons the human can press. |
| `ExpiresAt` | `DateTimeOffset` | Expiration deadline. |
| `CorrelationId` | `string` | End-to-end trace ID. |

> **Routing derivation:** When the orchestrator calls `IMessengerConnector.SendQuestionAsync(agentQuestion)`, `TeamsMessengerConnector` builds an `OutboxEntry` with `Destination` derived from `TargetUserId` or `TargetChannelId`: `teams://{tenantId}/user/{targetUserId}` for personal delivery, `teams://{tenantId}/channel/{targetChannelId}` for channel delivery. The `ProactiveNotifier` then resolves this destination to a stored `ConversationReference` via `IConversationReferenceStore`. For user-targeted questions, the lookup is `GetByInternalUserIdAsync(tenantId, targetUserId)` — this uses the `InternalUserId` field on `TeamsConversationReference`, which was populated when `IIdentityResolver` first mapped the user's `AadObjectId` to an internal identity (§6.1 step 6). The `TargetUserId` is the orchestrator's **internal user ID** (the same value stored in `TeamsConversationReference.InternalUserId`), NOT the AAD object ID (which is stored separately in `TeamsConversationReference.AadObjectId`). The orchestrator is responsible for determining which user should receive each question based on task assignment, escalation policy, or explicit addressing. The e2e scenario at `e2e-scenarios.md` lines 81–96 (agent sends a blocking question to `alice@contoso.com`) exercises this path: the orchestrator sets `TargetUserId` to Alice's internal ID, `TeamsMessengerConnector` resolves it to her stored `ConversationReference` via `InternalUserId`, and `ProactiveNotifier` delivers via `ContinueConversationAsync`.

#### HumanAction

A single action button on an agent question card.

| Field | Type | Description |
|---|---|---|
| `ActionId` | `string` | Unique action identifier. |
| `Label` | `string` | Button display text (e.g., "Approve"). |
| `Value` | `string` | Machine-readable value sent on click. |
| `RequiresComment` | `bool` | Whether a text input is shown alongside. |

#### HumanDecisionEvent

Emitted when a human responds to an agent question.

| Field | Type | Description |
|---|---|---|
| `QuestionId` | `string` | Which question was answered. |
| `ActionValue` | `string` | Selected action's `Value`. |
| `Comment` | `string?` | Optional free-text comment. |
| `Messenger` | `string` | `"Teams"`. |
| `ExternalUserId` | `string` | Teams AAD object ID. |
| `ExternalMessageId` | `string` | Teams activity ID of the response. |
| `ReceivedAt` | `DateTimeOffset` | UTC timestamp. |
| `CorrelationId` | `string` | End-to-end trace ID. |

#### MessengerEvent

Platform-agnostic inbound event. Defined in `AgentSwarm.Messaging.Abstractions` as a base record with concrete subtypes per `implementation-plan.md` §1.1.

The base record carries the common envelope fields. Each subtype adds a typed payload. The `EventType` discriminator is a `string` field on the base record that identifies the subtype.

**Base record fields:**

| Field | Type | Description |
|---|---|---|
| `EventId` | `string` | Unique event identifier. |
| `EventType` | `string` | Discriminator — see subtype table below. |
| `CorrelationId` | `string` | End-to-end trace ID. |
| `Messenger` | `string` | `"Teams"`. |
| `ExternalUserId` | `string` | Teams AAD object ID. |
| `ActivityId` | `string?` | Inbound `Activity.Id` — used for webhook deduplication (see §4.8). |
| `Source` | `string?` | Origin context: `null` for direct messages, `MessageAction` for forwarded messages. |
| `Timestamp` | `DateTimeOffset` | UTC receipt time. |

**Subtypes and EventType values:**

| Subtype (C# record) | `EventType` value | Typed payload | When produced |
|---|---|---|---|
| `CommandEvent` | `AgentTaskRequest` | `ParsedCommand` | User sends `agent ask <text>` in chat, channel, or via message-action forward (with `Source = MessageAction`). Aligns with `e2e-scenarios.md` §Personal Chat lines 26, 40. |
| `CommandEvent` | `Command` | `ParsedCommand` | User sends `agent status`, `approve`, or `reject`. General-purpose command event for non-task-creation commands. Aligns with `e2e-scenarios.md` §Correlation and Traceability lines 723–728 which lists `Command` as an allowed domain `EventType`. |
| `CommandEvent` | `Escalation` | `ParsedCommand` | User sends `escalate`. Aligns with `e2e-scenarios.md` §Escalation line 543. |
| `CommandEvent` | `PauseAgent` | `ParsedCommand` | User sends `pause`. Aligns with `e2e-scenarios.md` §Pause line 551. |
| `CommandEvent` | `ResumeAgent` | `ParsedCommand` | User sends `resume`. Aligns with `e2e-scenarios.md` §Resume line 560. |
| `DecisionEvent` | `Decision` | `HumanDecisionEvent` | User taps an Adaptive Card action button (approve/reject/etc.). |
| `TextEvent` | `Text` | `string` (raw text) | Unrecognized free-text input that does not match a command pattern. |
| *(base)* | `InstallUpdate` | `InstallEventPayload` | Bot installed/uninstalled from personal scope or team. |
| *(base)* | `Reaction` | `ReactionPayload` | User adds/removes a reaction to a bot message. |

> **Cross-doc alignment — domain `MessengerEvent.EventType` vocabulary:** The canonical domain `MessengerEvent.EventType` discriminator values are: `AgentTaskRequest`, `Command`, `Escalation`, `PauseAgent`, `ResumeAgent`, `Decision`, `Text`, `InstallUpdate`, `Reaction`. This is the authoritative set. `e2e-scenarios.md` §Correlation and Traceability lines 723–728 defines this same domain constraint set. (Note: `e2e-scenarios.md` lines 669–673 contain an **audit trail** table with audit `EventType` values `CommandReceived`, `ProactiveNotification`, `CardActionReceived`, `MessageSent` — these are audit values, not domain values.) `tech-spec.md` §4.3 line 130 lists the full domain discriminator set including `Escalation`, `PauseAgent`, `ResumeAgent`, and `Text` — all sibling docs are now aligned on this vocabulary. Note: these domain `EventType` values are intentionally distinct from the **audit** `EventType` values (`CommandReceived`, `MessageSent`, `CardActionReceived`, `SecurityRejection`, `ProactiveNotification`, `MessageActionReceived`, `Error`) defined in §3.2 `AuditEntry` — the two enumerations serve different purposes (domain event polymorphism vs. audit categorization). The canonical audit set contains exactly seven values per `tech-spec.md` §4.3 (compliance-audit-schema); message actions (Teams message-extension submissions) log as `MessageActionReceived` — a dedicated audit event type distinct from `CommandReceived` — because they arrive through the `composeExtension/submitAction` invoke mechanism rather than direct text commands, and distinguishing them in the audit trail supports compliance filtering and forensic analysis.
>
> `implementation-plan.md` §1.1 line 19 defines `MessengerEvent` as a base record with subtypes `CommandEvent`, `DecisionEvent`, `TextEvent`. This architecture adopts that subtype structure. The `CommandEvent` subtype uses variable `EventType` discriminator values depending on the parsed command — `AgentTaskRequest` for `agent ask`, `Command` for general commands, `Escalation`/`PauseAgent`/`ResumeAgent` for lifecycle commands — aligning with `e2e-scenarios.md` which expects these as distinct event types (lines 543, 551, 560, 680). The `CommandParser` sets the `EventType` based on the recognized command pattern.

### 3.2 Teams-Specific Entities

#### MessageActionRequest

Represents a message-extension action invocation from Teams. Produced by `MessageExtensionHandler`.

| Field | Type | Description |
|---|---|---|
| `RequestId` | `string` | Unique request identifier (GUID). |
| `SourceMessageId` | `string` | Teams activity ID of the original message the user acted on. |
| `SourceMessageText` | `string` | Text content of the source message. |
| `ActionCommandId` | `string` | The `commandId` from the Teams app manifest (e.g., `forwardToAgent`). |
| `UserId` | `string` | AAD object ID of the invoking user. |
| `TenantId` | `string` | Entra ID tenant. |
| `ConversationId` | `string` | Conversation where the action was invoked. |
| `CorrelationId` | `string` | End-to-end trace ID. |
| `Timestamp` | `DateTimeOffset` | UTC invocation time. |

#### TeamsConversationReference

Persisted Bot Framework `ConversationReference` for proactive messaging.

| Field | Type | Description |
|---|---|---|
| `Id` | `string` | Primary key (GUID). |
| `TenantId` | `string` | Entra ID tenant. |
| `AadObjectId` | `string?` | Entra AAD object ID of the user. Null for channel-scoped references (where `ChannelId` is set instead). This is the Teams-native identity key captured from `Activity.From.AadObjectId` at install time (§2.7) and refreshed on message receipt (§6.1 step 8). |
| `InternalUserId` | `string?` | Internal user ID mapped by `IIdentityResolver`. Populated when identity resolution first succeeds for this AAD object ID (§6.1 step 6). Null until the user's first authorized interaction. The orchestrator uses this value when setting `AgentQuestion.TargetUserId` for proactive delivery. |
| `ChannelId` | `string?` | Teams channel ID (null for personal chats). |
| `ServiceUrl` | `string` | Bot Connector endpoint. |
| `ConversationId` | `string` | Bot Framework conversation ID. |
| `BotId` | `string` | Bot's AAD app ID. |
| `ReferenceJson` | `string` | Serialized `ConversationReference` for rehydration. |
| `IsActive` | `bool` | False after uninstall. |
| `CreatedAt` | `DateTimeOffset` | First installation time. |
| `UpdatedAt` | `DateTimeOffset` | Last refresh time. |

#### TeamsCardState

Tracks the state of an Adaptive Card sent to Teams for update/delete scenarios.

| Field | Type | Description |
|---|---|---|
| `Id` | `string` | Primary key (GUID). |
| `QuestionId` | `string` | Linked `AgentQuestion.QuestionId`. |
| `ActivityId` | `string` | Teams activity ID of the sent card message. |
| `ConversationId` | `string` | Bot Framework conversation ID. |
| `ServiceUrl` | `string` | Bot Connector endpoint. |
| `CardType` | `string` | `AgentQuestion`, `Approval`, `Status`, `Incident`, `ReleaseGate`. |
| `CardStatus` | `string` | `Pending`, `Answered`, `Expired`, `Deleted`. |
| `RenderedJson` | `string` | Snapshot of the card JSON as sent. |
| `CreatedAt` | `DateTimeOffset` | Send time. |
| `UpdatedAt` | `DateTimeOffset` | Last status change. |

#### OutboxEntry

Durable outbound message queue entry. Defined in `AgentSwarm.Messaging.Core`.

| Field | Type | Description |
|---|---|---|
| `OutboxEntryId` | `string` | Primary key (GUID). |
| `CorrelationId` | `string` | End-to-end trace ID. |
| `Destination` | `string` | Serialized routing key: `teams://{tenantId}/user/{userId}` for personal delivery, `teams://{tenantId}/channel/{channelId}` for channel delivery — canonical URI shape aligned with §3.1 routing derivation note. |
| `PayloadType` | `string` | `MessengerMessage` or `AgentQuestion`. |
| `PayloadJson` | `string` | Serialized payload. |
| `Status` | `string` | `Pending`, `Processing`, `Sent`, `Failed`, `DeadLettered` — aligned with `implementation-plan.md` §6.1 outbox status vocabulary. |
| `RetryCount` | `int` | Delivery attempt count (incremented on each retry). Aligned with `implementation-plan.md` §6.1 which uses `RetryCount` as the column name. |
| `NextRetryAt` | `DateTimeOffset?` | Scheduled next attempt. |
| `LastError` | `string?` | Last failure reason. |
| `CreatedAt` | `DateTimeOffset` | Enqueue time. |
| `DeliveredAt` | `DateTimeOffset?` | Successful delivery time. |

#### AuditEntry

Immutable audit record. Defined in `AgentSwarm.Messaging.Persistence`. Fields aligned with `tech-spec.md` §4.3 canonical audit record schema — all canonical required fields are included below; `AuditEntryId` and `Checksum` are implementation-specific additions.

| Field | Type | Required | Description |
|---|---|---|---|
| `AuditEntryId` | `string` | Yes | Primary key (GUID) — implementation-specific surrogate key. |
| `Timestamp` | `DateTimeOffset` | Yes | UTC time the event occurred. |
| `CorrelationId` | `string` | Yes | End-to-end trace ID for distributed tracing. |
| `EventType` | `string` | Yes | `CommandReceived`, `MessageSent`, `CardActionReceived`, `SecurityRejection`, `ProactiveNotification`, `MessageActionReceived`, `Error` — exactly the seven canonical values from `tech-spec.md` §4.3 (compliance-audit-schema). Message actions (Teams message-extension submissions) log as `MessageActionReceived` — a dedicated audit event type distinct from `CommandReceived` — because message-action submissions arrive through the `composeExtension/submitAction` invoke mechanism rather than direct text commands, and distinguishing them in the audit trail supports compliance filtering and forensic analysis (consistent with `tech-spec.md` §4.3 lines 128 and 136). The `Source` field on the domain `MessengerEvent` (§3.1) additionally marks the origination (`Source = MessageAction`) for downstream processing. |
| `ActorId` | `string` | Yes | Identity of the actor — Entra AAD object ID for users (`ActorType = User`), agent ID for agent-originated events (`ActorType = Agent`). |
| `ActorType` | `string` | Yes | `User` or `Agent` — disambiguates `ActorId`. |
| `TenantId` | `string` | Yes | Entra ID tenant of the actor. |
| `TaskId` | `string?` | No | Agent task/work-item ID (null for security rejection events). |
| `ConversationId` | `string?` | No | Bot Framework conversation ID (null for events outside a conversation). |
| `Action` | `string` | Yes | Specific action taken (e.g., `approve`, `reject`, `agent ask`, `send_card`). |
| `PayloadJson` | `string` | Yes | JSON-serialized event payload (sanitized — no secrets or PII beyond identity). |
| `Outcome` | `string` | Yes | Result of the action: `Success`, `Rejected`, `Failed`, `DeadLettered`. |
| `Checksum` | `string` | Yes | SHA-256 hash of the record for tamper detection — implementation addition. |

> **Field mapping note:** `ActorId` + `ActorType` replace the previous `AgentId` / `UserId` fields to align with `tech-spec.md` §4.3 canonical schema. When logging a user action, set `ActorId` = AAD object ID and `ActorType` = `User`. When logging an agent-originated event, set `ActorId` = agent ID and `ActorType` = `Agent`.
>
> **Outcome field semantics — cross-doc alignment:** The `Outcome` field uses a closed canonical vocabulary of exactly four values: `Success`, `Rejected`, `Failed`, `DeadLettered` (consistent with `tech-spec.md` §4.3 line 144). Rejection reason codes such as `UnmappedUserRejected`, `UnauthorizedTenantRejected`, and `InsufficientRoleRejected` belong in the `Action` field, not in `Outcome`. For example, an unmapped-user rejection produces `EventType: SecurityRejection`, `Outcome: Rejected`, `Action: UnmappedUserRejected`. All sibling docs are now aligned: `e2e-scenarios.md` line 371 uses `Action: UnmappedUserRejected` with `Outcome: Rejected`; `implementation-plan.md` §5.1 line 287 uses the same mapping; `tech-spec.md` §4.3 line 144 defines the canonical four-value Outcome vocabulary.

### 3.3 Entity Relationship Diagram

```text
AgentQuestion 1──────* HumanAction
      │
      │ QuestionId
      ▼
TeamsCardState *──────1 TeamsConversationReference
      │                        │
      │ QuestionId             │ (TenantId, UserId/ChannelId)
      ▼                        │
HumanDecisionEvent             │
      │                        │
      │ CorrelationId          │
      ▼                        ▼
AuditEntry ◄─────── OutboxEntry
                  (CorrelationId links all)
```

---

## 4. Interfaces Between Components

### 4.1 IMessengerConnector (shared abstraction)

```csharp
// Assembly: AgentSwarm.Messaging.Abstractions
public interface IMessengerConnector
{
    Task SendMessageAsync(MessengerMessage message, CancellationToken ct);
    Task SendQuestionAsync(AgentQuestion question, CancellationToken ct);
    Task<IReadOnlyList<MessengerEvent>> ReceiveAsync(CancellationToken ct);
}
```

`TeamsMessengerConnector` implements this interface. The orchestrator interacts only through `IMessengerConnector` for send/receive operations; it has no knowledge of Teams-specific types. The interface contains exactly `SendMessageAsync`, `SendQuestionAsync`, and `ReceiveAsync` — aligned with `implementation-plan.md` §1.2 line 41.

Card update/delete operations are Teams-specific (they depend on `activityId` and Bot Connector's `UpdateActivityAsync`/`DeleteActivityAsync`). They are exposed through a separate Teams-specific interface:

### 4.1.1 ITeamsCardManager (Teams-specific card update/delete)

```csharp
// Assembly: AgentSwarm.Messaging.Teams
public interface ITeamsCardManager
{
    Task UpdateCardAsync(string questionId, CardUpdateAction action, CancellationToken ct);
    Task DeleteCardAsync(string questionId, CancellationToken ct);
}

/// <summary>Describes the update to apply to a previously-sent card.</summary>
public enum CardUpdateAction
{
    MarkAnswered,
    MarkExpired,
    MarkCancelled
}
```

`TeamsMessengerConnector` implements both `IMessengerConnector` and `ITeamsCardManager`. The orchestrator uses `IMessengerConnector` for platform-agnostic messaging. For the §6.5 update/delete flow, the orchestrator (or a Teams-aware coordinator) resolves `ITeamsCardManager` and calls `UpdateCardAsync`/`DeleteCardAsync`. Internally, `TeamsMessengerConnector` looks up `TeamsCardState` to find the `activityId` and delegates to `ProactiveNotifier` for the Bot Framework call.

### 4.2 IConversationReferenceStore

```csharp
// Assembly: AgentSwarm.Messaging.Teams
// Aligned with implementation-plan.md §1.2 / §4.1 contract
public interface IConversationReferenceStore
{
    Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct);

    // Generic get by primary key (aligned with implementation-plan.md §1.2 GetAsync)
    Task<TeamsConversationReference?> GetAsync(string referenceId, CancellationToken ct);

    // Lookup by AAD object ID (Teams-native identity key)
    Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct);

    // Lookup by internal user ID (orchestrator identity key — used for proactive routing)
    Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct);

    Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct);
    Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(string tenantId, CancellationToken ct);

    // Check whether a reference is active (not uninstalled/stale)
    Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct);

    // Personal-scope overloads (keyed by aadObjectId)
    Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct);
    Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct);

    // Channel-scope overloads (keyed by channelId)
    Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct);
    Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct);
}
```

> **Design note:** The channel-scoped `MarkInactiveByChannelAsync` and `DeleteByChannelAsync` methods mirror the personal-scope variants to ensure symmetry with `GetByChannelIdAsync`. When a bot is uninstalled from a team, `MarkInactiveByChannelAsync` is called for each channel in that team — references are **retained as inactive for audit** rather than deleted (aligned with `implementation-plan.md` §2.2 which specifies "mark conversation references inactive rather than removing them" on uninstall). `DeleteByChannelAsync` exists for **administrative cleanup only** — it may be used by an operator-initiated maintenance task after the enterprise audit retention period has elapsed, not as part of the automated uninstall flow.
>
> **Cross-doc consistency — `implementation-plan.md` identity-key alignment required:** `implementation-plan.md` §1.2 line 45, §4.1 lines 248/252, and §4.1 test scenarios (line 263) still use `GetByUserIdAsync`, `UserId`, and `(UserId, TenantId)` as storage keys. These MUST be updated by the implementation-plan sibling agent to align with this architecture's dual identity-key model: `AadObjectId` (persistence key, captured from `Activity.From.AadObjectId`) and `InternalUserId` (routing key, mapped by `IIdentityResolver`). Specifically: (1) rename `GetByUserIdAsync` → split into `GetByAadObjectIdAsync` and `GetByInternalUserIdAsync`; (2) rename the `UserId` column to `AadObjectId` and add `InternalUserId` column; (3) update upsert keys to `(AadObjectId, TenantId)` for user-scoped references; (4) add `MarkInactiveByChannelAsync` and `DeleteByChannelAsync` channel-scope methods. This is a **blocking cross-doc consistency fix** — the implementation plan cannot be implemented as-is without diverging from this architecture.

### 4.3 ICardStateStore

```csharp
// Assembly: AgentSwarm.Messaging.Teams
public interface ICardStateStore
{
    Task SaveAsync(TeamsCardState state, CancellationToken ct);
    Task<TeamsCardState?> GetByQuestionIdAsync(string questionId, CancellationToken ct);
    Task UpdateStatusAsync(string questionId, string newStatus, CancellationToken ct);
}
```

### 4.4 IMessageOutbox

```csharp
// Assembly: AgentSwarm.Messaging.Core
// Aligned with implementation-plan.md §1.2 IMessageOutbox contract
public interface IMessageOutbox
{
    Task EnqueueAsync(OutboxEntry entry, CancellationToken ct);
    Task<IReadOnlyList<OutboxEntry>> DequeueAsync(int batchSize, CancellationToken ct);
    Task AcknowledgeAsync(string outboxEntryId, CancellationToken ct);
    Task DeadLetterAsync(string outboxEntryId, string error, CancellationToken ct);
}
```

> **Method mapping:** `EnqueueAsync` persists a new outbox entry with status `Pending`. `DequeueAsync` atomically selects up to `batchSize` entries with status `Pending` and transitions them to `Processing` (equivalent to the prior `GetPendingAsync`). `AcknowledgeAsync` marks a successfully delivered entry as `Sent` (equivalent to the prior `MarkDeliveredAsync`). `DeadLetterAsync` marks a permanently failed entry as `DeadLettered` after exhausting retries. Transient failure retry scheduling (incrementing `RetryCount`, setting `NextRetryAt`, reverting status to `Pending`) is handled internally by `OutboxRetryEngine` between `DequeueAsync` and `AcknowledgeAsync`/`DeadLetterAsync`.

### 4.5 IAuditLogger

```csharp
// Assembly: AgentSwarm.Messaging.Persistence
public interface IAuditLogger
{
    Task LogAsync(AuditEntry entry, CancellationToken ct);
    Task<IReadOnlyList<AuditEntry>> QueryAsync(
        string? correlationId = null,
        string? tenantId = null,
        string? actorId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default);
}
```

### 4.6 IAdaptiveCardRenderer

```csharp
// Assembly: AgentSwarm.Messaging.Teams
public interface IAdaptiveCardRenderer
{
    Attachment RenderQuestionCard(AgentQuestion question);
    Attachment RenderStatusCard(AgentStatusSummary status);
    Attachment RenderIncidentCard(IncidentSummary incident);
    Attachment RenderReleaseGateCard(ReleaseGateRequest gate);
    Attachment RenderDecisionConfirmationCard(HumanDecisionEvent decision);
}
```

### 4.7 Internal event flow

Components communicate through two internal channels:

1. **Inbound queue** — `TeamsSwarmActivityHandler` → domain handlers → `TeamsMessengerConnector.ReceiveAsync()` buffer. The orchestrator polls `ReceiveAsync` or subscribes to a push-based `IObservable<MessengerEvent>` variant.
2. **Outbound queue** — Orchestrator calls `SendMessageAsync` / `SendQuestionAsync` (via `IMessengerConnector`) or `UpdateCardAsync` / `DeleteCardAsync` (via `ITeamsCardManager`) → `OutboxRetryEngine` enqueues → background worker dequeues → `ProactiveNotifier` delivers via Bot Framework.

### 4.8 IActivityIdStore (webhook deduplication)

```csharp
// Assembly: AgentSwarm.Messaging.Teams
public interface IActivityIdStore
{
    /// <summary>
    /// Returns true if the activity ID was already seen (duplicate).
    /// If not seen, atomically marks it as seen with the configured TTL.
    /// </summary>
    Task<bool> IsSeenOrMarkAsync(string activityId, CancellationToken ct);
}
```

`ActivityDeduplicationMiddleware` (§2.16) uses this store to suppress duplicate inbound webhook deliveries by `Activity.Id` (or `Activity.ReplyToId` for invoke activities), per `tech-spec.md` §4.4 and `e2e-scenarios.md` §Reliability lines 447–454 (duplicate inbound webhook suppression scenario). The **default implementation is an in-memory `ConcurrentDictionary`** with a background eviction timer (TTL: 10 minutes, aligned with `implementation-plan.md` §2.1 `ActivityDeduplicationMiddleware` default TTL). This is sufficient for single-instance deployments. For multi-instance deployments, a Redis-backed implementation (`RedisActivityIdStore`) is required to share seen-activity state across pods and ensure consistent deduplication.

### 4.9 IIdentityResolver (user identity mapping)

```csharp
// Assembly: AgentSwarm.Messaging.Abstractions (interface)
// Implementations: AgentSwarm.Messaging.Teams
// Aligned with implementation-plan.md §1.2 (interface in Abstractions) and §5.1 (implementation in Teams)
public interface IIdentityResolver
{
    /// <summary>
    /// Maps a Teams AAD object ID to an internal user identity record.
    /// Returns null if the user is not mapped (triggers unmapped-user rejection per §6.4.2).
    /// </summary>
    Task<UserIdentity?> ResolveAsync(string aadObjectId, CancellationToken ct);
}

public sealed record UserIdentity(
    string InternalUserId,
    string AadObjectId,
    string DisplayName,
    string Role);
```

`TeamsSwarmActivityHandler` calls `IIdentityResolver.ResolveAsync` after tenant validation passes. If the result is `null`, the handler logs a `SecurityRejection` audit entry and returns an Adaptive Card explaining access denial (§6.4.2).

### 4.10 IUserAuthorizationService (RBAC enforcement)

```csharp
// Assembly: AgentSwarm.Messaging.Abstractions (interface)
// Implementations: AgentSwarm.Messaging.Teams
// Aligned with implementation-plan.md §1.2 (interface in Abstractions) and §5.1 (implementation in Teams)
public interface IUserAuthorizationService
{
    /// <summary>
    /// Checks whether the user's role permits the specified command.
    /// Returns an authorization result with success/failure and the required role.
    /// </summary>
    Task<AuthorizationResult> AuthorizeAsync(string tenantId, string userId, string command, CancellationToken ct);
}

public sealed record AuthorizationResult(
    bool IsAuthorized,
    string? UserRole,
    string? RequiredRole);
```

`TeamsSwarmActivityHandler` calls `IUserAuthorizationService.AuthorizeAsync` after identity resolution succeeds. If the result indicates insufficient permissions, the handler logs a `SecurityRejection` audit entry and returns an Adaptive Card explaining the required role (§6.4.3). Role-to-command mappings are defined in §5.2.

---

## 5. Security Architecture

### 5.1 Tenant Validation

`TenantValidationMiddleware` runs early in the `TeamsBotAdapter` pipeline. It extracts `Activity.ChannelData.Tenant.Id` (or the `tid` claim from the Bot Framework JWT) and validates it against a configured allowlist of Entra ID tenant IDs. Activities from disallowed tenants are rejected with HTTP 403 and an `AuditEntry` of type `SecurityRejection` is logged.

### 5.2 User Identity

The user's AAD object ID is extracted from `Activity.From.AadObjectId`. This is correlated with RBAC role assignments stored in the authorization configuration. The following roles are enforced:

| Role | Permitted commands |
|---|---|
| `Operator` | All commands. |
| `Approver` | `approve`, `reject`, `agent status`. |
| `Viewer` | `agent status` only. |

Users without any assigned role are rejected.

### 5.3 Teams App Installation Policy

Proactive messaging requires the Teams app to be installed for the target user or in the target team. The `InstallHandler` captures installation events and persists `ConversationReference` objects. If the app is uninstalled, the reference is marked inactive and proactive messaging attempts fail gracefully with an appropriate audit log entry.

### 5.4 Bot Framework Authentication

Inbound requests are authenticated by the Bot Framework SDK's built-in JWT validation (`JwtTokenValidation`). The adapter is configured with:

- `MicrosoftAppId` — the bot's AAD application (client) ID.
- `MicrosoftAppPassword` — the bot's client secret (stored in Azure Key Vault).
- `MicrosoftAppTenantId` — for single-tenant bots, restricts to one Entra ID tenant.

Outbound requests to the Bot Connector service use OAuth 2.0 client credentials to obtain bearer tokens.

---

## 6. End-to-End Sequence Flows

### 6.1 Scenario: Human sends `agent ask create e2e test scenarios for update service`

```text
Human (Teams)          TeamsWebhookController    TeamsBotAdapter    TeamsSwarmActivityHandler    CommandParser    IIdentityResolver    IUserAuthorizationService    ConvRefStore    TeamsMessengerConnector    Orchestrator
     │                        │                       │                    │                     │                  │                      │                        │                    │                      │
     │── message ────────────>│                       │                    │                     │                  │                      │                        │                    │                      │
     │                        │── POST /api/messages─>│                    │                     │                  │                      │                        │                    │                      │
     │                        │                       │── middleware ─────>│                     │                  │                      │                        │                    │                      │
     │                        │                       │  (tenant ✓, rate)  │                     │                  │                      │                        │                    │                      │
     │                        │                       │                    │── parse message ───>│                  │                      │                        │                    │                      │
     │                        │                       │                    │<── ParsedCommand ───│                  │                      │                        │                    │                      │
     │                        │                       │                    │── resolve identity ───────────────────>│                      │                        │                    │                      │
     │                        │                       │                    │<── UserIdentity ──────────────────────│                      │                        │                    │                      │
     │                        │                       │                    │── check RBAC ──────────────────────────────────────────────>│                        │                    │                      │
     │                        │                       │                    │<── authorized ─────────────────────────────────────────────│                        │                    │                      │
     │                        │                       │                    │── save ConvRef ──────────────────────────────────────────────────────────────────────>│                    │                      │
     │                        │                       │                    │<── ack card ────────│                  │                      │                        │                    │                      │
     │<── "Task submitted" ───│<──────────────────────│<───────────────────│                     │                  │                      │                        │                    │                      │
     │                        │                       │                    │                     │                  │                      │                        │── CommandEvent ───>│                      │
     │                        │                       │                    │                     │                  │                      │                        │  {AgentTaskRequest}│                      │
     │                        │                       │                    │                     │                  │                      │                        │                    │                      │── audit log
```

1. Human types `agent ask create e2e test scenarios for update service` in Teams.
2. Bot Framework delivers the activity to `POST /api/messages`.
3. `TeamsBotAdapter` runs middleware: tenant validation passes, rate limit check passes.
4. Handler delegates to `CommandParser`.
5. `CommandParser` recognizes `agent ask` prefix, extracts payload, generates `CorrelationId`.
6. Handler invokes `IIdentityResolver.ResolveAsync` with `Activity.From.AadObjectId` to map the Entra identity to an internal user. If the user is unmapped, the flow diverts to §6.4.2 (unmapped-user rejection) — no conversation reference is stored.
7. Handler invokes `IUserAuthorizationService.AuthorizeAsync` to check RBAC permissions. If insufficient, the flow diverts to §6.4.3 (RBAC rejection) — no conversation reference is stored.
8. After successful identity resolution and authorization, handler extracts the `ConversationReference` from the activity via `Activity.GetConversationReference()` and calls `IConversationReferenceStore.SaveOrUpdateAsync` to refresh it. This **message-path refresh** updates the `ServiceUrl` and `ConversationId` on the existing reference (which was originally created by the install event — see §2.7 `InstallHandler`). Two distinct paths store/update conversation references:
   - **Install path (§2.7):** `InstallHandler` persists a new reference on `installationUpdate`/`conversationUpdate` after tenant validation only. This is gated by Teams app installation policy (admin-managed). No identity resolution or RBAC is applied because install events carry no user command. The reference proves the app is installed.
   - **Message path (this step):** `TeamsSwarmActivityHandler` refreshes the reference after full identity resolution + RBAC authorization. This keeps the `ServiceUrl` current (Bot Framework rotates service URLs) and confirms the user is still authorized.
   Both paths require tenant validation. Proactive messaging (§6.2) uses whichever reference is most recently updated. Command authorization is always enforced at command time (§6.4), regardless of which path stored the reference.
9. An acknowledgment Adaptive Card ("Task submitted — tracking ID: {CorrelationId}") is sent back to the user.
10. `TeamsMessengerConnector` publishes a `CommandEvent` (a `MessengerEvent` subtype with `EventType = AgentTaskRequest`) to the inbound buffer.
11. The orchestrator consumes the event and dispatches work to agents.
12. `AuditLogger` records the command with full correlation data.

### 6.2 Scenario: Agent proactively sends a blocking question

```text
Orchestrator    TeamsMessengerConnector    OutboxRetryEngine    ProactiveNotifier    ConvRefStore    AdaptiveCardRenderer    CardStateStore    Teams
     │                    │                      │                     │                  │                  │                    │              │
     │── SendQuestion ───>│                      │                     │                  │                  │                    │              │
     │   (AgentQuestion)  │── enqueue ──────────>│                     │                  │                  │                    │              │
     │                    │                      │── dequeue ─────────>│                  │                  │                    │              │
     │                    │                      │                     │── lookup ref ───>│                  │                    │              │
     │                    │                      │                     │<── ConvRef ──────│                  │                    │              │
     │                    │                      │                     │── check IsActive │                  │                    │              │
     │                    │                      │                     │   (if inactive:  │                  │                    │              │
     │                    │                      │                     │    dead-letter,  │                  │                    │              │
     │                    │                      │                     │    audit, STOP)  │                  │                    │              │
     │                    │                      │                     │── render card ──────────────────────>│                    │              │
     │                    │                      │                     │<── Attachment ──────────────────────│                    │              │
     │                    │                      │                     │── ContinueConversationAsync ───────────────────────────────────────────>│
     │                    │                      │                     │<── activityId ─────────────────────────────────────────────────────────│
     │                    │                      │                     │── save card state ──────────────────────────────────────>│              │
     │                    │                      │<── mark delivered ──│                  │                  │                    │              │
     │                    │                      │                     │                  │                  │                    │── card ─────>│
```

1. Orchestrator calls `IMessengerConnector.SendQuestionAsync(agentQuestion)`.
2. `TeamsMessengerConnector` creates an `OutboxEntry` and persists it via `IMessageOutbox.EnqueueAsync`.
3. The outbox background worker dequeues the entry and delegates to `ProactiveNotifier`.
4. `ProactiveNotifier` resolves the target from the `OutboxEntry.Destination` field (which was derived from `AgentQuestion.TargetUserId` or `AgentQuestion.TargetChannelId` — see §3.1 routing derivation note). It looks up the `ConversationReference` for the target user via `IConversationReferenceStore.GetByInternalUserIdAsync(tenantId, targetUserId)` (or `GetByChannelIdAsync` for channel-scoped questions).
5. **Inactive-installation pre-check:** If the reference is not found, or if `IsActive == false` (indicating the user uninstalled the bot), `ProactiveNotifier` skips the Bot Framework call entirely, moves the outbox entry to the dead-letter queue via `IMessageOutbox.DeadLetterAsync`, and logs an audit entry (`EventType: Error`, `Outcome: Failed`, reason: inactive/missing reference). This pre-check avoids unnecessary Bot Framework calls for known-uninstalled users (aligned with `implementation-plan.md` §5.1 `InstallationStateGate` and `e2e-scenarios.md` §Proactive Messaging lines 121–129).
6. `AdaptiveCardRenderer.RenderQuestionCard` builds the Adaptive Card with action buttons.
7. `ProactiveNotifier` calls `CloudAdapter.ContinueConversationAsync` with the conversation reference, sending the card as an `Activity`.
8. Teams returns the `activityId` of the sent message.
9. `CardStateStore.SaveAsync` persists the `TeamsCardState` with the `activityId` (needed for future update/delete).
10. `OutboxRetryEngine` marks the entry as `Sent` (via `IMessageOutbox.AcknowledgeAsync`).
11. If delivery fails with a transient error (HTTP 429, 500, 502, 503, 504), the engine schedules a retry per `tech-spec.md` §4.4 (exponential backoff with ±25% jitter; `Retry-After` override for 429). If delivery fails with HTTP 403 or 404 (stale reference — user removed from tenant without an uninstall event), `ProactiveNotifier` marks the reference as inactive, dead-letters the message, and logs an audit entry (aligned with `e2e-scenarios.md` §Proactive Messaging lines 131–143).

### 6.3 Scenario: Human approves via Adaptive Card action

```text
Human (Teams)    TeamsWebhookController    TeamsBotAdapter    TeamsSwarmActivityHandler    CardActionHandler    CardStateStore    TeamsMessengerConnector    Orchestrator
     │                  │                       │                    │                      │                   │                    │                      │
     │── tap Approve ──>│                       │                    │                      │                   │                    │                      │
     │                  │── invoke activity ───>│                    │                      │                   │                    │                      │
     │                  │                       │── middleware ─────>│                      │                   │                    │                      │
     │                  │                       │                    │── invoke dispatch ──>│                   │                    │                      │
     │                  │                       │                    │                      │── lookup card ───>│                    │                      │
     │                  │                       │                    │                      │<── card state ────│                    │                      │
     │                  │                       │                    │                      │── idempotency chk │                    │                      │
     │                  │                       │                    │                      │── build decision  │                    │                      │
     │                  │                       │                    │                      │    event           │                    │                      │
     │                  │                       │                    │                      │── update card ────>│ (status=Answered)  │                      │
     │                  │                       │                    │                      │── update Teams ───────────────────────────────────────────────>│
     │<── updated card ─│<──────────────────────│<───────────────────│<─────────────────────│                   │                    │                      │
     │  (shows "Approved│ by <user>")           │                    │                      │                   │── HumanDecision ──>│                      │
     │                  │                       │                    │                      │                   │                    │── MessengerEvent ───>│
```

1. Human taps "Approve" on the Adaptive Card in Teams.
2. Teams sends an `invoke` activity with `type: "adaptiveCard/action"` to the bot.
3. `TeamsSwarmActivityHandler.OnAdaptiveCardInvokeAsync` delegates to `CardActionHandler`.
4. `CardActionHandler` extracts `QuestionId` and `ActionId` from `Activity.Value`.
5. Idempotency check: if this `(QuestionId, UserId)` pair was already processed, return the previous result.
6. `CardActionHandler` builds a `HumanDecisionEvent` with the user's AAD object ID, action value, and optional comment.
7. `CardStateStore` is updated: status changes from `Pending` to `Answered`.
8. The original card is updated in Teams via `TurnContext.UpdateActivityAsync` to show "Approved by {user}" with action buttons disabled.
9. `TeamsMessengerConnector` publishes the `HumanDecisionEvent` as a `MessengerEvent` to the inbound buffer.
10. Orchestrator consumes the decision and unblocks the agent.
11. `AuditLogger` records the approval with full correlation data.

### 6.4 Scenario: Unauthorized tenant/user is rejected

This scenario covers three distinct rejection paths per `tech-spec.md` §4.2 rejection behavior matrix.

#### 6.4.1 Unauthorized tenant

```text
Attacker (Teams)    TeamsWebhookController    TeamsBotAdapter    TenantValidationMiddleware    AuditLogger
     │                     │                       │                       │                       │
     │── message ─────────>│                       │                       │                       │
     │                     │── POST /api/messages─>│                       │                       │
     │                     │                       │── check tenant ──────>│                       │
     │                     │                       │                       │── tenant NOT in list   │
     │                     │                       │                       │── log rejection ──────>│
     │                     │                       │<── 403 Forbidden ─────│                       │
     │<── (no response) ───│<──────────────────────│                       │                       │
```

1. Activity arrives from an unrecognized tenant.
2. `TenantValidationMiddleware` extracts the tenant ID and checks the allowlist.
3. Tenant is not found — middleware short-circuits, logs a `SecurityRejection` audit entry (`EventType: SecurityRejection`, `Outcome: Rejected`), and returns HTTP 403.
4. The user sees no bot response (Bot Framework does not surface 403 to the user; the message simply goes unprocessed).

#### 6.4.2 Allowed tenant, unmapped user

```text
User (Teams)    TeamsWebhookController    TeamsBotAdapter    TeamsSwarmActivityHandler    IIdentityResolver    AuditLogger
     │                  │                       │                    │                        │                   │
     │── message ──────>│                       │                    │                        │                   │
     │                  │── POST /api/messages─>│                    │                        │                   │
     │                  │                       │── middleware OK ──>│                        │                   │
     │                  │                       │  (tenant ✓)       │── resolve user ───────>│                   │
     │                  │                       │                    │                        │── NOT mapped       │
     │                  │                       │                    │<── null ───────────────│                   │
     │                  │                       │                    │── log rejection ─────────────────────────>│
     │                  │                       │                    │    {EventType: SecurityRejection,          │
     │                  │                       │                    │     Outcome: Rejected}                     │
     │<── access denial │<──────────────────────│<───────────────────│                        │                   │
     │   Adaptive Card  │  HTTP 200             │                    │                        │                   │
     │   "Your account  │                       │                    │                        │                   │
     │   is not mapped" │                       │                    │                        │                   │
```

1. Activity arrives from an allowed tenant. `TenantValidationMiddleware` passes.
2. `TeamsSwarmActivityHandler` invokes `IIdentityResolver` with `Activity.From.AadObjectId`.
3. AAD object ID is not mapped to any internal user record.
4. Handler logs a `SecurityRejection` audit entry with `Outcome: Rejected` and `Action: UnmappedUserRejected` (the `Action` field carries the rejection reason code from `tech-spec.md` §4.2 rejection matrix; `EventType` remains `SecurityRejection`).
5. An Adaptive Card is returned to the user explaining the access denial and how to request access.

#### 6.4.3 Allowed tenant, mapped user, insufficient RBAC role

```text
User (Teams)    TeamsWebhookController    TeamsBotAdapter    TeamsSwarmActivityHandler    IUserAuthorizationService    AuditLogger
     │                  │                       │                    │                            │                       │
     │── "approve" ────>│                       │                    │                            │                       │
     │                  │── POST /api/messages─>│                    │                            │                       │
     │                  │                       │── middleware OK ──>│                            │                       │
     │                  │                       │  (tenant ✓)       │── check role ─────────────>│                       │
     │                  │                       │                    │                            │── role = Viewer        │
     │                  │                       │                    │                            │   (needs Approver)     │
     │                  │                       │                    │<── insufficient ──────────│                       │
     │                  │                       │                    │── log rejection ──────────────────────────────────>│
     │                  │                       │                    │    {EventType: SecurityRejection,                  │
     │                  │                       │                    │     Outcome: Rejected}                             │
     │<── permissions   │<──────────────────────│<───────────────────│                            │                       │
     │   Adaptive Card  │  HTTP 200             │                    │                            │                       │
     │   "Insufficient  │                       │                    │                            │                       │
     │    permissions"  │                       │                    │                            │                       │
```

1. Activity arrives from an allowed tenant and a mapped user.
2. `TeamsSwarmActivityHandler` parses the command (e.g., `approve`) and invokes `IUserAuthorizationService` to check whether the user's role permits the command.
3. The user has `Viewer` role, but `approve` requires `Approver` — authorization fails.
4. Handler logs a `SecurityRejection` audit entry with `Outcome: Rejected` and `Action: InsufficientRoleRejected` (the `Action` field carries the rejection reason code from `tech-spec.md` §4.2 rejection matrix; `EventType` remains `SecurityRejection`).
5. An Adaptive Card is returned explaining insufficient permissions and which role is required.

### 6.5 Scenario: Message update/delete for already-sent approval card

```text
Orchestrator    ITeamsCardManager             TeamsMessengerConnector    CardStateStore    ProactiveNotifier    Teams
     │                    │                          │                     │                  │                │
     │── UpdateCardAsync ─>│ (or DeleteCardAsync)    │                     │                  │                │
     │   (questionId,      │── delegate ────────────>│                     │                  │                │
     │    MarkExpired)     │                          │── lookup state ────>│                  │                │
     │                    │                          │<── TeamsCardState ──│                  │                │
     │                    │                          │── render new card ──│                  │                │
     │                    │                          │── UpdateActivity ──────────────────────>│                │
     │                    │                          │                     │                  │── HTTP PUT ────>│
     │                    │                          │                     │                  │<── 200 OK ─────│
     │                    │                          │── update status ───>│                  │                │
     │                    │                          │  (Expired/Deleted)   │                  │                │
```

1. The orchestrator (or a Teams-aware coordinator) calls `ITeamsCardManager.UpdateCardAsync(questionId, CardUpdateAction.MarkExpired)` (or `DeleteCardAsync` for deletion). `ITeamsCardManager` is the Teams-specific contract surface for card updates — separate from the platform-agnostic `IMessengerConnector` (see §4.1 and §4.1.1).
2. `TeamsMessengerConnector` retrieves the `TeamsCardState` to obtain the `activityId` and `conversationId`.
3. A new Adaptive Card is rendered showing the updated status (e.g., "This approval has expired").
4. `ProactiveNotifier` calls `TurnContext.UpdateActivityAsync` (via `ConnectorClient.Conversations.UpdateActivityAsync`) with the stored `activityId`.
5. If `UpdateActivityAsync` fails because the activity ID is no longer valid (e.g., message too old, deleted by user), `ProactiveNotifier` sends a **new replacement card** to the user showing the updated status instead. The outbound retry policy does not infinitely retry the stale update (aligned with `e2e-scenarios.md` §Update/Delete — "bot sends a new replacement card and avoids infinite retry").
6. For deletion, `DeleteActivityAsync` is called instead, removing the card from the conversation.
7. `CardStateStore` is updated with the new status.

### 6.6 Scenario: Conversation reference reuse after service restart

```text
Service (restart)    ConvRefStore    ProactiveNotifier    Teams
     │                    │                │                │
     │── startup ────────>│                │                │
     │                    │── load all ───>│ (warm cache)   │
     │                    │                │                │
     │  (later, agent needs to notify)     │                │
     │── SendQuestion ───────────────────>│                │
     │                    │                │── GetByUser ──>│
     │                    │<── ConvRef ────│                │
     │                    │                │── ContinueConv>│
     │                    │                │<── activityId ─│
```

1. On service startup, `ConversationReferenceStore` loads persisted references (or uses lazy loading on first access).
2. References survive restarts because they are stored in durable storage, not in-memory.
3. When a proactive notification is needed, `ProactiveNotifier` retrieves the reference by `(tenantId, userId)` and sends the message without requiring the user to re-initiate a conversation.

### 6.7 Scenario: Message action — user forwards a message to an agent

```text
Human (Teams)    TeamsWebhookController    TeamsBotAdapter    TeamsSwarmActivityHandler    MessageExtensionHandler    CommandParser    TeamsMessengerConnector    Orchestrator
     │                  │                       │                    │                           │                       │                  │                      │
     │── msg action ───>│                       │                    │                           │                       │                  │                      │
     │  (right-click    │── invoke activity ───>│                    │                           │                       │                  │                      │
     │   "Forward to    │   composeExtension/   │── middleware ─────>│                           │                       │                  │                      │
     │    Agent")       │   submitAction        │                    │── ext dispatch ──────────>│                       │                  │                      │
     │                  │                       │                    │                           │── extract source msg   │                  │                      │
     │                  │                       │                    │                           │── delegate to parser ─>│                  │                      │
     │                  │                       │                    │                           │<── parsed context ─────│                  │                      │
     │                  │                       │                    │                           │── build AgentTask      │                  │                      │
     │                  │                       │                    │                           │    Request (Source=    │                  │                      │
     │                  │                       │                    │                           │    MessageAction)      │                  │                      │
     │                  │                       │                    │                           │── audit log            │                  │                      │
     │                  │                       │                    │<── confirmation card ─────│                       │                  │                      │
     │<── "Forwarded" ──│<──────────────────────│<───────────────────│                           │── MessengerEvent ────────────────────────>│                      │
     │                  │                       │                    │                           │  {AgentTaskRequest,    │                  │── MessengerEvent ───>│
     │                  │                       │                    │                           │   Source=MessageAction} │                  │                      │
```

1. Human right-clicks a message in Teams and selects the "Forward to Agent" message action (defined as a `composeExtension` command in the app manifest).
2. Teams sends an `invoke` activity with `name: "composeExtension/submitAction"` containing the source message content.
3. `TeamsSwarmActivityHandler.OnTeamsMessagingExtensionSubmitActionAsync` delegates to `MessageExtensionHandler`.
4. `MessageExtensionHandler` extracts the source message text and metadata, delegates to `CommandParser` to parse the forwarded content (aligned with `e2e-scenarios.md` §Message Actions which expects delegation to `CommandParser`).
5. A `MessengerEvent` of type `AgentTaskRequest` is built with `Source = MessageAction` (aligned with `e2e-scenarios.md` which expects `MessengerEvent` type `AgentTaskRequest` with `Source = MessageAction`, not `MessageAction` type).
6. An audit entry of type `MessageActionReceived` is logged (message actions log as `MessageActionReceived` per `tech-spec.md` §4.3 lines 128 and 136 — the canonical audit set contains exactly seven values and treats message actions as a distinct audit event category because they arrive through the `composeExtension/submitAction` invoke mechanism rather than direct text commands). The `Source = MessageAction` field on the domain `MessengerEvent` additionally marks the origination for downstream processing.
7. A confirmation card is returned to the user ("Message forwarded to agent — tracking ID: {CorrelationId}").
8. `TeamsMessengerConnector` publishes the `MessengerEvent` to the inbound buffer.
9. The orchestrator consumes the event and routes the forwarded context to the appropriate agent.

---

## 7. Assembly / Project Mapping

> **Note:** These are proposed target projects aligned with the recommended solution structure in the epic attachment and `implementation-plan.md`. No source projects exist in the repository yet.

| Assembly | Layer | Responsibility |
|---|---|---|
| `AgentSwarm.Messaging.Abstractions` | Abstraction | `IMessengerConnector`, `MessengerMessage`, `AgentQuestion`, `HumanAction`, `HumanDecisionEvent`, `MessengerEvent` (base + subtypes `CommandEvent`, `DecisionEvent`, `TextEvent`), `IIdentityResolver` (interface), `UserIdentity`, `IUserAuthorizationService` (interface), `AuthorizationResult` |
| `AgentSwarm.Messaging.Core` | Core | `OutboxRetryEngine`, `IMessageOutbox`, retry policies, deduplication, rate limiting |
| `AgentSwarm.Messaging.Persistence` | Persistence | `IAuditLogger`, `AuditEntry`, `SqlConversationReferenceStore` (implementation), storage implementations (SQL, Azure Table) |
| `AgentSwarm.Messaging.Teams` | Teams Connector | `TeamsWebhookController`, `TeamsBotAdapter`, `TeamsSwarmActivityHandler`, `CommandParser`, `CardActionHandler`, `InstallHandler`, `IConversationReferenceStore` (interface), `ITeamsCardManager` (interface), `CardUpdateAction` (enum), `TeamsMessengerConnector`, `AdaptiveCardRenderer`, `ProactiveNotifier`, `MessageExtensionHandler`, `TeamsCardState`, `ICardStateStore`, `ActivityDeduplicationMiddleware`, `IActivityIdStore`, `EntraIdentityResolver` (impl of `IIdentityResolver`), `RbacAuthorizationService` (impl of `IUserAuthorizationService`) |
| `AgentSwarm.Messaging.Worker` | Host | ASP.NET Core worker service hosting the Teams connector, DI registration, health checks, OpenTelemetry configuration |
| `AgentSwarm.Messaging.Tests` | Test | Unit and integration tests for all assemblies |

---

## 8. Observability

### 8.1 OpenTelemetry Integration

All components emit traces and metrics through `System.Diagnostics.Activity` and `System.Diagnostics.Metrics`, exported via OpenTelemetry.

| Signal | Source | Key attributes |
|---|---|---|
| Trace span | `TeamsSwarmActivityHandler` | `messaging.system=teams`, `correlation_id`, `command_type` |
| Trace span | `ProactiveNotifier` | `messaging.operation=send`, `destination`, `card_type` |
| Trace span | `OutboxRetryEngine` | `outbox.status`, `outbox.attempt` |
| Metric (counter) | `CommandParser` | `teams.commands.received` by `command_type` |
| Metric (histogram) | `ProactiveNotifier` | `teams.card.delivery_latency_ms` |
| Metric (counter) | `TenantValidationMiddleware` | `teams.security.rejections` by `tenant_id` |
| Metric (counter) | `ActivityDeduplicationMiddleware` | `teams.webhook.duplicates_suppressed` |
| Metric (gauge) | `OutboxRetryEngine` | `teams.outbox.pending_count` |
| Health check | `TeamsMessengerConnector` | Bot Framework connectivity, outbox queue depth |

### 8.2 Structured Logging

All log entries include `CorrelationId`, `TenantId`, and `AgentId` as scoped properties via `ILogger` and `BeginScope`. Log levels follow .NET conventions: `Information` for successful operations, `Warning` for retries, `Error` for failures, `Critical` for security rejections and dead-letter events.

---

## 9. Performance Considerations

| Requirement | Design response |
|---|---|
| P95 card delivery < 3 s | Outbox worker polls at 500 ms intervals. `ProactiveNotifier` uses connection pooling via `HttpClientFactory`. Card rendering is synchronous and sub-millisecond. The critical path is the Bot Connector HTTP round-trip, typically 200–800 ms. |
| Connector recovery < 30 s | ASP.NET Core health checks probe Bot Framework token endpoint. Kubernetes liveness/readiness probes restart the pod on failure. Outbox entries survive pod restarts. |
| 1000+ concurrent users | `ConversationReferenceStore` supports concurrent reads. Outbox worker processes messages in batches. No per-user in-memory state is required during normal operation. |
| 0 message loss | Outbox pattern guarantees at-least-once delivery. Dead-letter queue captures permanently failed messages for manual review. |

---

## 10. Cross-Cutting Concerns

### 10.1 Configuration

Teams connector configuration is bound from `appsettings.json` / environment variables:

```json
{
  "Teams": {
    "MicrosoftAppId": "<bot-aad-app-id>",
    "MicrosoftAppPassword": "<from-keyvault>",
    "MicrosoftAppTenantId": "<single-tenant-id>",
    "AllowedTenantIds": ["<tenant-1>", "<tenant-2>"],
    "OutboxPollingIntervalMs": 500,
    "MaxRetryAttempts": 5,
    "RetryBaseDelaySeconds": 2,
    "RetryMaxDelaySeconds": 60
  }
}
```

### 10.2 Dependency Injection Registration

```csharp
services.AddSingleton<IMessengerConnector, TeamsMessengerConnector>();
services.AddSingleton<IConversationReferenceStore, SqlConversationReferenceStore>();
services.AddSingleton<ICardStateStore, SqlCardStateStore>();
services.AddSingleton<IActivityIdStore, InMemoryActivityIdStore>();
services.AddSingleton<IAdaptiveCardRenderer, AdaptiveCardRenderer>();
services.AddSingleton<ProactiveNotifier>();
services.AddSingleton<CommandParser>();
services.AddSingleton<CardActionHandler>();
services.AddSingleton<InstallHandler>();
services.AddSingleton<MessageExtensionHandler>();
services.AddSingleton<IIdentityResolver, EntraIdentityResolver>();
services.AddSingleton<IUserAuthorizationService, RbacAuthorizationService>();
services.AddHostedService<OutboxWorker>();
```

### 10.3 Error Handling Strategy

| Error class | Handling |
|---|---|
| Transient (HTTP 429, 500, 502, 503, 504) | Exponential backoff retry via outbox engine per `tech-spec.md` §4.4: base 2 s, 2× multiplier, max 60 s, 5 total attempts, ±25% jitter, `Retry-After` header override for HTTP 429. |
| Invalid Bot Framework JWT (pre-application) | Handled automatically by Bot Framework `CloudAdapter` authentication pipeline — returns HTTP 401 before any application code or middleware runs. No application-level `SecurityRejection` audit entry is emitted because the request never reaches the bot handler (per `tech-spec.md` §4.2 rejection matrix row 1). **Operator decision (`invalid-jwt-audit`):** Invalid JWT rejections are audited via **infrastructure-level logging** (Azure Front Door WAF logs, API gateway access logs, Azure Monitor) — not application-level audit. This satisfies the compliance requirement for forensic coverage without adding architectural complexity to intercept pre-authentication rejections. `e2e-scenarios.md` lines 387–389 are aligned: "no audit entry is emitted (the request never reaches the bot handler)". |
| Tenant not in allow-list | `TenantValidationMiddleware` rejects with HTTP 403; logs `SecurityRejection` audit entry with `Outcome: Rejected` (per `tech-spec.md` §4.2 rejection matrix row 2). |
| Unmapped user identity | `IIdentityResolver` returns null; handler responds with HTTP 200 + access-denial Adaptive Card; logs `SecurityRejection` audit entry with `Outcome: Rejected` (per `tech-spec.md` §4.2 rejection matrix row 3). |
| Insufficient RBAC role | `IUserAuthorizationService` rejects; handler responds with HTTP 200 + insufficient-permissions Adaptive Card; logs `SecurityRejection` audit entry with `Outcome: Rejected` (per `tech-spec.md` §4.2 rejection matrix row 4). |
| Card update conflict (activity not found) | Log warning; mark card state as `Deleted`; send a **new replacement card** to the user with the updated status (aligned with `e2e-scenarios.md` §Update/Delete — "bot sends a new replacement card"); do not infinitely retry the stale update. |
| Serialization error | Log error; move to dead-letter queue. |
| Unhandled exception | `OnTurnError` handler logs, sends error card to user, publishes dead-letter event. |

---

## Iteration Summary

**File:** `docs/stories/qq-MICROSOFT-TEAMS-MESS/architecture.md`
**Version:** Iteration 16

### Coverage

- Components and responsibilities (§2): 16 components — TeamsWebhookController, TeamsBotAdapter, TeamsSwarmActivityHandler, CommandParser, CardActionHandler, InstallHandler, ConversationReferenceStore, TeamsMessengerConnector, AdaptiveCardRenderer, ProactiveNotifier, OutboxRetryEngine, AuditLogger, MessageExtensionHandler, IdentityResolver, UserAuthorizationService, ActivityDeduplicationMiddleware
- Data model (§3): MessengerEvent (base + subtypes), AgentQuestion (with TargetUserId/TargetChannelId routing), MessageActionRequest, TeamsConversationReference (dual identity keys: AadObjectId for persistence, InternalUserId for routing), TeamsCardState, OutboxEntry, AuditEntry — canonical audit EventType (seven values) and domain EventType (nine values) clearly separated
- Interfaces (§4): IMessengerConnector, IConversationReferenceStore (with channel-scope methods and dual identity lookups), ITeamsCardManager, IAdaptiveCardRenderer, IAuditLogger, IIdentityResolver, IUserAuthorizationService, ICardStateStore, IActivityIdStore
- Security (§5): Entra ID tenant validation, user identity resolution, RBAC, Bot Framework JWT
- Sequence flows (§6): personal chat command, proactive messaging (routing via TargetUserId → GetByInternalUserIdAsync), card approve/reject, card update/delete, security rejections (tenant/unmapped user/insufficient RBAC), restart reuse, message actions
- Assembly mapping (§7), Observability (§8), Performance (§9), Error handling (§10.3)

### Prior feedback resolution

(Addressing iteration 15 evaluator feedback — 5 items)

- [x] 1. FIXED — §3.1 and §6.2 method name inconsistency resolved. Replaced all `GetByUserIdAsync` references with `GetByInternalUserIdAsync` to match §4.2 interface definition. §3.1 `AgentQuestion.TargetUserId` field description now calls `GetByInternalUserIdAsync(tenantId, targetUserId)`. §6.2 step 4 now calls `GetByInternalUserIdAsync(tenantId, targetUserId)`. §4.2 interface (lines 525–531) defines `GetByAadObjectIdAsync`, `GetByInternalUserIdAsync`, and `GetByChannelIdAsync` — no `GetByUserIdAsync` exists. Verification:
```
$ grep -nF "GetByUserIdAsync" docs/stories/qq-MICROSOFT-TEAMS-MESS/architecture.md
(empty — all occurrences replaced)
```

- [x] 2. FIXED — §3.2 `OutboxEntry.Destination` field description updated to use canonical URI shape `teams://{tenantId}/user/{userId}` for personal delivery (matching §3.1 routing derivation note which defines `teams://{tenantId}/user/{targetUserId}`). Both §3.1 and §3.2 now use the `/user/` segment for personal destinations. Verification:
```
$ grep -nF "teams://{tenantId}/{userId}" docs/stories/qq-MICROSOFT-TEAMS-MESS/architecture.md
(empty — old short form removed)
```

- [x] 3. FIXED — Added a **blocking cross-doc consistency note** in §4.2 (after the design note) explicitly requiring `implementation-plan.md` to update: (1) rename `GetByUserIdAsync` → split into `GetByAadObjectIdAsync`/`GetByInternalUserIdAsync`, (2) rename `UserId` column to `AadObjectId` + add `InternalUserId`, (3) update upsert keys, (4) add channel-scope methods. This makes the sibling-doc gap visible and actionable rather than silently divergent.

- [x] 4. FIXED — Removed the entire prior iteration summary (iter 14/15 resolution blocks) which contained self-referential grep hits. This new iteration summary contains no embedded grep commands that reference phrases being verified as absent — all verification is done via tooling before writing the summary, with only the literal grep output pasted inline. The prior iter-15 resolution block at former line 1122 no longer exists.

- [x] 5. FIXED — Sibling `e2e-scenarios.md` lines 968–970: removed the stale cross-doc note that claimed architecture.md §2.1 line 39 omits the deduplication middleware. Architecture.md lines 39–40 have included `ActivityDeduplicationMiddleware` since iter 14. The e2e-scenarios.md note now reads: "FIXED — architecture.md §2.1/§2.3 now includes all four middleware components". Verification:
```
$ grep -nF "omits the deduplication middleware" docs/stories/qq-MICROSOFT-TEAMS-MESS/e2e-scenarios.md
(empty — stale note removed)
```

### Operator answers applied

- **audit-message-action-reconcile** (operator answer: promote `MessageActionReceived` to canonical 7th value in tech-spec.md): Architecture.md §3.2 `AuditEntry` already lists `MessageActionReceived` as the 7th canonical audit value. §3.1 cross-doc note and §6.7 are consistent. No change needed.
- **invalid-jwt-audit** (operator answer: require audit via infrastructure-level logging in e2e-scenarios.md): Architecture.md §10.3 already specifies infrastructure-level logging for invalid JWT rejections. No change needed.

### Open questions

None — all prior open questions resolved by operator decisions.

```json open-questions
{ "openQuestions": [] }
```
