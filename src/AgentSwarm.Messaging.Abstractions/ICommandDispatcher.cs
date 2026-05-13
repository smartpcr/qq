namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Minimal command-dispatch contract injected into the messenger-specific activity handler
/// (Stage 2.2). The concrete <c>CommandDispatcher</c> implementation (Stage 3.2) parses
/// <see cref="CommandContext.NormalizedText"/> to identify the command keyword, resolves the
/// matching <c>ICommandHandler</c>, and invokes it with structured logging and correlation.
/// </summary>
/// <remarks>
/// Defined here in <c>AgentSwarm.Messaging.Abstractions</c> so the activity handler does not
/// take a compile-time dependency on the concrete dispatcher (which lives in
/// <c>AgentSwarm.Messaging.Teams</c>). Per <c>implementation-plan.md</c> §3.2, the dispatcher
/// MUST NOT perform any <c>@mention</c> stripping — it operates exclusively on the
/// pre-cleaned <see cref="CommandContext.NormalizedText"/>.
/// </remarks>
public interface ICommandDispatcher
{
    /// <summary>
    /// Dispatch the parsed command represented by <paramref name="context"/> to the matching
    /// command handler.
    /// </summary>
    /// <param name="context">Carrier populated by the activity handler with the normalized text and resolved user identity.</param>
    /// <param name="ct">Cancellation token co-operating with shutdown.</param>
    /// <returns>A task that completes when the chosen command handler has finished.</returns>
    Task DispatchAsync(CommandContext context, CancellationToken ct);
}
