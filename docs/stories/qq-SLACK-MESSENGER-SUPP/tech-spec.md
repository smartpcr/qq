# Tech Spec: Slack Messenger Support (qq-SLACK-MESSENGER-SUPP)

## 1. Problem Statement

The agent swarm platform contains 100+ autonomous agents performing
software-factory tasks (planning, architecture, coding, testing, releasing,
incident response, operational remediation). Human operators currently have no
channel-integrated way to interact with these agents. This story adds Slack as
a messenger channel so that engineering teams can issue commands, receive agent
questions, deliver approvals/rejections, and review task progress without
leaving the collaboration tool they already use.

Concretely, the system must:

- Accept inbound events from Slack (slash commands, app mentions, button
  clicks, modal submissions) and route them to the agent orchestrator.
- Deliver outbound messages from agents (status updates, questions, approval
  requests) into dedicated Slack threads per task.
- Map every interactive Slack response (button click, modal submit) to a typed
  `HumanDecisionEvent` that the orchestrator consumes.
- Guarantee idempotent processing so that Slack's retry behavior does not
  produce duplicate agent tasks or decisions.
- Enforce workspace-, channel-, and user-group-level authorization.
- Persist an immutable audit trail of every agent/human exchange, queryable by
  correlation ID and other key fields.

The integration targets Slack's Events API / Socket Mode for inbound events and
the Web API for outbound messages. The preferred C# library is SlackNet, with
Slack.NetStandard or direct `HttpClient` as fallbacks.

---

## 2. In-Scope

The following capabilities are within the boundary of this story:

### 2.1 Inbound Event Handling

- **Slash commands.** `/agent ask <prompt>`, `/agent status [task-id]`,
  `/agent approve <question-id>`, `/agent reject <question-id>`,
  `/agent review <task-id>`, `/agent escalate <task-id>` -- six sub-commands
  under the single `/agent` slash command. `approve` and `reject` require a
  `question-id` argument (matching the upstream command table in
  architecture.md section 2.7). Users may also approve/reject via Block Kit
  button clicks in the thread (both CLI and button paths are supported per
  operator decision OQ-3).
- **App mentions.** `@AgentBot <sub-command> <args>` as an alternative
  invocation surface (same sub-commands).
- **Interactive payloads.** Block Kit button clicks and modal view submissions
  arriving at `/api/slack/interactions`.
- **Events API subscription.** `app_mention` events, URL verification
  handshake, and message events relevant to agent conversations.
- **Socket Mode.** WebSocket-based transport as the **default** for
  development and environments without public ingress. Per-workspace
  transport selection is driven by `SlackWorkspaceConfig.app_level_token_ref`:
  when present, Socket Mode is used; when absent, Events API is used
  (architecture.md section 4.2). Deployments requiring prod-scale horizontal
  scaling should configure Events API by omitting the app-level token ref.

### 2.2 Outbound Messaging

- **Threaded conversations.** Every agent task gets a dedicated Slack thread.
  The root message is the task summary; all follow-up messages are threaded
  replies.
- **Agent questions.** Rendered as Block Kit messages with action buttons
  (one per `HumanAction`). If `HumanAction.RequiresComment` is true, the
  button opens a modal with a free-text input.
- **Status updates.** Progress messages posted as threaded replies.
- **Message updates.** After a human responds, the original question message
  is updated to disable buttons and display the decision.
- **Modals.** Review and escalation flows open Slack modals via `views.open`.
  Modal submissions produce `HumanDecisionEvent` values with both `ActionValue`
  and `Comment`.

### 2.3 Data Model (Slack-specific)

- `SlackWorkspaceConfig` -- workspace registration, secret references, channel
  and user-group ACLs.
- `SlackThreadMapping` -- maps `task_id` to `(team_id, channel_id, thread_ts)`.
- `SlackInboundRequestRecord` -- processed request log for idempotency.
- `SlackAuditEntry` -- immutable record of every inbound and outbound exchange.

These entities are defined in the sibling architecture document (architecture.md
sections 3.1--3.5).

