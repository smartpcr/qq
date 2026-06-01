# Epic: Messenger Gateway for Agent Swarm

## Overview

Enable human interaction with a fully autonomous software-factory agent swarm through messenger platforms.

The system contains 100+ autonomous agents performing:

- Product planning
- Architecture design
- Technical specification generation
- Coding
- Unit testing
- E2E testing
- Release orchestration
- Incident response
- Operational remediation

Human interaction occurs **only through messenger applications** on desktop or mobile devices.

Supported messenger platforms:

- Telegram
- Discord
- Slack
- Microsoft Teams

Implementation language must be:

- **C#**
- **.NET 8+**

---

# Common Platform Requirements

## Functional Requirements

### FR-001 Unified Messaging Abstraction

All messenger implementations must implement a shared interface:

```csharp
public interface IMessengerConnector
{
    Task SendMessageAsync(MessengerMessage message, CancellationToken ct);

    Task SendQuestionAsync(
        AgentQuestion question,
        CancellationToken ct);

    Task<IReadOnlyList<MessengerEvent>> ReceiveAsync(
        CancellationToken ct);
}
```

---

### FR-002 Human Interaction Model

Humans must be able to:

- Start new agent tasks
- Respond to blocking questions
- Approve/reject operations
- Pause/resume agents
- Escalate incidents
- Query status
- Subscribe to alerts

---

### FR-003 Agent Interaction Model

Agents must be able to:

- Send progress updates
- Ask questions
- Request approvals
- Escalate incidents
- Deliver summaries
- Notify failures
- Notify completion

---

### FR-004 Correlation & Traceability

Every message must include:

| Field | Description |
|---|---|
| CorrelationId | End-to-end trace ID |
| AgentId | Agent identity |
| TaskId | Task/work item |
| ConversationId | Human conversation/thread |
| Timestamp | UTC timestamp |

---

### FR-005 Reliability

Messaging system must support:

- Retry policies
- Dead-letter queue
- Idempotency
- Duplicate suppression
- Durable outbound queue
- Durable inbound queue
- Connector restart recovery

---

### FR-006 Security

All connectors must support:

- User allowlist
- Tenant validation
- Role validation
- Secret management
- Encrypted token storage
- Audit logging

---

### FR-007 Performance

| Metric | Requirement |
|---|---|
| P95 outbound latency | < 3 seconds |
| Concurrent agents | 100+ |
| Message loss | 0 tolerated |
| Connector recovery | < 30 seconds |

---

### FR-008 Observability

Must integrate with:

- OpenTelemetry
- Structured logging
- Distributed tracing
- Metrics
- Health checks

---

## Recommended Architecture

```text
+------------------------------+
| Human Messenger Apps         |
|------------------------------|
| Telegram                     |
| Discord                      |
| Slack                        |
| Microsoft Teams              |
+---------------+--------------+
                |
                v
+------------------------------+
| Messenger Gateway            |
|------------------------------|
| ASP.NET Core Worker Service  |
| Connector Adapters           |
| Retry Engine                 |
| Rate Limiter                 |
| Deduplication                |
| Outbox/Inbox                 |
+---------------+--------------+
                |
                v
+------------------------------+
| Agent Swarm Orchestrator     |
+------------------------------+
```

---

## Recommended Solution Structure

```text
AgentSwarm.Messaging.Abstractions
AgentSwarm.Messaging.Core
AgentSwarm.Messaging.Telegram
AgentSwarm.Messaging.Discord
AgentSwarm.Messaging.Slack
AgentSwarm.Messaging.Teams
AgentSwarm.Messaging.Persistence
AgentSwarm.Messaging.Worker
AgentSwarm.Messaging.Tests
```

---

# User Story: Telegram Support

## Story ID

MSG-TG-001

---

## Title

Telegram integration for agent swarm communication

---

## User Story

As a software-factory operator,

I want to communicate with autonomous agents through Telegram,

So that I can manage agent workflows from a mobile device.

---

## Technical Requirements

### Protocol

Use:

- Telegram Bot API
- HTTPS webhook transport

Development mode may use:

- Long polling

---

