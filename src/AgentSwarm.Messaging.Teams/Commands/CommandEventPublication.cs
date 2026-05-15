using AgentSwarm.Messaging.Abstractions;
using Microsoft.Bot.Builder;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Commands;

/// <summary>
/// Internal helper shared by the simple-command handlers (Ask / Status / Escalate / Pause /
/// Resume) — builds a <see cref="CommandEvent"/> with the verb-specific
/// <see cref="MessengerEventTypes"/> discriminator and publishes it via
/// <see cref="IInboundEventPublisher.PublishAsync"/>. Centralised here so each handler can
/// own its own publication (per <c>implementation-plan.md</c> §3.2 step 2 — the dispatcher
/// must be self-sufficient outside the activity-handler path) without duplicating the
/// envelope-construction boilerplate.
///
/// <para>Per FR-004 (Correlation &amp; Traceability) callers MUST supply the
/// <paramref name="correlationId"/> they intend to render on the Adaptive Card reply, so
/// the <see cref="CommandEvent.CorrelationId"/> on the published event is guaranteed to
/// match the Tracking ID shown to the user. The helper deliberately does NOT synthesise
/// its own GUID fallback: an internal <c>Guid.NewGuid()</c> would diverge from any
/// correlation ID the handler already minted for the reply card, breaking end-to-end
/// traceability in the self-sufficient mode where <see cref="CommandContext.CorrelationId"/>
/// is null (handler invoked outside the Teams activity-handler pipeline). The handler is
/// expected to compute the correlation ID exactly once (e.g.
/// <c>context.CorrelationId ?? Guid.NewGuid().ToString()</c>) and thread the same value
/// into both <see cref="CommandReplyCards"/> and this method.</para>
/// </summary>
internal static class CommandEventPublication
{
    public static Task PublishCommandEventAsync(
        IInboundEventPublisher publisher,
        CommandContext context,
        string commandVerb,
        string eventType,
        string body,
        string correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(correlationId))
        {
            throw new ArgumentException(
                "CorrelationId must be supplied by the caller so the published event's CorrelationId matches the Tracking ID rendered on the Adaptive Card reply (FR-004 Correlation & Traceability).",
                nameof(correlationId));
        }

        var commandEvent = new CommandEvent(eventType)
        {
            EventId = Guid.NewGuid().ToString(),
            CorrelationId = correlationId,
            Messenger = "Teams",
            ExternalUserId = context.ResolvedIdentity?.AadObjectId ?? string.Empty,
            ActivityId = context.ActivityId,
            Source = null,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = new ParsedCommand(commandVerb, body, correlationId),
        };

        return publisher.PublishAsync(commandEvent, ct);
    }

    public static Task SendReplyAsync(CommandContext context, Microsoft.Bot.Schema.IMessageActivity reply, CancellationToken ct)
    {
        if (context.TurnContext is ITurnContext turnContext)
        {
            return turnContext.SendActivityAsync(reply, ct);
        }

        return Task.CompletedTask;
    }
}
