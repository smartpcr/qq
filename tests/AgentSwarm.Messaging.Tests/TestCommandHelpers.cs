using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Shared test helpers for Stage 3.2 command-handler tests:
/// <see cref="AuthorizedOperator"/> defaults and a small
/// <see cref="ParsedCommand"/> builder that mirrors what
/// <c>TelegramCommandParser</c> produces. Centralized here so each
/// handler test stays focused on its own behaviour.
/// </summary>
internal static class TestOperator
{
    public static AuthorizedOperator Default => new()
    {
        OperatorId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        TenantId = "t-acme",
        WorkspaceId = "w-1",
        Roles = new[] { "operator" },
        TelegramUserId = 42,
        TelegramChatId = 100,
        OperatorAlias = "@operator-1",
    };
}

internal static class TestCommands
{
    /// <summary>
    /// Parse a slash command roughly the way
    /// <c>TelegramCommandParser</c> does, but without spinning up the
    /// real parser — handler tests only need command name + args.
    /// </summary>
    public static ParsedCommand Parse(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        var trimmed = raw.TrimStart('/').Trim();
        var pieces = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = pieces.Length == 0 ? string.Empty : pieces[0];
        var args = pieces.Length <= 1 ? Array.Empty<string>() : pieces[1..];
        return new ParsedCommand
        {
            CommandName = name,
            Arguments = args,
            RawText = raw,
            IsValid = true,
        };
    }

    public static ParsedCommand Build(string name, params string[] args) => new()
    {
        CommandName = name,
        Arguments = args,
        RawText = "/" + name + (args.Length == 0 ? "" : " " + string.Join(' ', args)),
        IsValid = true,
    };
}
