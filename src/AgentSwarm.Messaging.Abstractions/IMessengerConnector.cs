namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Shared messenger connector abstraction implemented by every platform
/// adapter (Slack, Telegram, Discord, Teams). Outbound calls
/// (<see cref="SendMessageAsync"/>, <see cref="SendQuestionAsync"/>) are
/// expected to enqueue work into the platform's durable outbound queue,
/// while <see cref="ReceiveAsync"/> drains processed inbound events from
/// the platform's durable inbound pipeline.
/// </summary>
/// <remarks>
/// COMPILE STUB. The canonical contract is owned by the upstream
/// <c>AgentSwarm.Messaging.Abstractions</c> story. This declaration exists
/// solely to unblock compilation of <c>AgentSwarm.Messaging.Slack</c> while
/// the upstream story is still in flight. The method signatures match
/// section 4.1 of <c>docs/stories/qq-SLACK-MESSENGER-SUPP/architecture.md</c>
/// and the uploaded reference doc
/// <c>.forge-attachments/agent_swarm_messenger_user_stories.md</c> (FR-001).
/// <para>
/// Parameter name <c>ct</c> (rather than the .NET convention
/// <c>cancellationToken</c>) is deliberate so that downstream call sites
/// using named arguments (e.g., <c>connector.SendMessageAsync(m, ct: token)</c>)
/// remain source-compatible with the canonical upstream interface.
/// </para>
/// </remarks>
public interface IMessengerConnector
{
    /// <summary>
    /// Posts a generic <see cref="MessengerMessage"/> to the configured
    /// messenger platform.
    /// </summary>
    Task SendMessageAsync(MessengerMessage message, CancellationToken ct);

    /// <summary>
    /// Posts an <see cref="AgentQuestion"/> with interactive controls
    /// rendered for the target platform.
    /// </summary>
    Task SendQuestionAsync(AgentQuestion question, CancellationToken ct);

    /// <summary>
    /// Drains processed inbound events from the connector's pipeline.
    /// </summary>
    Task<IReadOnlyList<MessengerEvent>> ReceiveAsync(CancellationToken ct);
}
