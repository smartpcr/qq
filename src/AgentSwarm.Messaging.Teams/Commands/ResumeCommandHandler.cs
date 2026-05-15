using AgentSwarm.Messaging.Abstractions;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Commands;

/// <summary>
/// Handles the <c>resume</c> command — publishes a <see cref="CommandEvent"/> with the
/// <see cref="MessengerEventTypes.ResumeAgent"/> discriminator and sends an
/// acknowledgement Adaptive Card. Implements part of <c>implementation-plan.md</c> §3.2
/// step 5 (lifecycle commands). The agent-swarm orchestrator (§2.14 external boundary)
/// consumes the event and performs the resume.
/// </summary>
public sealed class ResumeCommandHandler : ICommandHandler
{
    private readonly IInboundEventPublisher _inboundEventPublisher;
    private readonly ILogger<ResumeCommandHandler> _logger;

    /// <summary>Construct the handler with the inbound event publisher and logger.</summary>
    public ResumeCommandHandler(
        IInboundEventPublisher inboundEventPublisher,
        ILogger<ResumeCommandHandler> logger)
    {
        _inboundEventPublisher = inboundEventPublisher ?? throw new ArgumentNullException(nameof(inboundEventPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string CommandName => CommandNames.Resume;

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
            "ResumeCommandHandler accepted resume request (correlation {CorrelationId}, user {UserId}).",
            correlationId,
            context.ResolvedIdentity?.InternalUserId ?? "(unmapped)");

        await CommandEventPublication.PublishCommandEventAsync(
            _inboundEventPublisher,
            context,
            commandVerb: CommandNames.Resume,
            eventType: MessengerEventTypes.ResumeAgent,
            body: (context.CommandArguments ?? string.Empty).Trim(),
            ct).ConfigureAwait(false);

        var card = CommandReplyCards.BuildAcknowledgementCard(
            title: "Resume request submitted",
            detail: "I'll resume the paused agent bound to this conversation.",
            correlationId: correlationId);
        await CommandEventPublication.SendReplyAsync(context, card, ct).ConfigureAwait(false);
    }
}
