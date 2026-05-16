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
            : parent?.CorrelationId;
        var effectiveTenantId = !string.IsNullOrEmpty(tenantId)
            ? tenantId
            : parent?.TenantId;
        var effectiveUserId = !string.IsNullOrEmpty(userId)
            ? userId
            : parent?.UserId;

        // If nothing changed (every supplied value was empty AND the parent already
        // carries the same data), return a cheap no-op instead of allocating a new
        // entry — keeps the hot path on the connector free of pointless GC pressure.
        if (effectiveCorrelationId is null && effectiveTenantId is null && effectiveUserId is null)
        {
            return NoopToken.Instance;
        }

        var entry = new TeamsLogContextEntry(
            effectiveCorrelationId,
            effectiveTenantId,
            effectiveUserId);

        CurrentEntry.Value = entry;
        return new PopToken(parent);
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

    private sealed class PopToken : IDisposable
    {
        private readonly TeamsLogContextEntry? _previous;
        private bool _disposed;

        public PopToken(TeamsLogContextEntry? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentEntry.Value = _previous;
        }
    }

    private sealed class NoopToken : IDisposable
    {
        public static readonly NoopToken Instance = new();

        private NoopToken()
        {
        }

        public void Dispose()
        {
        }
    }
}
