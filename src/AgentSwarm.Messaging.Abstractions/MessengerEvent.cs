namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Platform-agnostic inbound event raised by a messenger connector when a human interacts
/// with the bot. The <see cref="EventType"/> discriminator (set by each subtype) selects the
/// concrete payload shape — see the canonical subtype table in architecture.md §3.1.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EventType"/> uses a <c>protected init</c> setter so subtype constructors can
/// stamp the discriminator while preventing external callers (including <c>with</c>
/// expressions originating outside the type) from corrupting the invariant that a given
/// concrete subtype carries a fixed (or constrained, in the case of <see cref="CommandEvent"/>)
/// discriminator value.
/// </para>
/// <para>
/// The other fields make up the canonical FR-004 correlation envelope: <see cref="EventId"/>,
/// <see cref="CorrelationId"/>, <see cref="Messenger"/>, <see cref="ExternalUserId"/>,
/// <see cref="ActivityId"/>, <see cref="Source"/>, and <see cref="Timestamp"/>.
/// </para>
/// </remarks>
public abstract record MessengerEvent
{
    /// <summary>
    /// Subclass-only protected constructor. The base class is abstract — concrete instances
    /// are always one of <see cref="CommandEvent"/>, <see cref="DecisionEvent"/>, or
    /// <see cref="TextEvent"/>.
    /// </summary>
    protected MessengerEvent()
    {
    }

    /// <summary>Unique event identifier.</summary>
    public required string EventId { get; init; }

    /// <summary>
    /// Discriminator value identifying which concrete <see cref="MessengerEvent"/> subtype
    /// (and, for <see cref="CommandEvent"/>, which command variant) this event represents.
    /// Stamped by subtype constructors; external code cannot mutate this field via
    /// <c>with</c> expressions because the init setter is <c>protected</c>.
    /// </summary>
    public string EventType { get; protected init; } = string.Empty;

    /// <summary>End-to-end trace ID propagated from the originating user activity.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>The messenger that emitted the event (for example, <c>"Teams"</c>).</summary>
    public required string Messenger { get; init; }

    /// <summary>External user identifier of the actor (AAD object ID for Teams).</summary>
    public required string ExternalUserId { get; init; }

    /// <summary>Underlying inbound activity ID — used for webhook deduplication.</summary>
    public string? ActivityId { get; init; }

    /// <summary>
    /// Origin context. <c>null</c> for direct messages; one of
    /// <see cref="MessengerEventSources.All"/> otherwise. See architecture.md §3.1.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>UTC time the gateway received the underlying inbound activity.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Inbound command event produced when a user invokes one of the canonical command verbs
/// (<c>agent ask</c>, <c>agent status</c>, <c>approve</c>, <c>reject</c>, <c>escalate</c>,
/// <c>pause</c>, <c>resume</c>). The <see cref="MessengerEvent.EventType"/> discriminator
/// varies based on the parsed command — see <see cref="MessengerEventTypes.CommandEventTypes"/>.
/// </summary>
public sealed record CommandEvent : MessengerEvent
{
    /// <summary>
    /// Construct a command event with the supplied discriminator. The value must be one of
    /// <see cref="MessengerEventTypes.CommandEventTypes"/>; otherwise an
    /// <see cref="ArgumentException"/> is thrown.
    /// </summary>
    /// <param name="eventType">The discriminator stamped on <see cref="MessengerEvent.EventType"/>.</param>
    /// <exception cref="ArgumentException">If <paramref name="eventType"/> is not a valid command event type.</exception>
    public CommandEvent(string eventType)
    {
        if (!MessengerEventTypes.IsCommandEventType(eventType))
        {
            throw new ArgumentException(
                $"'{eventType}' is not a valid CommandEvent type. " +
                $"Allowed values: [{string.Join(", ", MessengerEventTypes.CommandEventTypes)}].",
                nameof(eventType));
        }

        EventType = eventType;
    }

    /// <summary>The parsed command payload. Required.</summary>
    public required ParsedCommand Payload { get; init; }
}

/// <summary>
/// Inbound decision event produced when a user taps an Adaptive Card action button.
/// <see cref="MessengerEvent.EventType"/> is fixed to <see cref="MessengerEventTypes.Decision"/>.
/// </summary>
public sealed record DecisionEvent : MessengerEvent
{
    /// <summary>Initialize a decision event with the canonical Decision discriminator.</summary>
    public DecisionEvent()
    {
        EventType = MessengerEventTypes.Decision;
    }

    /// <summary>The decision payload. Required.</summary>
    public required HumanDecisionEvent Payload { get; init; }
}

/// <summary>
/// Inbound text event produced for unrecognized free-text input that does not match a
/// command pattern. <see cref="MessengerEvent.EventType"/> is fixed to
/// <see cref="MessengerEventTypes.Text"/>.
/// </summary>
public sealed record TextEvent : MessengerEvent
{
    /// <summary>Initialize a text event with the canonical Text discriminator.</summary>
    public TextEvent()
    {
        EventType = MessengerEventTypes.Text;
    }

    /// <summary>The raw text payload as received from the user. Required.</summary>
    public required string Payload { get; init; }
}
