# Architecture — Microsoft Teams Messenger Support

**Story:** `qq:MICROSOFT-TEAMS-MESS`
**Status:** Draft — iteration 2

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
│  Middleware pipeline: Telemetry → TenantFilter → RateLimit          │
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
| **Middleware** | `TelemetryMiddleware` → `TenantValidationMiddleware` → `RateLimitMiddleware` |
| **Responsibility** | Deserialize Bot Framework `Activity` objects, run middleware pipeline, route to `TeamsActivityHandler`. Handles authentication of inbound requests via Bot Framework JWT validation. |
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
| **Idempotency** | Maintains a processed-action set (keyed on `QuestionId + UserId`) to reject duplicate submissions. |

### 2.7 InstallHandler

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Namespace** | `AgentSwarm.Messaging.Teams.Lifecycle` |
| **Responsibility** | Handle `installationUpdate` and `conversationUpdate` activities. On install/member-add: validate tenant ID against allowlist, extract `ConversationReference`, persist it in `ConversationReferenceStore`, and send a welcome Adaptive Card. On uninstall: mark the conversation reference as inactive. |

### 2.8 ConversationReferenceStore

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Interface** | `IConversationReferenceStore` (defined in `AgentSwarm.Messaging.Teams`) |
| **Responsibility** | Persist and retrieve Bot Framework `ConversationReference` objects for proactive messaging. Each reference is keyed by `(TenantId, UserId)` for personal chats or `(TenantId, ChannelId)` for team channels. Survives service restarts. |
| **Storage** | Pluggable — default implementation uses the same durable store as the outbox (e.g., SQL Server, Azure Table Storage, or PostgreSQL). |

### 2.9 TeamsMessengerConnector

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Interface** | `IMessengerConnector` |
| **Responsibility** | Implements `SendMessageAsync`, `SendQuestionAsync`, and `ReceiveAsync` from the shared abstraction. Translates `MessengerMessage` to Bot Framework `Activity` with Adaptive Card attachments. Translates inbound `Activity` to `MessengerEvent`. Uses `ProactiveNotifier` for outbound delivery and `ConversationReferenceStore` for addressing. |

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
| **Responsibility** | Durable outbox pattern. Every outbound message is first persisted to the outbox table, then delivered. A background worker polls for undelivered or failed messages and retries with exponential backoff (base 2 s, max 60 s, 5 attempts). Messages exceeding max retries move to the dead-letter queue. |
| **Idempotency** | Each outbox entry has a unique `OutboxEntryId`; the Bot Connector service is tolerant of duplicate `Activity` sends, but the engine deduplicates on its side as well. |

