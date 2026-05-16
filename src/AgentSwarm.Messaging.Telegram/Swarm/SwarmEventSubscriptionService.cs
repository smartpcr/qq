using System.Collections.Generic;
using System.Globalization;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Telegram.Swarm;

/// <summary>
/// Stage 2.7 background service that bridges the agent swarm event
/// stream (<see cref="ISwarmCommandBus.SubscribeAsync"/>) into the
/// Telegram messenger connector
/// (<see cref="IMessengerConnector.SendMessageAsync"/> /
/// <see cref="IMessengerConnector.SendQuestionAsync"/>) per
/// implementation-plan.md Stage 2.7 and architecture.md §4.6.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bootstrap.</b> On startup the service resolves the current set of
/// active tenants from <see cref="IOperatorRegistry.GetActiveTenantsAsync"/>
/// and starts one <see cref="Task"/> per tenant that drives the
/// <see cref="ISwarmCommandBus.SubscribeAsync"/> stream for that tenant
/// in isolation. If the active-tenant query yields zero tenants the
/// service idles until cancellation — a host with no configured
/// operators has no events to route.
/// </para>
/// <para>
/// <b>Per-tenant loop.</b> Each per-tenant task runs an outer
/// <c>while (!stopping)</c> loop that:
/// <list type="number">
///   <item>Calls <see cref="ISwarmCommandBus.SubscribeAsync"/>.</item>
///   <item>Awaits <c>await foreach</c> over the resulting
///   <see cref="IAsyncEnumerable{T}"/>, routing each
///   <see cref="SwarmEvent"/> via <see cref="RouteAsync"/>.</item>
///   <item>On <see cref="OperationCanceledException"/> propagates the
///   shutdown signal upward and exits cleanly.</item>
///   <item>On any other exception, logs a <see cref="LogLevel.Warning"/>
///   "disconnected", sleeps for an exponential backoff capped by
///   <see cref="MaxReconnectDelay"/>, and re-subscribes — emitting a
///   <see cref="LogLevel.Information"/> "reconnected" line on the next
///   iteration so operators can correlate flapping with their alert
///   dashboards. A successful subscription resets the backoff so a
///   transient cluster blip does not permanently inflate the
///   reconnect cadence.</item>
/// </list>
/// </para>
/// <para>
/// <b>Routing matrix.</b>
/// <list type="bullet">
///   <item><b><see cref="AgentQuestionEvent"/></b> — extract the
///   <see cref="AgentQuestionEnvelope"/>, resolve the target operator's
///   <see cref="OperatorBinding.TelegramChatId"/> (via
///   <c>RoutingMetadata["TelegramChatId"]</c> when provided, otherwise
///   the workspace default for the tenant), stamp the chat id into
///   <see cref="AgentQuestionEnvelope.RoutingMetadata"/> so the
///   <c>TelegramMessengerConnector</c> can route the outbound message,
///   and call <see cref="IMessengerConnector.SendQuestionAsync"/>.</item>
///   <item><b><see cref="AgentAlertEvent"/></b> — convert to a
///   <see cref="MessengerMessage"/> stamped with
///   <see cref="MessengerMessage.Severity"/> from the alert, resolve the
///   target chat via <see cref="ITaskOversightRepository.GetByTaskIdAsync"/>
///   first, falling back to the first active
///   <see cref="OperatorBinding"/> in
///   <see cref="AgentAlertEvent.WorkspaceId"/> per architecture.md §5.6,
///   and call <see cref="IMessengerConnector.SendMessageAsync"/>.</item>
///   <item><b><see cref="AgentStatusUpdateEvent"/></b> — convert to a
///   <see cref="MessengerMessage"/> with
///   <see cref="MessageSeverity.Normal"/>, resolve via
///   <see cref="ITaskOversightRepository.GetByTaskIdAsync"/>; when no
///   oversight is recorded, broadcast to every active operator in the
///   tenant via <see cref="IOperatorRegistry.GetByTenantAsync"/>.</item>
/// </list>
/// Routing failures (no operator could be resolved) are logged at
/// <see cref="LogLevel.Warning"/> with the correlation id so the
/// operator can trace the dropped event back to the producing agent.
/// Per-event exceptions are also logged at warning without breaking the
/// stream — one poison-pill event must not stall the whole subscription.
/// </para>
/// </remarks>
public sealed class SwarmEventSubscriptionService : BackgroundService
{
    /// <summary>Initial reconnect backoff after a stream disconnects.</summary>
    public static readonly TimeSpan DefaultInitialReconnectDelay = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound on the exponential reconnect backoff.</summary>
    public static readonly TimeSpan DefaultMaxReconnectDelay = TimeSpan.FromMinutes(1);

