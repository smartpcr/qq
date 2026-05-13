namespace Qq.Messaging.Abstractions;

/// <summary>
/// A normalized inbound interaction that wraps either a command or a callback response.
/// </summary>
public sealed record InboundInteraction
{
    public required InteractionType Type { get; init; }
    public InboundCommand? Command { get; init; }
    public InboundCallbackResponse? Callback { get; init; }

    public PlatformPrincipal Principal =>
        Type == InteractionType.Command
            ? Command!.Principal
            : Callback!.Principal;

    public CorrelationContext Correlation =>
        Type == InteractionType.Command
            ? Command!.Correlation
            : Callback!.Correlation;

    public static InboundInteraction FromCommand(InboundCommand command) =>
        new() { Type = InteractionType.Command, Command = command };

    public static InboundInteraction FromCallback(InboundCallbackResponse callback) =>
        new() { Type = InteractionType.CallbackResponse, Callback = callback };
}