### 2.4 Security

- Slack request signature verification (HMAC SHA-256).
- Three-layer authorization: workspace, channel, user group.
- Secret management via external providers (Azure Key Vault, Kubernetes
  secrets, DPAPI).

### 2.5 Reliability

- Idempotency enforcement using Slack event ID / trigger ID as the
  idempotency key.
- Durable inbound and outbound queues (surviving connector restarts).
- Retry with exponential backoff; dead-letter queue for poison messages.
- Rate-limit handling (HTTP 429 / `Retry-After` backoff).

### 2.6 Observability

- OpenTelemetry traces (`ActivitySource`), structured logging (`ILogger<T>`),
  metrics (`System.Diagnostics.Metrics`), and ASP.NET Core health checks.

### 2.7 Audit

- Every exchange (inbound and outbound) is persisted as a `SlackAuditEntry`.
- Fields captured: team ID, channel ID, thread timestamp, user ID, command
  text, response payload, outcome.
- Queryable by correlation ID, task ID, agent ID, team/channel/user, time
  range.
- **Retention: 30 days.** A background cleanup job purges `SlackAuditEntry`
  and `SlackInboundRequestRecord` rows older than 30 days.

---

## 3. Out of Scope

The following items are explicitly excluded from this story:

| Item | Reason |
|---|---|
| Other messenger platforms (Telegram, Discord, Microsoft Teams) | Separate stories (MSG-TG-001, MSG-DC-001, MSG-MT-001) |
| Shared abstractions (`IMessengerConnector`, `AgentQuestion`, `HumanDecisionEvent`, `MessengerMessage`) | Proposed in `AgentSwarm.Messaging.Abstractions`; this story consumes them but does not define them |
| Persistence infrastructure (outbox engine, DLQ store, EF Core DbContext, migrations) | Proposed in `AgentSwarm.Messaging.Persistence` and `AgentSwarm.Messaging.Core`; this story depends on them |
| Agent orchestrator / runtime | Upstream consumer; this story emits events and receives commands but does not modify orchestrator logic |
| Slack app installation OAuth flow | Workspace registration is assumed to be a configuration step (manual or via ops tooling); the OAuth install-to-workspace flow is not built in this story |
| Slack app distribution (App Directory listing) | Internal-use app only; no public distribution |
| Multi-workspace federation (cross-workspace routing) | Each workspace is configured independently; cross-workspace message routing is deferred |
| Mobile push notification customization | Slack handles push delivery natively; no custom push logic is in scope |
| File/attachment upload from agents | Text and Block Kit messages only; file sharing is deferred |
| Slack Workflow Builder integration | Custom steps for Workflow Builder are not in scope |
| User on-boarding / self-service registration | User access is managed through Slack user group membership, not an in-app registration flow |

---

## 4. Non-Goals

These items could be mistaken for requirements based on the story description
but are intentionally not pursued:

1. **Real-time bidirectional streaming.** The interaction model is
   request/response (human issues command or clicks button; agent responds in
   thread). Continuous real-time streaming of agent internal logs to Slack is
   not a goal.

2. **Replacing the orchestrator's decision engine.** The Slack connector
   translates interactive payloads into `HumanDecisionEvent` and publishes
   them. It does not evaluate, enforce, or override decisions.

3. **Multi-tenant SaaS deployment.** The initial implementation assumes a
   single deployment serving one or more known workspaces. There is no
   tenant-isolation boundary, billing model, or per-tenant data partitioning.

4. **Backward compatibility with an existing Slack bot.** No prior Slack
   integration exists in this repository; there is no migration path to
   maintain.

5. **Custom Slack emoji or theme management.** Visual customization of the
   Slack app beyond Block Kit rendering is not a goal.

6. **Agent-to-agent communication via Slack.** Slack is a human-agent channel.
   Inter-agent messaging uses the swarm's internal bus, not Slack threads.

7. **Offline message buffering on the Slack client side.** If a user is
   offline, Slack's native notification/unread mechanisms apply. The connector
   does not implement additional delivery-receipt tracking.

