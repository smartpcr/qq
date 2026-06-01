using Serilog.Context;

namespace AgentSwarm.Messaging.Teams.Diagnostics;

/// <summary>
/// Ambient async-flow store of the three Stage 6.3 enrichment keys
/// (<see cref="TeamsLogScope.CorrelationIdKey"/>,
/// <see cref="TeamsLogScope.TenantIdKey"/>, <see cref="TeamsLogScope.UserIdKey"/>).
/// Populated by <see cref="TeamsLogScope.BeginScope"/> and read by
/// <see cref="TeamsLogEnricher"/> so log entries written via Serilog inside the
/// scope carry the same enrichment as log entries written via
/// <see cref="Microsoft.Extensions.Logging.ILogger"/>.
/// </summary>
/// <remarks>
/// <para>
/// The context is backed by a single <see cref="AsyncLocal{T}"/> entry per active
/// scope chain — nesting is supported via a parent pointer so <see cref="Push"/>
/// composes (an inner scope inherits the outer scope's keys for any value the inner
/// caller leaves null). Disposing the returned token pops the entry back to the
/// parent.
/// </para>
/// <para>
/// The values flow with the .NET async execution context, so a span that crosses
/// <c>Task</c> boundaries (the Bot Framework
/// <c>CloudAdapter.ContinueConversationAsync</c> callback chain in particular)
/// preserves the enrichment without explicit plumbing. AsyncLocal allocations are
/// O(1) per push.
/// </para>
/// <para>
/// <b>Stage 6.3 iter-5 evaluator feedback item 1 — Serilog auto-wiring.</b>
/// <see cref="Push"/> additionally invokes
/// <see cref="LogContext.PushProperty(string, object?, bool)"/> for each non-empty
/// enrichment key. Any host whose Serilog configuration includes the canonical
/// <c>Enrich.FromLogContext()</c> call — which is the default ASP.NET Core /
/// <c>Serilog.Extensions.Hosting</c> pattern — therefore receives the
/// <c>CorrelationId</c>, <c>TenantId</c>, and <c>UserId</c> properties on every
/// <see cref="Serilog.Events.LogEvent"/> emitted inside the scope <i>without</i>
/// having to wire <see cref="TeamsLogEnricher"/> or
/// <see cref="LoggerEnrichmentConfigurationExtensions.WithTeamsContext"/>
/// explicitly. The <see cref="TeamsLogEnricher"/> path is preserved as a defence
/// in depth for hosts that disable <c>FromLogContext</c>.
/// </para>
/// </remarks>
public static class TeamsLogContext
{
    private static readonly AsyncLocal<TeamsLogContextEntry?> CurrentEntry = new();

    /// <summary>
    /// Push a new entry onto the ambient context, inheriting any key the caller
    /// leaves <c>null</c>/empty from the parent entry. Returns a disposable token
    /// that restores the previous entry when disposed.
    /// </summary>
    /// <param name="correlationId">End-to-end correlation ID; inherits when null/empty.</param>
    /// <param name="tenantId">Entra ID tenant; inherits when null/empty.</param>
    /// <param name="userId">Acting / target user; inherits when null/empty.</param>
    /// <returns>Disposable token that pops the entry back to the parent on dispose.</returns>
    public static IDisposable Push(string? correlationId, string? tenantId, string? userId)
    {
        var parent = CurrentEntry.Value;

        var effectiveCorrelationId = !string.IsNullOrEmpty(correlationId)
            ? correlationId
            : parent?.CorrelationId ?? TeamsLogScope.EmptyValueSentinel;
        var effectiveTenantId = !string.IsNullOrEmpty(tenantId)
            ? tenantId
            : parent?.TenantId ?? TeamsLogScope.EmptyValueSentinel;
        var effectiveUserId = !string.IsNullOrEmpty(userId)
            ? userId
            : parent?.UserId ?? TeamsLogScope.EmptyValueSentinel;

        var entry = new TeamsLogContextEntry(
            effectiveCorrelationId,
            effectiveTenantId,
            effectiveUserId);

        CurrentEntry.Value = entry;

        // Iter-10 evaluator fix item 3 — Stage 6.3 step 5 "every log entry" contract
        // requires ALL three keys on every emitted LogEvent. Push effective values
        // (sentinel-substituted for null/empty) for ALL three keys so the canonical
        // Serilog `Enrich.FromLogContext()` enricher always sees a complete frame.
        // We push EFFECTIVE values (not just caller-supplied) so the inner scope's
        // LogContext frame shadows the parent's per-key — a log entry emitted inside
        // the inner scope sees the inner scope's effective key set, including the
        // parent values that were inherited (rather than the parent's frame leaking
        // a different value if the inner caller passed a sentinel).
        var serilogTokens = new List<IDisposable>(capacity: 3)
        {
            LogContext.PushProperty(TeamsLogScope.CorrelationIdKey, effectiveCorrelationId),
            LogContext.PushProperty(TeamsLogScope.TenantIdKey, effectiveTenantId),
            LogContext.PushProperty(TeamsLogScope.UserIdKey, effectiveUserId),
        };

        return new CompositeContextToken(parent, serilogTokens);
    }

    /// <summary>
    /// Snapshot the current ambient enrichment values. Returns <c>(null, null, null)</c>
    /// when no scope is active.
    /// </summary>
    public static (string? CorrelationId, string? TenantId, string? UserId) Snapshot()
    {
        var entry = CurrentEntry.Value;
        return (entry?.CorrelationId, entry?.TenantId, entry?.UserId);
    }

    private sealed class TeamsLogContextEntry
    {
        public TeamsLogContextEntry(string? correlationId, string? tenantId, string? userId)
        {
            CorrelationId = correlationId;
            TenantId = tenantId;
            UserId = userId;
        }

        public string? CorrelationId { get; }

        public string? TenantId { get; }

        public string? UserId { get; }
    }

    /// <summary>
    /// Iter-5 item 1 — disposes both the AsyncLocal pop (restoring
    /// <see cref="CurrentEntry"/> to its parent) and every
    /// <see cref="LogContext.PushProperty(string, object?, bool)"/> token returned
    /// during <see cref="Push"/>. Serilog pops are invoked in reverse-push order
    /// (the canonical LIFO contract for <c>LogContext</c>) so a parent scope's
    /// <c>CorrelationId</c> remains the active frame after an inner scope ends.
    /// Pops are guarded so a single throw does not leak ambient state — the
    /// AsyncLocal pop is in a <c>finally</c> so it always runs.
    /// </summary>
    private sealed class CompositeContextToken : IDisposable
    {
        private readonly TeamsLogContextEntry? _previous;
        private readonly List<IDisposable> _serilogTokens;
        private bool _disposed;

        public CompositeContextToken(TeamsLogContextEntry? previous, List<IDisposable> serilogTokens)
        {
            _previous = previous;
            _serilogTokens = serilogTokens;
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
                // LIFO pop matches the order Serilog's LogContext maintains its
                // internal stack — dispose newest first so older frames are
                // restored as the active enrichment.
                for (var i = _serilogTokens.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        _serilogTokens[i].Dispose();
                    }
                    catch
                    {
                        // A single Serilog pop failure must not strand the
                        // AsyncLocal pop — swallow per-token exceptions so the
                        // finally block still restores CurrentEntry to the parent.
                    }
                }
            }
            finally
            {
                CurrentEntry.Value = _previous;
            }
        }
    }
}
