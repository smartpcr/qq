namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Structured representation of a free-text command parsed from an inbound message. Produced
/// by the messenger-specific command parser and carried as the typed payload of
/// <see cref="CommandEvent"/>. Aligned with architecture.md §3.1 / §2 CommandParser output.
/// </summary>
/// <param name="CommandType">Canonical command vocabulary value (for example, <c>agent ask</c>, <c>agent status</c>, <c>approve</c>, <c>reject</c>, <c>escalate</c>, <c>pause</c>, <c>resume</c>).</param>
/// <param name="Payload">Remaining text after the command keyword (for example, the task description following <c>agent ask</c>); empty for parameterless commands.</param>
/// <param name="CorrelationId">End-to-end trace ID assigned to the parsed command.</param>
public sealed record ParsedCommand(
    string CommandType,
    string Payload,
    string CorrelationId);
