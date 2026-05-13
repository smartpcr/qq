namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Processes inline-button callback events (<see cref="EventType.CallbackResponse"/>)
/// and the follow-up text replies when the tapped action carries
/// <see cref="HumanAction.RequiresComment"/>. The handler resolves the
/// originating <see cref="PendingQuestion"/>, builds a strongly-typed
/// <see cref="HumanDecisionEvent"/>, and publishes it via
/// <see cref="ISwarmCommandBus.PublishHumanDecisionAsync"/> — together these
/// two interfaces are the contract that converts approval/rejection button
/// taps into strongly typed agent events per the story acceptance criterion.
/// Decoupled from <see cref="ICommandRouter"/> because callbacks carry
/// their own <c>callback_data</c> payload rather than a slash command.
/// </summary>
public interface ICallbackHandler
{
    /// <summary>
    /// Process a callback or comment-reply event. Returns the
    /// <see cref="CommandResult"/> that the pipeline surfaces back to the
    /// operator (e.g. an acknowledgement); the strongly-typed
    /// <see cref="HumanDecisionEvent"/> is published as a side effect via
    /// <see cref="ISwarmCommandBus.PublishHumanDecisionAsync"/>.
    /// </summary>
    Task<CommandResult> HandleAsync(MessengerEvent messengerEvent, CancellationToken ct);
}
