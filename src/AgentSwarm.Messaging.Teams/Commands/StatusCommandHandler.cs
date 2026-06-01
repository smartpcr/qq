using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Diagnostics;
using Microsoft.Bot.Builder;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Commands;

/// <summary>
/// Handles the <c>agent status</c> command — queries the agent-swarm orchestrator via
/// <see cref="IAgentSwarmStatusProvider"/>, renders the per-agent status Adaptive Cards via
/// <see cref="IAdaptiveCardRenderer.RenderStatusCard"/>, and publishes a
/// <see cref="CommandEvent"/> with the <see cref="MessengerEventTypes.Command"/>
/// discriminator. Implements <c>implementation-plan.md</c> §3.2 step 3 (status query +
/// status summary card rendering) end-to-end inside the handler so
/// <see cref="ICommandDispatcher.DispatchAsync"/> is self-sufficient.
/// </summary>
/// <remarks>
/// <para>
/// The handler is the only producer of <see cref="CommandEvent"/> with
/// <see cref="MessengerEventTypes.Command"/> for the <c>agent status</c> verb —
/// <see cref="TeamsSwarmActivityHandler"/> no longer publishes a post-dispatch event for
/// command verbs (the activity handler owns mention-stripping + RBAC; the dispatcher /
/// handlers own command processing and event publication).
/// </para>
/// <para>
/// The default <see cref="IAgentSwarmStatusProvider"/> implementation
/// (<see cref="NullAgentSwarmStatusProvider"/>) returns an empty list — hosts that wire a
/// real orchestrator integration replace it via DI. When the provider returns an empty
/// list, the handler responds with a "no active agents" card built by
/// <see cref="CommandReplyCards.BuildEmptyStatusCard"/> so users see a friendly response
/// in dev / unwired environments rather than a missing reply.
/// </para>
/// </remarks>
public sealed class StatusCommandHandler : ICommandHandler
{
    private readonly IAgentSwarmStatusProvider _statusProvider;
    private readonly IAdaptiveCardRenderer _cardRenderer;
    private readonly IInboundEventPublisher _inboundEventPublisher;
    private readonly ILogger<StatusCommandHandler> _logger;

    /// <summary>Construct the handler with its status provider, card renderer, event publisher, and logger.</summary>
    public StatusCommandHandler(
        IAgentSwarmStatusProvider statusProvider,
        IAdaptiveCardRenderer cardRenderer,
        IInboundEventPublisher inboundEventPublisher,
        ILogger<StatusCommandHandler> logger)
    {
        _statusProvider = statusProvider ?? throw new ArgumentNullException(nameof(statusProvider));
        _cardRenderer = cardRenderer ?? throw new ArgumentNullException(nameof(cardRenderer));
        _inboundEventPublisher = inboundEventPublisher ?? throw new ArgumentNullException(nameof(inboundEventPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string CommandName => CommandNames.AgentStatus;

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
        // Iter-6 — delegate tenant resolution to the shared
        // CommandEventPublication.ResolveTenantId helper so every command handler
        // uses an identical extraction shape (CommandContext.TenantId first,
        // TeamsChannelData fallback).
        var tenantId = CommandEventPublication.ResolveTenantId(context);
        var identity = context.ResolvedIdentity ?? new UserIdentity(string.Empty, string.Empty, string.Empty, string.Empty);

        // Stage 6.3 iter-4 evaluator feedback item 6 — wrap the handler body in a
        // TeamsLogScope so the LogInformation/LogError below AND any downstream
        // logging (status provider call, send-reply, error-card path) carry the
        // CorrelationId / TenantId / UserId enrichment. The scope is layered on
        // top of the ambient TeamsLogContext push from TeamsSwarmActivityHandler
        // when present, and is the only enrichment source when the dispatcher is
        // invoked from outside the activity handler (background reprocessor,
        // unit test, etc.).
        using var logScope = TeamsLogScope.BeginScope(
            _logger,
            correlationId: correlationId,
            tenantId: tenantId,
            userId: identity.InternalUserId);

        _logger.LogInformation(
            "StatusCommandHandler querying swarm status (correlation {CorrelationId}, user {UserId}).",
            correlationId,
            identity.InternalUserId);

        await CommandEventPublication.PublishCommandEventAsync(
            _inboundEventPublisher,
            context,
            commandVerb: CommandNames.AgentStatus,
            eventType: MessengerEventTypes.Command,
            body: (context.CommandArguments ?? string.Empty).Trim(),
            ct).ConfigureAwait(false);

        IReadOnlyList<AgentStatusSummary> agents;
        try
        {
            agents = await _statusProvider
                .GetStatusAsync(identity, tenantId, correlationId, ct)
                .ConfigureAwait(false)
                ?? Array.Empty<AgentStatusSummary>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The query port is a §2.14 external boundary — failures here are not fatal
            // to the command pipeline (we've already published the CommandEvent so a
            // downstream consumer can react). Surface a friendly error card and let the
            // log + correlation ID lead the operator to the failure.
            _logger.LogError(
                ex,
                "StatusCommandHandler failed to query IAgentSwarmStatusProvider (correlation {CorrelationId}, user {UserId}).",
                correlationId,
                identity.InternalUserId);
            await CommandEventPublication.SendReplyAsync(
                context,
                CommandReplyCards.BuildErrorCard(
                    title: "Status unavailable",
                    detail: $"Failed to fetch agent status. Tracking ID: {correlationId}. Please try again."),
                ct).ConfigureAwait(false);
            return;
        }

        if (agents.Count == 0)
        {
            await CommandEventPublication.SendReplyAsync(
                context,
                CommandReplyCards.BuildEmptyStatusCard(correlationId),
                ct).ConfigureAwait(false);
            return;
        }

        var reply = CommandReplyCards.BuildStatusReply(_cardRenderer, agents);
        await CommandEventPublication.SendReplyAsync(context, reply, ct).ConfigureAwait(false);
    }
}
