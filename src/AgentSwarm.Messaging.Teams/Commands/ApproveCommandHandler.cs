using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Commands;

/// <summary>
/// Handles the <c>approve [questionId]</c> command — resolves an open
/// <see cref="AgentQuestion"/> with action value <c>"approve"</c>. Implements
/// <c>implementation-plan.md</c> §3.2 step 4 and §3.2 step 5 (the explicit /
/// bare-single / bare-zero / bare-ambiguous resolution paths).
/// </summary>
/// <remarks>
/// All resolution logic is shared with <see cref="RejectCommandHandler"/> and lives on
/// <see cref="ApproveRejectCommandExecutor"/> so the two handlers stay diff-equivalent
/// except for the command keyword and action value.
/// </remarks>
public sealed class ApproveCommandHandler : ICommandHandler
{
    /// <summary>The canonical action value emitted on <see cref="HumanDecisionEvent.ActionValue"/>.</summary>
    public const string ActionValue = "approve";

    private readonly ApproveRejectCommandExecutor _executor;

    /// <summary>Construct the handler with the question store, event publisher, and renderer required by <see cref="ApproveRejectCommandExecutor"/>.</summary>
    public ApproveCommandHandler(
        IAgentQuestionStore questionStore,
        IInboundEventPublisher inboundEventPublisher,
        Cards.IAdaptiveCardRenderer cardRenderer,
        ILogger<ApproveCommandHandler> logger)
    {
        if (logger is null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        _executor = new ApproveRejectCommandExecutor(questionStore, inboundEventPublisher, cardRenderer, logger);
    }

    /// <inheritdoc />
    public string CommandName => CommandNames.Approve;

    /// <inheritdoc />
    public Task HandleAsync(CommandContext context, CancellationToken ct)
        => _executor.ExecuteAsync(CommandNames.Approve, ActionValue, context, ct);
}
