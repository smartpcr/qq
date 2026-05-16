// -----------------------------------------------------------------------
// <copyright file="SlackInboundTransportServiceCollectionExtensions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Collections.Generic;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Queues;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// DI extensions that register the Stage 4.1 Slack inbound HTTP
/// transport: the three controllers
/// (<see cref="SlackEventsController"/>,
/// <see cref="SlackCommandsController"/>,
/// <see cref="SlackInteractionsController"/>), the
/// <see cref="SlackInboundEnvelopeFactory"/> they share, the in-process
/// <see cref="ISlackInboundQueue"/> implementation, and the default
/// <see cref="ISlackModalFastPathHandler"/> fall-back.
/// </summary>
/// <remarks>
/// <para>
/// All bindings use <see cref="ServiceCollectionDescriptorExtensions.TryAdd"/>
/// so a composition root may override any single component (e.g., swap
/// the in-memory <see cref="ChannelBasedSlackInboundQueue"/> for a
/// durable Azure Service Bus implementation supplied by
/// <c>AgentSwarm.Messaging.Core</c>) by registering its own
/// implementation BEFORE calling
/// <see cref="AddSlackInboundTransport"/>.
/// </para>
/// <para>
/// The extension also calls
/// <see cref="MvcCoreMvcBuilderExtensions.AddApplicationPart"/> so that
/// MVC's controller scanner discovers the three controllers even when
/// the hosting application's entry assembly does not contain them
/// directly -- e.g., the Stage 4.1 <c>AgentSwarm.Messaging.Worker</c>
/// host whose <c>Program</c> class lives in a different assembly.
/// </para>
/// </remarks>
public static class SlackInboundTransportServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Stage 4.1 Slack inbound transport services on
    /// <paramref name="services"/>.
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddSlackInboundTransport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The factory only needs a TimeProvider and is otherwise
        // stateless; singleton lifetime keeps allocations off the hot
        // ACK path.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SlackInboundEnvelopeFactory>();

        // The in-process channel queue. Production hosts swap in a
        // durable implementation BEFORE this call.
        services.TryAddSingleton<ChannelBasedSlackInboundQueue>();
        services.TryAddSingleton<ISlackInboundQueue>(sp =>
            sp.GetRequiredService<ChannelBasedSlackInboundQueue>());

        // Dead-letter sink for post-ACK enqueue failures (Stage 4.1
        // iter-3 evaluator item 2). The in-memory default keeps
        // failures observable and operator-recoverable until a
        // production host registers a durable sink BEFORE this call.
        services.TryAddSingleton<InMemorySlackInboundEnqueueDeadLetterSink>();
        services.TryAddSingleton<ISlackInboundEnqueueDeadLetterSink>(sp =>
            sp.GetRequiredService<InMemorySlackInboundEnqueueDeadLetterSink>());

        // Default modal fast-path handler is a real implementation that
        // runs idempotency + views.open synchronously inside the HTTP
        // request lifetime (architecture.md §5.3, tech-spec.md §5.2).
        // Iter-3 (evaluator items 2 + 3) introduces the
        // ISlackFastPathIdempotencyStore abstraction (default: in-process
        // L1 only; SlackInboundDurabilityServiceCollectionExtensions
        // adds the durable EF L2) and the SlackModalAuditRecorder so
        // every views.open call is recorded in slack_audit_entry.
        services.TryAddSingleton<SlackInProcessIdempotencyStore>();
        services.TryAddSingleton<ISlackFastPathIdempotencyStore>(sp =>
            sp.GetRequiredService<SlackInProcessIdempotencyStore>());
        services.TryAddSingleton<Rendering.ISlackMessageRenderer, Rendering.DefaultSlackMessageRenderer>();
        services.TryAddSingleton<ISlackModalPayloadBuilder, DefaultSlackModalPayloadBuilder>();

        // Audit recorder for modal_open entries (architecture.md §5.3
        // step 5, implementation-plan.md line 377). Depends on the
        // ISlackAuditEntryWriter that the Worker host wires against the
        // EF backend via AddSlackEntityFrameworkAuditWriter; test hosts
        // get the InMemorySlackAuditEntryWriter via
        // AddSlackSignatureValidation's TryAdd fallback.
        services.TryAddSingleton<SlackModalAuditRecorder>();

        // Stage 6.4 (evaluator iter-2 item #1, STRUCTURAL): the
        // production modal fast-path resolves to SlackDirectApiClient
        // -- the SlackNet-backed implementation that wraps views.open
        // through ISlackApiClient.Post, shares the per-tier
        // ISlackRateLimiter singleton with SlackOutboundDispatcher,
        // and enforces the ~2.5s trigger_id deadline that Slack's
        // 3-second ACK budget requires. The legacy
        // HttpClientSlackViewsOpenClient remains in the codebase as a
        // fall-back for hosts that explicitly register it BEFORE
        // calling AddSlackInboundTransport (TryAdd lets the earlier
        // registration win), but the default for every Worker host is
        // now the SlackNet wrapper -- implementation-plan.md Stage
        // 6.4 step 1.
        //
        // The shared ISlackRateLimiter singleton is TryAdd-registered
        // here so any host that wires only AddSlackInboundTransport
        // (e.g., a fast-path-only deployment) still resolves the
        // direct client. AddSlackOutboundDispatcher uses the same
        // TryAddSingleton<ISlackRateLimiter, SlackTokenBucketRateLimiter>
        // call, so whichever extension runs first wins and the second
        // one becomes a no-op; either way both pipelines bind to the
        // SAME singleton instance per architecture.md §2.12.
        services.AddHttpClient(HttpClientSlackViewsOpenClient.HttpClientName);
        services.AddOptions<SlackConnectorOptions>();
        services.TryAddSingleton<ISlackRateLimiter, SlackTokenBucketRateLimiter>();
        services.TryAddSingleton<SlackDirectApiClient>();
        services.TryAddSingleton<ISlackViewsOpenClient>(sp =>
            sp.GetRequiredService<SlackDirectApiClient>());

        // Stage 5.2: production threaded-reply poster (chat.postMessage)
        // for the app-mention handler. Registered here -- alongside the
        // other HTTP-backed Slack Web API clients -- so any host that
        // wires AddSlackInboundTransport (the Worker, integration
        // tests with real transport) automatically gets the real HTTP
        // implementation. The dispatcher extension
        // (AddSlackCommandDispatcher) keeps a TryAddSingleton NoOp
        // fall-back so unit-test fixtures that skip the transport
        // wiring still resolve a non-null binding; because both
        // registrations use TryAdd, the FIRST extension call wins per
        // composition. Stage 6.4's consolidated SlackDirectApiClient
        // can supersede via pre-registration.
        services.AddHttpClient(Pipeline.HttpClientSlackThreadedReplyPoster.HttpClientName);
        services.TryAddSingleton<Pipeline.ISlackThreadedReplyPoster, Pipeline.HttpClientSlackThreadedReplyPoster>();

        services.TryAddSingleton<ISlackModalFastPathHandler, DefaultSlackModalFastPathHandler>();

        // Register a NO-OP ISlackInteractionFastPathHandler so the
        // SlackInteractionsController can always resolve a fast-path
        // even when the host has not opted into the Stage 5.3
        // interaction dispatcher. Hosts that DO call
        // AddSlackInteractionDispatcher swap this NoOp out for the
        // real DefaultSlackInteractionFastPathHandler via
        // RemoveAll<>+AddSingleton<>.
        services.TryAddSingleton<ISlackInteractionFastPathHandler, NoOpSlackInteractionFastPathHandler>();

        return services;
    }

    /// <summary>
    /// Registers the Stage 4.2 Socket Mode WebSocket transport services
    /// (<see cref="ISlackSocketModeConnectionFactory"/>,
    /// <see cref="ISlackInboundTransportFactory"/>, options binding,
    /// named <see cref="System.Net.Http.HttpClient"/> for
    /// <c>apps.connections.open</c>) on the supplied service
    /// collection.
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <param name="configuration">
    /// Optional configuration root. When supplied the extension binds
    /// the <c>Slack:SocketMode</c> section into
    /// <see cref="SlackSocketModeOptions"/> so operators can override
    /// the reconnect bounds, ACK timeout, and receive-buffer size from
    /// <c>appsettings.json</c> / environment variables without
    /// recompiling the host. Stage 4.2 evaluator iter-1 item 3.
    /// </param>
    /// <remarks>
    /// <para>
    /// All bindings use <c>TryAdd*</c> so a composition root can
    /// override any single component by registering its own
    /// implementation BEFORE calling this method (e.g., tests inject
    /// a fake <see cref="ISlackSocketModeConnectionFactory"/>).
    /// </para>
    /// <para>
    /// The Worker host calls
    /// <see cref="AddSlackInboundTransport(IServiceCollection)"/> for
    /// the Stage 4.1 HTTP transport AND
    /// <see cref="AddSlackSocketModeTransport(IServiceCollection, IConfiguration)"/>
    /// for the Stage 4.2 Socket Mode transport; the per-workspace
    /// <see cref="ISlackInboundTransportFactory"/> chooses which one
    /// to use for each registered workspace.
    /// </para>
    /// <para>
    /// The bound <see cref="SlackSocketModeOptions"/> are validated by
    /// <see cref="SlackSocketModeOptionsValidator"/> (registered as an
    /// <see cref="IValidateOptions{TOptions}"/> singleton via
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddEnumerable"/>)
    /// and the validation is forced to run at host startup via
    /// <see cref="OptionsBuilderExtensions.ValidateOnStart{TOptions}(OptionsBuilder{TOptions})"/>.
    /// Operator misconfigurations -- <c>ReceiveBufferSize</c> &lt;= 0,
    /// <c>InitialReconnectDelay</c> &gt; <c>MaxReconnectDelay</c>,
    /// non-positive <c>AckTimeout</c>, etc. -- therefore fail the
    /// generic host at boot rather than surfacing as
    /// <see cref="ArgumentException"/> deep inside
    /// <see cref="SlackSocketModeReceiver"/> /
    /// <see cref="SlackSocketModeBackoffPolicy"/> at first connection
    /// time.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSlackSocketModeTransport(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Bind the Slack:SocketMode section so operators can override
        // any field (initial/max reconnect delay, ACK timeout,
        // receive-buffer size) from appsettings.json or environment
        // variables. When no configuration is supplied (test hosts,
        // composition roots that wire the options manually) the
        // default SlackSocketModeOptions values apply.
        OptionsBuilder<SlackSocketModeOptions> optionsBuilder =
            services.AddOptions<SlackSocketModeOptions>();
        if (configuration is not null)
        {
            IConfigurationSection section = configuration.GetSection(SlackSocketModeOptions.SectionName);
            optionsBuilder.Bind(section);
        }

        // Wire a typed IValidateOptions<SlackSocketModeOptions> into
        // the options pipeline so misconfigurations from
        // appsettings.json / environment variables fail the host
        // eagerly rather than surfacing deep inside the
        // SlackSocketModeReceiver / SlackSocketModeBackoffPolicy
        // constructors at first connection time. TryAddEnumerable keys
        // on (serviceType, implementationType) so repeated calls to
        // AddSlackSocketModeTransport register the validator exactly
        // once.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<SlackSocketModeOptions>, SlackSocketModeOptionsValidator>());

        // Force validation to run when the generic Host starts (rather
        // than lazily on first IOptions<T>.Value resolution) so
        // operators see the failure synchronously during host boot.
        // Requires Microsoft.Extensions.Hosting (already referenced).
        optionsBuilder.ValidateOnStart();

        // Resolve a value-instance of SlackSocketModeOptions so the
        // receiver / connection factory can take it by value without
        // forcing every consumer to depend on IOptions<T>. The
        // TryAdd registration lets a test host override the options
        // block before this call. Note: IOptions<T>.Value resolution
        // itself triggers the registered IValidateOptions<T>, which
        // means test hosts that don't run ValidateOnStart() still get
        // the same validation when they first resolve this singleton.
        services.TryAddSingleton<SlackSocketModeOptions>(sp =>
            sp.GetRequiredService<IOptions<SlackSocketModeOptions>>().Value);

        services.AddHttpClient(DefaultSlackSocketModeConnectionFactory.HttpClientName);
        services.TryAddSingleton<ISlackSocketModeConnectionFactory, DefaultSlackSocketModeConnectionFactory>();

        services.TryAddSingleton<ISlackInboundTransportFactory, SlackInboundTransportFactory>();

        // Stage 4.2 evaluator iter-1 item 1: register the hosted
        // service that enumerates ISlackWorkspaceConfigStore and
        // actually starts the per-workspace transports on host boot.
        // Without this AddHostedService call the receiver classes
        // exist in the container but no Slack workspace ever connects.
        services.AddHostedService<SlackInboundTransportHostedService>();

        return services;
    }

    /// <summary>
    /// Registers the Slack inbound controllers
    /// (<see cref="SlackEventsController"/>,
    /// <see cref="SlackCommandsController"/>,
    /// <see cref="SlackInteractionsController"/>) with MVC's
    /// application-part scanner. Required when the host's entry
    /// assembly does not contain the controllers (e.g., the
    /// <c>AgentSwarm.Messaging.Worker</c> host whose
    /// <c>Program</c> lives in a separate assembly).
    /// </summary>
    public static IMvcBuilder AddSlackInboundControllers(this IMvcBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddApplicationPart(typeof(SlackEventsController).Assembly);
    }

    /// <summary>
    /// Replaces the default in-memory
    /// <see cref="ISlackInboundEnqueueDeadLetterSink"/> with the
    /// durable <see cref="FileSystemSlackInboundEnqueueDeadLetterSink"/>
    /// that writes every dead-lettered envelope as a JSONL line under
    /// <paramref name="directoryPath"/>. Hosts that survive process
    /// restarts (worker daemons, web hosts behind orchestrators that
    /// reschedule the pod) MUST opt in to a durable sink so post-ACK
    /// enqueue failures captured before the restart are not lost --
    /// Stage 4.1 iter-4 evaluator item 2.
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <param name="directoryPath">Absolute or relative directory path
    /// where the JSONL file is appended. Created if absent.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddFileSystemSlackInboundEnqueueDeadLetterSink(
        this IServiceCollection services,
        string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Dead-letter directory path must be supplied.", nameof(directoryPath));
        }

        // Remove the default in-memory registration so the file sink
        // is the single resolved implementation of the interface.
        services.RemoveAll<ISlackInboundEnqueueDeadLetterSink>();
        services.RemoveAll<InMemorySlackInboundEnqueueDeadLetterSink>();

        services.AddSingleton(sp => new FileSystemSlackInboundEnqueueDeadLetterSink(
            directoryPath,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileSystemSlackInboundEnqueueDeadLetterSink>>()));
        services.AddSingleton<ISlackInboundEnqueueDeadLetterSink>(sp =>
            sp.GetRequiredService<FileSystemSlackInboundEnqueueDeadLetterSink>());

        return services;
    }
}

