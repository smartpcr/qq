namespace AgentSwarm.Messaging.Core.Commands;

using System.Globalization;
using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Shared body for <c>/pause</c> and <c>/resume</c>. Both commands take
/// a single positional argument — either an <c>AGENT-ID</c> (single-agent
/// scope) or the literal <c>all</c> (workspace-wide fan-out scoped to
/// the operator's <see cref="AuthorizedOperator.WorkspaceId"/>) — and
/// emit a <see cref="SwarmCommand"/> on <see cref="ISwarmCommandBus"/>
/// with the appropriate <see cref="SwarmCommand.CommandType"/>,
/// <see cref="SwarmCommand.AgentId"/>, <see cref="SwarmCommand.WorkspaceId"/>
/// and <see cref="SwarmCommand.Scope"/>. The architecture.md §5 command
/// table specifies <c>/pause AGENT-ID</c> | <c>/pause all</c> and
/// <c>/resume AGENT-ID</c> | <c>/resume all</c>; the orchestrator
/// receives the structured fields rather than re-parsing the raw text.
/// Sharing the orchestration here makes the contract a single edit away
/// when the team adds e.g. a comment-payload override or a per-task scope.
/// </summary>
public abstract class LifecycleCommandHandlerBase : ICommandHandler
{
    /// <summary>
    /// Literal token the operator types to fan out the command to every
    /// agent in their workspace. Case-insensitive match per architecture
    /// (<c>/pause all</c>, <c>/PAUSE All</c>, <c>/pause ALL</c> all match).
    /// </summary>
    public const string AllScopeToken = "all";

    public const string MissingTargetMessage =
        "Usage: `/{0} <agentId>` or `/{0} all` — name the agent to {0} or use `all` for every agent in your workspace.";

    public const string SingleConfirmationTemplate =
        "✅ {0} signal sent to agent {1}. Correlation: {2}";

    public const string AllConfirmationTemplate =
        "✅ {0} signal sent to all agents in workspace {1}. Correlation: {2}";

    private readonly ISwarmCommandBus _bus;
    private readonly ILogger _logger;

    protected LifecycleCommandHandlerBase(
        ISwarmCommandBus bus,
        ILogger logger)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public abstract string CommandName { get; }

    /// <summary>
    /// Canonical <see cref="SwarmCommand.CommandType"/> the concrete
    /// handler emits (e.g. <see cref="SwarmCommandType.Pause"/>).
    /// </summary>
    protected abstract string SwarmCommandTypeValue { get; }

    /// <summary>
    /// Display-friendly verb used in the operator-facing confirmation
    /// (e.g. <c>"Pause"</c>). Distinct from <see cref="CommandName"/>
    /// (which is lowercase) so the surfaced text reads naturally.
    /// </summary>
    protected abstract string DisplayVerb { get; }

    /// <inheritdoc />
    public async Task<CommandResult> HandleAsync(
        ParsedCommand command,
        AuthorizedOperator @operator,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(@operator);

        if (command.Arguments.Count == 0
            || string.IsNullOrWhiteSpace(command.Arguments[0]))
        {
            return new CommandResult
            {
                Success = false,
                ResponseText = string.Format(
                    CultureInfo.InvariantCulture,
                    MissingTargetMessage,
                    CommandName),
                ErrorCode = $"{CommandName}_missing_target",
                CorrelationId = Guid.NewGuid().ToString("N"),
            };
        }

        var target = command.Arguments[0].Trim();
        var isAllScope = string.Equals(target, AllScopeToken, StringComparison.OrdinalIgnoreCase);
        var correlationId = Guid.NewGuid().ToString("N");

        var swarmCommand = new SwarmCommand
        {
            CommandType = SwarmCommandTypeValue,
            AgentId = isAllScope ? null : target,
            WorkspaceId = @operator.WorkspaceId,
            Scope = isAllScope ? SwarmCommandScope.All : SwarmCommandScope.Single,
            OperatorId = @operator.OperatorId,
            CorrelationId = correlationId,
        };

        _logger.LogInformation(
            "{Command}CommandHandler publishing lifecycle signal. AgentId={AgentId} WorkspaceId={WorkspaceId} Scope={Scope} OperatorId={OperatorId} CorrelationId={CorrelationId}",
            CommandName,
            swarmCommand.AgentId ?? "(all)",
            swarmCommand.WorkspaceId,
            swarmCommand.Scope,
            @operator.OperatorId,
            correlationId);

        await _bus.PublishCommandAsync(swarmCommand, ct).ConfigureAwait(false);

        var responseText = isAllScope
            ? string.Format(
                CultureInfo.InvariantCulture,
                AllConfirmationTemplate,
                DisplayVerb,
                @operator.WorkspaceId,
                correlationId)
            : string.Format(
                CultureInfo.InvariantCulture,
                SingleConfirmationTemplate,
                DisplayVerb,
                target,
                correlationId);

        return new CommandResult
        {
            Success = true,
            ResponseText = responseText,
            CorrelationId = correlationId,
        };
    }
}

/// <summary>Handles <c>/pause &lt;agentId&gt;</c> and <c>/pause all</c>.</summary>
public sealed class PauseCommandHandler : LifecycleCommandHandlerBase
{
    public PauseCommandHandler(
        ISwarmCommandBus bus,
        ILogger<PauseCommandHandler> logger)
        : base(bus, logger) { }

    public override string CommandName => TelegramCommands.Pause;

    protected override string SwarmCommandTypeValue => SwarmCommandType.Pause;

    protected override string DisplayVerb => "Pause";
}

/// <summary>Handles <c>/resume &lt;agentId&gt;</c> and <c>/resume all</c>.</summary>
public sealed class ResumeCommandHandler : LifecycleCommandHandlerBase
{
    public ResumeCommandHandler(
        ISwarmCommandBus bus,
        ILogger<ResumeCommandHandler> logger)
        : base(bus, logger) { }

    public override string CommandName => TelegramCommands.Resume;

    protected override string SwarmCommandTypeValue => SwarmCommandType.Resume;

    protected override string DisplayVerb => "Resume";
}
