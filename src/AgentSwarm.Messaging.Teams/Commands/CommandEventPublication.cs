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
            Source = ResolveEventSource(context),
            Timestamp = DateTimeOffset.UtcNow,
            Payload = new ParsedCommand(commandVerb, body, correlationId),
        };

        return publisher.PublishAsync(commandEvent, ct);
    }

    public static Task SendReplyAsync(CommandContext context, Microsoft.Bot.Schema.IMessageActivity reply, CancellationToken ct)
    {
        // Honour the producer's explicit suppression flag — the Stage 3.4 message-extension
        // path owns its own user-facing response (the MessagingExtensionActionResponse
        // invoke reply) and must not also post a chat-thread message via the turn context.
        if (context.SuppressReply)
        {
            return Task.CompletedTask;
        }

        if (context.TurnContext is ITurnContext turnContext)
        {
            return turnContext.SendActivityAsync(reply, ct);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolve the canonical <see cref="MessengerEventSources"/> discriminator for the
    /// inbound activity backing <paramref name="context"/>. Mirrors the
    /// <c>ResolveEventSource(activity)</c> helper previously inlined in
    /// <c>TeamsSwarmActivityHandler</c> before the Stage 3.2 split moved
    /// <see cref="CommandEvent"/> publication into the per-handler path — restoring the
    /// <see cref="MessengerEventSources.PersonalChat"/> /
    /// <see cref="MessengerEventSources.TeamChannel"/> discrimination that downstream
    /// consumers (per <c>architecture.md</c> §3.1) rely on when distinguishing 1:1 chat
    /// events from team-channel events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the upstream producer has already pinned the canonical source value (for
    /// example, the Stage 3.4 message-extension handler sets
    /// <see cref="CommandContext.Source"/> to <see cref="MessengerEventSources.MessageAction"/>),
    /// the explicit hint wins. This preserves the
    /// <see cref="MessengerEventSources.MessageAction"/> origination on forwarded messages
    /// regardless of the underlying conversation type (personal vs channel).
    /// </para>
    /// <para>
    /// Reads <c>ConversationType</c> from the Bot Framework <see cref="ITurnContext"/>
    /// hanging off <see cref="CommandContext.TurnContext"/>. The cast mirrors
    /// <see cref="SendReplyAsync"/> so we keep the Bot-Framework dependency confined to
    /// this Teams-specific assembly — the platform-agnostic <c>CommandContext</c> contract
    /// in <c>AgentSwarm.Messaging.Abstractions</c> stays unchanged.
    /// </para>
    /// <para>
    /// Returns <c>null</c> when the turn context is absent (unit-test scenarios that omit
    /// it) or when the conversation reference does not carry a <c>ConversationType</c>,
    /// preserving the nullable contract on <see cref="MessengerEvent.Source"/>.
    /// </para>
    /// </remarks>
    private static string? ResolveEventSource(CommandContext context)
    {
        // Producer-supplied hint wins — message-extension forwards stamp Source = MessageAction
        // up front so the published CommandEvent carries the correct origination.
        if (!string.IsNullOrEmpty(context.Source))
        {
            return context.Source;
        }

        if (context.TurnContext is not ITurnContext turnContext)
        {
            return null;
        }

        var conversation = turnContext.Activity?.Conversation;
        if (conversation is null)
        {
            return null;
        }

        return string.Equals(conversation.ConversationType, "channel", StringComparison.OrdinalIgnoreCase)
            ? MessengerEventSources.TeamChannel
            : MessengerEventSources.PersonalChat;
    }
}