### 2.13 AuditLogger

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Persistence` |
| **Namespace** | `AgentSwarm.Messaging.Persistence.Audit` |
| **Responsibility** | Append-only, immutable audit trail. Logs every inbound command, outbound message, card action, proactive notification, security rejection, and error. Each entry carries `CorrelationId`, `Timestamp`, `ActorId`, `TenantId`, `EventType`, and a JSON payload. Suitable for enterprise compliance review. |
| **Storage** | Write-once store — Azure Table Storage (append-only), SQL Server with row-level immutability triggers, or a dedicated audit log service. |

### 2.14 Agent Swarm Orchestrator (external boundary)

The orchestrator is outside the scope of this story. It produces `AgentQuestion` and `MessengerMessage` payloads for outbound delivery and consumes `MessengerEvent` / `HumanDecisionEvent` from the inbound queue. The interface contract is defined in `AgentSwarm.Messaging.Abstractions`.

### 2.15 MessageExtensionHandler

| Attribute | Value |
|---|---|
| **Assembly** | `AgentSwarm.Messaging.Teams` |
| **Namespace** | `AgentSwarm.Messaging.Teams.Extensions` |
| **Responsibility** | Handle Teams message-extension action commands (`composeExtension/submitAction`). When a user invokes a message action (e.g., right-clicks a message and selects "Forward to Agent"), this handler extracts the source message content, builds a `MessageActionRequest`, and publishes it as a `MessengerEvent` of type `MessageAction` to the inbound buffer. Returns a task-submitted confirmation card to the user. |
| **Trigger** | `TeamsSwarmActivityHandler.OnTeamsMessagingExtensionSubmitActionAsync` delegates to this handler. |
| **Manifest** | Requires a `composeExtensions` entry in the Teams app manifest with `type: "action"` and `fetchTask: false` (or `true` if a task module is used to collect additional input). |

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
| `Title` | `string` | Short title for the card header. |
| `Body` | `string` | Detailed question text. |
| `Severity` | `string` | `Info`, `Warning`, `Error`, `Critical`. |
| `AllowedActions` | `IReadOnlyList<HumanAction>` | Buttons the human can press. |
| `ExpiresAt` | `DateTimeOffset` | Expiration deadline. |
| `CorrelationId` | `string` | End-to-end trace ID. |

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

Platform-agnostic inbound event. Defined in `AgentSwarm.Messaging.Abstractions`.

| Field | Type | Description |
|---|---|---|
| `EventId` | `string` | Unique event identifier. |
| `EventType` | `string` | `Command`, `Decision`, `Reaction`, `InstallUpdate`, `MessageAction`. |
| `CorrelationId` | `string` | End-to-end trace ID. |
| `Messenger` | `string` | `"Teams"`. |
| `ExternalUserId` | `string` | Teams AAD object ID. |
| `Payload` | `object` | Typed payload — `ParsedCommand`, `HumanDecisionEvent`, or `MessageActionRequest`. |
| `Timestamp` | `DateTimeOffset` | UTC receipt time. |

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
| `UserId` | `string` | AAD object ID (null for channel refs). |
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
| `Destination` | `string` | Serialized routing key: `teams://{tenantId}/{userId}` or `teams://{tenantId}/channel/{channelId}`. |
| `PayloadType` | `string` | `MessengerMessage` or `AgentQuestion`. |
| `PayloadJson` | `string` | Serialized payload. |
| `Status` | `string` | `Pending`, `Delivered`, `Failed`, `DeadLettered`. |
| `Attempts` | `int` | Delivery attempt count. |
| `NextRetryAt` | `DateTimeOffset?` | Scheduled next attempt. |
| `LastError` | `string?` | Last failure reason. |
| `CreatedAt` | `DateTimeOffset` | Enqueue time. |
| `DeliveredAt` | `DateTimeOffset?` | Successful delivery time. |

#### AuditEntry

Immutable audit record. Defined in `AgentSwarm.Messaging.Persistence`. Fields aligned with `tech-spec.md` §4.3 compliance constraints.

| Field | Type | Description |
|---|---|---|
| `AuditEntryId` | `string` | Primary key (GUID). |
| `CorrelationId` | `string` | End-to-end trace ID. |
| `EventType` | `string` | `CommandReceived`, `MessageSent`, `CardActionReceived`, `SecurityRejection`, `ProactiveNotification`, `MessageActionReceived`, `Error`. |
| `AgentId` | `string?` | Originating or target agent identity (null for security rejections). |
| `TaskId` | `string?` | Associated task/work item (null when not applicable). |
| `ConversationId` | `string` | Bot Framework conversation ID. |
| `UserId` | `string` | AAD object ID (Entra ID) of the acting user. |
| `TenantId` | `string` | Entra ID tenant. |
| `Action` | `string` | Specific action taken (e.g., `approve`, `reject`, `agent ask`). |
| `Outcome` | `string` | Result of the action: `Success`, `Rejected`, `Failed`, `DeadLettered`. |
| `PayloadJson` | `string` | Full event payload (JSON, sanitized). |
| `Timestamp` | `DateTimeOffset` | UTC event time. |

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

`TeamsMessengerConnector` implements this interface. The orchestrator interacts only through `IMessengerConnector`; it has no knowledge of Teams-specific types.

### 4.2 IConversationReferenceStore

