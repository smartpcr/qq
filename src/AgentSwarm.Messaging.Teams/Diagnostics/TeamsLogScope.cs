using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Diagnostics;

/// <summary>
/// Structured-logging scope helpers that push the canonical Stage 6.3 enrichment keys
/// (<see cref="CorrelationIdKey"/>, <see cref="TenantIdKey"/>, <see cref="UserIdKey"/>)
/// onto every <see cref="ILogger"/> log entry written inside the scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>Serilog enricher compatibility (per <c>implementation-plan.md</c> §6.3 step 5).</b>
/// When the host registers the standard
/// <c>Serilog.Extensions.Logging</c> bridge with <c>LogContext</c> enrichment enabled,
/// the dictionary passed to <see cref="ILogger.BeginScope{TState}(TState)"/> is
/// projected onto Serilog's <c>LogContext</c> as a top-level property block — the
/// keys land in the log envelope under the <i>same</i> names defined here. Tests can
/// therefore assert on the enrichment by inspecting either the scope state in a
/// captured <see cref="ILogger"/> backend or by inspecting Serilog sink output.
/// </para>
/// <para>
/// Centralising the keys here eliminates the typo risk that would otherwise come
/// from each call site spelling its own dictionary keys (one site writing
/// <c>"correlation_id"</c>, another writing <c>"CorrelationId"</c>, etc. — a dashboard
/// query that ignores either would silently miss half the traffic). The constants
/// below are referenced by <see cref="TeamsMessengerConnector"/> and by Stage 6.3
/// tests.
/// </para>
/// </remarks>
public static class TeamsLogScope
{
    /// <summary>Canonical log-context key for the end-to-end correlation ID.</summary>
    public const string CorrelationIdKey = "CorrelationId";

    /// <summary>Canonical log-context key for the Entra ID tenant.</summary>
    public const string TenantIdKey = "TenantId";

    /// <summary>Canonical log-context key for the actor / target user identity.</summary>
    public const string UserIdKey = "UserId";

    /// <summary>
    /// Stable sentinel emitted into the <see cref="CorrelationIdKey"/>,
    /// <see cref="TenantIdKey"/>, or <see cref="UserIdKey"/> slot when the caller
    /// has no actual value to enrich with (e.g. background workers, lifecycle
    /// logs, pre-resolution security checks).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stage 6.3 iter-10 evaluator fix item 3.</b> §6.3 step 5 mandates that
    /// <i>every</i> Teams log entry carry all three enrichment keys
    /// (<c>CorrelationId</c>, <c>TenantId</c>, <c>UserId</c>). Earlier iterations
    /// omitted keys whose values were null/empty, which left lifecycle and
    /// security logs structurally lacking the contract. The sentinel
    /// (<c>"-"</c>) is the smallest, most dashboard-safe placeholder: it is
    /// one character, parses as a string scalar in Serilog/JSON sinks, and is
    /// trivially filtered out of dashboards that need to ignore unenriched
    /// frames (<c>WHERE UserId != '-'</c>).
    /// </para>
    /// <para>
    /// Callers that legitimately have no value for a key (e.g. a
    /// channel-targeted send has no acting user) pass <c>null</c>/empty for
    /// that key; the helper substitutes this sentinel so the resulting scope
    /// (and any log entry emitted inside it) still carries all three keys.
    /// </para>
    /// </remarks>
    public const string EmptyValueSentinel = "-";

    /// <summary>
    /// Begin a logging scope that enriches <i>every</i> <see cref="ILogger"/> entry
    /// written inside the returned <see cref="IDisposable"/> with <b>all three</b>
    /// Stage 6.3 canonical keys (<see cref="CorrelationIdKey"/>,
    /// <see cref="TenantIdKey"/>, <see cref="UserIdKey"/>). Null/empty values are
    /// replaced with <see cref="EmptyValueSentinel"/> so the scope state ALWAYS
    /// carries a stable three-key shape — dashboards never see a missing slot.
    /// </summary>
    /// <param name="logger">Logger that owns the scope; required.</param>
    /// <param name="correlationId">End-to-end trace ID; sentinel-substituted if null/empty.</param>
    /// <param name="tenantId">Entra ID tenant; sentinel-substituted if null/empty.</param>
    /// <param name="userId">Acting / target user identity; sentinel-substituted if null/empty.</param>
    /// <returns>Disposable scope; never <c>null</c>.</returns>
    /// <remarks>
    /// <para>
    /// <b>Inherited values, not sentinels, when a parent scope is active.</b> When
    /// the caller passes null/empty for a key AND a parent
    /// <see cref="TeamsLogContext"/> entry already carries a real value for that
    /// key, the inherited value is used rather than the sentinel — nested scopes
    /// therefore propagate the outer scope's CorrelationId / TenantId / UserId
    /// without the inner caller having to plumb them through.
    /// </para>
    /// <para>
    /// The same effective values are also pushed onto <see cref="TeamsLogContext"/>
    /// so the optional Serilog <see cref="TeamsLogEnricher"/> can stamp the keys
    /// onto every <see cref="Serilog.Events.LogEvent"/> emitted inside the scope.
    /// Disposing the returned token pops both the MEL scope and the ambient
    /// context entry back to the parent.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">If <paramref name="logger"/> is null.</exception>
    public static IDisposable BeginScope(
        ILogger logger,
        string? correlationId = null,
        string? tenantId = null,
        string? userId = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        // Capture the parent's effective values (if any) BEFORE Push runs — caller
        // null/empty falls back to parent values rather than overwriting them with
        // sentinels (otherwise a nested scope would shadow a real outer CorrelationId
        // with "-" in the MEL dictionary stack, breaking inheritance).
        var (parentCorrelationId, parentTenantId, parentUserId) = TeamsLogContext.Snapshot();

        var effectiveCorrelationId = !string.IsNullOrEmpty(correlationId)
            ? correlationId
            : parentCorrelationId ?? EmptyValueSentinel;
        var effectiveTenantId = !string.IsNullOrEmpty(tenantId)
            ? tenantId
            : parentTenantId ?? EmptyValueSentinel;
        var effectiveUserId = !string.IsNullOrEmpty(userId)
            ? userId
            : parentUserId ?? EmptyValueSentinel;

        var state = new Dictionary<string, object?>(capacity: 3)
        {
            [CorrelationIdKey] = effectiveCorrelationId,
            [TenantIdKey] = effectiveTenantId,
            [UserIdKey] = effectiveUserId,
        };

        var loggerScope = logger.BeginScope(state) ?? NullScope.Instance;
        var contextScope = TeamsLogContext.Push(correlationId, tenantId, userId);
        return new CompositeScope(loggerScope, contextScope);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Disposes the underlying <see cref="ILogger"/> scope AND the
    /// <see cref="TeamsLogContext"/> entry in a single call. Both disposes are
    /// invoked even if the first throws — the AsyncLocal pop is paired with the
    /// scope state and must always run to avoid leaking enrichment onto subsequent
    /// log entries on the same execution context.
    /// </summary>
    private sealed class CompositeScope : IDisposable
    {
        private readonly IDisposable _loggerScope;
        private readonly IDisposable _contextScope;
        private bool _disposed;

        public CompositeScope(IDisposable loggerScope, IDisposable contextScope)
        {
            _loggerScope = loggerScope;
            _contextScope = contextScope;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _loggerScope.Dispose();
            }
            finally
            {
                _contextScope.Dispose();
            }
        }
    }
}
