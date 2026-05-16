// -----------------------------------------------------------------------
// <copyright file="SlackInboundIngestorServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using AgentSwarm.Messaging.Slack.Persistence;
using AgentSwarm.Messaging.Slack.Queues;
using AgentSwarm.Messaging.Slack.Retry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Composition-root wiring for Stage 4.3's inbound ingestion pipeline.
/// </summary>
public static class SlackInboundIngestorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SlackInboundIngestor"/> together with the
    /// EF-backed <see cref="SlackIdempotencyGuard{TContext}"/>, the
    /// envelope-level authorizer, the default retry policy, the
    /// in-process DLQ stand-in, the audit recorder, and the
    /// processing pipeline.
    /// </summary>
    /// <typeparam name="TContext">
    /// EF Core context that surfaces both
    /// <see cref="ISlackInboundRequestRecordDbContext.SlackInboundRequestRecords"/>
    /// and the audit table -- the
    /// <see cref="Persistence.SlackPersistenceDbContext"/> in the Worker
    /// host. Must already be registered via
    /// <see cref="EntityFrameworkServiceCollectionExtensions.AddDbContext{TContext}(IServiceCollection, Action{DbContextOptionsBuilder}, ServiceLifetime, ServiceLifetime)"/>.
    /// </typeparam>
    /// <remarks>
    /// <para>
    /// Stage 4.3 of
    /// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
    /// Most registrations use the TryAdd-style pattern so a downstream
    /// composition root that has already wired a production
    /// alternative (durable DLQ, custom retry policy) is honoured.
    /// The hosted-service registration is the only non-conditional
    /// call: a host that calls this extension MUST want the ingestor
    /// running.
    /// </para>
    /// <para>
    /// <b>Iter 5 evaluator item #2 (STRUCTURAL fix).</b> Earlier iters
    /// registered <see cref="NoOpSlackCommandHandler"/>,
    /// <see cref="NoOpSlackAppMentionHandler"/>, and
    /// <see cref="NoOpSlackInteractionHandler"/> as the production
    /// defaults via <c>TryAddSingleton</c>; a host that called
    /// <c>AddSlackInboundIngestor</c> and forgot to register real
    /// Stage 5 handlers would silently ack-and-drop every Slack
    /// request because the no-op completes the envelope and the
    /// idempotency guard marks it <c>completed</c>. To eliminate
    /// that silent-loss class of bug, this extension no longer
    /// registers the no-op handlers. A host that does not register
    /// <see cref="ISlackCommandHandler"/> /
    /// <see cref="ISlackAppMentionHandler"/> /
    /// <see cref="ISlackInteractionHandler"/> will get a clear
    /// <see cref="InvalidOperationException"/> at the first envelope
    /// dispatch (the pipeline resolves the handlers from DI when it
    /// is constructed) -- a fail-fast surface that a production
    /// deployment can detect at startup. Development hosts that
    /// genuinely want the no-op stand-ins (e.g. the Worker before
    /// Stage 5.1/5.2/5.3 ships) opt in explicitly by additionally
    /// calling <see cref="AddSlackInboundDevelopmentHandlerStubs"/>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSlackInboundIngestor<TContext>(this IServiceCollection services)
        where TContext : DbContext, ISlackInboundRequestRecordDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotency guard (durable by default). Singleton + scope
        // factory pattern lets the guard create a per-call DbContext
        // scope without violating EF's "not thread-safe" rule.
        services.TryAddSingleton<ISlackIdempotencyGuard, SlackIdempotencyGuard<TContext>>();

        // Envelope-level authorizer. Reuses the Stage 3.2 services
        // registered by AddSlackAuthorization, so a Worker host that
        // forgot the AddSlackAuthorization call will get a clear DI
        // error at resolve time rather than silently letting traffic
        // through.
        services.TryAddSingleton<ISlackInboundAuthorizer, SlackInboundAuthorizer>();

        // Retry policy. The fast-path / outbound stages also resolve
        // ISlackRetryPolicy; using TryAdd lets either side win and
        // ensures both share the same backoff configuration.
        services.TryAddSingleton<ISlackRetryPolicy, DefaultSlackRetryPolicy>();

        // Dead-letter queue. The in-process queue is the development
        // default; production hosts override this binding with a
        // durable backend by calling AddFileSystemSlackDeadLetterQueue
        // BEFORE this extension so the TryAdd here becomes a no-op.
        services.TryAddSingleton<ISlackDeadLetterQueue, InMemorySlackDeadLetterQueue>();

        // NOTE: ISlackCommandHandler / ISlackAppMentionHandler /
        // ISlackInteractionHandler are INTENTIONALLY NOT registered
        // here -- see the class-level remarks for the iter-5
        // evaluator-driven rationale. Hosts MUST register real
        // handlers (Stage 5.1/5.2/5.3) or call
        // AddSlackInboundDevelopmentHandlerStubs to opt into the
        // no-op stand-ins.

        // Audit recorder + pipeline are concrete singletons -- they
        // have no swap-in seam at this stage.
        services.TryAddSingleton<SlackInboundAuditRecorder>();
        services.TryAddSingleton<SlackInboundProcessingPipeline>();

        services.AddHostedService<SlackInboundIngestor>();

        return services;
    }

    /// <summary>
    /// Opts the host into the Stage 4.3 no-op handler stand-ins
    /// (<see cref="NoOpSlackCommandHandler"/>,
    /// <see cref="NoOpSlackAppMentionHandler"/>,
    /// <see cref="NoOpSlackInteractionHandler"/>) for the three
    /// inbound handler contracts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Do NOT call this in production composition roots.</b>
    /// The stubs LogWarning and complete the envelope without
    /// producing an agent task, app-mention action, or
    /// <c>HumanDecisionEvent</c>; calling them is an explicit
    /// acknowledgement that real Stage 5.1 / 5.2 / 5.3 handlers
    /// are not yet wired into this host. The Worker calls this
    /// extension until Stage 5.x ships so the ingestor remains
    /// resolvable; production hosts replace this call with the
    /// real handler DI extensions.
    /// </para>
    /// <para>
    /// Uses <c>TryAddSingleton</c> so a host that has ALREADY
    /// registered a real handler (e.g. Stage 5.1 ran first) wins.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSlackInboundDevelopmentHandlerStubs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISlackCommandHandler, NoOpSlackCommandHandler>();
        services.TryAddSingleton<ISlackAppMentionHandler, NoOpSlackAppMentionHandler>();
        services.TryAddSingleton<ISlackInteractionHandler, NoOpSlackInteractionHandler>();

        return services;
    }
}
