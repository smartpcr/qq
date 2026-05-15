using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Telegram.Pipeline.Stubs;

/// <summary>
/// Stage 2.2 stub <see cref="ICommandParser"/>. Provides a minimal slash-command
/// tokenizer so the pipeline can run end-to-end before Stage 3.1 ships the
/// real <c>TelegramCommandParser</c>. Recognises the
/// <see cref="TelegramCommands"/> vocabulary (case-insensitive) and strips
/// the trailing <c>@botname</c> suffix Telegram may append in group chats.
/// </summary>
internal sealed class StubCommandParser : ICommandParser
{
    private static readonly char[] WhitespaceSeparators = new[] { ' ', '\t', '\r', '\n' };

    public ParsedCommand Parse(string messageText)
    {
        if (string.IsNullOrWhiteSpace(messageText))
        {
            return new ParsedCommand
            {
                CommandName = string.Empty,
                RawText = messageText ?? string.Empty,
                IsValid = false,
                ValidationError = "Message text is null or whitespace.",
            };
        }

        var trimmed = messageText.Trim();
        if (!trimmed.StartsWith('/'))
        {
            return new ParsedCommand
            {
                CommandName = string.Empty,
                RawText = messageText,
                IsValid = false,
                ValidationError = "Message does not start with '/'.",
            };
        }

        var tokens = trimmed[1..].Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return new ParsedCommand
            {
                CommandName = string.Empty,
                RawText = messageText,
                IsValid = false,
                ValidationError = "No command name after '/'.",
            };
        }

        var head = tokens[0];
        var atIndex = head.IndexOf('@');
        var name = (atIndex >= 0 ? head[..atIndex] : head).ToLowerInvariant();
        var args = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();

        var isKnown = TelegramCommands.IsKnown(name);
        return new ParsedCommand
        {
            CommandName = name,
            Arguments = args,
            RawText = messageText,
            IsValid = isKnown,
            ValidationError = isKnown ? null : $"Unknown command: '{name}'.",
        };
    }
}