    private readonly ISwarmCommandBus _bus;
    private readonly IOperatorRegistry _operatorRegistry;
    private readonly ITaskOversightRepository _taskOversight;
    private readonly IMessengerConnector _connector;
    private readonly ILogger<SwarmEventSubscriptionService> _logger;

    /// <summary>
    /// Initial reconnect backoff. Overridable for tests so the per-tenant
    /// reconnect loop can be exercised in milliseconds instead of
    /// seconds; production hosts inherit the
    /// <see cref="DefaultInitialReconnectDelay"/> default.
    /// </summary>
    public TimeSpan InitialReconnectDelay { get; init; } = DefaultInitialReconnectDelay;

    /// <summary>
    /// Maximum reconnect backoff. Overridable for tests for the same
    /// reason as <see cref="InitialReconnectDelay"/>.
    /// </summary>
    public TimeSpan MaxReconnectDelay { get; init; } = DefaultMaxReconnectDelay;

    public SwarmEventSubscriptionService(
        ISwarmCommandBus bus,
        IOperatorRegistry operatorRegistry,
        ITaskOversightRepository taskOversight,
        IMessengerConnector connector,
        ILogger<SwarmEventSubscriptionService> logger)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _operatorRegistry = operatorRegistry ?? throw new ArgumentNullException(nameof(operatorRegistry));
        _taskOversight = taskOversight ?? throw new ArgumentNullException(nameof(taskOversight));
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SwarmEventSubscriptionService starting.");

        IReadOnlyList<string> tenants;
        try
        {
            tenants = await _operatorRegistry
                .GetActiveTenantsAsync(stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "SwarmEventSubscriptionService could not resolve active tenants. The subscription loop will not start; restart the host once IOperatorRegistry is available.");
            return;
        }

        if (tenants.Count == 0)
        {
            _logger.LogInformation(
                "SwarmEventSubscriptionService idling — no active tenants returned by IOperatorRegistry. "
                + "Configure Telegram:DevOperators (dev) or register PersistentOperatorRegistry (Stage 3.4) to seed tenants.");
            // Idle until cancellation so the host can shut down cleanly
            // without a hard exit from ExecuteAsync.
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            return;
        }

        _logger.LogInformation(
            "SwarmEventSubscriptionService subscribing to {TenantCount} tenants: {TenantIds}",
            tenants.Count,
            string.Join(",", tenants));

