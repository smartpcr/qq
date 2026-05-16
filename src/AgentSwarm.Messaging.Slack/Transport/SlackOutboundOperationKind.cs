namespace AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Classification of the Slack Web API call carried by a
/// <see cref="SlackOutboundEnvelope"/>. The <c>SlackOutboundDispatcher</c>
/// routes envelopes to the matching SlackNet method based on this value.
/// </summary>
/// <remarks>
/// COMPILE STUB introduced by Stage 1.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>. The
/// brief for Stage 4.1 (Slack Outbound Dispatcher) lists the three
/// post-only operations the dispatcher supports: <c>postMessage</c>,
/// <c>update</c>, <c>viewsUpdate</c>. (<c>views.open</c> bypasses the
/// outbound queue per architecture.md section 2.16 and is handled by
/// <c>SlackDirectApiClient</c>.)
/// <para>
/// Named <c>SlackOutboundOperationKind</c> rather than <c>MessageType</c>
/// to avoid a name collision with
/// <see cref="AgentSwarm.Messaging.Abstractions.MessageType"/>, which
/// classifies the <see cref="AgentSwarm.Messaging.Abstractions.MessengerMessage"/>
/// payload (status/completion/error) rather than the API verb.
/// </para>
/// </remarks>
internal enum SlackOutboundOperationKind
{
    /// <summary>
    /// Default value reserved for an uninitialised envelope. The dispatcher
    /// should treat this as a programming error.
    /// </summary>
    Unspecified = 0,

    /// <summary>Slack Web API <c>chat.postMessage</c>.</summary>
    PostMessage = 1,

    /// <summary>Slack Web API <c>chat.update</c>.</summary>
    UpdateMessage = 2,

    /// <summary>Slack Web API <c>views.update</c> (modal update; not <c>views.open</c>).</summary>
    ViewsUpdate = 3,
}