---

## 5. Hard Constraints

These constraints are non-negotiable and derive from the story description,
the epic-level requirements, and the Slack platform itself.

### 5.1 Platform and Language

| Constraint | Source |
|---|---|
| Implementation in C# on .NET 8+ | Epic requirement |
| Prefer SlackNet NuGet package; fallback to Slack.NetStandard or direct `HttpClient` | Story description |
| Target solution project: `AgentSwarm.Messaging.Slack` (proposed -- see note below) | Epic solution structure |

> **Repo status.** As of this writing the repository contains no `src/`
> directory, no `.csproj` files, and no implementation code. All project and
> namespace references in this document (e.g., `AgentSwarm.Messaging.Slack`,
> `AgentSwarm.Messaging.Core`) describe the **proposed target structure** to be
> created during implementation. (See architecture.md lines 28-32.)

### 5.2 Slack Platform Constraints

| Constraint | Detail |
|---|---|
| 3-second ACK deadline | Slack requires HTTP 200 within 3 seconds of delivering a slash command, interactive payload, or Events API event. Processing must be deferred to async pipelines after the ACK. |
| `trigger_id` expiry (~3 seconds) | Modal-opening commands (`review`, `escalate`) must call `views.open` synchronously within the HTTP request lifecycle; they cannot be deferred to the outbound queue. |
| Rate limits (tiered) | Slack Web API methods are subject to per-method rate limits (e.g., `chat.postMessage` is Tier 2, ~1 req/s/channel). The connector must implement token-bucket rate limiting per API method tier. |
| Block Kit size limits | A single message may contain a maximum of 50 blocks. `text` fields have a 3000-character limit. The renderer must truncate or paginate accordingly. |
| Events API retry behavior | Slack retries events up to 3 times if HTTP 200 is not received. The connector must handle retries idempotently using `event_id`. |
| Socket Mode envelope ACK | In Socket Mode, each event envelope must be acknowledged within 5 seconds via the WebSocket connection. |
| Thread timestamp format | Slack thread parents are identified by `ts` (message timestamp). Thread replies use `thread_ts` pointing to the parent `ts`. These are strings, not numeric timestamps. |
| Max workspaces per deployment | 15 (configurable via `MaxWorkspaces` in `SlackConnectorOptions`). Socket Mode maintains one WebSocket per workspace; connection-pool sizing must respect this limit. |
| Enterprise Grid | Not supported in this story. Org-level apps and cross-workspace channels are out of scope. |

### 5.3 Shared Abstraction Contracts

The Slack connector must implement the `IMessengerConnector` interface as
proposed in `AgentSwarm.Messaging.Abstractions`:

```
IMessengerConnector
  - SendMessageAsync(MessengerMessage, CancellationToken)
  - SendQuestionAsync(AgentQuestion, CancellationToken)
  - ReceiveAsync(CancellationToken) -> IReadOnlyList<MessengerEvent>
```

The following shared data types are consumed (not owned) by this story:

- `AgentQuestion` -- carries `QuestionId`, `AgentId`, `TaskId`, `Title`,
  `Body`, `Severity`, `AllowedActions`, `ExpiresAt`, `CorrelationId`.
- `HumanAction` -- carries `ActionId`, `Label`, `Value`, `RequiresComment`.
- `HumanDecisionEvent` -- carries `QuestionId`, `ActionValue`, `Comment`,
  `Messenger`, `ExternalUserId`, `ExternalMessageId`, `ReceivedAt`,
  `CorrelationId`.
- `MessengerMessage` -- carries `MessageId`, `AgentId`, `TaskId`, `Content`,
  `MessageType`, `CorrelationId`, `Timestamp`.

### 5.4 Correlation and Tracing

Every message and event must carry:

| Field | Description |
|---|---|
| `CorrelationId` | End-to-end trace ID propagated from agent to Slack and back |
| `AgentId` | Identity of the agent involved |
| `TaskId` | Work item identifier |
| `ConversationId` | Maps to the Slack thread (`team_id:channel_id:thread_ts`) |
| `Timestamp` | UTC timestamp of the exchange |

