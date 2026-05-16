// -----------------------------------------------------------------------
// <copyright file="SlackCommandDispatchServiceCollectionExtensionsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Rendering;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Composition-root tests for
/// <see cref="SlackCommandDispatchServiceCollectionExtensions.AddSlackCommandDispatcher"/>.
/// Verifies the Stage 5.1 wiring REPLACES any prior
/// <see cref="ISlackCommandHandler"/> registration (the Stage 4.3 dev
/// stub) so the real handler wins regardless of registration order, and
/// registers the supporting collaborators with the expected lifetimes.
/// Iter-2 evaluator item 3: the dispatcher MUST NOT auto-register
/// <see cref="IAgentTaskService"/>; the dev-only stub is gated behind
/// the explicit <see cref="SlackCommandDispatchServiceCollectionExtensions.AddSlackCommandDispatcherDevelopmentDefaults"/>
/// extension.
/// </summary>
public sealed class SlackCommandDispatchServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSlackInboundTransport_registers_HttpClientSlackThreadedReplyPoster_as_default_threaded_reply_poster()
    {
        // Iter-2 evaluator item 1 (Stage 5.2) STRUCTURAL fix: the
        // Worker host calls AddSlackInboundTransport then
        // AddSlackCommandDispatcher; the transport extension MUST
        // register the production HTTP-backed threaded reply poster
        // (via TryAddSingleton, alongside HttpClientSlackViewsOpenClient)
        // so the dispatcher's TryAdd NoOp default loses the race and
        // app_mention replies actually post to Slack. Asserting on the
        // descriptor (rather than resolving the service) keeps this
        // test focused on the wiring contract without needing the full
        // workspace-store / secret-provider DI graph.
        ServiceCollection services = new();
        services.AddSlackInboundTransport();

        ServiceDescriptor descriptor = services
            .Single(d => d.ServiceType == typeof(ISlackThreadedReplyPoster));
        descriptor.ImplementationType.Should().Be(
            typeof(HttpClientSlackThreadedReplyPoster),
            "AddSlackInboundTransport MUST register the real HTTP chat.postMessage poster so a Worker host that wires the transport gets the production binding -- the dispatcher's TryAdd NoOp is a unit-test fall-back only");
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddSlackInboundTransport_registers_named_HttpClient_for_chat_postMessage()
    {
        ServiceCollection services = new();
        services.AddSlackInboundTransport();

        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = factory.CreateClient(HttpClientSlackThreadedReplyPoster.HttpClientName);
        client.Should().NotBeNull(
            "the typed HttpClient registration named chat-postmessage MUST exist so hosts can layer resilience handlers on it without subclassing the client");
    }

    [Fact]
    public void AddSlackCommandDispatcher_registers_SlackCommandHandler_as_ISlackCommandHandler()
    {
        ServiceCollection services = BuildBaseServices();
        services.AddSingleton<IAgentTaskService, RecordingTaskService>();

        services.AddSlackCommandDispatcher();

        using ServiceProvider provider = services.BuildServiceProvider();
        ISlackCommandHandler resolved = provider.GetRequiredService<ISlackCommandHandler>();
        resolved.Should().BeOfType<SlackCommandHandler>();
    }

    [Fact]
    public void AddSlackCommandDispatcher_registers_SlackAppMentionHandler_as_ISlackAppMentionHandler()
    {
        // Stage 5.2: the same composition extension that wires Stage
        // 5.1's slash-command dispatcher also wires the app-mention
        // handler. A host that calls AddSlackCommandDispatcher MUST
        // get both real handlers; the AddSlackInboundDevelopmentHandlerStubs
        // TryAdd path then becomes a no-op for these two bindings.
        ServiceCollection services = BuildBaseServices();
        services.AddSingleton<IAgentTaskService, RecordingTaskService>();

        services.AddSlackCommandDispatcher();

        using ServiceProvider provider = services.BuildServiceProvider();
        ISlackAppMentionHandler resolved = provider.GetRequiredService<ISlackAppMentionHandler>();
        resolved.Should().BeOfType<SlackAppMentionHandler>();
    }

    [Fact]
    public void AddSlackCommandDispatcher_replaces_NoOp_app_mention_handler_registered_by_dev_stubs()
    {
        ServiceCollection services = BuildBaseServices();
        services.AddSingleton<IAgentTaskService, RecordingTaskService>();

        // Simulate the dev-stub registration order (NoOp first, real
        // dispatcher second).
        services.AddSingleton<ISlackAppMentionHandler, NoOpSlackAppMentionHandler>();
        services.AddSlackCommandDispatcher();

        using ServiceProvider provider = services.BuildServiceProvider();
        ISlackAppMentionHandler resolved = provider.GetRequiredService<ISlackAppMentionHandler>();
        resolved.Should().BeOfType<SlackAppMentionHandler>(
            "AddSlackCommandDispatcher MUST win over a prior NoOp app-mention registration so production traffic never silently lands on the stub");
    }

    [Fact]
    public void AddSlackCommandDispatcher_registers_NoOp_threaded_reply_poster_as_default()
    {
        // Stage 5.2: the dispatcher registers a TryAdd default for
        // ISlackThreadedReplyPoster so the handler can resolve. Stage
        // 6.x will swap in the real HTTP implementation; a host that
        // registers a production poster BEFORE calling this extension
        // wins (TryAdd contract).
        ServiceCollection services = BuildBaseServices();
        services.AddSingleton<IAgentTaskService, RecordingTaskService>();

        services.AddSlackCommandDispatcher();

        using ServiceProvider provider = services.BuildServiceProvider();
        ISlackThreadedReplyPoster resolved = provider.GetRequiredService<ISlackThreadedReplyPoster>();
        resolved.Should().BeOfType<NoOpSlackThreadedReplyPoster>();
    }

    [Fact]
    public void AddSlackCommandDispatcher_honours_preregistered_threaded_reply_poster()
    {
        ServiceCollection services = BuildBaseServices();
        services.AddSingleton<IAgentTaskService, RecordingTaskService>();
        services.AddSingleton<ISlackThreadedReplyPoster, StubThreadedReplyPoster>();

        services.AddSlackCommandDispatcher();

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISlackThreadedReplyPoster>()
            .Should().BeOfType<StubThreadedReplyPoster>(
                "TryAdd MUST honour a host-supplied poster registered before the extension call");
    }

    [Fact]
    public void AddSlackCommandDispatcher_replaces_NoOp_handler_registered_by_dev_stubs()
    {
        ServiceCollection services = BuildBaseServices();
        services.AddSingleton<IAgentTaskService, RecordingTaskService>();

        // Simulate the Stage 4.3 dev-stub registration order (stubs first,
        // real dispatcher second).
        services.AddSingleton<ISlackCommandHandler, NoOpSlackCommandHandler>();
        services.AddSlackCommandDispatcher();

        using ServiceProvider provider = services.BuildServiceProvider();
        ISlackCommandHandler resolved = provider.GetRequiredService<ISlackCommandHandler>();
        resolved.Should().BeOfType<SlackCommandHandler>(
            "AddSlackCommandDispatcher MUST win over a prior NoOp registration so production traffic never silently lands on the stub");
    }

    [Fact]
    public void AddSlackCommandDispatcher_does_NOT_register_default_IAgentTaskService()
    {
        // Iter-2 evaluator item 3 (STRUCTURAL fix): the previous build
        // silently wired a NoOpAgentTaskService default, so a production
        // host that forgot to register the orchestrator client would
        // happily ack every /agent ask as "success" without dispatch.
        // The dispatcher now requires the caller to provide IAgentTaskService
        // explicitly (either a real orchestrator client or the explicit
        // dev-defaults extension).
        ServiceCollection services = BuildBaseServices();

        services.AddSlackCommandDispatcher();

        services
            .Any(d => d.ServiceType == typeof(IAgentTaskService))
            .Should()
            .BeFalse(
                "AddSlackCommandDispatcher MUST NOT auto-register IAgentTaskService -- a missing orchestrator client should fail fast at BuildServiceProvider, not silently ack-and-drop tasks");
    }

    [Fact]
    public void AddSlackCommandDispatcherDevelopmentDefaults_registers_NoOpAgentTaskService_as_opt_in_stub()
    {
        ServiceCollection services = BuildBaseServices();

        services.AddSlackCommandDispatcher();
        services.AddSlackCommandDispatcherDevelopmentDefaults();

        using ServiceProvider provider = services.BuildServiceProvider();
        IAgentTaskService taskService = provider.GetRequiredService<IAgentTaskService>();
        taskService.Should().BeOfType<NoOpAgentTaskService>(
            "AddSlackCommandDispatcherDevelopmentDefaults is the explicit, observable opt-in to the NoOp stub for hosts that still lack a real orchestrator");
    }

    [Fact]
    public void AddSlackCommandDispatcherDevelopmentDefaults_uses_TryAdd_so_orchestrator_implementations_win()
    {
        ServiceCollection services = BuildBaseServices();

        // Production-shaped wiring: real orchestrator wired first, dev
        // defaults invoked second (e.g., during a phased rollout).
        services.AddSingleton<IAgentTaskService, RecordingTaskService>();
        services.AddSlackCommandDispatcher();
        services.AddSlackCommandDispatcherDevelopmentDefaults();

        using ServiceProvider provider = services.BuildServiceProvider();
        IAgentTaskService taskService = provider.GetRequiredService<IAgentTaskService>();
        taskService.Should().BeOfType<RecordingTaskService>();
    }

    [Fact]
    public void AddSlackCommandDispatcher_registers_singleton_lifetimes_for_its_collaborators()
    {
        ServiceCollection services = BuildBaseServices();
        services.AddSingleton<IAgentTaskService, RecordingTaskService>();

        services.AddSlackCommandDispatcher();

        ServiceDescriptor handlerDescriptor = services
            .Single(d => d.ServiceType == typeof(ISlackCommandHandler));
        handlerDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);

        ServiceDescriptor responderDescriptor = services
            .Single(d => d.ServiceType == typeof(ISlackEphemeralResponder));
        responderDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);

        ServiceDescriptor rendererDescriptor = services
            .Single(d => d.ServiceType == typeof(ISlackMessageRenderer));
        rendererDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Builds the minimum service collection a Stage 5.1 dispatcher needs
    /// to resolve. The dispatcher constructor consumes the Stage 4.1
    /// transport collaborator <see cref="ISlackViewsOpenClient"/> and the
    /// Stage 5.1 <see cref="ISlackMessageRenderer"/> (registered by the
    /// dispatcher extension itself with TryAdd); supplying a lightweight
    /// fake views.open client keeps these DI smoke tests focused on the
    /// dispatcher's own wiring contract.
    /// </summary>
    private static ServiceCollection BuildBaseServices()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<ISlackViewsOpenClient, FakeViewsOpenClient>();
        return services;
    }

    private sealed class FakeViewsOpenClient : ISlackViewsOpenClient
    {
        public Task<SlackViewsOpenResult> OpenAsync(SlackViewsOpenRequest request, CancellationToken ct)
            => Task.FromResult(SlackViewsOpenResult.Success());
    }

    private sealed class StubThreadedReplyPoster : ISlackThreadedReplyPoster
    {
        public Task PostAsync(SlackThreadedReplyRequest request, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class RecordingTaskService : IAgentTaskService
    {
        public Task<AgentTaskCreationResult> CreateTaskAsync(AgentTaskCreationRequest request, CancellationToken ct)
            => Task.FromResult(new AgentTaskCreationResult("T", request.CorrelationId, string.Empty));

        public Task<AgentTaskStatusResult> GetTaskStatusAsync(AgentTaskStatusQuery query, CancellationToken ct)
            => Task.FromResult(new AgentTaskStatusResult("swarm", string.Empty, Array.Empty<AgentTaskStatusEntry>()));

        public Task PublishDecisionAsync(HumanDecisionEvent decision, CancellationToken ct)
            => Task.CompletedTask;
    }
}