### Recommended C# Libraries

Preferred:

```text
Telegram.Bot
```

Alternative:

```text
Telegram.BotAPI
```

---

### Authentication

Bot token must be stored in:

- Azure Key Vault
- DPAPI-protected local storage
- Kubernetes secret

Never log tokens.

---

### Supported Commands

| Command | Description |
|---|---|
| /start | Register user |
| /status | Query swarm status |
| /agents | List active agents |
| /ask | Create new task |
| /approve | Approve action |
| /reject | Reject action |
| /pause | Pause agent |
| /resume | Resume agent |

---

### Interaction Features

Must support:

- Inline buttons
- Rich formatting
- Threaded context
- Reply correlation
- Deep links

---

### Reliability Requirements

Must support:

- Durable outbound queue
- Retry policy
- Duplicate suppression
- Webhook replay protection
- Dead-letter queue

---

### Security Requirements

Must validate:

- Telegram user ID
- Chat ID
- Authorized tenant/workspace

---

### Performance Requirements

| Metric | Requirement |
|---|---|
| P95 send latency | < 2 seconds |
| Burst handling | 1000+ queued events |
| Delivery guarantee | At least once |

---

## Acceptance Criteria

### AC-001

Human can create tasks using:

```text
/ask generate implementation plan
```

---

### AC-002

Agent can send blocking question with buttons:

- Approve
- Reject
- Need more info

---

### AC-003

Duplicate webhook delivery does not duplicate execution.

---

### AC-004

Connector automatically retries transient failures.

---

### AC-005

All messages are traceable using CorrelationId.

---

# User Story: Discord Support

## Story ID

MSG-DC-001

---

## Title

Discord integration for agent swarm communication

---

## User Story

As an engineering operator,

I want to interact with the agent swarm through Discord,

So that autonomous workflows can integrate with team collaboration channels.

---

## Technical Requirements

### Protocol

Use:

- Discord Gateway WebSocket
- Discord REST API

---

### Recommended C# Library

```text
Discord.Net
```

---

### Interaction Model

Support:

- Slash commands
- Buttons
- Select menus
- Threads
- Rich embeds

---

### Supported Commands

| Command | Description |
|---|---|
| /agent ask | Create task |
| /agent status | Query status |
| /agent approve | Approve |
| /agent reject | Reject |
| /agent pause | Pause |
| /agent resume | Resume |

---

### Reliability Requirements

Must support:

- Gateway reconnect
- Backoff retry
- REST rate-limit handling
- Durable command queue

---

### Security Requirements

Validate:

- Guild ID
- Channel ID
- Discord role
- User ID

---

### Performance Requirements

| Metric | Requirement |
|---|---|
| Concurrent active agents | 100+ |
| Gateway reconnect | < 15 seconds |
| Message ordering | Preserved per channel |

---

## Acceptance Criteria

### AC-001

Human can invoke:

```text
/agent ask design persistence layer
```

---

### AC-002

Agent questions render interactive buttons.

---

### AC-003

Gateway disconnect automatically recovers.

---

### AC-004

Rate limits do not cause message loss.

---

### AC-005

Unauthorized users cannot issue commands.

---

# User Story: Slack Support

## Story ID

MSG-SL-001

---

## Title

Slack integration for agent swarm communication

---

## User Story

As an engineering lead,

I want autonomous agents to communicate through Slack,

So that approvals, questions, and operational workflows integrate with engineering channels.

---

## Technical Requirements

### Protocol

Use:

- Slack Events API
- Slack Web API
- Socket Mode optional

---

### Recommended C# Libraries

Preferred:

```text
SlackNet
```

Alternative:

```text
Slack.NetStandard
```

---

### Interaction Model

Support:

- Slash commands
- Block Kit
- Interactive buttons
- Threads
- Modals

---

### Supported Commands

| Command | Description |
|---|---|
| /agent ask | Create task |
| /agent status | Query status |
| /agent approve | Approve |
| /agent reject | Reject |
| /agent review | Request review |
| /agent escalate | Escalate issue |

---

### Threading Requirements

Each agent task must create:

