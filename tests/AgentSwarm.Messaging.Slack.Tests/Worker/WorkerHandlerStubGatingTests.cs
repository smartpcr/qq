// -----------------------------------------------------------------------
// <copyright file="WorkerHandlerStubGatingTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Worker;

using System;
using System.Collections.Generic;
using System.IO;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Worker;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// Stage 4.3 iter-2 evaluator item #2 regression tests for the
/// <see cref="Program.EnableDevelopmentHandlerStubsKey"/> gate that
/// prevents the production Worker from silently ack-and-dropping
/// Slack traffic via the no-op handler stand-ins.
/// </summary>
/// <remarks>
/// <para>
/// The bug shape: <c>Program.cs</c> unconditionally called
/// <see cref="SlackInboundIngestorServiceCollectionExtensions.AddSlackInboundDevelopmentHandlerStubs"/>.
/// The stubs complete every envelope without producing an agent task,
/// app-mention action, or <c>HumanDecisionEvent</c>; the idempotency
/// guard then marks the row <c>completed</c>, so a Slack retry is
/// silently deduped and the message is permanently lost. The fix gates
/// the call on <c>Slack:Inbound:EnableDevelopmentHandlerStubs</c>
/// (defaulting to <see cref="IHostEnvironment.IsDevelopment"/>), so
/// Production / Staging / Testing hosts MUST register real Stage 5
/// handlers or fail fast at DI resolve time.
/// </para>
/// </remarks>
public sealed class WorkerHandlerStubGatingTests
{
    [Fact]
    public void Worker_in_production_environment_without_explicit_opt_in_does_NOT_register_no_op_handlers()
    {
        using GatedWorkerFactory factory = new(
            environment: "Production",
            stubOptInOverride: null);

        IServiceProvider services = factory.Services;

        services.GetService<ISlackCommandHandler>().Should().BeNull(
            "Production environment with no explicit opt-in MUST NOT silently register a no-op command handler -- silently ack-and-dropping every Slack command violates FR-005 / FR-007 zero-message-loss");
        services.GetService<ISlackAppMentionHandler>().Should().BeNull(
            "Production environment MUST NOT silently register a no-op app_mention handler");
        services.GetService<ISlackInteractionHandler>().Should().BeNull(
            "Production environment MUST NOT silently register a no-op interaction handler");
    }

    [Fact]
    public void Worker_in_development_environment_without_explicit_opt_in_DOES_register_no_op_handlers()
    {
        // Development environment defaults the gate to true so a dev
        // laptop continues to boot the Worker without requiring a
        // real Stage 5 handler set.
        using GatedWorkerFactory factory = new(
            environment: "Development",
            stubOptInOverride: null);

        IServiceProvider services = factory.Services;

        services.GetService<ISlackCommandHandler>().Should().BeOfType<NoOpSlackCommandHandler>(
            "Development environment MUST default to registering the no-op stand-ins so the ingestor pipeline resolves at startup");
        services.GetService<ISlackAppMentionHandler>().Should().BeOfType<NoOpSlackAppMentionHandler>();
        services.GetService<ISlackInteractionHandler>().Should().BeOfType<NoOpSlackInteractionHandler>();
    }

    [Fact]
    public void Worker_in_production_with_explicit_opt_out_does_NOT_register_no_op_handlers()
    {
        // The override `false` MUST win even in environments where
        // the default would have been true. (Production default is
        // already false; this test mainly guards against a future
        // change that flips the default.)
        using GatedWorkerFactory factory = new(
            environment: "Production",
            stubOptInOverride: false);

        IServiceProvider services = factory.Services;

        services.GetService<ISlackCommandHandler>().Should().BeNull();
    }

