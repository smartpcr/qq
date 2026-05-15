using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Telegram.Pipeline.Stubs;

/// <summary>
/// Stage 2.2 stub <see cref="ICallbackHandler"/>. Returns a static
/// acknowledgement so the pipeline can finish CallbackResponse and
/// awaiting-comment TextReply flows before Stage 3.3 ships
/// <c>CallbackQueryHandler</c> with real <see cref="HumanDecisionEvent"/>
/// publication.
/// </summary>
internal sealed class StubCallbackHandler : ICallbackHandler
{
    public const string StubCorrelationId = "stub-callback";

    public Task<CommandResult> HandleAsync(MessengerEvent messengerEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messengerEvent);

        return Task.FromResult(new CommandResult
        {
            Success = true,
            ResponseText = null,
            CorrelationId = string.IsNullOrWhiteSpace(messengerEvent.CorrelationId)
                ? StubCorrelationId
                : messengerEvent.CorrelationId,
        });
    }
}
