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
/// </summary>
internal static class CommandEventPublication
{
    public static Task PublishCommandEventAsync(
        IInboundEventPublisher publisher,
        CommandContext context,
        string commandVerb,
        string eventType,
        string body,
        CancellationToken ct)
    {
        var correlationId = string.IsNullOrEmpty(context.CorrelationId)
            ? Guid.NewGuid().ToString()
            : context.CorrelationId!;

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