    [Fact]
    public void Worker_in_testing_environment_with_explicit_opt_in_DOES_register_no_op_handlers()
    {
        // An operator should be able to opt INTO the stubs in any
        // environment for a smoke test -- the gate is environment-
        // defaulted, not environment-locked.
        using GatedWorkerFactory factory = new(
            environment: "Testing",
            stubOptInOverride: true);

        IServiceProvider services = factory.Services;

        services.GetService<ISlackCommandHandler>().Should().BeOfType<NoOpSlackCommandHandler>(
            "explicit operator opt-in MUST win regardless of environment");
    }

    [Fact]
    public void Worker_in_production_default_gate_fails_fast_when_pipeline_is_resolved()
    {
        // The whole point of removing the no-op default is so a
        // production Worker that has not wired real Stage 5 handlers
        // fails LOUDLY at first envelope dispatch, instead of silently
        // ack-and-dropping. Verify the failure surface end-to-end.
        using GatedWorkerFactory factory = new(
            environment: "Production",
            stubOptInOverride: null);

        Action act = () => _ = factory.Services.GetRequiredService<SlackInboundProcessingPipeline>();

        act.Should().Throw<InvalidOperationException>(
            "with no handler registrations the pipeline ctor MUST throw at DI resolve time -- this is the fail-fast surface that prevents silent ack-and-drop in production");
    }

    /// <summary>
    /// Hosts the Worker via <see cref="WebApplicationFactory{TEntryPoint}"/>
    /// with a controllable environment + opt-in flag. Persistence is
    /// pointed at a per-test SQLite path so the test does not pollute
    /// the default <c>slack-audit.db</c> file.
    /// </summary>
    private sealed class GatedWorkerFactory : WebApplicationFactory<Program>
    {
        private readonly string environment;
        private readonly bool? stubOptInOverride;
        private readonly string sqlitePath = Path.Combine(Path.GetTempPath(),
            $"slack-stub-gate-{Guid.NewGuid():N}.db");

        public GatedWorkerFactory(string environment, bool? stubOptInOverride)
        {
            this.environment = environment;
            this.stubOptInOverride = stubOptInOverride;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(this.environment);

            // The opt-in MUST be visible to Program.BuildApp at the
            // moment Program.ShouldEnableDevelopmentHandlerStubs reads
            // it from builder.Configuration. IWebHostBuilder.UseSetting
            // pushes the value into the WebApplicationBuilder's live
            // ConfigurationManager BEFORE Main runs, unlike
            // ConfigureAppConfiguration whose in-memory collection is
            // applied later during host Build() and is therefore not
            // visible to gate logic that runs inside BuildApp.
            if (this.stubOptInOverride is bool value)
            {
                builder.UseSetting(
                    Program.EnableDevelopmentHandlerStubsKey,
                    value ? "true" : "false");
            }

            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                Dictionary<string, string?> overrides = new()
                {
                    ["SecretProvider:ProviderType"] = "InMemory",
                    ["ConnectionStrings:" + Program.SlackAuditConnectionStringKey] =
                        $"Data Source={this.sqlitePath}",

                    // Keep workspace seed minimal so the host doesn't
                    // try to call real Slack APIs for a workspace that
                    // does not exist.
                    ["Slack:Workspaces:0:TeamId"] = "T-stub-gate",
                    ["Slack:Workspaces:0:WorkspaceName"] = "Stub Gate Test",
                    ["Slack:Workspaces:0:SigningSecretRef"] = "test://signing/T-stub-gate",
                    ["Slack:Workspaces:0:BotTokenSecretRef"] = "test://bot/T-stub-gate",
                    ["Slack:Workspaces:0:DefaultChannelId"] = "C-stub-gate",
                    ["Slack:Workspaces:0:AllowedChannelIds:0"] = "C-stub-gate",
                    ["Slack:Workspaces:0:Enabled"] = "true",
                };

                cfg.AddInMemoryCollection(overrides);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing && File.Exists(this.sqlitePath))
            {
                try
                {
                    File.Delete(this.sqlitePath);
                }
                catch
                {
                    // best-effort cleanup; CI volume cleanup handles
                    // the rest.
                }
            }
        }
    }
}
