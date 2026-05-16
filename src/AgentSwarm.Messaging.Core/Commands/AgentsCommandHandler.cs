// -----------------------------------------------------------------------
// <copyright file="AgentsCommandHandler.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core.Commands;

using System.Globalization;
using System.Text;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles <c>/agents</c>. Queries
/// <see cref="ISwarmCommandBus.QueryAgentsAsync"/> for the workspace the
/// operator is currently scoped to (or an explicit workspace argument)
/// and returns the formatted agent roster.
/// </summary>
/// <remarks>
/// <para>
/// <b>Multi-workspace disambiguation is the pipeline's responsibility</b>
/// (per <c>TelegramUpdatePipeline.ExecuteAsync</c>'s resolve-operator
/// stage and the iter-2 evaluator pins on items 1ΓÇô3). When an operator
/// has more than one <c>OperatorBinding</c> AND types <c>/agents</c>
/// with NO argument, the pipeline emits the workspace-selection prompt
/// using the durable
/// <see cref="PendingDisambiguation"/> server-side handle (callback
/// wire format <c>ws:&lt;token&gt;:&lt;index&gt;</c>) and this handler
/// is NEVER invoked for that case. The pipeline contract handles the
/// callback (Stage 3.3) and re-issues the original command bound to the
/// chosen workspace; at re-issue time the handler runs with
/// <see cref="AuthorizedOperator.WorkspaceId"/> already pointing at the
/// chosen workspace.
/// </para>
/// <para>
/// <b>Authorization scope for explicit workspace args.</b> When the
/// operator supplies <c>/agents WORKSPACE</c>, the handler verifies the
/// operator actually has a binding in that workspace via
/// <see cref="IOperatorRegistry.GetAllBindingsAsync"/> ΓÇö otherwise a
/// hostile actor with one binding could enumerate any workspace's
/// agent roster by typing <c>/agents OTHER-WORKSPACE</c>.
/// </para>
/// </remarks>
public sealed class AgentsCommandHandler : ICommandHandler
{
    public const string UnauthorizedWorkspaceTemplate =
        "Γ¥î You do not have access to workspace {0}.";

    public const string EmptyRosterTemplate =
        "Workspace {0} has no active agents.";

    private readonly ISwarmCommandBus _bus;
    private readonly IOperatorRegistry _registry;
    private readonly ILogger<AgentsCommandHandler> _logger;

    public AgentsCommandHandler(
        ISwarmCommandBus bus,
        IOperatorRegistry registry,
        ILogger<AgentsCommandHandler> logger)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string CommandName => TelegramCommands.Agents;

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(
        ParsedCommand command,
        AuthorizedOperator @operator,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(@operator);

        var explicitWorkspace = command.Arguments.Count > 0 ? command.Arguments[0] : null;

        if (string.IsNullOrWhiteSpace(explicitWorkspace))
        {
            // No workspace argument. The pipeline already resolved the
            // operator to a single workspace (either directly when the
            // operator has a single binding, or via the multi-binding
            // disambiguation prompt + Stage 3.3 callback re-issue path).
            // Query the swarm for that workspace.
            return await QueryAndFormatAsync(@operator, @operator.WorkspaceId, ct).ConfigureAwait(false);
        }

        // Operator explicitly named a workspace. Verify they have access
        // to it via GetAllBindingsAsync ΓÇö refusing to enumerate workspaces
        // the operator is not bound to.
        var operatorBindings = await _registry
            .GetAllBindingsAsync(@operator.TelegramUserId, ct)
            .ConfigureAwait(false);
        var hasBinding = operatorBindings.Any(b =>
            string.Equals(b.WorkspaceId, explicitWorkspace, StringComparison.Ordinal));
        if (!hasBinding)
        {
            _logger.LogWarning(
                "AgentsCommandHandler rejected unauthorized workspace. OperatorId={OperatorId} RequestedWorkspaceId={WorkspaceId}",
                @operator.OperatorId,
                explicitWorkspace);
            return new CommandResult
            {
                Success = false,
                ResponseText = string.Format(
                    CultureInfo.InvariantCulture,
                    UnauthorizedWorkspaceTemplate,
                    explicitWorkspace),
                ErrorCode = "agents_unauthorized_workspace",
                CorrelationId = Guid.NewGuid().ToString("N"),
            };
        }

        return await QueryAndFormatAsync(@operator, explicitWorkspace, ct).ConfigureAwait(false);
    }

    private async Task<CommandResult> QueryAndFormatAsync(
        AuthorizedOperator @operator,
        string workspaceId,
        CancellationToken ct)
    {
        var query = new SwarmAgentsQuery { WorkspaceId = workspaceId };
        _logger.LogInformation(
            "AgentsCommandHandler querying swarm agents. OperatorId={OperatorId} WorkspaceId={WorkspaceId}",
            @operator.OperatorId,
            workspaceId);
        var agents = await _bus.QueryAgentsAsync(query, ct).ConfigureAwait(false);

        return new CommandResult
        {
            Success = true,
            ResponseText = FormatRoster(workspaceId, agents),
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
    }

    /// <summary>
    /// Renders the agent roster for <paramref name="workspaceId"/> as a
    /// human-readable text block. Public so unit tests can pin the
    /// exact formatting.
    /// </summary>
    public static string FormatRoster(string workspaceId, IReadOnlyList<AgentInfo> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        if (agents.Count == 0)
        {
            return string.Format(CultureInfo.InvariantCulture, EmptyRosterTemplate, workspaceId);
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Agents in workspace {workspaceId} ({agents.Count}):");
        foreach (var agent in agents)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\nΓÇó {agent.AgentId} [{agent.Role}] ΓÇö {agent.State}");
            if (!string.IsNullOrWhiteSpace(agent.CurrentTaskId))
            {
                sb.Append(CultureInfo.InvariantCulture, $" (task {agent.CurrentTaskId})");
            }
        }
        return sb.ToString();
    }
}