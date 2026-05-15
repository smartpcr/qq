using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Telegram.Pipeline.Stubs;

/// <summary>
/// Stage 2.2 stub <see cref="ICommandRouter"/>. Returns a static
/// acknowledgement so the pipeline can finish a Command flow before Phase 3
/// registers the real <c>CommandRouter</c> + per-command
/// <c>ICommandHandler</c> implementations.
/// </summary>
/// <remarks>
/// <para>
/// The interface forces <see cref="CommandResult.CorrelationId"/> to be a
/// non-empty string (validated at construction). The router has no access
/// to the originating <see cref="MessengerEvent.CorrelationId"/> through
/// the current Stage 1.3 contract, so we emit
/// <see cref="StubCorrelationId"/> as a non-empty placeholder. Callers
/// MUST surface the originating event's correlation id in their own logs
/// (the pipeline already does so via <c>PipelineResult.CorrelationId</c>).
/// </para>
/// </remarks>
internal sealed class StubCommandRouter : ICommandRouter
{
    public const string StubCorrelationId = "stub-router";

    public Task<CommandResult> RouteAsync(
        ParsedCommand command,
        AuthorizedOperator @operator,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(@operator);

        return Task.FromResult(new CommandResult
        {
            Success = true,
            ResponseText = $"[stub] /{command.CommandName} accepted; concrete handler pending.",
            CorrelationId = StubCorrelationId,
        });
    }
}
