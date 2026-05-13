namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Canonical vocabulary of slash commands the Telegram connector understands.
/// Implements the supported-command set from the story brief (<c>/start</c>,
/// <c>/status</c>, <c>/agents</c>, <c>/ask</c>, <c>/approve</c>,
/// <c>/reject</c>, <c>/handoff</c>, <c>/pause</c>, <c>/resume</c>). The
/// inbound <see cref="ICommandParser"/> populates
/// <see cref="ParsedCommand.CommandName"/> with one of these values; the
/// <see cref="ICommandRouter"/> dispatches by switching on them. Centralizing
/// the strings here prevents typo-driven dispatch bugs across the codebase.
/// </summary>
public static class TelegramCommands
{
    public const string Start = "start";
    public const string Status = "status";
    public const string Agents = "agents";
    public const string Ask = "ask";
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Handoff = "handoff";
    public const string Pause = "pause";
    public const string Resume = "resume";

    /// <summary>The complete set of recognized command names.</summary>
    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        Start, Status, Agents, Ask, Approve, Reject, Handoff, Pause, Resume
    };

    /// <summary>
    /// <c>true</c> when <paramref name="commandName"/> is one of the
    /// recognized commands. Comparison is ordinal case-insensitive because
    /// Telegram client UIs lowercase typed commands but bot mentions may
    /// preserve case (<c>/Status@MyBot</c>).
    /// </summary>
    public static bool IsKnown(string? commandName) =>
        commandName is not null &&
        All.Contains(commandName, StringComparer.OrdinalIgnoreCase);
}
