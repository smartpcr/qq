// -----------------------------------------------------------------------
// <copyright file="StubGuardHealthCheckTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram;
using AgentSwarm.Messaging.Telegram.Swarm;
using AgentSwarm.Messaging.Worker.Observability;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

/// <summary>
/// Stage 6.3 — pins <see cref="StubGuardHealthCheck"/> against the
/// brief: "asserts none are stub implementations when
/// ASPNETCORE_ENVIRONMENT is Production; if any stub is detected,
/// return HealthCheckResult.Unhealthy(...) — this prevents production
/// deployments from running with stubs while allowing integration
/// tests and dev mode to use them freely". The tests cover:
/// <list type="bullet">
///   <item><description>Production + at least one stub → Unhealthy with the brief-mandated description text.</description></item>
///   <item><description>Production + all concrete implementations → Healthy.</description></item>
///   <item><description>Development (and any other non-Production env) + stubs → Healthy (the guard is intentionally a no-op outside Production so integration tests are not regressed).</description></item>
/// </list>
/// <para>
/// Stage 6.3 iter-3 evaluator items 1-3 — the AllowedStubInterfaces
/// bypass mechanism that earlier iterations of this suite asserted
/// was removed by the evaluator. The strict fail-closed contract is
/// pinned here: no configuration value can grant a Production stub
/// a pass.
/// </para>
/// </summary>
public sealed class StubGuardHealthCheckTests
{
    /// <summary>
    /// Lightweight non-stub <see cref="ISwarmCommandBus"/> for the
    /// "all concrete" assertion. Mirrors the shape the production
    /// adapter is expected to take — its only role here is to be
    /// a type the <see cref="StubGuardHealthCheck"/>'s
    /// <see cref="StubSwarmCommandBus"/> check will reject.
    /// </summary>
    private sealed class FakeProductionSwarmCommandBus : ISwarmCommandBus
    {
        public Task PublishCommandAsync(SwarmCommand command, CancellationToken ct)
            => Task.CompletedTask;

        public Task PublishHumanDecisionAsync(HumanDecisionEvent decision, CancellationToken ct)
            => Task.CompletedTask;

        public Task<SwarmStatusSummary> QueryStatusAsync(SwarmStatusQuery query, CancellationToken ct)
            => Task.FromResult(new SwarmStatusSummary
            {
                WorkspaceId = query.WorkspaceId,
                State = "running",
            });

        public Task<IReadOnlyList<AgentInfo>> QueryAgentsAsync(SwarmAgentsQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AgentInfo>>(Array.Empty<AgentInfo>());

        public async IAsyncEnumerable<SwarmEvent> SubscribeAsync(
            string tenantId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    private static IHostEnvironment ProductionEnvironment()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);
        return env.Object;
    }

    private static IHostEnvironment DevelopmentEnvironment()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
        return env.Object;
    }

    private static StubOperatorRegistry NewStubOperatorRegistry()
        => new(new StaticOptionsMonitor<TelegramOptions>(new TelegramOptions()));

    private static StubTaskOversightRepository NewStubTaskOversightRepository()
        => new();

