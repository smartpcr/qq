namespace AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Discriminator for an inbound Slack payload after normalization into a
/// <see cref="SlackInboundEnvelope"/>. The three values cover every Slack
/// surface the connector accepts: Events API callbacks
/// (<see cref="Event"/>), slash command invocations
/// (<see cref="Command"/>), and Block Kit / modal interactions
/// (<see cref="Interaction"/>).
/// </summary>
/// <remarks>
/// COMPILE STUB introduced by Stage 1.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c> so
/// the queue contracts have a typed normalized envelope to carry. Stage 3.1
/// (Slack Inbound Transport) routes envelopes by this discriminator.
/// </remarks>
internal enum SlackInboundSourceType
{
    /// <summary>
    /// Default value reserved for an uninitialised envelope. The ingestor
    /// should treat this as a programming error.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Slack Events API callback (e.g., <c>app_mention</c>, <c>message</c>
    /// subscriptions). Identified at the transport layer by the
    /// <c>X-Slack-Signature</c> header path.
    /// </summary>
    Event = 1,

    /// <summary>
    /// Slash command invocation (e.g., <c>/agent ask ...</c>). Identified
    /// at the transport layer by the <c>application/x-www-form-urlencoded</c>
    /// command POST endpoint.
    /// </summary>
    Command = 2,

    /// <summary>
    /// Block Kit interaction (button click) or modal submission. Identified
    /// at the transport layer by the interactions endpoint and the
    /// <c>payload</c> form field.
    /// </summary>
    Interaction = 3,
}
