using AgentSwarm.Messaging.Teams.Diagnostics;
using Serilog;
using Serilog.Events;

namespace AgentSwarm.Messaging.Teams.Tests.Diagnostics;

/// <summary>
/// Unit tests for <see cref="TeamsLogEnricher"/> — the Serilog
/// <see cref="Serilog.Core.ILogEventEnricher"/> that stamps the three §6.3
/// enrichment keys onto every <see cref="LogEvent"/> emitted inside a
/// <see cref="TeamsLogScope.BeginScope"/>. Each test builds a real Serilog
/// <see cref="Logger"/> wired to a capturing sink and verifies the emitted
/// properties on the captured <see cref="LogEvent"/>.
/// </summary>
public sealed class TeamsLogEnricherTests
{
    [Fact]
    public void Enrich_InsideBeginScope_StampsAllThreeCanonicalProperties()
    {
        using var harness = new SerilogHarness();
        var logger = new RecordingLogger();

        using (TeamsLogScope.BeginScope(logger, correlationId: "corr-42", tenantId: "tenant-42", userId: "user-42"))
        {
            harness.Logger.Information("test event inside scope");
        }

        var captured = Assert.Single(harness.Captured);
        AssertScalarProperty(captured, TeamsLogScope.CorrelationIdKey, "corr-42");
        AssertScalarProperty(captured, TeamsLogScope.TenantIdKey, "tenant-42");
        AssertScalarProperty(captured, TeamsLogScope.UserIdKey, "user-42");
    }

    [Fact]
    public void Enrich_OutsideScope_AddsNoEnrichmentProperties()
    {
        using var harness = new SerilogHarness();

        harness.Logger.Information("test event outside scope");

        var captured = Assert.Single(harness.Captured);
        Assert.False(captured.Properties.ContainsKey(TeamsLogScope.CorrelationIdKey));
        Assert.False(captured.Properties.ContainsKey(TeamsLogScope.TenantIdKey));
        Assert.False(captured.Properties.ContainsKey(TeamsLogScope.UserIdKey));
    }

    [Fact]
    public void Enrich_OnlyCorrelationIdSet_DoesNotEmitEmptyTenantOrUserSlots()
    {
        using var harness = new SerilogHarness();
        var logger = new RecordingLogger();

        using (TeamsLogScope.BeginScope(logger, correlationId: "corr-only"))
        {
            harness.Logger.Information("partial");
        }

        var captured = Assert.Single(harness.Captured);
        AssertScalarProperty(captured, TeamsLogScope.CorrelationIdKey, "corr-only");
        Assert.False(captured.Properties.ContainsKey(TeamsLogScope.TenantIdKey));
        Assert.False(captured.Properties.ContainsKey(TeamsLogScope.UserIdKey));
    }

    [Fact]
    public void Enrich_AfterScopeDisposed_ResumesEmittingWithoutProperties()
    {
        using var harness = new SerilogHarness();
        var logger = new RecordingLogger();

        using (TeamsLogScope.BeginScope(logger, correlationId: "corr-A", tenantId: "tenant-A", userId: "user-A"))
        {
            harness.Logger.Information("inside");
        }

        harness.Logger.Information("outside");

        Assert.Equal(2, harness.Captured.Count);
        AssertScalarProperty(harness.Captured[0], TeamsLogScope.CorrelationIdKey, "corr-A");
        Assert.False(harness.Captured[1].Properties.ContainsKey(TeamsLogScope.CorrelationIdKey));
    }

    [Fact]
    public void Enrich_NestedScopeOverridesOuter_AndOuterValueRestoredAfterInnerDispose()
    {
        using var harness = new SerilogHarness();
        var logger = new RecordingLogger();

        using (TeamsLogScope.BeginScope(logger, correlationId: "corr-outer", tenantId: "tenant-outer"))
        {
            using (TeamsLogScope.BeginScope(logger, correlationId: "corr-inner"))
            {
                harness.Logger.Information("inner event");
            }

            harness.Logger.Information("outer event after inner dispose");
        }

        Assert.Equal(2, harness.Captured.Count);
        AssertScalarProperty(harness.Captured[0], TeamsLogScope.CorrelationIdKey, "corr-inner");
        AssertScalarProperty(harness.Captured[0], TeamsLogScope.TenantIdKey, "tenant-outer");
        AssertScalarProperty(harness.Captured[1], TeamsLogScope.CorrelationIdKey, "corr-outer");
        AssertScalarProperty(harness.Captured[1], TeamsLogScope.TenantIdKey, "tenant-outer");
    }

    [Fact]
    public void Enrich_NullLogEvent_Throws()
    {
        var enricher = new TeamsLogEnricher();
        Assert.Throws<ArgumentNullException>(() => enricher.Enrich(null!, new CapturingSink()));
    }

    [Fact]
    public void Enrich_NullPropertyFactory_Throws()
    {
        var enricher = new TeamsLogEnricher();
        var logEvent = new LogEvent(
            timestamp: DateTimeOffset.UtcNow,
            level: LogEventLevel.Information,
            exception: null,
            messageTemplate: new Serilog.Events.MessageTemplate("x", Array.Empty<Serilog.Parsing.MessageTemplateToken>()),
            properties: Array.Empty<LogEventProperty>());

        Assert.Throws<ArgumentNullException>(() => enricher.Enrich(logEvent, null!));
    }

    private static void AssertScalarProperty(LogEvent logEvent, string key, string expected)
    {
        Assert.True(logEvent.Properties.TryGetValue(key, out var value), $"Expected property '{key}'.");
        var scalar = Assert.IsType<ScalarValue>(value);
        Assert.Equal(expected, scalar.Value);
    }

    /// <summary>
    /// Wires a real Serilog <see cref="Logger"/> with <see cref="TeamsLogEnricher"/>
    /// composed via <see cref="LoggerConfiguration.Enrich"/> and a capturing sink so
    /// each test can assert on the actual <see cref="LogEvent"/> the enricher produced.
    /// </summary>
    private sealed class SerilogHarness : IDisposable
    {
        private readonly Serilog.Core.Logger _logger;
        private readonly CapturingSink _sink;

        public SerilogHarness()
        {
            _sink = new CapturingSink();
            _logger = new LoggerConfiguration()
                .Enrich.With(new TeamsLogEnricher())
                .WriteTo.Sink(_sink)
                .MinimumLevel.Verbose()
                .CreateLogger();
        }

        public ILogger Logger => _logger;

        public IReadOnlyList<LogEvent> Captured => _sink.Events;

        public void Dispose() => _logger.Dispose();
    }

    private sealed class CapturingSink : Serilog.Core.ILogEventSink, Serilog.Core.ILogEventPropertyFactory
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);

        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new(name, new ScalarValue(value));
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        IDisposable Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => new NoopScope();

        bool Microsoft.Extensions.Logging.ILogger.IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;

        void Microsoft.Extensions.Logging.ILogger.Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }

        private sealed class NoopScope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
