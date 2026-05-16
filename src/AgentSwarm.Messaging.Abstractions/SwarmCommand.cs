namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// A validated, strongly-typed command produced by a messenger connector's
/// slash-command dispatcher and forwarded to the swarm orchestrator via
/// <c>ISwarmCommandBus.PublishCommandAsync</c>.
/// </summary>
/// <param name="CommandId">Unique identifier for this command invocation.</param>
/// <param name="CommandType">
/// Logical command name (e.g. <c>"ask"</c>, <c>"approve"</c>, <c>"reject"</c>,
/// <c>"status"</c>). Matches the subcommand under the <c>/agent</c> group.
/// </param>
/// <param name="AgentTarget">
/// Target agent identifier or selector string. May be a specific agent id, a role
/// name, or a wildcard depending on <paramref name="CommandType"/>.
/// </param>
/// <param name="Arguments">
/// Command-specific arguments captured as string key/value pairs. Keys are
/// case-sensitive. The caller-supplied dictionary is defensively copied at
/// construction into a read-only wrapper that cannot be downcast back to
/// <see cref="Dictionary{TKey, TValue}"/>.
/// </param>
/// <param name="CorrelationId">End-to-end trace identifier.</param>
/// <param name="Timestamp">When the command was emitted by the dispatcher.</param>
public sealed record SwarmCommand(
    Guid CommandId,
    string CommandType,
    string AgentTarget,
    IReadOnlyDictionary<string, string> Arguments,
    string CorrelationId,
    DateTimeOffset Timestamp)
{
    private readonly IReadOnlyDictionary<string, string> _arguments =
        ImmutableSnapshot.FromRequiredStringMap(Arguments, nameof(Arguments));

    /// <inheritdoc cref="SwarmCommand(Guid, string, string, IReadOnlyDictionary{string, string}, string, DateTimeOffset)"/>
    public IReadOnlyDictionary<string, string> Arguments
    {
        get => _arguments;
        init => _arguments = ImmutableSnapshot.FromRequiredStringMap(value, nameof(Arguments));
    }
}