        var tasks = new List<Task>(tenants.Count);
        foreach (var tenant in tenants)
        {
            tasks.Add(RunTenantLoopAsync(tenant, stoppingToken));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _logger.LogInformation("SwarmEventSubscriptionService stopped.");
        }
    }

    /// <summary>
    /// Outer per-tenant loop: subscribe, drain, retry on disconnect with
    /// exponential backoff. Public for unit tests that exercise a single
    /// tenant loop directly without spinning up the full BackgroundService.
    /// </summary>
    internal async Task RunTenantLoopAsync(string tenantId, CancellationToken stoppingToken)
    {
        var delay = InitialReconnectDelay;
        // Tracks whether the *previous* iteration terminated abnormally
        // (transport exception or end-of-stream). Used to distinguish
        // the first connection attempt — which is logged at Information
        // as "connecting" — from a true reconnect, which is logged at
        // Information as "reconnected" so operators can correlate
        // flapping with their alert dashboards. We intentionally do NOT
        // gate "reconnected" on having previously consumed an event,
        // because a SubscribeAsync that fails before yielding anything
        // is still a transient disconnect from the operator's point of
        // view and the recovery telemetry must be the same.
        var isReconnect = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (isReconnect)
                {
                    _logger.LogInformation(
                        "SwarmEventSubscriptionService reconnected to tenant {TenantId}.",
                        tenantId);
                }
                else
                {
                    _logger.LogInformation(
                        "SwarmEventSubscriptionService connecting to tenant {TenantId}.",
                        tenantId);
                }

                await foreach (var ev in _bus
                    .SubscribeAsync(tenantId, stoppingToken)
                    .WithCancellation(stoppingToken)
                    .ConfigureAwait(false))
                {
                    // Reset the backoff so a healthy subscription doesn't
                    // inherit an inflated delay from a prior transient.
                    delay = InitialReconnectDelay;

                    if (ev is null) { continue; }

                    try
                    {
                        await RouteAsync(tenantId, ev, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Per-event isolation: one poison-pill event must
                        // not break the subscription. Log and continue.
                        _logger.LogWarning(
                            ex,
                            "SwarmEventSubscriptionService failed to route event for tenant {TenantId}. CorrelationId={CorrelationId} EventType={EventType}",
                            tenantId,
                            ev.CorrelationId,
                            ev.GetType().Name);
                    }
                }

                // The stream completed normally without an exception.
                // Treat as a (clean) disconnect: re-subscribe with the
                // current backoff so we resume listening promptly.
                _logger.LogWarning(
                    "SwarmEventSubscriptionService stream completed normally for tenant {TenantId}; re-subscribing after {Delay}.",
                    tenantId,
                    delay);
                isReconnect = true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "SwarmEventSubscriptionService disconnected from tenant {TenantId}; reconnecting after {Delay}.",
                    tenantId,
                    delay);
                isReconnect = true;
            }

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            // Exponential backoff capped at MaxReconnectDelay. Compute
            // via Math.Min on Ticks to avoid TimeSpan-overflow when the
            // doubled delay exceeds TimeSpan.MaxValue (unlikely with
            // sane caps but cheap insurance).
            var nextTicks = Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks);
            delay = TimeSpan.FromTicks(Math.Max(nextTicks, InitialReconnectDelay.Ticks));
        }
    }

    /// <summary>
    /// Route a single <see cref="SwarmEvent"/> to the messenger
    /// connector based on its concrete type. Internal so the test
    /// suite can drive specific events without spinning up the full
    /// per-tenant loop.
    /// </summary>
    internal Task RouteAsync(string tenantId, SwarmEvent ev, CancellationToken ct)
    {
        return ev switch
        {
            AgentQuestionEvent questionEvent => RouteQuestionAsync(tenantId, questionEvent, ct),
            AgentAlertEvent alertEvent => RouteAlertAsync(tenantId, alertEvent, ct),
            AgentStatusUpdateEvent statusEvent => RouteStatusAsync(tenantId, statusEvent, ct),
            _ => LogUnknownEvent(tenantId, ev),
        };
    }

    private Task LogUnknownEvent(string tenantId, SwarmEvent ev)
    {
        _logger.LogWarning(
            "SwarmEventSubscriptionService received unknown SwarmEvent subtype {EventType} for tenant {TenantId}. CorrelationId={CorrelationId}",
            ev.GetType().Name,
            tenantId,
            ev.CorrelationId);
        return Task.CompletedTask;
    }

    private async Task RouteQuestionAsync(string tenantId, AgentQuestionEvent ev, CancellationToken ct)
    {
        var envelope = ev.Envelope;
        if (envelope is null)
        {
            _logger.LogWarning(
                "SwarmEventSubscriptionService dropped AgentQuestionEvent with null Envelope for tenant {TenantId}. CorrelationId={CorrelationId}",
                tenantId,
                ev.CorrelationId);
            return;
        }

        var question = envelope.Question;
        var routingMetadata = envelope.RoutingMetadata ?? new Dictionary<string, string>();

        long? chatId = null;
        if (routingMetadata.TryGetValue(TelegramMessengerConnector.TelegramChatIdMetadataKey, out var raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            chatId = parsed;
        }

        if (chatId is null)
        {
            // Fall back to the tenant's first active binding so questions
            // with no explicit routing still reach a human operator. The
            // selected chat id is stamped back into RoutingMetadata so
            // the downstream connector can route the outbound message.
            var bindings = await _operatorRegistry
                .GetByTenantAsync(tenantId, ct)
                .ConfigureAwait(false);
            var first = bindings.FirstOrDefault(b => b.IsActive);
            if (first is null)
            {
                _logger.LogWarning(
                    "SwarmEventSubscriptionService dropped AgentQuestionEvent — tenant {TenantId} has no active operator bindings to receive QuestionId={QuestionId}. CorrelationId={CorrelationId}",
                    tenantId,
                    question.QuestionId,
                    ev.CorrelationId);
                return;
            }

            chatId = first.TelegramChatId;
        }

        var enriched = StampChatId(envelope, chatId.Value);

        _logger.LogInformation(
            "SwarmEventSubscriptionService routing AgentQuestionEvent. Tenant={TenantId} QuestionId={QuestionId} AgentId={AgentId} ChatId={ChatId} CorrelationId={CorrelationId}",
            tenantId,
            question.QuestionId,
            question.AgentId,
            chatId.Value,
            ev.CorrelationId);

        await _connector.SendQuestionAsync(enriched, ct).ConfigureAwait(false);
    }

    private async Task RouteAlertAsync(string tenantId, AgentAlertEvent ev, CancellationToken ct)
    {
        var resolvedChatId = await ResolveAlertChatIdAsync(tenantId, ev, ct).ConfigureAwait(false);
        if (resolvedChatId is null)
        {
            _logger.LogWarning(
                "SwarmEventSubscriptionService dropped AgentAlertEvent — could not resolve a target chat for AlertId={AlertId} TaskId={TaskId} WorkspaceId={WorkspaceId} TenantId={TenantId}. CorrelationId={CorrelationId}",
                ev.AlertId,
                ev.TaskId,
                ev.WorkspaceId,
                tenantId,
                ev.CorrelationId);
            return;
        }

        var message = BuildAlertMessage(ev, resolvedChatId.Value);

        _logger.LogInformation(
            "SwarmEventSubscriptionService routing AgentAlertEvent. Tenant={TenantId} AlertId={AlertId} TaskId={TaskId} Severity={Severity} ChatId={ChatId} CorrelationId={CorrelationId}",
            tenantId,
            ev.AlertId,
            ev.TaskId,
            ev.Severity,
            resolvedChatId.Value,
            ev.CorrelationId);

        await _connector.SendMessageAsync(message, ct).ConfigureAwait(false);
    }

    private async Task RouteStatusAsync(string tenantId, AgentStatusUpdateEvent ev, CancellationToken ct)
    {
        var oversight = await _taskOversight
            .GetByTaskIdAsync(ev.TaskId, ct)
            .ConfigureAwait(false);

        if (oversight is not null)
        {
            // Concrete TaskOversight → resolve the specific operator's
            // chat id. We don't know the workspace for the status event
            // (the type doesn't carry one) so we enumerate the tenant
            // and filter by binding id.
            var tenantBindings = await _operatorRegistry
                .GetByTenantAsync(tenantId, ct)
                .ConfigureAwait(false);
            var match = tenantBindings.FirstOrDefault(b => b.Id == oversight.OperatorBindingId);
            if (match is null)
            {
                _logger.LogWarning(
                    "SwarmEventSubscriptionService dropped AgentStatusUpdateEvent — TaskOversight for TaskId={TaskId} references OperatorBindingId={OperatorBindingId} that is not active in tenant {TenantId}. CorrelationId={CorrelationId}",
                    ev.TaskId,
                    oversight.OperatorBindingId,
                    tenantId,
                    ev.CorrelationId);
                return;
            }

            var directed = BuildStatusMessage(ev, match.TelegramChatId);
            _logger.LogInformation(
                "SwarmEventSubscriptionService routing AgentStatusUpdateEvent to oversight operator. Tenant={TenantId} TaskId={TaskId} AgentId={AgentId} ChatId={ChatId} CorrelationId={CorrelationId}",
                tenantId,
                ev.TaskId,
                ev.AgentId,
                match.TelegramChatId,
                ev.CorrelationId);
            await _connector.SendMessageAsync(directed, ct).ConfigureAwait(false);
            return;
        }

        // No TaskOversight record → broadcast to every active binding in
        // the tenant. The stub repository ALWAYS hits this branch.
        var broadcastTargets = await _operatorRegistry
            .GetByTenantAsync(tenantId, ct)
            .ConfigureAwait(false);
        var active = broadcastTargets.Where(b => b.IsActive).ToList();
        if (active.Count == 0)
        {
            _logger.LogWarning(
                "SwarmEventSubscriptionService dropped AgentStatusUpdateEvent — tenant {TenantId} has no active operator bindings for broadcast. TaskId={TaskId} CorrelationId={CorrelationId}",
                tenantId,
                ev.TaskId,
                ev.CorrelationId);
            return;
        }

        _logger.LogInformation(
            "SwarmEventSubscriptionService broadcasting AgentStatusUpdateEvent to {OperatorCount} operators in tenant {TenantId}. TaskId={TaskId} AgentId={AgentId} CorrelationId={CorrelationId}",
            active.Count,
            tenantId,
            ev.TaskId,
            ev.AgentId,
            ev.CorrelationId);

        // Distinct chat ids: a single operator may be reachable from
        // multiple bindings (different workspaces). Sending once per
        // chat avoids duplicate messages.
        var seenChats = new HashSet<long>();
        for (var i = 0; i < active.Count; i++)
        {
            var target = active[i];
            if (!seenChats.Add(target.TelegramChatId)) { continue; }

            var perOperator = BuildStatusMessage(ev, target.TelegramChatId, suffix: i);
            await _connector.SendMessageAsync(perOperator, ct).ConfigureAwait(false);
        }
    }

    private async Task<long?> ResolveAlertChatIdAsync(
        string tenantId,
        AgentAlertEvent ev,
        CancellationToken ct)
    {
        var oversight = await _taskOversight
            .GetByTaskIdAsync(ev.TaskId, ct)
            .ConfigureAwait(false);

        if (oversight is not null)
        {
            // Resolve via WorkspaceId (we have it on the alert) so the
            // binding lookup is a single workspace query rather than a
            // full-tenant scan.
            var workspaceBindings = await _operatorRegistry
                .GetByWorkspaceAsync(ev.WorkspaceId, ct)
                .ConfigureAwait(false);
            var match = workspaceBindings.FirstOrDefault(b => b.Id == oversight.OperatorBindingId);
            if (match is not null)
            {
                return match.TelegramChatId;
            }

            // The oversight references a binding outside the alert's
            // workspace. Fall back to a tenant-scoped search rather than
            // dropping the alert silently — the oversight assignment is
            // the operator's stated intent.
            var tenantBindings = await _operatorRegistry
                .GetByTenantAsync(tenantId, ct)
                .ConfigureAwait(false);
            var tenantMatch = tenantBindings.FirstOrDefault(b => b.Id == oversight.OperatorBindingId);
            if (tenantMatch is not null)
            {
                _logger.LogWarning(
                    "SwarmEventSubscriptionService resolved AgentAlertEvent oversight binding via tenant scan (binding workspace {BindingWorkspace} differs from alert workspace {AlertWorkspace}). TaskId={TaskId} CorrelationId={CorrelationId}",
                    tenantMatch.WorkspaceId,
                    ev.WorkspaceId,
                    ev.TaskId,
                    ev.CorrelationId);
                return tenantMatch.TelegramChatId;
            }
        }

        // Workspace-default fallback per architecture.md §5.6 — first
        // active binding for the alert's WorkspaceId.
        var fallbackBindings = await _operatorRegistry
            .GetByWorkspaceAsync(ev.WorkspaceId, ct)
            .ConfigureAwait(false);
        var first = fallbackBindings.FirstOrDefault(b => b.IsActive);
        return first?.TelegramChatId;
    }

    /// <summary>
    /// Convert an <see cref="AgentAlertEvent"/> into a
    /// <see cref="MessengerMessage"/> stamped with the resolved chat id,
    /// the alert id (so the connector's <c>alert:{AgentId}:{AlertId}</c>
    /// idempotency key is well-formed), and
    /// <c>SourceType=Alert</c>.
    /// </summary>
    private static MessengerMessage BuildAlertMessage(AgentAlertEvent ev, long chatId)
    {
        var text = string.IsNullOrEmpty(ev.Body)
            ? ev.Title
            : ev.Title + "\n\n" + ev.Body;

        return new MessengerMessage
        {
            MessageId = ev.AlertId,
            CorrelationId = ev.CorrelationId,
            ConversationId = ev.TaskId,
            AgentId = ev.AgentId,
            TaskId = ev.TaskId,
            Timestamp = ev.Timestamp,
            Text = text,
            Severity = ev.Severity,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TelegramMessengerConnector.TelegramChatIdMetadataKey] = chatId.ToString(CultureInfo.InvariantCulture),
                [TelegramMessengerConnector.SourceTypeMetadataKey] = nameof(OutboundSourceType.Alert),
                [TelegramMessengerConnector.AlertIdMetadataKey] = ev.AlertId,
            },
        };
    }

    /// <summary>
    /// Convert an <see cref="AgentStatusUpdateEvent"/> into a
    /// <see cref="MessengerMessage"/> with
    /// <see cref="MessageSeverity.Normal"/> and the resolved chat id.
    /// </summary>
    /// <param name="suffix">
    /// Disambiguates the
    /// <see cref="MessengerMessage.MessageId"/> when broadcasting a single
    /// status event to multiple operators — each per-operator copy gets a
    /// distinct id while sharing the source correlation id.
    /// </param>
    private static MessengerMessage BuildStatusMessage(
        AgentStatusUpdateEvent ev,
        long chatId,
        int suffix = -1)
    {
        var messageId = suffix < 0
            ? "status:" + ev.CorrelationId
            : string.Create(CultureInfo.InvariantCulture, $"status:{ev.CorrelationId}:{suffix}");

        return new MessengerMessage
        {
            MessageId = messageId,
            CorrelationId = ev.CorrelationId,
            ConversationId = ev.TaskId,
            AgentId = ev.AgentId,
            TaskId = ev.TaskId,
            Timestamp = DateTimeOffset.UtcNow,
            Text = ev.StatusText,
            Severity = MessageSeverity.Normal,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TelegramMessengerConnector.TelegramChatIdMetadataKey] = chatId.ToString(CultureInfo.InvariantCulture),
                [TelegramMessengerConnector.SourceTypeMetadataKey] = nameof(OutboundSourceType.StatusUpdate),
            },
        };
    }

    /// <summary>
    /// Returns a copy of <paramref name="envelope"/> whose
    /// <see cref="AgentQuestionEnvelope.RoutingMetadata"/> has the
    /// resolved <see cref="TelegramMessengerConnector.TelegramChatIdMetadataKey"/>
    /// set to <paramref name="chatId"/>. Existing routing metadata is
    /// preserved so callers can carry their own context through to the
    /// connector.
    /// </summary>
    private static AgentQuestionEnvelope StampChatId(AgentQuestionEnvelope envelope, long chatId)
    {
        var routing = new Dictionary<string, string>(StringComparer.Ordinal);
        if (envelope.RoutingMetadata is not null)
        {
            foreach (var kv in envelope.RoutingMetadata)
            {
                routing[kv.Key] = kv.Value;
            }
        }
        routing[TelegramMessengerConnector.TelegramChatIdMetadataKey] =
            chatId.ToString(CultureInfo.InvariantCulture);

        return envelope with { RoutingMetadata = routing };
    }
}
