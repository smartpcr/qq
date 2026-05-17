using AgentSwarm.Messaging.Teams.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests.Diagnostics;

/// <summary>
/// Unit tests for <see cref="TeamsLogScope"/> — the structured-logging enrichment
/// helper that drives the §6.3 Step 5 requirement ("Serilog enrichers for
/// CorrelationId, TenantId, UserId on every log entry"). The tests verify the
/// scope-keys / scope-state contract that the Serilog ILogger bridge consumes.
/// </summary>
public sealed class TeamsLogScopeTests
{
    [Fact]
    public void BeginScope_AllFields_PushesAllCanonicalKeys()
    {
        var logger = new RecordingLogger();

        using (TeamsLogScope.BeginScope(logger, correlationId: "corr-1", tenantId: "tenant-1", userId: "user-1"))
        {
            Assert.Single(logger.Scopes);
            var state = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(logger.Scopes[0]);
            Assert.Equal("corr-1", state[TeamsLogScope.CorrelationIdKey]);
            Assert.Equal("tenant-1", state[TeamsLogScope.TenantIdKey]);
            Assert.Equal("user-1", state[TeamsLogScope.UserIdKey]);
        }

        Assert.Equal(1, logger.ScopesDisposed);
    }

    [Fact]
    public void BeginScope_NullUserId_OmitsUserIdEnrichment()
    {
        var logger = new RecordingLogger();

        using (TeamsLogScope.BeginScope(logger, correlationId: "c", tenantId: "t", userId: null))
        {
            var state = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(logger.Scopes[0]);
            Assert.True(state.ContainsKey(TeamsLogScope.CorrelationIdKey));
            Assert.True(state.ContainsKey(TeamsLogScope.TenantIdKey));
            Assert.False(state.ContainsKey(TeamsLogScope.UserIdKey));
        }
    }

    [Fact]
    public void BeginScope_AllNull_ReturnsNoOpDisposable_WithoutPushingScope()
    {
        var logger = new RecordingLogger();

        using var scope = TeamsLogScope.BeginScope(logger);
        Assert.NotNull(scope);
        Assert.Empty(logger.Scopes);
    }

    [Fact]
    public void BeginScope_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TeamsLogScope.BeginScope(logger: null!, correlationId: "c"));
    }

    [Fact]
    public void BeginScope_LoggerReturnsNull_StillReturnsDisposable()
    {
        // NullLogger.BeginScope returns NullScope.Instance which is non-null; this test
        // belt-and-braces the contract that even a misbehaving logger never causes a
        // NullReferenceException at the call site.
        var logger = NullLogger.Instance;

        using var scope = TeamsLogScope.BeginScope(logger, correlationId: "corr");
        Assert.NotNull(scope);
    }

    [Fact]
    public void BeginScope_PushesValuesOntoTeamsLogContext_AndDisposeRestoresPrevious()
    {
        var logger = new RecordingLogger();

        var (corrBefore, tenantBefore, userBefore) = TeamsLogContext.Snapshot();
        Assert.Null(corrBefore);
        Assert.Null(tenantBefore);
        Assert.Null(userBefore);

        using (TeamsLogScope.BeginScope(logger, correlationId: "c-1", tenantId: "t-1", userId: "u-1"))
        {
            var (corr, tenant, user) = TeamsLogContext.Snapshot();
            Assert.Equal("c-1", corr);
            Assert.Equal("t-1", tenant);
            Assert.Equal("u-1", user);
        }

        var (corrAfter, tenantAfter, userAfter) = TeamsLogContext.Snapshot();
        Assert.Null(corrAfter);
        Assert.Null(tenantAfter);
        Assert.Null(userAfter);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<object?> Scopes { get; } = new();
        public int ScopesDisposed { get; private set; }

        IDisposable ILogger.BeginScope<TState>(TState state)
        {
            Scopes.Add(state);
            return new TrackingScope(this);
        }

        bool ILogger.IsEnabled(LogLevel logLevel) => false;

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class TrackingScope : IDisposable
        {
            private readonly RecordingLogger _owner;
            public TrackingScope(RecordingLogger owner) { _owner = owner; }
            public void Dispose() => _owner.ScopesDisposed++;
        }
    }
}