The `CorrelationId` is the primary key for querying audit trails across the
entire system.

### 5.5 Audit Field Requirements

The story description mandates persisting these fields for every exchange:

- Slack team ID
- Channel ID
- Thread timestamp
- User ID
- Command text
- Response payload

The `SlackAuditEntry` entity (architecture.md section 3.5) satisfies all six
requirements plus additional operational fields (`correlation_id`, `agent_id`,
`task_id`, `direction`, `outcome`, `error_detail`).

### 5.6 Idempotency

Every inbound request must be deduplicated using a deterministic idempotency
key:

| Source | Key derivation |
|---|---|
| Events API | `event:{event_id}` |
| Slash commands | `cmd:{team_id}:{user_id}:{command}:{trigger_id}` |
| Interactive payloads | `interact:{team_id}:{user_id}:{action_id or view_id}:{trigger_id}` |

Duplicate requests are silently ACKed and recorded in audit with
`outcome = duplicate`.

### 5.7 Security Invariants

1. **Signature verification is mandatory.** Every inbound HTTP request must
   pass HMAC SHA-256 signature validation before any processing occurs.
   Unsigned or mis-signed requests are rejected with HTTP 401.
2. **Authorization is three-layered.** Workspace (team_id), channel
   (channel_id), and user group (user must belong to an allowed group). All
   three must pass.
3. **Secrets are never stored in configuration or logs.** Bot tokens and
   signing secrets are referenced by URI and resolved at runtime from a secret
   provider.

---

## 6. Identified Risks

### 6.1 Technical Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| TR-01 | **SlackNet library gaps.** SlackNet may not support all required Slack API features (e.g., Socket Mode, newer Block Kit elements) or may lag behind Slack API changes. | Medium | High | Evaluate SlackNet coverage during spike. Encapsulate SlackNet behind internal interfaces so that direct `HttpClient` calls can substitute for unsupported endpoints. The architecture already isolates SlackNet behind `SlackDirectApiClient` and `SlackOutboundDispatcher`. |
| TR-02 | **3-second ACK + modal fast-path complexity.** The synchronous fast-path for modal commands (`review`, `escalate`) must perform signature validation, authorization, idempotency, and `views.open` within 3 seconds. Under load, database or Slack API latency could cause timeouts. | Medium | High | Keep fast-path I/O minimal (in-memory caches for auth, connection-pooled DB for idempotency). Add circuit breaker on `views.open`. Monitor P99 of fast-path latency. Degrade gracefully with ephemeral error if deadline is exceeded. |
| TR-03 | **Rate limiting under high agent concurrency.** 100+ agents posting to Slack concurrently could exhaust Slack's per-method rate limits, causing 429 responses and message delivery delays. | High | Medium | Token-bucket rate limiter per API method tier (architecture section 2.12). Per-channel queue prioritization. Alert on sustained 429 rates. Consider batching multiple agent updates into a single message per thread at high concurrency. |
| TR-04 | **Thread sprawl.** Each agent task creates a Slack thread. With many concurrent tasks, the target channel could become noisy and difficult to navigate. | Medium | Medium | Default channel configuration per workspace. Allow per-agent or per-task-type channel routing in future iterations. Document recommended channel topology (e.g., separate channels for incidents vs. routine tasks). |
| TR-05 | **Durable queue infrastructure dependency.** The connector relies on durable inbound/outbound queues from `AgentSwarm.Messaging.Core`, which does not yet exist. | High | High | This story and the Core infrastructure must be sequenced carefully. Define the queue interface early so Slack connector development can proceed with an in-memory stub. Track as a cross-story dependency. |
| TR-06 | **Schema migration coordination.** Four new Slack-specific tables (`SlackWorkspaceConfig`, `SlackThreadMapping`, `SlackInboundRequestRecord`, `SlackAuditEntry`) require EF Core migrations. Conflicts with concurrent migrations from other messenger stories could arise. | Medium | Medium | Isolate Slack entities in their own migration set. Use a prefixed table naming convention (`slack_*`). Coordinate migration ordering at the solution level. |

