// -----------------------------------------------------------------------
// <copyright file="SlackInboundProductionDurabilityGuardTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Collections.Generic;
using System.IO;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Transport;
using AgentSwarm.Messaging.Worker;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// Stage 4.1 iter-2 evaluator item 3: the Worker host MUST refuse to
/// boot in Production when the resolved
/// <see cref="ISlackInboundQueue"/> is the in-process
/// <see cref="ChannelBasedSlackInboundQueue"/>, because a pod restart
/// would lose every buffered envelope and violate FR-005 / FR-007
/// "no message loss" from <c>agent_swarm_messenger_user_stories.md</c>.
/// These tests pin every branch of the guard so a future refactor
/// cannot silently regress the safety net.
/// </summary>
public sealed class SlackInboundProductionDurabilityGuardTests : IDisposable
{
    private readonly string testRoot;
    private readonly string sqlitePath;
    private readonly string deadLetterDir;

    public SlackInboundProductionDurabilityGuardTests()
    {
        this.testRoot = Path.Combine(
            Path.GetTempPath(),
            "qq-stage-4.1-prod-guard-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.testRoot);
        this.sqlitePath = Path.Combine(this.testRoot, "slack-audit.db");
        this.deadLetterDir = Path.Combine(this.testRoot, "dead-letter");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.testRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; held SQLite/JSONL handles on
            // Windows must not fail the test run.
        }
    }

    [Fact]
    public void BuildApp_in_production_with_in_memory_queue_and_no_opt_in_throws()
    {
        // The Worker host defaults to ChannelBasedSlackInboundQueue
        // via AddSlackInboundTransport(). Without an explicit opt-in
        // and with ASPNETCORE_ENVIRONMENT=Production, the durability
        // guard MUST throw an InvalidOperationException pointing the
        // operator at the precise configuration key that unlocks the
        // in-memory default. This is the regression contract for
        // iter-2 evaluator item 3.
        string previousEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? string.Empty;
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");

            Action build = () => Program.BuildApp(this.BuildArgsWithoutOptIn());

            build.Should()
                .Throw<InvalidOperationException>(
                    "running in Production with the in-memory ChannelBasedSlackInboundQueue and without an explicit AllowInMemoryInProduction opt-in MUST fail-fast so the operator wires a durable queue before any inbound traffic is accepted")
                .WithMessage("*AllowInMemoryInProduction*",
                    "the exception message must name the exact configuration key the operator needs to flip");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnv);
        }
    }

    [Fact]
    public void BuildApp_in_production_with_explicit_opt_in_succeeds()
    {
        // An operator who has validated the in-memory queue is
        // acceptable for their deployment (single-instance, transient
        // workloads, behind idempotency + retries) can acknowledge
        // the trade-off via the AllowInMemoryInProduction config key.
        // The guard MUST honor the opt-in.
        string previousEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? string.Empty;
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");

            using WebApplication app = Program.BuildApp(this.BuildArgsWithOptIn());

            ISlackInboundQueue queue =
                app.Services.GetRequiredService<ISlackInboundQueue>();
            queue.Should()
                .BeOfType<ChannelBasedSlackInboundQueue>(
                    "AllowInMemoryInProduction=true is the operator's explicit acknowledgement that the in-memory queue is the resolved implementation");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnv);
        }
    }

    [Fact]
    public void BuildApp_in_development_with_in_memory_queue_does_not_throw()
    {
        // Local dev, CI runners, and Staging clones MUST be able to
        // boot with the default in-memory queue without any opt-in --
        // the guard is intentionally Production-only so it does not
        // make the developer inner loop noisy.
        string previousEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? string.Empty;
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

            using WebApplication app = Program.BuildApp(this.BuildArgsWithoutOptIn());

            ISlackInboundQueue queue =
                app.Services.GetRequiredService<ISlackInboundQueue>();
            queue.Should()
                .BeOfType<ChannelBasedSlackInboundQueue>(
                    "Development hosts default to the in-memory queue without complaint -- the guard is Production-only");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnv);
        }
    }

    [Fact]
    public void EnsureDurableInboundQueueForProduction_skips_non_production_environments()
    {
        // Unit-level pin on the guard helper so any future change to
        // the environment matcher (e.g., adding Staging coverage) is
        // a deliberate design decision, not an accidental regression.
        ServiceCollection services = new();
        services.AddSingleton<ISlackInboundQueue>(new ChannelBasedSlackInboundQueue());

        using ServiceProvider provider = services.BuildServiceProvider();

        IConfiguration cfg = new ConfigurationBuilder().Build();
        IHostEnvironment env = new StubHostEnvironment("Staging");

        Action guard = () => provider.EnsureDurableInboundQueueForProduction(env, cfg);

        guard.Should()
            .NotThrow(
                "Staging is not Production; the guard must short-circuit before inspecting the resolved queue type so deployment topologies that intentionally exercise the in-memory queue (canary, soak, perf) keep working");
    }

    [Fact]
    public void EnsureDurableInboundQueueForProduction_accepts_non_channel_based_queue()
    {
        // A composition root that registers a durable
        // ISlackInboundQueue implementation BEFORE
        // AddSlackInboundTransport() (which uses TryAdd) wins. The
        // guard must allow Production startup in that case even
        // without the AllowInMemoryInProduction opt-in.
        ServiceCollection services = new();
        services.AddSingleton<ISlackInboundQueue>(new DurableInboundQueueStub());

        using ServiceProvider provider = services.BuildServiceProvider();

        IConfiguration cfg = new ConfigurationBuilder().Build();
        IHostEnvironment env = new StubHostEnvironment("Production");

        Action guard = () => provider.EnsureDurableInboundQueueForProduction(env, cfg);

        guard.Should()
            .NotThrow(
                "the guard rejects ONLY the in-process ChannelBasedSlackInboundQueue; a durable replacement is the production-correct shape and must pass");
    }

    private string[] BuildArgsWithoutOptIn()
    {
        return new[]
        {
            $"--ConnectionStrings:{Program.SlackAuditConnectionStringKey}=Data Source={this.sqlitePath}",
            $"--Slack:Inbound:DeadLetterDirectory={this.deadLetterDir}",
        };
    }

    private string[] BuildArgsWithOptIn()
    {
        List<string> args = new(this.BuildArgsWithoutOptIn());
        args.Add($"--{SlackInboundTransportServiceCollectionExtensions.AllowInMemoryQueueInProductionConfigKey}=true");
        return args.ToArray();
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public StubHostEnvironment(string environmentName)
        {
            this.EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "AgentSwarm.Messaging.Slack.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class DurableInboundQueueStub : ISlackInboundQueue
    {
        public System.Threading.Tasks.ValueTask EnqueueAsync(SlackInboundEnvelope envelope)
            => System.Threading.Tasks.ValueTask.CompletedTask;

        public System.Threading.Tasks.ValueTask<SlackInboundEnvelope> DequeueAsync(
            System.Threading.CancellationToken ct)
            => throw new NotSupportedException("stub queue is not actually drained");
    }
}
