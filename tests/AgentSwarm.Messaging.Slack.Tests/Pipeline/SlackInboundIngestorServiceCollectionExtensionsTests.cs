// -----------------------------------------------------------------------
// <copyright file="SlackInboundIngestorServiceCollectionExtensionsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Linq;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Retry;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Tests.Persistence;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Stage 4.3 DI-shape tests for
/// <see cref="SlackInboundIngestorServiceCollectionExtensions.AddSlackInboundIngestor{TContext}"/>.
/// Pin the registration contract so a later refactor that drops a
/// binding (or downgrades an interface) is caught here.
/// </summary>
public sealed class SlackInboundIngestorServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSlackInboundIngestor_registers_every_pipeline_dependency()
    {
        using ServiceProvider provider = BuildProvider();

        // Resolving each interface MUST yield a non-null instance.
        provider.GetService<ISlackIdempotencyGuard>().Should().BeOfType<SlackIdempotencyGuard<SlackTestDbContext>>();
        provider.GetService<ISlackInboundAuthorizer>().Should().BeOfType<SlackInboundAuthorizer>();
        provider.GetService<ISlackRetryPolicy>().Should().BeOfType<DefaultSlackRetryPolicy>();
        provider.GetService<ISlackDeadLetterQueue>().Should().BeOfType<InMemorySlackDeadLetterQueue>();
        provider.GetService<SlackInboundAuditRecorder>().Should().NotBeNull();
        provider.GetService<SlackInboundProcessingPipeline>().Should().NotBeNull();
    }

    [Fact]
    public void AddSlackInboundIngestor_does_NOT_register_handler_defaults_so_production_hosts_fail_fast_without_real_handlers()
    {
        // Iter 5 evaluator item #2 (STRUCTURAL fix): the production
        // extension MUST NOT register the no-op handler stand-ins.
        // A host that forgot to wire real Stage 5 handlers WAS
        // silently ack-and-dropping Slack traffic via the no-ops;
        // dropping the defaults flips that into a clear DI startup
        // error.
        ServiceCollection services = BuildCommonServices();
        services.AddSlackInboundIngestor<SlackTestDbContext>();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetService<ISlackCommandHandler>().Should().BeNull(
            "production extension MUST NOT register a no-op default -- a host that forgot real handlers must fail fast at DI resolve time, not silently absorb traffic");
        provider.GetService<ISlackAppMentionHandler>().Should().BeNull(
            "production extension MUST NOT register a no-op default for app_mention handler");
        provider.GetService<ISlackInteractionHandler>().Should().BeNull(
            "production extension MUST NOT register a no-op default for interaction handler");

        // Resolving the pipeline itself MUST throw, proving the
        // fail-fast surface a production host would hit at startup.
        Action act = () => provider.GetRequiredService<SlackInboundProcessingPipeline>();
        act.Should().Throw<InvalidOperationException>(
            "the pipeline ctor takes ISlackCommandHandler etc by parameter; with no registration the DI container MUST throw so production startup catches the missing wiring");
    }

    [Fact]
    public void AddSlackInboundDevelopmentHandlerStubs_registers_the_three_no_op_handlers_only_when_explicitly_called()
    {
        // The dev-stubs extension is the explicit opt-in for
        // development hosts (the Worker, before Stage 5.x ships).
        ServiceCollection services = BuildCommonServices();
        services.AddSlackInboundIngestor<SlackTestDbContext>();
        services.AddSlackInboundDevelopmentHandlerStubs();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetService<ISlackCommandHandler>().Should().BeOfType<NoOpSlackCommandHandler>();
        provider.GetService<ISlackAppMentionHandler>().Should().BeOfType<NoOpSlackAppMentionHandler>();
        provider.GetService<ISlackInteractionHandler>().Should().BeOfType<NoOpSlackInteractionHandler>();

        // And the pipeline now resolves cleanly because every
        // handler dependency is present.
        provider.GetRequiredService<SlackInboundProcessingPipeline>().Should().NotBeNull();
    }

    [Fact]
    public void AddSlackInboundIngestor_registers_the_background_service()
    {
        using ServiceProvider provider = BuildProvider();

        IHostedService[] hostedServices = provider.GetServices<IHostedService>().ToArray();
        hostedServices.Should().Contain(svc => svc is SlackInboundIngestor,
            "the ingestor is a BackgroundService and MUST be hosted-service registered");
    }

    [Fact]
    public void AddSlackInboundIngestor_honours_preexisting_handler_overrides()
    {
        ServiceCollection services = BuildCommonServices();

        // Pre-register a stand-in handler BEFORE the extension call;
        // the TryAdd contract in the extension should leave it alone.
        services.AddSingleton<ISlackCommandHandler, StubCommandHandler>();

        services.AddSlackInboundIngestor<SlackTestDbContext>();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetService<ISlackCommandHandler>().Should().BeOfType<StubCommandHandler>();
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = BuildCommonServices();
        services.AddSlackInboundIngestor<SlackTestDbContext>();

        // Iter 5 evaluator item #2 (STRUCTURAL fix): the production
        // extension no longer registers no-op handler defaults.
        // The general "smoke that every dep is wired" provider opts
        // INTO the dev stubs so resolving the pipeline still works;
        // the contract that production defaults are absent is pinned
        // separately by
        // AddSlackInboundIngestor_does_NOT_register_handler_defaults_so_production_hosts_fail_fast_without_real_handlers.
        services.AddSlackInboundDevelopmentHandlerStubs();
        return services.BuildServiceProvider();
    }

    private static ServiceCollection BuildCommonServices()
    {
        ServiceCollection services = new();
        IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();
        services.AddSingleton(cfg);
        services.AddLogging();

        // The audit recorder depends on ISlackAuditEntryWriter;
        // register the in-memory writer for the DI smoke test.
        services.AddSingleton<AgentSwarm.Messaging.Slack.Persistence.ISlackAuditEntryWriter,
            AgentSwarm.Messaging.Slack.Persistence.InMemorySlackAuditEntryWriter>();

        // Slack connector options are needed by DefaultSlackRetryPolicy.
        services.AddSlackConnectorOptions(cfg);

        // AddSlackInboundTransport registers the in-process
        // ISlackInboundQueue that SlackInboundIngestor depends on.
        services.AddSlackInboundTransport();

        // The Stage 3.2 authorization stack registers
        // ISlackWorkspaceConfigStore, ISlackMembershipResolver, and
        // ISlackAuthorizationAuditSink that the envelope authorizer
        // depends on.
        services.AddSlackAuthorization(cfg);

        // DbContext for the EF guard.
        services.AddDbContext<SlackTestDbContext>(opts => opts.UseSqlite("DataSource=:memory:"));

        return services;
    }

    private sealed class StubCommandHandler : ISlackCommandHandler
    {
        public System.Threading.Tasks.Task HandleAsync(SlackInboundEnvelope envelope, System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.CompletedTask;
    }
}
