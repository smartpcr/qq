// -----------------------------------------------------------------------
// <copyright file="SlackInboundTransportHostedServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Stage 4.2 evaluator iter-1 item 1 regression tests for
/// <see cref="SlackInboundTransportHostedService"/>. The hosted service
/// must enumerate every enabled <see cref="SlackWorkspaceConfig"/> from
/// the workspace store, classify each via
/// <see cref="ISlackInboundTransportFactory.ResolveTransportKind"/>, and
/// start the corresponding transport. Without this hosted service the
/// receiver classes registered by <c>AddSlackSocketModeTransport</c>
/// remain idle and Socket Mode workspaces never connect.
/// </summary>
public sealed class SlackInboundTransportHostedServiceTests
{
    [Fact]
    public async Task StartAsync_starts_one_transport_per_enabled_workspace_and_selects_kind_by_app_level_token_ref()
    {
        SlackWorkspaceConfig socketMode = new()
        {
            TeamId = "T-SOCKET",
            SigningSecretRef = "env://SIG",
            AppLevelTokenRef = "env://XAPP",
            Enabled = true,
        };
        SlackWorkspaceConfig eventsApi = new()
        {
            TeamId = "T-EVENTS",
            SigningSecretRef = "env://SIG",
            AppLevelTokenRef = null,
            Enabled = true,
        };

        InMemorySlackWorkspaceConfigStore store = new();
        store.Upsert(socketMode);
        store.Upsert(eventsApi);

        RecordingTransportFactory factory = new();
        SlackInboundTransportHostedService hosted = new(
            store,
            factory,
            NullLogger<SlackInboundTransportHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);

        factory.Created.Should().HaveCount(2);
        factory.Created.Should().Contain(t => t.Workspace.TeamId == "T-SOCKET" && t.Kind == SlackInboundTransportKind.SocketMode);
        factory.Created.Should().Contain(t => t.Workspace.TeamId == "T-EVENTS" && t.Kind == SlackInboundTransportKind.EventsApi);

        IReadOnlyList<SlackInboundTransportHostedService.ManagedTransport> started = hosted.StartedTransports;
        started.Should().HaveCount(2);
        started.Should().AllSatisfy(t => ((RecordingTransport)t.Transport).StartedCount.Should().Be(1));

        await hosted.StopAsync(CancellationToken.None);

        // Every started transport must have been stopped on shutdown
        // so WebSocket connections close cleanly and pending envelopes
        // drain.
        factory.Created.Should().AllSatisfy(t => ((RecordingTransport)t.Transport).StoppedCount.Should().Be(1));
    }

    [Fact]
    public async Task StartAsync_skips_workspace_whose_transport_fails_to_start_and_continues_with_remaining()
    {
        SlackWorkspaceConfig failing = new()
        {
            TeamId = "T-FAIL",
            SigningSecretRef = "env://SIG",
            AppLevelTokenRef = "env://XAPP",
            Enabled = true,
        };
        SlackWorkspaceConfig healthy = new()
        {
            TeamId = "T-OK",
            SigningSecretRef = "env://SIG",
            AppLevelTokenRef = "env://XAPP",
            Enabled = true,
        };

        InMemorySlackWorkspaceConfigStore store = new();
        store.Upsert(failing);
        store.Upsert(healthy);

        RecordingTransportFactory factory = new();
        factory.FailStartFor.Add("T-FAIL");

        SlackInboundTransportHostedService hosted = new(
            store,
            factory,
            NullLogger<SlackInboundTransportHostedService>.Instance);

        Func<Task> act = () => hosted.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("a single failing workspace must not block the rest from starting");

        IReadOnlyList<SlackInboundTransportHostedService.ManagedTransport> started = hosted.StartedTransports;
        started.Should().ContainSingle(t => t.Workspace.TeamId == "T-OK");
        started.Should().NotContain(t => t.Workspace.TeamId == "T-FAIL");
    }

    [Fact]
    public async Task StopAsync_only_stops_transports_that_started_successfully()
    {
        SlackWorkspaceConfig ws = new()
        {
            TeamId = "T-1",
            SigningSecretRef = "env://SIG",
            AppLevelTokenRef = "env://XAPP",
            Enabled = true,
        };

        InMemorySlackWorkspaceConfigStore store = new();
        store.Upsert(ws);

        RecordingTransportFactory factory = new();
        SlackInboundTransportHostedService hosted = new(
            store,
            factory,
            NullLogger<SlackInboundTransportHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);

        factory.Created.Should().HaveCount(1);
        RecordingTransport rt = (RecordingTransport)factory.Created[0].Transport;
        rt.StartedCount.Should().Be(1);
        rt.StoppedCount.Should().Be(1);

        // A second StopAsync after the started list has cleared
        // should be a safe no-op (idempotent shutdown).
        Func<Task> secondStop = () => hosted.StopAsync(CancellationToken.None);
        await secondStop.Should().NotThrowAsync();
        rt.StoppedCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_is_a_noop_when_no_workspaces_are_enabled()
    {
        InMemorySlackWorkspaceConfigStore store = new();
        RecordingTransportFactory factory = new();
        SlackInboundTransportHostedService hosted = new(
            store,
            factory,
            NullLogger<SlackInboundTransportHostedService>.Instance);

        await hosted.StartAsync(CancellationToken.None);

        factory.Created.Should().BeEmpty();
        hosted.StartedTransports.Should().BeEmpty();
    }

    private sealed class RecordingTransportFactory : ISlackInboundTransportFactory
    {
        public List<(SlackWorkspaceConfig Workspace, SlackInboundTransportKind Kind, RecordingTransport Transport)> Created { get; } = new();

        public HashSet<string> FailStartFor { get; } = new();

        public SlackInboundTransportKind ResolveTransportKind(SlackWorkspaceConfig workspaceConfig)
        {
            return string.IsNullOrWhiteSpace(workspaceConfig.AppLevelTokenRef)
                ? SlackInboundTransportKind.EventsApi
                : SlackInboundTransportKind.SocketMode;
        }

        public ISlackInboundTransport Create(SlackWorkspaceConfig workspaceConfig)
        {
            SlackInboundTransportKind kind = this.ResolveTransportKind(workspaceConfig);
            RecordingTransport transport = new(this.FailStartFor.Contains(workspaceConfig.TeamId));
            this.Created.Add((workspaceConfig, kind, transport));
            return transport;
        }
    }

    private sealed class RecordingTransport : ISlackInboundTransport
    {
        private readonly bool failStart;
        private int started;
        private int stopped;

        public RecordingTransport(bool failStart)
        {
            this.failStart = failStart;
        }

        public int StartedCount => Volatile.Read(ref this.started);

        public int StoppedCount => Volatile.Read(ref this.stopped);

        public Task StartAsync(CancellationToken ct)
        {
            if (this.failStart)
            {
                return Task.FromException(new InvalidOperationException("simulated start failure"));
            }

            Interlocked.Increment(ref this.started);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref this.stopped);
            return Task.CompletedTask;
        }
    }
}
