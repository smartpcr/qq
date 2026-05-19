// -----------------------------------------------------------------------
// <copyright file="SlackStartupDiagnosticsHostedServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Diagnostics;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Diagnostics;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 7.3 unit tests for
/// <see cref="SlackStartupDiagnosticsHostedService"/>. Pins the brief
/// scenario verbatim:
/// <list type="bullet">
///   <item><description>"Given the connector starts with 2 configured
///   workspaces, When startup completes, Then structured logs include
///   workspace IDs and transport types."</description></item>
/// </list>
/// </summary>
public sealed class SlackStartupDiagnosticsHostedServiceTests
{
    [Fact]
    public async Task Brief_scenario_two_workspaces_produces_structured_logs_with_team_id_and_transport()
    {
        // Brief scenario: 2 configured workspaces; the first is
        // Events API (empty AppLevelTokenRef), the second is Socket
        // Mode (non-empty AppLevelTokenRef). The diagnostics line MUST
        // carry both the structured field (TeamId, TransportKind) AND
        // the human-readable display string.
        FakeWorkspaceStore store = new(
            new SlackWorkspaceConfig
            {
                TeamId = "T-EVENTS-1",
                WorkspaceName = "Acme Events",
                BotTokenSecretRef = "env://BOT-1",
                SigningSecretRef = "env://SIG-1",
                AppLevelTokenRef = string.Empty,
                Enabled = true,
            },
            new SlackWorkspaceConfig
            {
                TeamId = "T-SOCKET-1",
                WorkspaceName = "Acme Socket",
                BotTokenSecretRef = "env://BOT-2",
                SigningSecretRef = "env://SIG-2",
                AppLevelTokenRef = "env://APP-2",
                Enabled = true,
            });

        StubTransportFactory factory = new();
        RecordingLogger logger = new();
        ServiceCollection services = new();
        services.AddSingleton<ISlackInboundTransportFactory>(factory);
        ServiceProvider sp = services.BuildServiceProvider();

        SlackStartupDiagnosticsHostedService svc = new(
            store,
            sp,
            Options.Create(new SlackConnectorOptions()),
            logger);

        await svc.StartAsync(CancellationToken.None);

        // Header line MUST report the workspace count.
        logger.Records.Should().Contain(r =>
            r.Message.Contains("2 enabled workspace(s) registered"));

        // Per-workspace diagnostic lines MUST carry team id and
        // transport classification (the brief: "structured logs
        // include workspace IDs and transport types").
        logger.Records.Should().Contain(r =>
            r.Message.Contains("TeamId=T-EVENTS-1") &&
            r.Message.Contains("TransportKindDisplay=Events API"));

        logger.Records.Should().Contain(r =>
            r.Message.Contains("TeamId=T-SOCKET-1") &&
            r.Message.Contains("TransportKindDisplay=Socket Mode"));

        // Rate-limit summary line MUST be emitted after the
        // per-workspace lines.
        logger.Records.Should().Contain(r =>
            r.Message.Contains("rate-limit configuration") &&
            r.Message.Contains("MaxWorkspaces="));
    }

    [Fact]
    public async Task No_workspaces_produces_explicit_zero_log_and_no_throw()
    {
        FakeWorkspaceStore store = new();
        StubTransportFactory factory = new();
        RecordingLogger logger = new();
        ServiceCollection services = new();
        services.AddSingleton<ISlackInboundTransportFactory>(factory);
        ServiceProvider sp = services.BuildServiceProvider();

        SlackStartupDiagnosticsHostedService svc = new(
            store,
            sp,
            Options.Create(new SlackConnectorOptions()),
            logger);

        await svc.StartAsync(CancellationToken.None);

        logger.Records.Should().Contain(r =>
            r.Message.Contains("no enabled workspaces are registered"));

        // Even with zero workspaces the rate-limit configuration line
        // MUST still be emitted so operators see what defaults the
        // pod booted with.
        logger.Records.Should().Contain(r =>
            r.Message.Contains("rate-limit configuration"));
    }

    [Fact]
    public async Task Missing_transport_factory_infers_transport_kind_from_workspace_config()
    {
        // Stage 7.3 evaluator iter-2 item 2: the Stage 4.1 Events-API
        // Worker composition wires AddSlackInboundTransport but NOT
        // AddSlackSocketModeTransport, so ISlackInboundTransportFactory
        // is unregistered. The brief requires "log active workspaces,
        // transport type per workspace (Events API vs Socket Mode)"
        // regardless of which inbound extension is wired. The hosted
        // service MUST infer the transport kind directly from the
        // workspace's AppLevelTokenRef (mirrors
        // SlackInboundTransportFactory.ResolveTransportKind) and emit
        // the brief-required transport type. This test pins both
        // branches (empty AppLevelTokenRef -> Events API; non-empty
        // -> Socket Mode) so a future refactor cannot silently
        // regress the canonical Worker host back to "Unknown".
        FakeWorkspaceStore store = new(
            new SlackWorkspaceConfig
            {
                TeamId = "T-EVENTS-NOFACTORY",
                WorkspaceName = "Acme Events (no factory)",
                BotTokenSecretRef = "env://BOT",
                SigningSecretRef = "env://SIG",
                AppLevelTokenRef = string.Empty,
                Enabled = true,
            },
            new SlackWorkspaceConfig
            {
                TeamId = "T-SOCKET-NOFACTORY",
                WorkspaceName = "Acme Socket (no factory)",
                BotTokenSecretRef = "env://BOT-2",
                SigningSecretRef = "env://SIG-2",
                AppLevelTokenRef = "env://APP-2",
                Enabled = true,
            });

        RecordingLogger logger = new();

        // Empty service provider -> no ISlackInboundTransportFactory.
        ServiceProvider sp = new ServiceCollection().BuildServiceProvider();

        SlackStartupDiagnosticsHostedService svc = new(
            store,
            sp,
            Options.Create(new SlackConnectorOptions()),
            logger);

        await svc.StartAsync(CancellationToken.None);

        // The Events API workspace MUST be classified as such from
        // the absent AppLevelTokenRef even though no factory was
        // registered.
        logger.Records.Should().Contain(r =>
            r.Message.Contains("TeamId=T-EVENTS-NOFACTORY") &&
            r.Message.Contains("TransportKindDisplay=Events API") &&
            r.Message.Contains("TransportClassificationSource=config-inferred"));

        // The Socket Mode workspace MUST be classified from the
        // present AppLevelTokenRef.
        logger.Records.Should().Contain(r =>
            r.Message.Contains("TeamId=T-SOCKET-NOFACTORY") &&
            r.Message.Contains("TransportKindDisplay=Socket Mode") &&
            r.Message.Contains("TransportClassificationSource=config-inferred"));

        // The brief-forbidden "Unknown" fallback MUST NOT appear in
        // any per-workspace diagnostic line.
        logger.Records.Should().NotContain(r =>
            r.Message.Contains("TransportKindDisplay=Unknown"));
    }

