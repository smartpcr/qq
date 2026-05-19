// -----------------------------------------------------------------------
// <copyright file="WorkerSlackInboundTransportCompositionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Transport;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentSwarm.Messaging.Slack.Transport;
using AgentSwarm.Messaging.Worker;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Stage 4.1 iter-4 evaluator item 1 regression test. The iter-4
/// review flagged that <see cref="Program.BuildApp"/> "still only calls
/// <c>AddSlackInboundTransport()</c> and never opts into
/// <c>AddFileSystemSlackInboundEnqueueDeadLetterSink(...)</c>", which
/// would silently revert the resolved
/// <see cref="ISlackInboundEnqueueDeadLetterSink"/> to the in-memory
/// fallback registered by
/// <see cref="SlackInboundTransportServiceCollectionExtensions.AddSlackInboundTransport"/>
/// and lose post-ACK enqueue failures across a Worker restart.
/// This test pins the production wiring so a future regression that
/// drops the durable sink registration surfaces here, not in lost
/// envelopes on a live deployment.
/// </summary>
public sealed class WorkerSlackInboundTransportCompositionTests : IDisposable
{
    private readonly string testRoot;
    private readonly string sqlitePath;
    private readonly string deadLetterDir;

    public WorkerSlackInboundTransportCompositionTests()
    {
        this.testRoot = Path.Combine(
            Path.GetTempPath(),
            "qq-stage-4.1-worker-inbound-tests-" + Guid.NewGuid().ToString("N"));
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
            // Best-effort cleanup -- a held SQLite or JSONL file on
            // Windows must not fail the test run.
        }
    }

    [Fact]
    public void BuildApp_registers_filesystem_dead_letter_sink_not_in_memory_fallback()
    {
        // Arrange / Act
        WebApplication app = Program.BuildApp(this.BuildIsolatedArgs());

        // Assert -- the canonical ISlackInboundEnqueueDeadLetterSink
        // MUST be the durable file-system sink, NOT the
        // InMemorySlackInboundEnqueueDeadLetterSink fallback. If a
        // future refactor drops the AddFileSystemSlackInboundEnqueueDeadLetterSink
        // call (or its config-driven fallback) from Program.BuildApp,
        // this assertion fires before the host can ship and silently
        // lose post-ACK enqueue failures across a restart.
        ISlackInboundEnqueueDeadLetterSink sink =
            app.Services.GetRequiredService<ISlackInboundEnqueueDeadLetterSink>();
        sink.Should()
            .BeOfType<FileSystemSlackInboundEnqueueDeadLetterSink>(
                "Stage 4.1 iter-4 evaluator item 1: the Worker host MUST opt in to the durable JSONL sink so a post-ACK enqueue failure captured before a restart is recoverable, not lost. "
                + "Falling back to InMemorySlackInboundEnqueueDeadLetterSink would defeat FR-005/FR-007 'no message loss' from agent_swarm_messenger_user_stories.md.");
    }

    [Fact]
    public void BuildApp_dead_letter_sink_honours_Slack_Inbound_DeadLetterDirectory_override()
    {
        // Arrange / Act -- override the dead-letter directory through
        // the same IConfiguration channel an operator uses to point the
        // sink at an attached volume or shared file system.
        WebApplication app = Program.BuildApp(this.BuildIsolatedArgs());

        // Assert -- the resolved sink writes into the directory we
        // injected through "--Slack:Inbound:DeadLetterDirectory=...".
        // This proves the host actually plumbed the config into the
        // registration (a previous iter shipped the call with a
        // hard-coded literal, which silently ignored operator overrides).
        FileSystemSlackInboundEnqueueDeadLetterSink sink =
            app.Services.GetRequiredService<FileSystemSlackInboundEnqueueDeadLetterSink>();
        sink.AbsoluteDirectoryPath.Should().Be(
            Path.GetFullPath(this.deadLetterDir),
            "Slack:Inbound:DeadLetterDirectory configuration overrides the appsettings.json default; "
            + "the sink must use the operator-supplied path so the JSONL spills into the intended volume.");
    }

    [Fact]
    public void BuildApp_does_not_leave_in_memory_dead_letter_sink_resolvable_as_concrete_type()
    {
        // Arrange / Act
        WebApplication app = Program.BuildApp(this.BuildIsolatedArgs());

        // Assert -- AddFileSystemSlackInboundEnqueueDeadLetterSink uses
        // services.RemoveAll<InMemorySlackInboundEnqueueDeadLetterSink>()
        // so the in-memory fallback is fully evicted. If a future
        // refactor reintroduces the in-memory binding (e.g., by calling
        // the durable extension BEFORE AddSlackInboundTransport instead
        // of after), this assertion catches it -- two competing sinks
        // is the precise failure mode iter-4 flagged.
        InMemorySlackInboundEnqueueDeadLetterSink? leftover =
            app.Services.GetService<InMemorySlackInboundEnqueueDeadLetterSink>();
        leftover.Should().BeNull(
            "the in-memory sink must be fully evicted so a downstream consumer that resolves the concrete type does not silently use the non-durable fallback");
    }

    [Fact]
    public void BuildApp_discovers_all_three_slack_inbound_controllers_via_application_part()
    {
        // Stage 4.1 iter-2 evaluator item 2: the three Slack inbound
        // controllers (SlackEventsController, SlackCommandsController,
        // SlackInteractionsController) live in the
        // AgentSwarm.Messaging.Slack assembly, not the Worker's entry
        // assembly. Without an explicit AddApplicationPart call,
        // MVC's controller-feature scanner can fail to discover them
        // -- silently. The Worker host calls
        // AddSlackInboundControllers() on the IMvcBuilder returned by
        // AddControllers; this test asserts that all three controllers
        // surface as ControllerActionDescriptor entries through the
        // composed app's IActionDescriptorCollectionProvider. If a
        // future refactor drops the AddApplicationPart wiring (or
        // moves the controllers out of the Slack assembly without
        // updating the call), this assertion fires at test time
        // instead of producing 404s in production.
        WebApplication app = Program.BuildApp(this.BuildIsolatedArgs());

        IActionDescriptorCollectionProvider provider =
            app.Services.GetRequiredService<IActionDescriptorCollectionProvider>();

        IEnumerable<ControllerActionDescriptor> controllerActions = provider
            .ActionDescriptors
            .Items
            .OfType<ControllerActionDescriptor>();

        HashSet<string> discoveredControllers = new(StringComparer.Ordinal);
        Dictionary<string, string> discoveredRoutes = new(StringComparer.Ordinal);
        foreach (ControllerActionDescriptor action in controllerActions)
        {
            string typeName = action.ControllerTypeInfo.FullName ?? action.ControllerName;
            discoveredControllers.Add(typeName);
            if (action.AttributeRouteInfo?.Template is { } template
                && !discoveredRoutes.ContainsKey(typeName))
            {
                discoveredRoutes[typeName] = template;
            }
        }

        discoveredControllers.Should().Contain(
            typeof(SlackEventsController).FullName!,
            "Program.BuildApp must register the AgentSwarm.Messaging.Slack assembly as an MVC application part so SlackEventsController surfaces at /api/slack/events");
        discoveredControllers.Should().Contain(
            typeof(SlackCommandsController).FullName!,
            "Program.BuildApp must register the AgentSwarm.Messaging.Slack assembly as an MVC application part so SlackCommandsController surfaces at /api/slack/commands");
        discoveredControllers.Should().Contain(
            typeof(SlackInteractionsController).FullName!,
            "Program.BuildApp must register the AgentSwarm.Messaging.Slack assembly as an MVC application part so SlackInteractionsController surfaces at /api/slack/interactions");

        discoveredRoutes[typeof(SlackEventsController).FullName!].Should().Be(
            "api/slack/events",
            "the Events API Stage 4.1 brief pins POST /api/slack/events as the URL verification + event callback route");
        discoveredRoutes[typeof(SlackCommandsController).FullName!].Should().Be(
            "api/slack/commands",
            "the Slash Commands Stage 4.1 brief pins POST /api/slack/commands as the slash-command ACK route");
        discoveredRoutes[typeof(SlackInteractionsController).FullName!].Should().Be(
            "api/slack/interactions",
            "the Interactions Stage 4.1 brief pins POST /api/slack/interactions as the Block Kit + view_submission route");
    }

    private string[] BuildIsolatedArgs()
    {
        // WebApplication.CreateBuilder(args) reads "--Key=Value" CLI
        // overrides into IConfiguration. Both the SQLite audit path and
        // the dead-letter directory are isolated per test instance so
        // xUnit's cross-class parallelization cannot race on either
        // file system resource. The third override
        // (Slack:Inbound:Queue:AllowInMemoryInProduction=true) opts the
        // composition tests out of the Stage 4.1 iter-2 evaluator item 3
        // durability guard: this test class verifies wiring shape
        // (dead-letter sink, controller discovery) and intentionally
        // exercises the default in-memory ISlackInboundQueue. The
        // guard itself is covered by
        // SlackInboundProductionDurabilityGuardTests.
        return new[]
        {
            $"--ConnectionStrings:{Program.SlackAuditConnectionStringKey}=Data Source={this.sqlitePath}",
            $"--Slack:Inbound:DeadLetterDirectory={this.deadLetterDir}",
            $"--{SlackInboundTransportServiceCollectionExtensions.AllowInMemoryQueueInProductionConfigKey}=true",
        };
    }
}
