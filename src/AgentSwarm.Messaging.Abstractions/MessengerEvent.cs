namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Base type for all inbound events surfaced by a messenger connector via
/// <see cref="IMessengerConnector.ReceiveAsync"/>. Concrete subtypes (e.g.,
/// <see cref="HumanDecisionEvent"/>) carry payload-specific fields.
/// </summary>
/// <remarks>
/// COMPILE STUB. The canonical hierarchy is owned by the upstream
/// Abstractions story. Section 3.6 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/architecture.md</c> does not
/// pin any fields on the base type, so this stub is intentionally empty
/// and exists only to give <see cref="IMessengerConnector.ReceiveAsync"/>
/// a polymorphic return shape. Additional event subtypes (free-form chat,
/// command invocation, etc.) will derive from it later.
/// </remarks>
public abstract record MessengerEvent;