    private static StubSwarmCommandBus NewStubSwarmCommandBus()
        => new(NullLogger<StubSwarmCommandBus>.Instance);

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) { CurrentValue = value; }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>
    /// Builds a real <see cref="PersistentOperatorRegistry"/> backed by
    /// a unique in-memory SQLite database. Used to satisfy the
    /// "all concrete" assertion without standing up a full host.
    /// </summary>
    private static (PersistentOperatorRegistry Registry, ServiceProvider Provider) NewPersistentOperatorRegistry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var connection = "DataSource=stubguard-or-" + Guid.NewGuid().ToString("N") + ";Mode=Memory;Cache=Shared";
        services.AddDbContext<MessagingDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton<PersistentOperatorRegistry>();
        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            db.Database.EnsureCreated();
        }

        return (provider.GetRequiredService<PersistentOperatorRegistry>(), provider);
    }

    private static (PersistentTaskOversightRepository Repo, ServiceProvider Provider) NewPersistentTaskOversightRepository()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var connection = "DataSource=stubguard-tor-" + Guid.NewGuid().ToString("N") + ";Mode=Memory;Cache=Shared";
        services.AddDbContext<MessagingDbContext>(o => o.UseSqlite(connection));
        services.AddSingleton<PersistentTaskOversightRepository>();
        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            db.Database.EnsureCreated();
        }

        return (provider.GetRequiredService<PersistentTaskOversightRepository>(), provider);
    }

    [Fact]
    public async Task CheckHealthAsync_InProduction_WhenOperatorRegistryIsStub_ReturnsUnhealthyWithBriefMandatedDescription()
    {
        // Brief: "if any stub is detected, return HealthCheckResult.Unhealthy(
        //  \"Stub {InterfaceName} detected in Production — register concrete
        //  implementation\")". The description text is part of the contract
        // because operator dashboards / alert routers match on it.
        var check = new StubGuardHealthCheck(
            operatorRegistry: NewStubOperatorRegistry(),
            taskOversightRepository: NewStubTaskOversightRepository(),
            swarmCommandBus: NewStubSwarmCommandBus(),
            environment: ProductionEnvironment());

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(
            HealthStatus.Unhealthy,
            "the brief mandates Unhealthy when any stub is detected in Production");
        result.Description.Should()
            .Contain("Stub").And
            .Contain("detected in Production").And
            .Contain("register concrete implementation");
    }

    [Fact]
    public async Task CheckHealthAsync_InProduction_DescriptionNamesTheFirstStubInterface()
    {
        // The brief's literal description uses the INTERFACE name
        // (IOperatorRegistry / ITaskOversightRepository /
        // ISwarmCommandBus) — the operator-facing actionable signal —
        // not the FullName of the stub class.
        var check = new StubGuardHealthCheck(
            operatorRegistry: NewStubOperatorRegistry(),
            taskOversightRepository: NewStubTaskOversightRepository(),
            swarmCommandBus: NewStubSwarmCommandBus(),
            environment: ProductionEnvironment());

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Description.Should().Contain(
            nameof(IOperatorRegistry),
            "the description must name the interface so the operator knows which DI registration is missing");
    }

    [Fact]
    public async Task CheckHealthAsync_InProduction_ListsEveryOffenderInDataDictionary()
    {
        // When multiple stubs leak into Production the guard surfaces
        // ALL of them in the `data` dictionary so the operator can fix
        // every missing registration in a single round-trip instead of
        // re-rolling after every fix.
        var check = new StubGuardHealthCheck(
            operatorRegistry: NewStubOperatorRegistry(),
            taskOversightRepository: NewStubTaskOversightRepository(),
            swarmCommandBus: NewStubSwarmCommandBus(),
            environment: ProductionEnvironment());

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data.Should().ContainKey("stubInterfaces");
        var stubs = (string[])result.Data["stubInterfaces"];
        stubs.Should().BeEquivalentTo(new[]
        {
            nameof(IOperatorRegistry),
            nameof(ITaskOversightRepository),
            nameof(ISwarmCommandBus),
        });
    }

    [Fact]
    public async Task CheckHealthAsync_InProduction_WhenOnlySwarmCommandBusIsStub_NamesSwarmCommandBus()
    {
        // The persistence module replaces IOperatorRegistry and
        // ITaskOversightRepository with their persistent siblings via
        // AddMessagingPersistence; ISwarmCommandBus has no shipped
        // production replacement in this story, so it is the most
        // likely real-world offender. Pin that the guard names
        // ISwarmCommandBus when it is the only stub left.
        var (operatorRegistry, providerOr) = NewPersistentOperatorRegistry();
        var (taskOversightRepository, providerTor) = NewPersistentTaskOversightRepository();
        using var _1 = providerOr;
        using var _2 = providerTor;

        var check = new StubGuardHealthCheck(
            operatorRegistry: operatorRegistry,
            taskOversightRepository: taskOversightRepository,
            swarmCommandBus: NewStubSwarmCommandBus(),
            environment: ProductionEnvironment());

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain(nameof(ISwarmCommandBus));

        var stubs = (string[])result.Data["stubInterfaces"];
        stubs.Should().ContainSingle().Which.Should().Be(nameof(ISwarmCommandBus));
    }

    [Fact]
    public async Task CheckHealthAsync_InProduction_WhenAllConcrete_ReturnsHealthy()
    {
        // The check must NOT flap green-yellow-green on a fully
        // configured production host. Wire the persistent
        // registry/repository (real concrete types) and a non-stub
        // ISwarmCommandBus stand-in and assert Healthy.
        var (operatorRegistry, providerOr) = NewPersistentOperatorRegistry();
        var (taskOversightRepository, providerTor) = NewPersistentTaskOversightRepository();
        using var _1 = providerOr;
        using var _2 = providerTor;

        var check = new StubGuardHealthCheck(
            operatorRegistry: operatorRegistry,
            taskOversightRepository: taskOversightRepository,
            swarmCommandBus: new FakeProductionSwarmCommandBus(),
            environment: ProductionEnvironment());

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(
            HealthStatus.Healthy,
            "with concrete implementations of every guarded abstraction the production-readiness gate must not trip");
    }

    [Fact]
    public async Task CheckHealthAsync_InDevelopment_WithStubs_ReturnsHealthy()
    {
        // Brief: "allowing integration tests and dev mode to use them
        // freely". The guard is a Production-only gate; in
        // Development / Staging / Test environments the stubs are an
        // accepted configuration so /healthz must remain green.
        var check = new StubGuardHealthCheck(
            operatorRegistry: NewStubOperatorRegistry(),
            taskOversightRepository: NewStubTaskOversightRepository(),
            swarmCommandBus: NewStubSwarmCommandBus(),
            environment: DevelopmentEnvironment());

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(
            HealthStatus.Healthy,
            "stubs are explicitly permitted outside Production — the brief calls this out by name");
        result.Data["environment"].Should().Be(Environments.Development);
        result.Data["active"].Should().Be(false);
    }

    [Fact]
    public async Task CheckHealthAsync_InCustomNonProductionEnvironment_WithStubs_ReturnsHealthy()
    {
        // Some hosts use bespoke environment names ("Staging",
        // "Integration", "QA"). Any name other than Production must
        // leave the check as a Healthy no-op so the guard is
        // unambiguous: "Production = strict, everything else = lenient".
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Integration");

        var check = new StubGuardHealthCheck(
            operatorRegistry: NewStubOperatorRegistry(),
            taskOversightRepository: NewStubTaskOversightRepository(),
            swarmCommandBus: NewStubSwarmCommandBus(),
            environment: env.Object);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["environment"].Should().Be("Integration");
    }

    [Fact]
    public void Constructor_NullDependency_Throws()
    {
        var operatorRegistry = NewStubOperatorRegistry();
        var taskOversightRepository = NewStubTaskOversightRepository();
        var swarmCommandBus = NewStubSwarmCommandBus();
        var env = ProductionEnvironment();

        Action a = () => new StubGuardHealthCheck(
            null!, taskOversightRepository, swarmCommandBus, env);
        a.Should().Throw<ArgumentNullException>();

        Action b = () => new StubGuardHealthCheck(
            operatorRegistry, null!, swarmCommandBus, env);
        b.Should().Throw<ArgumentNullException>();

        Action c = () => new StubGuardHealthCheck(
            operatorRegistry, taskOversightRepository, null!, env);
        c.Should().Throw<ArgumentNullException>();

        Action d = () => new StubGuardHealthCheck(
            operatorRegistry, taskOversightRepository, swarmCommandBus, null!);
        d.Should().Throw<ArgumentNullException>();
    }
}
