// -----------------------------------------------------------------------
// <copyright file="ISlackInboundTransport.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Lifecycle abstraction shared by every Slack inbound transport (HTTP
/// Events API plus Socket Mode WebSocket). Implementations convert
/// Slack-native payloads into a normalized
/// <see cref="SlackInboundEnvelope"/> and enqueue them onto
/// <see cref="Queues.ISlackInboundQueue"/>; the connector hosts the
/// active transport via <see cref="StartAsync"/> /
/// <see cref="StopAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The contract is the literal one architecture.md §4.2 names:
/// </para>
/// <code language="csharp"><![CDATA[
/// internal interface ISlackInboundTransport
/// {
///     Task StartAsync(CancellationToken ct);
///     Task StopAsync(CancellationToken ct);
/// }
/// ]]></code>
/// <para>
/// Per architecture.md §4.2 the active transport is selected per
/// workspace by <see cref="ISlackInboundTransportFactory"/> based on
/// <see cref="AgentSwarm.Messaging.Slack.Entities.SlackWorkspaceConfig.AppLevelTokenRef"/>:
/// when the secret reference is present the workspace runs Socket Mode
/// (<see cref="SlackSocketModeReceiver"/>); when absent it runs
/// Events API (<see cref="SlackEventsApiReceiver"/> -- a no-op marker
/// because the ASP.NET HTTP controllers handle the work).
/// </para>
/// </remarks>
internal interface ISlackInboundTransport
{
    /// <summary>
    /// Begins receiving Slack payloads. Returns once the transport is
    /// scheduled (the actual receive loop runs in the background);
    /// errors during initial setup surface synchronously.
    /// </summary>
    /// <param name="ct">
    /// Cancellation token honoured for the start-up handshake. Once
    /// <see cref="StartAsync"/> returns the transport owns its own
    /// long-lived cancellation token; cancelling this one does not
    /// stop the transport (callers MUST use <see cref="StopAsync"/>
    /// for that).
    /// </param>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Cleanly stops the transport: cancels the receive loop, closes
    /// any open WebSocket / HTTP listener, and drains pending
    /// envelopes before returning. Idempotent -- calling
    /// <see cref="StopAsync"/> on a transport that was never started
    /// or has already been stopped completes without error.
    /// </summary>
    /// <param name="ct">
    /// Cancellation token honoured for the shutdown. Cancelling
    /// before the drain completes may leave envelopes un-enqueued;
    /// the caller is responsible for waiting long enough.
    /// </param>
    Task StopAsync(CancellationToken ct);
}