### 6.2 Operational Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| OR-01 | **Slack API outage or degradation.** Slack platform incidents could prevent inbound event delivery or outbound message posting. | Low | High | Durable outbound queue retries with backoff. Health check monitors Slack API connectivity. Fallback channel for critical notifications. Alert on sustained outbound failures. |
| OR-02 | **Signing secret rotation.** Rotating the Slack signing secret requires coordinated update in the secret provider and connector restart. During the rotation window, requests signed with the old secret would be rejected. | Low | Medium | Support dual-secret validation during rotation (accept either old or new secret for a configurable grace period). Document rotation runbook. |
| OR-03 | **User group membership cache staleness.** The `SlackMembershipResolver` caches group membership with a configurable TTL (default 5 minutes). A user removed from the authorized group could retain access for up to the TTL duration. | Medium | Low | TTL is configurable. For immediate revocation, expose a cache-invalidation endpoint or admin command. Document the caching behavior in the ops runbook. |
| OR-04 | **DLQ accumulation.** Failed messages landing in the dead-letter queue require operator attention. Without monitoring, the DLQ could grow silently. | Medium | Medium | Alert on DLQ depth exceeding a threshold. Expose a DLQ inspection and replay admin API. Include DLQ depth in health checks. |

### 6.3 Dependency Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| DR-01 | **Abstractions project not yet created.** `AgentSwarm.Messaging.Abstractions` defines `IMessengerConnector`, `AgentQuestion`, `HumanDecisionEvent`, and `MessengerMessage`. The Slack connector cannot compile without it. | High | High | Define the shared interfaces as the first implementation task (or as a prerequisite story). Use interface stubs during parallel development. |
| DR-02 | **Persistence project not yet created.** `AgentSwarm.Messaging.Persistence` provides the EF Core DbContext and repository layer. Slack data model entities depend on it. | High | High | Same approach as DR-01: define the persistence contracts early. Develop with in-memory or SQLite test databases until the full persistence layer is ready. |
| DR-03 | **Orchestrator integration surface undefined.** The Slack connector publishes `HumanDecisionEvent` and receives `AgentQuestion`/`MessengerMessage` from the orchestrator, but the orchestrator's contract for accepting decisions and emitting questions is not specified in this story. | High | Medium | Define a minimal orchestrator-facing interface (`IAgentTaskService` or similar) that both sides can code against. Track as a cross-story integration contract. |

### 6.4 Security Risks

| ID | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| SR-01 | **Compromised bot token.** A leaked bot token would allow unauthorized message posting to any channel where the app is installed. | Low | Critical | Store tokens exclusively in the secret provider. Rotate on suspicion. Scope bot token permissions to the minimum required OAuth scopes (`chat:write`, `commands`, `app_mentions:read`, `users:read`, `usergroups:read`). Monitor for unexpected API usage. |
| SR-02 | **Replay attack (stale signatures).** Without clock-skew validation, an attacker could replay a captured request. | Low | High | Reject requests with timestamps older than 5 minutes (standard Slack recommendation). This is part of the signature validation logic in `SlackSignatureValidator`. |
| SR-03 | **Privilege escalation via user-group manipulation.** If an attacker gains the ability to add themselves to an authorized Slack user group, they bypass the user-group ACL. | Low | High | User group management is a Slack workspace admin function. Document that authorized group membership should be restricted to workspace admins. Consider an additional per-user allowlist for high-sensitivity commands (`approve`, `reject`). |

---

## 7. Assumptions

These assumptions underlie the design. If any prove false, the affected sections
of the architecture and implementation plan will need revision.

1. **Single Slack app per deployment.** One Slack app (with one bot token and
   one signing secret per workspace) serves all agent interactions. There is no
   requirement for per-team or per-agent Slack apps.

2. **Workspace pre-registration.** Workspaces are registered via configuration
   (`SlackWorkspaceConfig`) before the connector starts. There is no runtime
   self-service workspace onboarding.

