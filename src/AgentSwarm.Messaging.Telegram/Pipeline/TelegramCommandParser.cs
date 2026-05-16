using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Telegram.Pipeline;

/// <summary>
/// Stage 3.1 production <see cref="ICommandParser"/>. Converts raw Telegram
/// message text into a strongly-typed <see cref="ParsedCommand"/> by:
/// <list type="bullet">
///   <item>tokenising the leading <c>/command</c> off the message head;</item>
///   <item>stripping the Bot API <c>@botname</c> suffix Telegram appends to
///         every command in group chats (<c>/status@MyBot</c> →
///         <c>status</c>);</item>
///   <item>normalising the command name to lower-case so downstream
///         dispatchers can pattern-match against the constants in
///         <see cref="TelegramCommands"/> without per-callsite
///         case-insensitive comparisons;</item>
///   <item>preserving the remainder of the line verbatim as the
///         arguments list and the joined argument text — the
///         <see cref="ParsedCommand.Arguments"/> shape is
///         <see cref="IReadOnlyList{T}"/> of whitespace tokens
///         (matching the Stage 2.2 stub contract that
///         <see cref="Pipeline.TelegramUpdatePipeline"/> already relies on)
///         while the full free-form payload remains recoverable via
///         <see cref="ParsedCommand.RawText"/> for handlers such as
///         <c>AskCommandHandler</c> that need the unmodified body;</item>
///   <item>rejecting non-command / empty / unknown-command messages with a
///         human-readable <see cref="ParsedCommand.ValidationError"/>;</item>
///   <item>rejecting commands whose semantics REQUIRE at least one
///         argument with no payload — for this stage that is just
///         <c>/ask</c> (Stage 3.2 handlers do their own richer usage-help
///         validation for the other commands, per implementation-plan.md
///         §3.2 — e.g. <c>HandoffCommandHandler</c> emits
///         <c>"Usage: /handoff TASK-ID @operator-alias"</c> from the
///         handler itself, not from the parser).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Replaces <see cref="Stubs.StubCommandParser"/> from Stage 2.2 via
/// last-wins registration in
/// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/>.
/// The implementation is stateless and reentrant; a single instance is
/// shared by the singleton inbound pipeline.
/// </para>
/// <para>
/// <b>Bot-mention stripping.</b> Telegram clients append <c>@&lt;bot
/// username&gt;</c> to a command when more than one bot is present in a
/// group chat (e.g. <c>/status@MyBot</c>). Per Bot API the username
/// matches <c>[A-Za-z][A-Za-z0-9_]{4,31}</c>; the suffix is always
/// directly attached to the command token with no whitespace, so we
/// take everything before the first <c>@</c> in the head token. The
/// suffix may also appear on the head token even when arguments follow
/// (<c>/handoff@MyBot TASK-99 @alice</c>) — we ONLY strip the suffix
/// on the head and leave any later <c>@</c>-prefixed tokens
/// (operator aliases, mentions) intact in the arguments list.
/// </para>
/// <para>
/// <b>Nullable input.</b> The <paramref name="messageText"/> parameter on
/// <see cref="Parse"/> is declared <see cref="string"/>? deliberately:
/// Telegram <c>Update.Message.Text</c> is nullable on the wire (a
/// caption-only photo, a sticker, a service message, or a join/leave
/// notification all surface with no <c>text</c> field), so callers
/// pulling the field straight off the deserialised payload must be
/// able to hand it through without a <c>!</c> suppression. The method
/// normalises null to an "empty / not a command" rejection via the
/// same <see cref="string.IsNullOrWhiteSpace"/> gate used for empty
/// strings. The interface declaration in
/// <c>AgentSwarm.Messaging.Abstractions.ICommandParser</c> should be
/// updated to a nullable parameter to match this contract; until
/// then the compiler may surface CS8767 (nullability of parameter
/// doesn't match the implicitly implemented interface member), which
/// is the intended prompt for that paired interface change.
/// </para>
/// </remarks>
internal sealed class TelegramCommandParser : ICommandParser
{
    private static readonly char[] WhitespaceSeparators = new[] { ' ', '\t', '\r', '\n' };

    /// <summary>
    /// Commands whose payload is REQUIRED for the command to be
    /// syntactically valid. Per implementation-plan.md §3.1 Test
    /// Scenarios this is only <c>/ask</c> — the brief explicitly calls
    /// out <c>/ask</c> with no text as the parser-level rejection case,
    /// and Stage 3.2 assigns argument-shape validation for the other
    /// commands (e.g. <c>/handoff</c> needs <c>TASK-ID @alias</c>) to
    /// the corresponding command handler so it can return a
    /// handler-specific usage-help message.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ArgumentRequiredCommands =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [TelegramCommands.Ask] = "/ask requires a task description. Usage: /ask <task description>.",
        };

    public ParsedCommand Parse(string? messageText)
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
                ValidationError = "Message is not a command (must start with '/').",
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
                ValidationError = "Message contains no command name after '/'.",
            };
        }

        var head = tokens[0];
        var atIndex = head.IndexOf('@');
        var rawName = atIndex >= 0 ? head[..atIndex] : head;

        if (rawName.Length == 0)
        {
            return new ParsedCommand
            {
                CommandName = string.Empty,
                RawText = messageText,
                IsValid = false,
                ValidationError = "Bot mention suffix is present but the command name is empty.",
            };
        }

        var commandName = rawName.ToLowerInvariant();
        var args = tokens.Length > 1
            ? (IReadOnlyList<string>)tokens[1..]
            : Array.Empty<string>();

        if (!TelegramCommands.IsKnown(commandName))
        {
            return new ParsedCommand
            {
                CommandName = commandName,
                Arguments = args,
                RawText = messageText,
                IsValid = false,
                ValidationError = $"Unknown command '/{commandName}'. Supported commands: "
                    + string.Join(", ", TelegramCommands.All.Select(c => "/" + c)) + ".",
            };
        }

        if (ArgumentRequiredCommands.TryGetValue(commandName, out var usageError) && args.Count == 0)
        {
            return new ParsedCommand
            {
                CommandName = commandName,
                Arguments = args,
                RawText = messageText,
                IsValid = false,
                ValidationError = usageError,
            };
        }

        return new ParsedCommand
        {
            CommandName = commandName,
            Arguments = args,
            RawText = messageText,
            IsValid = true,
        };
    }
}
