namespace Qq.Messaging.Abstractions;

/// <summary>
/// Supported operator commands.
/// </summary>
public enum CommandType
{
    Unknown = 0,
    Start,
    Status,
    Agents,
    Ask,
    Approve,
    Reject,
    Handoff,
    Pause,
    Resume
}

public static class CommandTypeParser
{
    private static readonly Dictionary<string, CommandType> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["/start"] = CommandType.Start,
            ["/status"] = CommandType.Status,
            ["/agents"] = CommandType.Agents,
            ["/ask"] = CommandType.Ask,
            ["/approve"] = CommandType.Approve,
            ["/reject"] = CommandType.Reject,
            ["/handoff"] = CommandType.Handoff,
            ["/pause"] = CommandType.Pause,
            ["/resume"] = CommandType.Resume,
        };

    /// <summary>
    /// Parse a slash-command string into a <see cref="CommandType"/>.
    /// Returns <see cref="CommandType.Unknown"/> for unrecognized input.
    /// </summary>
    public static CommandType Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return CommandType.Unknown;

        var token = input.Trim().Split(' ', 2)[0];
        return Map.TryGetValue(token, out var cmd) ? cmd : CommandType.Unknown;
    }
}
