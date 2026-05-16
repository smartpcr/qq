namespace AgentSwarm.Messaging.Slack.Queues;

/// <summary>
/// Discriminator that identifies which Slack pipeline produced a
/// poison message captured in <see cref="ISlackDeadLetterQueue"/>.
/// Operators use this to route an inspected entry back to the right
/// recovery tool (inbound replay vs. outbound resend).
/// </summary>
internal enum SlackDeadLetterSource
{
    /// <summary>
    /// Default value reserved for an uninitialised entry. Operators should
    /// treat this as a programming error.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Originated from <see cref="ISlackInboundQueue"/>; payload is a
    /// <see cref="Transport.SlackInboundEnvelope"/>.
    /// </summary>
    Inbound = 1,

    /// <summary>
    /// Originated from <see cref="ISlackOutboundQueue"/>; payload is a
    /// <see cref="Transport.SlackOutboundEnvelope"/>.
    /// </summary>
    Outbound = 2,
}
