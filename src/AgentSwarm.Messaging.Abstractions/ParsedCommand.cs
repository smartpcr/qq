namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Result of parsing a raw inbound text payload into a command name and
/// arguments. Returned by <see cref="ICommandParser.Parse(string)"/>.
/// </summary>
public sealed record ParsedCommand
{
    /// <summary>The command name without leading <c>/</c>, e.g. <c>status</c>.</summary>
    public required string CommandName { get; init; }

    /// <summary>Positional arguments after the command name.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>The raw message text the operator typed.</summary>
    public required string RawText { get; init; }

    /// <summary>
    /// <c>true</c> when the parser successfully identified a known command.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Human-readable reason for an invalid parse; <c>null</c> when
    /// <see cref="IsValid"/> is <c>true</c>.
    /// </summary>
    public string? ValidationError { get; init; }

    /// <summary>
    /// Stage 5.3 iter-6 evaluator item 3 — identifier of the inbound
    /// transport message that carried this command (Telegram
    /// <c>update_id</c>, propagated by the pipeline via
    /// <c>MessengerEvent.EventId</c>). Surfaced onto the
    /// <c>audit_logs.MessageId</c> column by
    /// <see cref="ICommandRouter"/> so command audit rows are
    /// joinable back to the originating inbound update — the prior
    /// router hard-coded <c>MessageId = null</c> which violated the
    /// Stage 5.3 brief's "full context" requirement.
    /// </summary>
    /// <remarks>
    /// Nullable so the parser (which has no transport context) and
    /// any caller that constructs a <see cref="ParsedCommand"/>
    /// outside the inbound pipeline (tests, programmatic dispatch)
    /// continue to work unchanged. The pipeline populates it via
    /// the record's <c>with</c> expression before calling
    /// <see cref="ICommandRouter.RouteAsync"/>.
    /// </remarks>
    public string? SourceMessageId { get; init; }

    /// <summary>
    /// Stage 5.3 iter-6 evaluator item 2 — inbound trace id propagated
    /// from <c>MessengerEvent.CorrelationId</c> by the pipeline. Used
    /// by <see cref="ICommandRouter"/> as the audit-row correlation
    /// id so the pre-handler "command receipt" row is joinable to
    /// every other artifact emitted for the same inbound update
    /// (pipeline logs, dedup record, outbound message). Nullable so
    /// callers outside the pipeline (tests, programmatic dispatch)
    /// continue to work unchanged — the router falls back to a
    /// freshly-minted GUID when this is missing so the persistence
    /// layer's non-null correlation contract is never violated.
    /// </summary>
    public string? TraceId { get; init; }
}
