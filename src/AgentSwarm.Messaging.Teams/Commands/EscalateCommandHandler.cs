using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Commands;

/// <summary>
/// Handles the <c>escalate</c> command — publishes a <see cref="CommandEvent"/> with the
/// <see cref="MessengerEventTypes.Escalation"/> discriminator and sends an acknowledgement
/// Adaptive Card. Implements part of <c>implementation-plan.md</c> §3.2 step 5 (lifecycle
/// commands). The agent-swarm orchestrator (§2.14 external boundary) consumes the event
/// and performs the routing.
/// </summary>
public sealed class EscalateCommandHandler : ICommandHandler
{
    private readonly IInboundEventPublisher _inboundEventPublisher;
    private readonly ILogger<EscalateCommandHandler> _logger;

    /// <summary>Construct the handler with the inbound event publisher and logger.</summary>
    public EscalateCommandHandler(
        IInboundEventPublisher inboundEventPublisher,
        ILogger<EscalateCommandHandler> logger)
    {
        _inboundEventPublisher = inboundEventPublisher ?? throw new ArgumentNullException(nameof(inboundEventPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string CommandName => CommandNames.Escalate;

    /// <inheritdoc />
    public async Task HandleAsync(CommandContext context, CancellationToken ct)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var correlationId = string.IsNullOrEmpty(context.CorrelationId)
            ? Guid.NewGuid().ToString()
            : context.CorrelationId!;

        _logger.LogInformation(
            "EscalateCommandHandler accepted escalation (correlation {CorrelationId}, user {UserId}).",
            correlationId,
            context.ResolvedIdentity?.InternalUserId ?? "(unmapped)");

        await CommandEventPublication.PublishCommandEventAsync(
            _inboundEventPublisher,
            context,
            commandVerb: CommandNames.Escalate,
            eventType: MessengerEventTypes.Escalation,
            body: (context.CommandArguments ?? string.Empty).Trim(),
            ct).ConfigureAwait(false);

        var card = CommandReplyCards.BuildAcknowledgementCard(
            title: "Escalation submitted",
            detail: "Your escalation has been logged. On-call will be notified shortly.",
            correlationId: correlationId);
        await CommandEventPublication.SendReplyAsync(context, card, ct).ConfigureAwait(false);
    }
}
