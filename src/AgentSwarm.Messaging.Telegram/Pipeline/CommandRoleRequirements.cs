using System.Collections.Generic;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Telegram.Pipeline;

/// <summary>
/// Maps a slash-command name to the operator role required to execute it,
/// per architecture.md §9 "Security Model": <c>/approve</c>+<c>/reject</c>
/// require the <see cref="ApproverRole"/> role; <c>/pause</c>+<c>/resume</c>
/// require the <see cref="OperatorRole"/> role; all other commands require
/// only Tier 2 binding authorization and have no role gate.
/// </summary>
internal static class CommandRoleRequirements
{
    public const string ApproverRole = "Approver";
    public const string OperatorRole = "Operator";

    private static readonly IReadOnlyDictionary<string, string> Required =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [TelegramCommands.Approve] = ApproverRole,
            [TelegramCommands.Reject] = ApproverRole,
            [TelegramCommands.Pause] = OperatorRole,
            [TelegramCommands.Resume] = OperatorRole,
        };

    /// <summary>
    /// Returns the role name required for <paramref name="commandName"/>, or
    /// <c>null</c> when the command is not role-gated.
    /// </summary>
    public static string? RequiredRole(string commandName) =>
        commandName is not null && Required.TryGetValue(commandName, out var role)
            ? role
            : null;

    /// <summary>
    /// <c>true</c> when <paramref name="operator"/> carries
    /// <paramref name="role"/> via <see cref="AuthorizedOperator.Roles"/>.
    /// Comparison is ordinal case-insensitive so configuration drift between
    /// "Approver" and "approver" cannot accidentally lock an operator out.
    /// </summary>
    public static bool HasRole(AuthorizedOperator @operator, string role) =>
        @operator.Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
}
