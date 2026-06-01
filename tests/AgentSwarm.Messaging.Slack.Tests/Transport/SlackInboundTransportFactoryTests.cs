// -----------------------------------------------------------------------
// <copyright file="SlackInboundTransportFactoryTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Test Scenario 3 from Stage 4.2 of <c>implementation-plan.md</c>:
/// "Transport selection by config -- Given a workspace with
/// AppLevelTokenRef = null, When the connector starts, Then Events API
/// transport is used for that workspace."
/// </summary>
public sealed class SlackInboundTransportFactoryTests
{
    [Fact]
    public void Resolves_events_api_when_app_level_token_ref_is_null()
    {
        ISlackInboundTransportFactory factory = BuildFactory();
        SlackWorkspaceConfig ws = new()
        {
            TeamId = "T-EVT",
            SigningSecretRef = "env://SIG",
            AppLevelTokenRef = null,
            Enabled = true,
        };

        factory.ResolveTransportKind(ws).Should().Be(SlackInboundTransportKind.EventsApi,
            "absent AppLevelTokenRef => Events API per architecture.md §4.2");

        ISlackInboundTransport transport = factory.Create(ws);
        transport.Should().BeOfType<SlackEventsApiReceiver>();
    }

    [Fact]
    public void Resolves_events_api_when_app_level_token_ref_is_blank()
    {
        ISlackInboundTransportFactory factory = BuildFactory();
        SlackWorkspaceConfig ws = new()
        {
            TeamId = "T-EVT2",
            SigningSecretRef = "env://SIG",
            AppLevelTokenRef = "   ",
            Enabled = true,
        };

        factory.ResolveTransportKind(ws).Should().Be(SlackInboundTransportKind.EventsApi);
        factory.Create(ws).Should().BeOfType<SlackEventsApiReceiver>();
    }

    [Fact]
    public void Resolves_socket_mode_when_app_level_token_ref_is_set()
    {
        ISlackInboundTransportFactory factory = BuildFactory();
        SlackWorkspaceConfig ws = new()
        {
            TeamId = "T-SOCK",
            SigningSecretRef = "env://SIG",
            AppLevelTokenRef = "env://XAPP_TOKEN",
            Enabled = true,
        };

        factory.ResolveTransportKind(ws).Should().Be(SlackInboundTransportKind.SocketMode,
            "present AppLevelTokenRef => Socket Mode per architecture.md §4.2");
        factory.Create(ws).Should().BeOfType<SlackSocketModeReceiver>();
    }

    [Fact]
    public void Per_workspace_selection_is_independent()
    {
        ISlackInboundTransportFactory factory = BuildFactory();

        SlackWorkspaceConfig socketWs = new()
        {
            TeamId = "T-WS1",
            SigningSecretRef = "env://SIG1",
            AppLevelTokenRef = "env://XAPP_TOKEN1",
            Enabled = true,
        };
        SlackWorkspaceConfig eventsWs = new()
        {
            TeamId = "T-WS2",
            SigningSecretRef = "env://SIG2",
            AppLevelTokenRef = null,
            Enabled = true,
        };

        // Matches e2e scenario 17.3: per-workspace transport selection.
        factory.Create(socketWs).Should().BeOfType<SlackSocketModeReceiver>();
        factory.Create(eventsWs).Should().BeOfType<SlackEventsApiReceiver>();
    }

    private static ISlackInboundTransportFactory BuildFactory()
    {
        return new SlackInboundTransportFactory(
            secretProvider: new StubSecretProvider(),
            connectionFactory: new StubConnectionFactory(),
            inboundQueue: new ChannelBasedSlackInboundQueue(),
            deadLetterSink: new InMemorySlackInboundEnqueueDeadLetterSink(NullLogger<InMemorySlackInboundEnqueueDeadLetterSink>.Instance),
            timeProvider: TimeProvider.System,
            loggerFactory: NullLoggerFactory.Instance,
            options: Options.Create(new SlackSocketModeOptions()));
    }

    private sealed class StubSecretProvider : ISecretProvider
    {
        public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
            => Task.FromResult("xapp-test");
    }

    private sealed class StubConnectionFactory : ISlackSocketModeConnectionFactory
    {
        public Task<ISlackSocketModeConnection> ConnectAsync(string appLevelToken, CancellationToken ct)
            => throw new InvalidOperationException("Stub: connection should not be opened for resolve-only tests.");
    }
}