```csharp
// Assembly: AgentSwarm.Messaging.Teams
// Aligned with implementation-plan.md §1.2 / §4.1 contract
public interface IConversationReferenceStore
{
    Task SaveAsync(TeamsConversationReference reference, CancellationToken ct);
    Task<TeamsConversationReference?> GetByUserIdAsync(string tenantId, string userId, CancellationToken ct);
    Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct);
    Task<IReadOnlyList<TeamsConversationReference>> GetAllAsync(string tenantId, CancellationToken ct);
    Task MarkInactiveAsync(string tenantId, string userId, CancellationToken ct);
    Task DeleteAsync(string tenantId, string userId, CancellationToken ct);
}
```

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

### 4.4 IOutboxStore

```csharp
// Assembly: AgentSwarm.Messaging.Core
public interface IOutboxStore
{
    Task EnqueueAsync(OutboxEntry entry, CancellationToken ct);
    Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int batchSize, CancellationToken ct);
    Task MarkDeliveredAsync(string outboxEntryId, CancellationToken ct);
    Task MarkFailedAsync(string outboxEntryId, string error, DateTimeOffset nextRetry, CancellationToken ct);
    Task MoveToDeadLetterAsync(string outboxEntryId, CancellationToken ct);
}
```

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

1. **Inbound queue** — `TeamsActivityHandler` → domain handlers → `TeamsMessengerConnector.ReceiveAsync()` buffer. The orchestrator polls `ReceiveAsync` or subscribes to a push-based `IObservable<MessengerEvent>` variant.
2. **Outbound queue** — Orchestrator calls `SendMessageAsync` / `SendQuestionAsync` → `OutboxRetryEngine` enqueues → background worker dequeues → `ProactiveNotifier` delivers via Bot Framework.

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
Human (Teams)          TeamsWebhookController    TeamsBotAdapter    TeamsActivityHandler    CommandParser    TeamsMessengerConnector    Orchestrator
     │                        │                       │                    │                     │                    │                      │
     │── message ────────────>│                       │                    │                     │                    │                      │
     │                        │── POST /api/messages─>│                    │                     │                    │                      │
     │                        │                       │── middleware ─────>│                     │                    │                      │
     │                        │                       │  (tenant ✓, rate)  │                     │                    │                      │
     │                        │                       │                    │── parse message ───>│                    │                      │
     │                        │                       │                    │                     │── ParsedCommand ──>│                      │
     │                        │                       │                    │                     │  {Ask, "create..."}│                      │
     │                        │                       │                    │<── ack card ────────│                    │                      │
     │<── "Task submitted" ───│<──────────────────────│<───────────────────│                     │                    │                      │
     │                        │                       │                    │                     │                    │── MessengerEvent ───>│
     │                        │                       │                    │                     │                    │  {Command, Ask}      │
     │                        │                       │                    │                     │                    │                      │── audit log
```

1. Human types `agent ask create e2e test scenarios for update service` in Teams.
2. Bot Framework delivers the activity to `POST /api/messages`.
3. `TeamsBotAdapter` runs middleware: tenant validation passes, rate limit check passes.
4. `TeamsActivityHandler.OnMessageActivityAsync` delegates to `CommandParser`.
5. `CommandParser` recognizes `agent ask` prefix, extracts payload, generates `CorrelationId`.
6. An acknowledgment Adaptive Card ("Task submitted — tracking ID: {CorrelationId}") is sent back to the user.
7. `TeamsMessengerConnector` publishes a `MessengerEvent` of type `Command` to the inbound buffer.
8. The orchestrator consumes the event and dispatches work to agents.
9. `AuditLogger` records the command with full correlation data.

### 6.2 Scenario: Agent proactively sends a blocking question

```text
Orchestrator    TeamsMessengerConnector    OutboxRetryEngine    ProactiveNotifier    ConvRefStore    AdaptiveCardRenderer    CardStateStore    Teams
     │                    │                      │                     │                  │                  │                    │              │
     │── SendQuestion ───>│                      │                     │                  │                  │                    │              │
     │   (AgentQuestion)  │── enqueue ──────────>│                     │                  │                  │                    │              │
     │                    │                      │── dequeue ─────────>│                  │                  │                    │              │
     │                    │                      │                     │── lookup ref ───>│                  │                    │              │
     │                    │                      │                     │<── ConvRef ──────│                  │                    │              │
     │                    │                      │                     │── render card ──────────────────────>│                    │              │
     │                    │                      │                     │<── Attachment ──────────────────────│                    │              │
     │                    │                      │                     │── ContinueConversationAsync ───────────────────────────────────────────>│
     │                    │                      │                     │<── activityId ─────────────────────────────────────────────────────────│
     │                    │                      │                     │── save card state ──────────────────────────────────────>│              │
     │                    │                      │<── mark delivered ──│                  │                  │                    │              │
     │                    │                      │                     │                  │                  │                    │── card ─────>│
