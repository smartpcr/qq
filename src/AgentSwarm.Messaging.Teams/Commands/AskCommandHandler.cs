using AgentSwarm.Messaging.Abstractions;
using Microsoft.Bot.Builder;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Commands;

/// <summary>
/// Handles the <c>agent ask &lt;text&gt;</c> command — creates a new agent task from the
/// user input and sends an acknowledgement Adaptive Card carrying the correlation/tracking
/// ID. Implements <c>implementation-plan.md</c> §3.2 step 2.
/// </summary>
/// <remarks>
/// <para>
/// The handler owns BOTH sides of the contract:
/// </para>
/// <list type="number">
/// <item><description><b>Publish</b> a <see cref="CommandEvent"/> with
/// <see cref="MessengerEventTypes.AgentTaskRequest"/> as the discriminator and the user's
/// prompt as the <see cref="ParsedCommand.Payload"/>. The agent-swarm orchestrator
/// (§2.14 external boundary) consumes this event to schedule the task.</description></item>
/// <item><description><b>Reply</b> with an acknowledgement Adaptive Card surfacing the
/// correlation ID so the user can correlate downstream agent activity (per
/// <c>architecture.md</c> §6.1 step 9).</description></item>
/// </list>
/// <para>
/// Self-publication means <see cref="ICommandDispatcher.DispatchAsync"/> is end-to-end
/// sufficient for the ask command outside the Teams activity-handler path — important for
/// any consumer that invokes the dispatcher without first running
/// <see cref="TeamsSwarmActivityHandler.OnMessageActivityAsync"/> (for example, the
/// upcoming Stage 4.x background-message reprocessor or a non-Teams messenger that reuses
/// the same dispatcher).
/// </para>
/// </remarks>
public sealed class AskCommandHandler : ICommandHandler
{
    private readonly IInboundEventPublisher _inboundEventPublisher;
    private readonly ILogger<AskCommandHandler> _logger;

    /// <summary>Construct the handler with the publisher and logger it needs to publish a task-request event.</summary>
    public AskCommandHandler(
        IInboundEventPublisher inboundEventPublisher,
        ILogger<AskCommandHandler> logger)
    {
        _inboundEventPublisher = inboundEventPublisher ?? throw new ArgumentNullException(nameof(inboundEventPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string CommandName => CommandNames.AgentAsk;

    /// <inheritdoc />
    public async Task HandleAsync(CommandContext context, CancellationToken ct)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var prompt = (context.CommandArguments ?? string.Empty).Trim();
        var correlationId = string.IsNullOrEmpty(context.CorrelationId)
            ? Guid.NewGuid().ToString()
            : context.CorrelationId!;

        _logger.LogInformation(
            "AskCommandHandler accepted task (correlation {CorrelationId}, user {UserId}, prompt length {PromptLength}).",
            correlationId,
            context.ResolvedIdentity?.InternalUserId ?? "(unmapped)",
            prompt.Length);

        await CommandEventPublication.PublishCommandEventAsync(
            _inboundEventPublisher,
            context,
            commandVerb: CommandNames.AgentAsk,
            eventType: MessengerEventTypes.AgentTaskRequest,
            body: prompt,
            ct).ConfigureAwait(false);

        var card = CommandReplyCards.BuildAskAcknowledgementCard(prompt, correlationId);
        await CommandEventPublication.SendReplyAsync(context, card, ct).ConfigureAwait(false);
    }
}

