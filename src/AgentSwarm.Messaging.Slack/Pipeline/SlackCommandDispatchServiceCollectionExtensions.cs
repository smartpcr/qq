// -----------------------------------------------------------------------
// <copyright file="SlackCommandDispatchServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Slack.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Composition-root wiring for Stage 5.1's slash-command dispatcher
/// and Stage 5.2's app-mention handler.
/// </summary>
/// <remarks>
/// <para>
/// Registers <see cref="SlackCommandHandler"/> as the production
/// <see cref="ISlackCommandHandler"/> binding and
/// <see cref="SlackAppMentionHandler"/> as the production
/// <see cref="ISlackAppMentionHandler"/> binding consumed by the Stage
/// 4.3 <see cref="SlackInboundProcessingPipeline"/>. Hosts that already
/// registered alternative implementations (e.g., the Stage 4.3
/// dev stand-ins <see cref="NoOpSlackCommandHandler"/> /
/// <see cref="NoOpSlackAppMentionHandler"/> for integration tests)
/// MUST call <c>RemoveAll</c> before invoking this extension; the
/// registrations use unconditional <c>AddSingleton</c> because Stage
/// 5.1 / 5.2's whole purpose is to replace those stand-ins.
/// </para>
/// <para>
/// <b>Iter-2 evaluator item 3 fix:</b> this extension NO LONGER
/// registers a default <see cref="IAgentTaskService"/>. The previous
/// behaviour silently wired a <see cref="NoOpAgentTaskService"/> stub,
/// so a production host that forgot to register the real orchestrator
/// would happily ack every <c>/agent ask</c> as "success" without ever
/// dispatching the work. Production hosts MUST register an
/// <see cref="IAgentTaskService"/> implementation BEFORE calling this
/// extension; if they do not, DI resolution will throw at
/// <c>BuildServiceProvider</c> time. Development hosts that want the
/// old NoOp behaviour opt in explicitly via
/// <see cref="AddSlackCommandDispatcherDevelopmentDefaults"/>.
/// </para>
/// <para>
/// The extension does wire the production
/// <see cref="ISlackEphemeralResponder"/> (HTTP-backed) and the Stage 5.1
/// <see cref="ISlackMessageRenderer"/> default using
/// <c>TryAddSingleton</c> so a host-supplied implementation registered
/// earlier wins.
/// </para>
/// </remarks>
public static class SlackCommandDispatchServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Stage 5.1 slash-command dispatcher and its
    /// collaborators on <paramref name="services"/>. Does NOT register
    /// a default <see cref="IAgentTaskService"/>; the caller MUST wire
    /// one separately (production: a real orchestrator client;
    /// development / tests: <see cref="AddSlackCommandDispatcherDevelopmentDefaults"/>).
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddSlackCommandDispatcher(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Ephemeral responder posts to response_url; the named
        // HttpClient lets hosts layer resilience handlers (retry,
        // circuit-breaker) by name without subclassing the responder.
        services.AddHttpClient(HttpClientSlackEphemeralResponder.HttpClientName);
        services.TryAddSingleton<ISlackEphemeralResponder, HttpClientSlackEphemeralResponder>();

        // Stage 5.1 renderer for review / escalate modal payloads.
        // Also registered (via TryAdd) inside AddSlackInboundTransport
        // so the synchronous modal fast-path resolves the same default.
        services.TryAddSingleton<ISlackMessageRenderer, DefaultSlackMessageRenderer>();

        // Stage 5.2: default threaded-reply poster. NoOp until Stage
        // 6.x ships the HTTP chat.postMessage client. TryAdd so a host
        // that has already registered a production poster (e.g. the
        // Stage 6.x outbound dispatcher) wins.
        services.TryAddSingleton<ISlackThreadedReplyPoster, NoOpSlackThreadedReplyPoster>();

        // Real command handler -- REPLACES any earlier ISlackCommandHandler
        // registration (notably the Stage 4.3 NoOpSlackCommandHandler
        // dev-stub). The Stage 4.3 ingestor extension intentionally
        // does NOT register a default for this interface, so production
        // hosts that forget to call AddSlackCommandDispatcher will fail
        // fast at DI resolve time (the ingestor pipeline ctor takes
        // ISlackCommandHandler by parameter).
        services.RemoveAll<ISlackCommandHandler>();
        services.AddSingleton<SlackCommandHandler>();
        services.AddSingleton<ISlackCommandHandler>(sp => sp.GetRequiredService<SlackCommandHandler>());

        // Stage 5.2 app-mention handler. Same RemoveAll + AddSingleton
        // pattern as ISlackCommandHandler so the Stage 4.3
        // NoOpSlackAppMentionHandler dev stand-in (registered by the
        // explicit AddSlackInboundDevelopmentHandlerStubs opt-in) is
        // unconditionally replaced. The handler depends on the
        // concrete SlackCommandHandler (to access the internal
        // DispatchAsync entry point that takes a per-call responder),
        // ISlackThreadedReplyPoster (registered above), and the host
        // logger -- every other Stage 5.1 collaborator is reachable
        // transitively through SlackCommandHandler so the wiring stays
        // a single extension call.
        services.RemoveAll<ISlackAppMentionHandler>();
        services.AddSingleton<SlackAppMentionHandler>();
        services.AddSingleton<ISlackAppMentionHandler>(sp => sp.GetRequiredService<SlackAppMentionHandler>());

        return services;
    }

    /// <summary>
    /// Opts in to the development-only stand-ins for orchestrator
    /// collaborators that Stage 5.1 cannot wire itself. As of iter-2
    /// (evaluator item 3 fix) the only such collaborator is
    /// <see cref="IAgentTaskService"/>; production composition roots
    /// MUST register the real implementation BEFORE calling
    /// <see cref="AddSlackCommandDispatcher"/> and MUST NOT call this
    /// extension.
    /// </summary>
    /// <remarks>
    /// The stub
    /// (<see cref="NoOpAgentTaskService"/>) logs every call at
    /// <see cref="Microsoft.Extensions.Logging.LogLevel.Warning"/> so an
    /// accidental production deployment is observable; it also returns
    /// a deterministic synthetic task-id so audit / thread mapping has
    /// a stable anchor while the real orchestrator is being built.
    /// </remarks>
    public static IServiceCollection AddSlackCommandDispatcherDevelopmentDefaults(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // TryAdd so a host-supplied IAgentTaskService registered first
        // still wins. The dev-defaults extension's job is to be a
        // safety net, not to force the NoOp on hosts that have real
        // orchestrator wiring partially in place.
        services.TryAddSingleton<IAgentTaskService, NoOpAgentTaskService>();

        return services;
    }
}
