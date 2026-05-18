// -----------------------------------------------------------------------
// <copyright file="SlackConnectorComponents.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Queues;

/// <summary>
/// Explicit composition object that names every internal Slack
/// subsystem <see cref="SlackConnector"/> "composes" per
/// implementation-plan.md Stage 8.1 step 1: the inbound transport
/// seam (<see cref="ISlackInboundQueue"/>) the
/// <see cref="SlackInboundIngestor"/> background service drains, the
/// outbound transport seam (<see cref="ISlackOutboundQueue"/>) the
/// <see cref="SlackOutboundDispatcher"/> background service drains,
/// the per-task thread manager (<see cref="ISlackThreadManager"/>),
/// the audit logger (<see cref="ISlackAuditLogger"/>), and the
/// connector-internal inbound event buffer
/// (<see cref="ISlackInboundEventBuffer"/>) that
/// <see cref="SlackConnector.ReceiveAsync"/> drains.
/// </summary>
/// <remarks>
/// <para>
/// Implementation-plan.md Stage 8.1 step 1:
/// "Create <see cref="SlackConnector"/> class implementing
/// <see cref="IMessengerConnector"/> that composes all internal
/// components: inbound transport, ingestor, outbound dispatcher,
/// thread manager, audit logger".
/// </para>
/// <para>
/// Bundling the references behind a single composition object keeps
/// the <see cref="SlackConnector"/> constructor signature stable
/// while still making the composition explicit at compile time -- a
/// regression that drops any of the named consumed components from
/// <c>AddSlackMessenger</c>'s registrations fails at
/// <c>SlackConnectorComponents</c> construction.
/// </para>
/// <para>
/// <b>Hosted-service peers (ingestor + outbound dispatcher) are
/// validated at facade build-time</b>, not as constructor deps,
/// because making them ctor deps would force every
/// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> in the
/// container to construct eagerly the first time a downstream
/// <see cref="SlackConnector"/> is resolved (and would fail
/// <c>BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true })</c>
/// for unrelated reasons). The Stage 8.1 facade calls
/// <c>SlackMessengerServiceCollectionExtensions.ValidateConnectorHostedServiceComposition</c>
/// at the END of <c>AddSlackMessenger</c> to scan the
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/>
/// for <see cref="SlackInboundIngestor"/> + <see cref="SlackOutboundDispatcher"/>
/// descriptors and throws a precise
/// <see cref="InvalidOperationException"/> when either is missing -- a
/// strictly stronger guarantee than the prior runtime check because
/// it surfaces BEFORE the host even starts.
/// </para>
/// </remarks>
internal sealed class SlackConnectorComponents
{
    /// <summary>
    /// Audit-log seam used by <see cref="SlackConnector"/> to record
    /// every connector-level outbound send (one row per
    /// <see cref="IMessengerConnector.SendMessageAsync"/> /
    /// <see cref="IMessengerConnector.SendQuestionAsync"/> call).
    /// Separate from (and complementary to) the per-HTTP-call row the
    /// <see cref="SlackOutboundDispatcher"/> writes after the actual
    /// <c>chat.postMessage</c> call.
    /// </summary>
    public ISlackAuditLogger AuditLogger { get; }

    /// <summary>
    /// Inbound transport queue (the seam the Slack inbound HTTP
    /// controllers push into and <see cref="SlackInboundIngestor"/>
    /// drains). Surfaced as a composition reference so the connector's
    /// SendMessageAsync audit row can correlate against the same
    /// transport even though the connector itself never enqueues
    /// inbound envelopes.
    /// </summary>
    public ISlackInboundQueue InboundTransport { get; }

    /// <summary>
    /// Outbound transport queue (the seam
    /// <see cref="SlackConnector"/> pushes rendered envelopes into and
    /// <see cref="SlackOutboundDispatcher"/> drains).
    /// </summary>
    public ISlackOutboundQueue OutboundTransport { get; }

    /// <summary>
    /// Per-task thread manager used by
    /// <see cref="SlackConnector"/> to resolve / create the
    /// destination Slack thread before enqueueing.
    /// </summary>
    public ISlackThreadManager ThreadManager { get; }

    /// <summary>
    /// Connector-internal inbound buffer that
    /// <see cref="SlackConnector.ReceiveAsync"/> drains.
    /// </summary>
    public ISlackInboundEventBuffer InboundEventBuffer { get; }

    public SlackConnectorComponents(
        ISlackAuditLogger auditLogger,
        ISlackInboundQueue inboundTransport,
        ISlackOutboundQueue outboundTransport,
        ISlackThreadManager threadManager,
        ISlackInboundEventBuffer inboundEventBuffer)
    {
        this.AuditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        this.InboundTransport = inboundTransport ?? throw new ArgumentNullException(nameof(inboundTransport));
        this.OutboundTransport = outboundTransport ?? throw new ArgumentNullException(nameof(outboundTransport));
        this.ThreadManager = threadManager ?? throw new ArgumentNullException(nameof(threadManager));
        this.InboundEventBuffer = inboundEventBuffer ?? throw new ArgumentNullException(nameof(inboundEventBuffer));
    }
}
