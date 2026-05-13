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
}
