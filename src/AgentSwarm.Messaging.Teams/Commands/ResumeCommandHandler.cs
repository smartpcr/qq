using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Diagnostics;
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

        // Stage 6.3 iter-4 evaluator feedback item 6 (structural fix) — wrap the
        // handler body in a TeamsLogScope so every log entry emitted below (and
        // any downstream publisher / send-reply path on the same async call stack)
        // carries the canonical CorrelationId / TenantId / UserId enrichment. The
        // scope layers on the ambient TeamsLogContext push from
        // TeamsSwarmActivityHandler when present, and is the sole enrichment source
        // when the dispatcher is invoked from outside the activity handler.
        //
        // Iter-6 evaluator feedback items 1 + 3 — extract the tenant id via the
        // shared CommandEventPublication.ResolveTenantId helper so the canonical
        // TenantId enrichment key is pushed alongside CorrelationId / UserId.
        var userId = context.ResolvedIdentity?.InternalUserId;
        var tenantId = CommandEventPublication.ResolveTenantId(context);
        using var logScope = TeamsLogScope.BeginScope(
            _logger,
            correlationId: correlationId,
            tenantId: tenantId,
            userId: userId);

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