- Dedicated thread
- Persistent context chain
- Correlated replies

---

### Reliability Requirements

Must support:

- Event deduplication
- Retry handling
- Durable event queue
- Rate-limit backoff

---

### Security Requirements

Must validate:

- Slack signing secret
- Team ID
- Channel ID
- User role/group

---

### Performance Requirements

| Metric | Requirement |
|---|---|
| Interactive response ACK | < 3 seconds |
| Message delivery latency | < 2 seconds |
| Concurrent thread support | 1000+ |

---

## Acceptance Criteria

### AC-001

Human can invoke:

```text
/agent ask generate e2e tests
```

---

### AC-002

Agent creates threaded workflow discussion.

---

### AC-003

Human can approve using interactive buttons.

---

### AC-004

Duplicate Slack retries do not duplicate actions.

---

### AC-005

Unauthorized channels are rejected.

---

# User Story: Microsoft Teams Support

## Story ID

MSG-MT-001

---

## Title

Microsoft Teams integration for agent swarm communication

---

## User Story

As an enterprise operator,

I want to manage autonomous agents through Microsoft Teams,

So that the system integrates with enterprise identity, compliance, and collaboration workflows.

---

## Technical Requirements

### Protocol

Use:

- Microsoft Bot Framework
- Teams Bot APIs

---

### Recommended C# Libraries

```text
Microsoft.Bot.Builder
Microsoft.Bot.Builder.Integration.AspNet.Core
Microsoft.Bot.Connector.Teams
```

---

### Identity Requirements

Must integrate with:

- Microsoft Entra ID
- Teams app policies
- Enterprise RBAC

---

### Interaction Model

Support:

- Personal chat
- Team channels
- Adaptive Cards
- Proactive notifications
- Message actions

---

### Supported Commands

| Command | Description |
|---|---|
| agent ask | Create task |
| agent status | Query status |
| approve | Approve |
| reject | Reject |
| escalate | Escalate |
| pause | Pause |
| resume | Resume |

---

### Proactive Messaging

Must support:

- Conversation reference persistence
- Rehydration after restart
- Proactive incident notifications

---

### Reliability Requirements

Must support:

- Retry handling
- Durable notifications
- Connector restart recovery
- Adaptive Card update retry

---

### Security Requirements

Must validate:

- Tenant ID
- User identity
- Teams installation
- RBAC permissions

---

### Compliance Requirements

Must support:

- Immutable audit logs
- Message retention
- Correlation tracing
- Enterprise compliance review

---

### Performance Requirements

| Metric | Requirement |
|---|---|
| Adaptive Card delivery | < 3 seconds |
| Connector recovery | < 30 seconds |
| Concurrent users | 1000+ |

---

## Acceptance Criteria

### AC-001

Human can message:

```text
agent ask generate release plan
```

---

### AC-002

Agent can proactively send approval requests.

---

### AC-003

Human can approve/reject using Adaptive Cards.

---

### AC-004

Conversation references survive service restart.

---

### AC-005

Unauthorized tenants are rejected.

---

# Shared Data Model

## AgentQuestion

```csharp
public sealed record AgentQuestion(
    string QuestionId,
    string AgentId,
    string TaskId,
    string Title,
    string Body,
    string Severity,
    IReadOnlyList<HumanAction> AllowedActions,
    DateTimeOffset ExpiresAt,
    string CorrelationId);
```

---

## HumanAction

```csharp
public sealed record HumanAction(
    string ActionId,
    string Label,
    string Value,
    bool RequiresComment);
```

---

## HumanDecisionEvent

```csharp
public sealed record HumanDecisionEvent(
    string QuestionId,
    string ActionValue,
    string? Comment,
    string Messenger,
    string ExternalUserId,
    string ExternalMessageId,
    DateTimeOffset ReceivedAt,
    string CorrelationId);
```

---

# Recommended Implementation Priority

| Priority | Platform | Reason |
|---|---|---|
| P0 | Microsoft Teams | Enterprise integration |
| P0 | Slack | Engineering workflows |
| P1 | Telegram | Mobile-first operations |
| P1 | Discord | Community/dev collaboration |