3. **Orchestrator cache provides task context.** When rendering a review or
   status modal, the connector reads task summary data from an orchestrator
   cache (local or shared) -- not via a synchronous RPC to the orchestrator
   service. This cache-only path keeps modal rendering within the 3-second
   ACK window with minimal I/O. If the cache is cold or stale, the modal
   displays a minimal skeleton and posts an async update once the cache is
   refreshed. (Aligned with architecture.md section 5.3 which specifies
   "fetched from orchestrator cache.")

4. **Shared infrastructure projects will exist.** The `Abstractions`, `Core`,
   and `Persistence` projects referenced throughout are prerequisites.
   Development of the Slack connector may proceed in parallel using stubs, but
   integration requires these projects.

5. **Slack OAuth scopes are pre-configured.** The Slack app is installed with
   the required OAuth scopes: `chat:write`, `commands`, `app_mentions:read`,
   `users:read`, `usergroups:read`, `reactions:read` (if needed). Scope
   negotiation is not handled in code.

6. **English-only UI.** Slash command names, button labels, and error messages
   are in English. Localization is not in scope.

---

## 8. Acceptance Criteria Traceability

This section maps each acceptance criterion from the story description to the
architectural components and flows that satisfy it.

| AC | Description | Satisfying Component(s) | Flow Reference |
|---|---|---|---|
| AC-1 | User can invoke `/agent ask generate implementation plan for persistence failover` | `SlackEventsApiReceiver`, `SlackCommandHandler`, orchestrator integration | architecture.md section 5.1 |
| AC-2 | Agent creates a Slack thread with task status and follow-up questions | `SlackThreadManager`, `SlackOutboundDispatcher`, `SlackMessageRenderer` | architecture.md section 5.1, 5.2 |
| AC-3 | Human can answer via button or modal | `SlackInteractionHandler`, `SlackMessageRenderer` | architecture.md section 5.2, 5.3 |
| AC-4 | Slack event retries do not duplicate agent tasks | `SlackIdempotencyGuard`, `SlackInboundRequestRecord` | architecture.md section 5.4 |
| AC-5 | Unauthorized channels are rejected | `SlackAuthorizationFilter`, `SlackWorkspaceConfig` | architecture.md section 5.5 |
| AC-6 | Every agent/human exchange is queryable by correlation ID | `SlackAuditLogger`, `SlackAuditEntry` | architecture.md section 2.14, 3.5 |

---

## 9. Resolved Decisions (formerly Open Questions)

All questions from iteration 1 have been answered by the operator:

| ID | Decision | Detail |
|---|---|---|
| OQ-1 | Socket Mode default; Events API for prod scale | Socket Mode is the default transport (no public endpoint required). The active transport is selected per-workspace via the `SlackWorkspaceConfig.app_level_token_ref` field: when present, the connector uses Socket Mode for that workspace; when absent, it uses Events API (matching architecture.md section 4.2). Deployments expecting high-throughput or requiring horizontal scaling should omit `app_level_token_ref` and configure the Events API endpoint. |
| OQ-2 | 30-day retention | `SlackInboundRequestRecord` and `SlackAuditEntry` rows are retained for 30 days. A background cleanup job purges rows older than 30 days. |
| OQ-3 | Both CLI arg and button | `/agent approve <question-id>` and `/agent reject <question-id>` require the question-id as a CLI argument (matching architecture.md section 2.7 command table). Users may also approve/reject via Block Kit button clicks in the thread, which carry the question-id implicitly via the button's `action_id`. Both interaction paths produce a `HumanDecisionEvent`. |
| OQ-4 | 15 workspaces, configurable | Maximum workspace count defaults to 15. The limit is configurable via `MaxWorkspaces` in `SlackConnectorOptions`. |
| OQ-5 | Not in this story | Enterprise Grid support (org-level apps, cross-workspace channels) is explicitly out of scope. |
| OQ-6 | All-or-nothing access | The three-layer authorization model (workspace, channel, user-group) grants or denies access to all sub-commands uniformly. Per-sub-command role mapping is not required. |
