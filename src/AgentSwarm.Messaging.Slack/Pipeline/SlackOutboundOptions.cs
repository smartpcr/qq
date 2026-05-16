// -----------------------------------------------------------------------
// <copyright file="SlackOutboundOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

/// <summary>
/// Operator-bound options for the connector-side enqueue path: tells the
/// Stage 6.3 <see cref="SlackConnector"/> which Slack workspace the
/// outbound <see cref="AgentSwarm.Messaging.Abstractions.MessengerMessage"/>
/// / <see cref="AgentSwarm.Messaging.Abstractions.AgentQuestion"/>
/// instances belong to. The platform-neutral
/// <see cref="AgentSwarm.Messaging.Abstractions.IMessengerConnector"/>
/// types carry no <c>TeamId</c>; the connector resolves the workspace
/// here so the downstream <see cref="ISlackThreadManager"/> can pin the
/// per-workspace bot OAuth token + default channel.
/// </summary>
/// <remarks>
/// <para>
/// Bound from the <c>"Slack:Outbound"</c> section of <c>IConfiguration</c>
/// (the same section the <see cref="SlackOutboundDispatchClientOptions"/>
/// timeout reads from; the property names do not collide). When the host
/// supports multiple workspaces simultaneously a future stage will
/// extend this to a per-agent lookup table; until then a single
/// default workspace is sufficient for the
/// <c>agent_swarm_messenger_user_stories.md</c>
/// acceptance criteria.
/// </para>
/// </remarks>
public sealed class SlackOutboundOptions
{
    /// <summary>Configuration section name (<c>"Slack:Outbound"</c>).</summary>
    public const string SectionName = "Slack:Outbound";

    /// <summary>
    /// Slack <c>team_id</c> the connector should bind every outbound
    /// message to. Required: <see cref="SlackConnector"/> throws
    /// <see cref="System.InvalidOperationException"/> at the first send
    /// when this is null or empty so a misconfigured host fails fast at
    /// the call site rather than dead-lettering the message after
    /// retries.
    /// </summary>
    public string? DefaultTeamId { get; set; }
}
