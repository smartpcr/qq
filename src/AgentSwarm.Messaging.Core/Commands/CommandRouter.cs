namespace AgentSwarm.Messaging.Core.Commands;

using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Production <see cref="ICommandRouter"/>. Builds an
/// ordinal-case-insensitive dictionary of
/// <see cref="ICommandHandler.CommandName"/> → handler at construction
/// time and dispatches each <see cref="ParsedCommand"/> to the matching
/// entry. An unknown command returns a <see cref="CommandResult"/> with
/// <see cref="CommandResult.Success"/>=<c>false</c> and a help text
/// listing the recognized command vocabulary from
/// <see cref="TelegramCommands.All"/> so the operator is told exactly
/// what to type instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stage 3.2 acceptance criterion.</b> "Unknown command rejected —
/// Given a <see cref="ParsedCommand"/> with <c>CommandName</c> =
/// <c>foo</c>, When routed, Then the result has
/// <see cref="CommandResult.Success"/>=<c>false</c> and a helpful error
/// message listing valid commands." The router emits
/// <see cref="UnknownCommandErrorCode"/> as the
/// <see cref="CommandResult.ErrorCode"/> so log queries and
/// alerting can pivot on the machine-readable identifier without
/// pattern-matching the human text.
/// </para>
/// <para>
/// <b>Duplicate handlers.</b> Two handlers advertising the same
/// <see cref="ICommandHandler.CommandName"/> indicate a wiring bug —
/// the router throws <see cref="InvalidOperationException"/> at
/// construction time to fail fast rather than silently picking one and
/// dropping the other. The case-insensitive comparison also catches
/// the <c>"Approve"</c> vs <c>"approve"</c> drift the
/// <see cref="TelegramCommands.IsKnown"/> contract notes.
/// </para>
/// </remarks>
public sealed class CommandRouter : ICommandRouter
{
    /// <summary>
    /// Machine-readable <see cref="CommandResult.ErrorCode"/> surfaced
    /// when <see cref="RouteAsync"/> receives a <see cref="ParsedCommand"/>
    /// whose <see cref="ParsedCommand.CommandName"/> is not in the
    /// dispatch table. Pinned as a constant so log queries and the
    /// pipeline-level test suite can reference it without duplicating
    /// the literal.
    /// </summary>
    public const string UnknownCommandErrorCode = "unknown_command";

    private readonly IReadOnlyDictionary<string, ICommandHandler> _handlers;
    private readonly ILogger<CommandRouter> _logger;

    public CommandRouter(
        IEnumerable<ICommandHandler> handlers,
        ILogger<CommandRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var dict = new Dictionary<string, ICommandHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            if (handler is null)
            {
                throw new ArgumentException(
                    "ICommandHandler enumeration must not contain null entries.",
                    nameof(handlers));
            }
            if (string.IsNullOrWhiteSpace(handler.CommandName))
            {
                throw new ArgumentException(
                    $"ICommandHandler {handler.GetType().FullName} returned a blank CommandName.",
                    nameof(handlers));
            }
            if (!dict.TryAdd(handler.CommandName, handler))
            {
                var existing = dict[handler.CommandName];
                throw new InvalidOperationException(
                    $"Duplicate ICommandHandler registration for command '{handler.CommandName}': "
                    + $"{existing.GetType().FullName} vs {handler.GetType().FullName}. "
                    + "Each command name must have exactly one handler.");
            }
        }
        _handlers = dict;
    }

    /// <inheritdoc />
    public async Task<CommandResult> RouteAsync(
        ParsedCommand command,
        AuthorizedOperator @operator,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(@operator);

        if (string.IsNullOrWhiteSpace(command.CommandName)
            || !_handlers.TryGetValue(command.CommandName, out var handler))
        {
            _logger.LogWarning(
                "CommandRouter received unknown command. Command={Command} OperatorId={OperatorId}",
                command.CommandName,
                @operator.OperatorId);

            return new CommandResult
            {
                Success = false,
                ResponseText = BuildUnknownCommandReply(command.CommandName),
                ErrorCode = UnknownCommandErrorCode,
                CorrelationId = Guid.NewGuid().ToString("N"),
            };
        }

        return await handler.HandleAsync(command, @operator, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the operator-facing reply for an unknown command. Lists every
    /// recognized command name (with a leading <c>/</c>) so the operator
    /// has the complete vocabulary in front of them without consulting
    /// external docs. Centralized here so tests can pin the exact text
    /// without duplicating the join logic.
    /// </summary>
    public static string BuildUnknownCommandReply(string? commandName)
    {
        var available = string.Join(", ", TelegramCommands.All.Select(c => "/" + c));
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return $"Command not recognized. Available commands: {available}.";
        }
        return $"Command not recognized: /{commandName}. Available commands: {available}.";
    }
}
