# AgentSwarm.Messaging.Abstractions

Shared contracts (interfaces, records, enums) consumed by every messaging
connector in the AgentSwarm gateway (Slack, Telegram, Discord, Teams).

## ΓÜá∩╕Å Stage 1.2 status: compile stubs only

Every public type in this project is currently a **compile-target stub**
introduced by the Slack story
(`qq-SLACK-MESSENGER-SUPP`, stage `prerequisite-abstraction-compile-stubs`).
They exist solely to unblock compilation of the platform connectors ΓÇö
notably `AgentSwarm.Messaging.Slack` ΓÇö while the canonical Abstractions
story is still in flight.

**Do not treat any type in this assembly as a stable contract yet.**
The upstream Abstractions story will replace these declarations with the
authoritative versions. Property names match the field lists in
`docs/stories/qq-SLACK-MESSENGER-SUPP/architecture.md` section 3.6 so that
the swap is mechanical; types (especially `Severity` and event/message
hierarchies) may shift.

## Stub inventory

| File | Source contract |
|---|---|
| `IMessengerConnector.cs` | architecture.md ┬º4.1 ΓÇö `SendMessageAsync`, `SendQuestionAsync`, `ReceiveAsync` |
| `MessengerMessage.cs` | architecture.md ┬º3.6.4 ΓÇö `MessageId, AgentId, TaskId, Content, MessageType, CorrelationId, Timestamp` |
| `AgentQuestion.cs` | architecture.md ┬º3.6.1 ΓÇö `QuestionId, AgentId, TaskId, Title, Body, Severity, AllowedActions, ExpiresAt, CorrelationId` |
| `HumanAction.cs` | architecture.md ┬º3.6.2 ΓÇö `ActionId, Label, Value, RequiresComment` |
| `HumanDecisionEvent.cs` | architecture.md ┬º3.6.3 ΓÇö `QuestionId, ActionValue, Comment, Messenger, ExternalUserId, ExternalMessageId, ReceivedAt, CorrelationId` |
| `MessengerEvent.cs` | architecture.md ┬º4.1 ΓÇö abstract base of inbound events returned from `ReceiveAsync` |
| `MessageType.cs` | implementation-plan.md Stage 4 ΓÇö `Unspecified`, `StatusUpdate`, `Completion`, `Error` |

## What replaces these stubs

When the upstream Abstractions story lands, the canonical assembly will
provide the authoritative versions of these types. At that point this
project's source files should be deleted in favour of a `PackageReference`
or `ProjectReference` to the canonical assembly, and consumers
(`AgentSwarm.Messaging.Slack` first) should rebuild without further code
changes.

If you discover a connector relying on a property name, default value, or
nullability that is *not* spelled out in `architecture.md` section 3.6,
treat that as a bug in the connector ΓÇö it is binding to a stub-specific
quirk that will not survive the canonical swap.