```

1. Orchestrator calls `IMessengerConnector.SendQuestionAsync(agentQuestion)`.
2. `TeamsMessengerConnector` creates an `OutboxEntry` and persists it via `IOutboxStore.EnqueueAsync`.
3. The outbox background worker dequeues the entry and delegates to `ProactiveNotifier`.
4. `ProactiveNotifier` looks up the `ConversationReference` for the target user from `ConversationReferenceStore`.
5. `AdaptiveCardRenderer.RenderQuestionCard` builds the Adaptive Card with action buttons.
6. `ProactiveNotifier` calls `CloudAdapter.ContinueConversationAsync` with the conversation reference, sending the card as an `Activity`.
7. Teams returns the `activityId` of the sent message.
8. `CardStateStore.SaveAsync` persists the `TeamsCardState` with the `activityId` (needed for future update/delete).
9. `OutboxRetryEngine` marks the entry as `Delivered`.
10. If delivery fails with a transient error (HTTP 429, 502, 503), the engine schedules a retry with exponential backoff.

### 6.3 Scenario: Human approves via Adaptive Card action

```text
Human (Teams)    TeamsWebhookController    TeamsBotAdapter    TeamsActivityHandler    CardActionHandler    CardStateStore    TeamsMessengerConnector    Orchestrator
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
3. `TeamsActivityHandler.OnAdaptiveCardInvokeAsync` delegates to `CardActionHandler`.
4. `CardActionHandler` extracts `QuestionId` and `ActionId` from `Activity.Value`.
5. Idempotency check: if this `(QuestionId, UserId)` pair was already processed, return the previous result.
6. `CardActionHandler` builds a `HumanDecisionEvent` with the user's AAD object ID, action value, and optional comment.
7. `CardStateStore` is updated: status changes from `Pending` to `Answered`.
8. The original card is updated in Teams via `TurnContext.UpdateActivityAsync` to show "Approved by {user}" with action buttons disabled.
9. `TeamsMessengerConnector` publishes the `HumanDecisionEvent` as a `MessengerEvent` to the inbound buffer.
10. Orchestrator consumes the decision and unblocks the agent.
11. `AuditLogger` records the approval with full correlation data.

### 6.4 Scenario: Unauthorized tenant/user is rejected

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
3. Tenant is not found — middleware short-circuits, logs a `SecurityRejection` audit entry, and returns HTTP 403.
4. The user sees no bot response (Bot Framework does not surface 403 to the user; the message simply goes unprocessed).

### 6.5 Scenario: Message update/delete for already-sent approval card

```text
Orchestrator    TeamsMessengerConnector    CardStateStore    ProactiveNotifier    Teams
     │                    │                     │                  │                │
     │── update card ────>│                     │                  │                │
     │  (expired/cancel)  │── lookup state ────>│                  │                │
     │                    │<── TeamsCardState ──│                  │                │
     │                    │── render new card ──│                  │                │
     │                    │── UpdateActivity ──────────────────────>│                │
     │                    │                     │                  │── HTTP PUT ────>│
     │                    │                     │                  │<── 200 OK ─────│
     │                    │── update status ───>│                  │                │
     │                    │  (Expired/Deleted)   │                  │                │
```