/// <summary>
/// <see cref="IValidateOptions{TOptions}"/> implementation that
/// enforces the invariants the Stage 4.2 Socket Mode receiver and
/// backoff policy require of <see cref="SlackSocketModeOptions"/>.
/// Registered by
/// <see cref="SlackInboundTransportServiceCollectionExtensions.AddSlackSocketModeTransport(IServiceCollection, IConfiguration)"/>
/// and wired to <see cref="OptionsBuilderExtensions.ValidateOnStart{TOptions}(OptionsBuilder{TOptions})"/>
/// so operator misconfigurations fail the host at boot.
/// </summary>
/// <remarks>
/// The checks mirror the lazy <see cref="ArgumentException"/>s that
/// <see cref="SlackSocketModeBackoffPolicy"/> raises in its constructor
/// (positive <c>InitialReconnectDelay</c>, <c>MaxReconnectDelay</c>
/// not below <c>InitialReconnectDelay</c>) and extend them to the two
/// invariants the receiver / connection factory depend on but never
/// validated explicitly: a positive <c>AckTimeout</c> (otherwise the
/// <see cref="System.Threading.CancellationTokenSource.CancelAfter(System.TimeSpan)"/>
/// call would cancel the ACK token immediately) and a positive
/// <c>ReceiveBufferSize</c> (the underlying
/// <see cref="System.Net.WebSockets.ClientWebSocket"/> requires a
/// non-zero buffer).
/// </remarks>
internal sealed class SlackSocketModeOptionsValidator
    : IValidateOptions<SlackSocketModeOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SlackSocketModeOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(SlackSocketModeOptions)} instance is null.");
        }

        List<string> failures = new();

        if (options.InitialReconnectDelay <= TimeSpan.Zero)
        {
            failures.Add(
                $"{SlackSocketModeOptions.SectionName}:{nameof(SlackSocketModeOptions.InitialReconnectDelay)} must be greater than zero (was {options.InitialReconnectDelay}).");
        }

        if (options.MaxReconnectDelay <= TimeSpan.Zero)
        {
            failures.Add(
                $"{SlackSocketModeOptions.SectionName}:{nameof(SlackSocketModeOptions.MaxReconnectDelay)} must be greater than zero (was {options.MaxReconnectDelay}).");
        }

        if (options.MaxReconnectDelay < options.InitialReconnectDelay)
        {
            failures.Add(
                $"{SlackSocketModeOptions.SectionName}:{nameof(SlackSocketModeOptions.MaxReconnectDelay)} ({options.MaxReconnectDelay}) must be greater than or equal to {nameof(SlackSocketModeOptions.InitialReconnectDelay)} ({options.InitialReconnectDelay}).");
        }

        if (options.AckTimeout <= TimeSpan.Zero)
        {
            failures.Add(
                $"{SlackSocketModeOptions.SectionName}:{nameof(SlackSocketModeOptions.AckTimeout)} must be greater than zero (was {options.AckTimeout}).");
        }

        if (options.ReceiveBufferSize <= 0)
        {
            failures.Add(
                $"{SlackSocketModeOptions.SectionName}:{nameof(SlackSocketModeOptions.ReceiveBufferSize)} must be greater than zero (was {options.ReceiveBufferSize}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
