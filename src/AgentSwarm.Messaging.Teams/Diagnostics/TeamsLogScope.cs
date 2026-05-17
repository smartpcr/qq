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
    /// Begin a logging scope that enriches every <see cref="ILogger"/> entry written
    /// inside the returned <see cref="IDisposable"/> with the supplied keys. Null /
    /// empty values are omitted from the scope so blank fields do not pollute the
    /// log envelope.
    /// </summary>
    /// <param name="logger">Logger that owns the scope; required.</param>
    /// <param name="correlationId">End-to-end trace ID.</param>
    /// <param name="tenantId">Entra ID tenant.</param>
    /// <param name="userId">Acting / target user identity.</param>
    /// <returns>
    /// Disposable scope; never <c>null</c>. Even when every enrichment value is empty
    /// the helper returns a no-op <see cref="IDisposable"/> so callers can use the
    /// scope inside a <c>using</c> block without a null check.
    /// </returns>
    /// <remarks>
    /// The same scope state is also pushed onto <see cref="TeamsLogContext"/> so the
    /// optional Serilog <see cref="TeamsLogEnricher"/> can stamp the keys onto every
    /// <see cref="Serilog.Events.LogEvent"/> emitted inside the scope. Disposing the
    /// returned token pops the ambient context entry back to its parent.
    /// </remarks>
    /// <exception cref="ArgumentNullException">If <paramref name="logger"/> is null.</exception>
    public static IDisposable BeginScope(
        ILogger logger,
        string? correlationId = null,
        string? tenantId = null,
        string? userId = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var state = new Dictionary<string, object?>(capacity: 3);
        if (!string.IsNullOrEmpty(correlationId))
        {
            state[CorrelationIdKey] = correlationId;
        }

        if (!string.IsNullOrEmpty(tenantId))
        {
            state[TenantIdKey] = tenantId;
        }

        if (!string.IsNullOrEmpty(userId))
        {
            state[UserIdKey] = userId;
        }

        if (state.Count == 0)
        {
            return NullScope.Instance;
        }

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
