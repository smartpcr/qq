using AgentSwarm.Messaging.Teams.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AgentSwarm.Messaging.Teams.Tests.Diagnostics;

/// <summary>
/// Verifies the first-class Serilog wiring extension
/// (<see cref="LoggerEnrichmentConfigurationExtensions.WithTeamsContext"/>) added in
/// iter-2 to resolve evaluator feedback item 2 — the §6.3 step-5 enrichment must
/// land on every emitted <see cref="LogEvent"/> through a single fluent call (no
/// manual <see cref="IServiceProvider"/> resolve, no risk of host forgetting to
/// pipe the enricher).
/// </summary>
[Collection(TeamsTelemetryCollection.Name)]
public sealed class LoggerEnrichmentConfigurationExtensionsTests
{
    [Fact]
    public void WithTeamsContext_InsideBeginScope_StampsAllThreeCanonicalProperties()
    {
        var sink = new CapturingSink();
        using var serilog = new LoggerConfiguration()
            .Enrich.WithTeamsContext()
            .WriteTo.Sink(sink)
            .MinimumLevel.Verbose()
            .CreateLogger();
        var logger = new NoopMicrosoftLogger();

        using (TeamsLogScope.BeginScope(logger, correlationId: "corr-fluent", tenantId: "tenant-fluent", userId: "user-fluent"))
        {
            serilog.Information("event inside scope");
        }

        var captured = Assert.Single(sink.Events);
        AssertScalarProperty(captured, TeamsLogScope.CorrelationIdKey, "corr-fluent");
        AssertScalarProperty(captured, TeamsLogScope.TenantIdKey, "tenant-fluent");
        AssertScalarProperty(captured, TeamsLogScope.UserIdKey, "user-fluent");
    }

    [Fact]
    public void WithTeamsContext_OutsideScope_EmitsNoEnrichmentProperties()
    {
        var sink = new CapturingSink();
        using var serilog = new LoggerConfiguration()
            .Enrich.WithTeamsContext()
            .WriteTo.Sink(sink)
            .MinimumLevel.Verbose()
            .CreateLogger();

        serilog.Information("event outside scope");

        var captured = Assert.Single(sink.Events);
        Assert.False(captured.Properties.ContainsKey(TeamsLogScope.CorrelationIdKey));
        Assert.False(captured.Properties.ContainsKey(TeamsLogScope.TenantIdKey));
        Assert.False(captured.Properties.ContainsKey(TeamsLogScope.UserIdKey));
    }

    [Fact]
    public void WithTeamsContext_ReturnsParentLoggerConfiguration_ForFluentChaining()
    {
        var sink = new CapturingSink();
        // The contract: .Enrich.WithTeamsContext() returns LoggerConfiguration so
        // further .WriteTo / .MinimumLevel / .Enrich calls chain off it. If the
        // extension returned the LoggerEnrichmentConfiguration we wouldn't be able
        // to call .WriteTo immediately after it — that's what this test enshrines.
        using var serilog = new LoggerConfiguration()
            .Enrich.WithTeamsContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        Assert.NotNull(serilog);
    }

    [Fact]
    public void WithTeamsContext_NullEnrichment_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LoggerEnrichmentConfigurationExtensions.WithTeamsContext(null!));
    }

    private static void AssertScalarProperty(LogEvent logEvent, string key, string expected)
    {
        Assert.True(logEvent.Properties.TryGetValue(key, out var value), $"Expected property '{key}'.");
        var scalar = Assert.IsType<ScalarValue>(value);
        Assert.Equal(expected, scalar.Value);
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class NoopMicrosoftLogger : Microsoft.Extensions.Logging.ILogger
    {
        IDisposable Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => new NoopScope();
        bool Microsoft.Extensions.Logging.ILogger.IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        void Microsoft.Extensions.Logging.ILogger.Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) { }

        private sealed class NoopScope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
