namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Contract for a single command handler resolved by <see cref="ICommandDispatcher"/>
/// (Stage 3.2). Each handler advertises the canonical command keyword it responds to
/// via <see cref="CommandName"/> and consumes the dispatcher-provided
/// <see cref="CommandContext"/> in <see cref="HandleAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Defined in <c>AgentSwarm.Messaging.Abstractions</c> so the contract is reusable across
/// messenger connectors (Slack, Discord, Telegram). Concrete handlers themselves are
/// allowed to depend on messenger-specific packages (the Teams handlers in
/// <c>AgentSwarm.Messaging.Teams.Commands</c> cast
/// <see cref="CommandContext.TurnContext"/> back to a Bot Framework
/// <c>ITurnContext&lt;IMessageActivity&gt;</c> to send replies).
/// </para>
/// <para>
/// Per <c>implementation-plan.md</c> §3.2 step 1 the contract is:
/// <code>
/// public interface ICommandHandler
/// {
///     string CommandName { get; }
///     Task HandleAsync(CommandContext context, CancellationToken ct);
/// }
/// </code>
/// </para>
/// </remarks>
public interface ICommandHandler
{
    /// <summary>
    /// The canonical command keyword this handler responds to (for example,
    /// <c>"agent ask"</c>, <c>"agent status"</c>, <c>"approve"</c>, <c>"reject"</c>,
    /// <c>"escalate"</c>, <c>"pause"</c>, <c>"resume"</c>). Match is performed
    /// case-insensitively on a whitespace-normalised prefix of
    /// <see cref="CommandContext.NormalizedText"/>.
    /// </summary>
    string CommandName { get; }

    /// <summary>
    /// Execute the command. Implementations should reply via the messenger-specific turn
    /// context carried on <see cref="CommandContext.TurnContext"/> and may publish
    /// downstream <see cref="MessengerEvent"/> records via an injected
    /// <see cref="IInboundEventPublisher"/>.
    /// </summary>
    /// <param name="context">Dispatcher-populated command carrier. Required.</param>
    /// <param name="ct">Cancellation token cooperating with shutdown.</param>
    Task HandleAsync(CommandContext context, CancellationToken ct);
}
