namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Discriminates inbound messenger events by origin type.
/// </summary>
public enum EventType
{
    Command,
    CallbackResponse,
    TextReply,
    Unknown
}
