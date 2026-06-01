// -----------------------------------------------------------------------
// <copyright file="SlackInboundTransportFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Queues;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="ISlackInboundTransportFactory"/>. Constructs the
/// Socket Mode receiver for workspaces whose
/// <see cref="SlackWorkspaceConfig.AppLevelTokenRef"/> is non-empty
/// and the Events-API no-op marker otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// All dependencies are resolved through DI so a production host can
/// inject the real <see cref="ISlackSocketModeConnectionFactory"/>
/// (the <c>apps.connections.open</c> + <see cref="System.Net.WebSockets.ClientWebSocket"/>
/// pair) while tests inject a fake.
/// </para>
/// </remarks>
internal sealed class SlackInboundTransportFactory : ISlackInboundTransportFactory
{
    private readonly ISecretProvider secretProvider;
    private readonly ISlackSocketModeConnectionFactory connectionFactory;
    private readonly ISlackInboundQueue inboundQueue;
    private readonly ISlackInboundEnqueueDeadLetterSink deadLetterSink;
    private readonly TimeProvider timeProvider;
    private readonly ILoggerFactory loggerFactory;
    private readonly SlackSocketModeOptions options;

    public SlackInboundTransportFactory(
        ISecretProvider secretProvider,
        ISlackSocketModeConnectionFactory connectionFactory,
        ISlackInboundQueue inboundQueue,
        ISlackInboundEnqueueDeadLetterSink deadLetterSink,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        IOptions<SlackSocketModeOptions> options)
    {
        this.secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        this.inboundQueue = inboundQueue ?? throw new ArgumentNullException(nameof(inboundQueue));
        this.deadLetterSink = deadLetterSink ?? throw new ArgumentNullException(nameof(deadLetterSink));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public SlackInboundTransportKind ResolveTransportKind(SlackWorkspaceConfig workspaceConfig)
    {
        ArgumentNullException.ThrowIfNull(workspaceConfig);

        return string.IsNullOrWhiteSpace(workspaceConfig.AppLevelTokenRef)
            ? SlackInboundTransportKind.EventsApi
            : SlackInboundTransportKind.SocketMode;
    }

    /// <inheritdoc />
    public ISlackInboundTransport Create(SlackWorkspaceConfig workspaceConfig)
    {
        ArgumentNullException.ThrowIfNull(workspaceConfig);

        return this.ResolveTransportKind(workspaceConfig) switch
        {
            SlackInboundTransportKind.SocketMode => new SlackSocketModeReceiver(
                workspaceConfig,
                this.secretProvider,
                this.connectionFactory,
                this.inboundQueue,
                this.deadLetterSink,
                this.timeProvider,
                this.loggerFactory.CreateLogger<SlackSocketModeReceiver>(),
                this.options),
            SlackInboundTransportKind.EventsApi => new SlackEventsApiReceiver(
                workspaceConfig.TeamId,
                this.loggerFactory.CreateLogger<SlackEventsApiReceiver>()),
            _ => throw new InvalidOperationException(
                $"Unknown transport kind for workspace {workspaceConfig.TeamId}."),
        };
    }
}