    [Fact]
    public void InferTransportKindFromConfig_classifies_using_AppLevelTokenRef()
    {
        // Pure unit test of the inference helper so the inference
        // contract is locked independent of the logging shape. Mirrors
        // SlackInboundTransportFactory.ResolveTransportKind exactly.
        SlackStartupDiagnosticsHostedService
            .InferTransportKindFromConfig(new SlackWorkspaceConfig
            {
                TeamId = "T-1",
                WorkspaceName = "x",
                BotTokenSecretRef = "x",
                SigningSecretRef = "x",
                AppLevelTokenRef = string.Empty,
            })
            .Should().Be(SlackInboundTransportKind.EventsApi);

        SlackStartupDiagnosticsHostedService
            .InferTransportKindFromConfig(new SlackWorkspaceConfig
            {
                TeamId = "T-2",
                WorkspaceName = "x",
                BotTokenSecretRef = "x",
                SigningSecretRef = "x",
                AppLevelTokenRef = null,
            })
            .Should().Be(SlackInboundTransportKind.EventsApi);

        SlackStartupDiagnosticsHostedService
            .InferTransportKindFromConfig(new SlackWorkspaceConfig
            {
                TeamId = "T-3",
                WorkspaceName = "x",
                BotTokenSecretRef = "x",
                SigningSecretRef = "x",
                AppLevelTokenRef = "env://APP-3",
            })
            .Should().Be(SlackInboundTransportKind.SocketMode);
    }

    [Fact]
    public async Task Workspace_enumeration_throw_is_swallowed_with_warning()
    {
        ThrowingWorkspaceStore store = new(new InvalidOperationException("boom"));
        RecordingLogger logger = new();
        ServiceProvider sp = new ServiceCollection().BuildServiceProvider();

        SlackStartupDiagnosticsHostedService svc = new(
            store,
            sp,
            Options.Create(new SlackConnectorOptions()),
            logger);

        Func<Task> act = () => svc.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync(
            "host startup must never fail because of a diagnostics-side throw");

        logger.Records.Should().Contain(r =>
            r.Level == LogLevel.Warning &&
            r.Message.Contains("failed to enumerate workspaces"));
    }

    private sealed class FakeWorkspaceStore : ISlackWorkspaceConfigStore
    {
        private readonly List<SlackWorkspaceConfig> workspaces;

        public FakeWorkspaceStore(params SlackWorkspaceConfig[] workspaces)
        {
            this.workspaces = workspaces.ToList();
        }

        public Task<SlackWorkspaceConfig?> GetByTeamIdAsync(string? teamId, CancellationToken ct)
            => Task.FromResult<SlackWorkspaceConfig?>(this.workspaces.Find(w => w.TeamId == teamId));

        public Task<IReadOnlyCollection<SlackWorkspaceConfig>> GetAllEnabledAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyCollection<SlackWorkspaceConfig>>(this.workspaces.ToArray());
    }

    private sealed class ThrowingWorkspaceStore : ISlackWorkspaceConfigStore
    {
        private readonly Exception exception;

        public ThrowingWorkspaceStore(Exception exception) => this.exception = exception;

        public Task<SlackWorkspaceConfig?> GetByTeamIdAsync(string? teamId, CancellationToken ct)
            => Task.FromException<SlackWorkspaceConfig?>(this.exception);

        public Task<IReadOnlyCollection<SlackWorkspaceConfig>> GetAllEnabledAsync(CancellationToken ct)
            => Task.FromException<IReadOnlyCollection<SlackWorkspaceConfig>>(this.exception);
    }

    private sealed class StubTransportFactory : ISlackInboundTransportFactory
    {
        public SlackInboundTransportKind ResolveTransportKind(SlackWorkspaceConfig workspaceConfig)
            => string.IsNullOrWhiteSpace(workspaceConfig.AppLevelTokenRef)
                ? SlackInboundTransportKind.EventsApi
                : SlackInboundTransportKind.SocketMode;

        public ISlackInboundTransport Create(SlackWorkspaceConfig workspaceConfig)
            => throw new NotSupportedException(
                "StubTransportFactory is for ResolveTransportKind only; the diagnostics path must never call Create.");
    }

    private sealed class RecordingLogger : ILogger<SlackStartupDiagnosticsHostedService>
    {
        private readonly List<LogRecord> records = new();

        public IReadOnlyList<LogRecord> Records => this.records;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            this.records.Add(new LogRecord(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogRecord(LogLevel Level, string Message);
}