1. The orchestrator signals that an approval card should be updated (e.g., question expired, or the task was cancelled).
2. `TeamsMessengerConnector` retrieves the `TeamsCardState` to obtain the `activityId` and `conversationId`.
3. A new Adaptive Card is rendered showing the updated status (e.g., "This approval has expired").
4. `ProactiveNotifier` calls `TurnContext.UpdateActivityAsync` (via `ConnectorClient.Conversations.UpdateActivityAsync`) with the stored `activityId`.
5. For deletion, `DeleteActivityAsync` is called instead, removing the card from the conversation.
6. `CardStateStore` is updated with the new status.

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

---

## 7. Assembly / Project Mapping

> **Note:** These are proposed target projects aligned with the recommended solution structure in the epic attachment and `implementation-plan.md`. No source projects exist in the repository yet.

| Assembly | Layer | Responsibility |
|---|---|---|
| `AgentSwarm.Messaging.Abstractions` | Abstraction | `IMessengerConnector`, `MessengerMessage`, `AgentQuestion`, `HumanAction`, `HumanDecisionEvent`, `MessengerEvent` |
| `AgentSwarm.Messaging.Core` | Core | `OutboxRetryEngine`, `IOutboxStore`, retry policies, deduplication, rate limiting |
| `AgentSwarm.Messaging.Persistence` | Persistence | `IAuditLogger`, `AuditEntry`, `SqlConversationReferenceStore` (implementation), storage implementations (SQL, Azure Table) |
| `AgentSwarm.Messaging.Teams` | Teams Connector | `TeamsWebhookController`, `TeamsBotAdapter`, `TeamsSwarmActivityHandler`, `CommandParser`, `CardActionHandler`, `InstallHandler`, `IConversationReferenceStore` (interface), `TeamsMessengerConnector`, `AdaptiveCardRenderer`, `ProactiveNotifier`, `MessageExtensionHandler`, `TeamsCardState`, `ICardStateStore` |
| `AgentSwarm.Messaging.Worker` | Host | ASP.NET Core worker service hosting the Teams connector, DI registration, health checks, OpenTelemetry configuration |
| `AgentSwarm.Messaging.Tests` | Test | Unit and integration tests for all assemblies |

---

## 8. Observability

### 8.1 OpenTelemetry Integration

All components emit traces and metrics through `System.Diagnostics.Activity` and `System.Diagnostics.Metrics`, exported via OpenTelemetry.

| Signal | Source | Key attributes |
|---|---|---|
| Trace span | `TeamsActivityHandler` | `messaging.system=teams`, `correlation_id`, `command_type` |
| Trace span | `ProactiveNotifier` | `messaging.operation=send`, `destination`, `card_type` |
| Trace span | `OutboxRetryEngine` | `outbox.status`, `outbox.attempt` |
| Metric (counter) | `CommandParser` | `teams.commands.received` by `command_type` |
| Metric (histogram) | `ProactiveNotifier` | `teams.card.delivery_latency_ms` |
| Metric (counter) | `TenantValidationMiddleware` | `teams.security.rejections` by `tenant_id` |
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
services.AddSingleton<IAdaptiveCardRenderer, AdaptiveCardRenderer>();
services.AddSingleton<ProactiveNotifier>();
services.AddSingleton<CommandParser>();
services.AddSingleton<CardActionHandler>();
services.AddSingleton<InstallHandler>();
services.AddHostedService<OutboxWorker>();
```

### 10.3 Error Handling Strategy

| Error class | Handling |
|---|---|
| Transient (HTTP 429, 502, 503) | Exponential backoff retry via outbox engine. |
| Authentication failure | Log `SecurityRejection` audit entry; return 403. |
| Card update conflict (activity not found) | Log warning; mark card state as `Deleted`; do not retry. |
| Serialization error | Log error; move to dead-letter queue. |
| Unhandled exception | `OnTurnError` handler logs, sends error card to user, publishes dead-letter event. |
