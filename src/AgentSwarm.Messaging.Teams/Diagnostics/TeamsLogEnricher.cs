using Serilog.Core;
using Serilog.Events;

namespace AgentSwarm.Messaging.Teams.Diagnostics;

/// <summary>
/// Serilog <see cref="ILogEventEnricher"/> that stamps the three Stage 6.3 enrichment
/// keys — <see cref="TeamsLogScope.CorrelationIdKey"/>,
/// <see cref="TeamsLogScope.TenantIdKey"/>, and <see cref="TeamsLogScope.UserIdKey"/>
/// — onto every <see cref="LogEvent"/> emitted inside an active
/// <see cref="TeamsLogScope.BeginScope"/>. Implements
/// <c>implementation-plan.md</c> §6.3 step 5 ("Serilog enrichers for CorrelationId,
/// TenantId, UserId on every log entry") for hosts that wire Serilog as their logging
/// backend.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wiring (preferred — first-class fluent extension).</b> Hosts that use Serilog
/// compose the enricher through the
/// <see cref="LoggerEnrichmentConfigurationExtensions.WithTeamsContext"/> extension —
/// a single fluent call, no DI resolve required:
/// <code>
/// Log.Logger = new LoggerConfiguration()
///     .Enrich.WithTeamsContext()
///     .WriteTo.Console()
///     .CreateLogger();
/// </code>
/// or in an ASP.NET Core host:
/// <code>
/// builder.Host.UseSerilog((ctx, services, cfg) => cfg
///     .ReadFrom.Configuration(ctx.Configuration)
///     .Enrich.WithTeamsContext());
/// </code>
/// </para>
/// <para>
/// <b>Wiring (DI-resolved singleton).</b> Hosts that prefer to resolve the singleton
/// registered by
/// <see cref="TeamsDiagnosticsServiceCollectionExtensions.AddTeamsSerilogEnricher"/>
/// can also pass it explicitly:
/// <code>
/// var serviceProvider = services.BuildServiceProvider();
/// Log.Logger = new LoggerConfiguration()
///     .Enrich.With(serviceProvider.GetRequiredService&lt;TeamsLogEnricher&gt;())
///     .WriteTo.Console()
///     .CreateLogger();
/// </code>
/// Both paths produce identical enrichment because the enricher is stateless — its
/// only input is the ambient <see cref="TeamsLogContext"/>.
/// </para>
/// <para>
/// <b>Source of truth.</b> The enricher reads ambient values from
/// <see cref="TeamsLogContext.Snapshot"/>, which is populated by every call to
/// <see cref="TeamsLogScope.BeginScope"/>. That keeps the API surface single-source —
/// callers only have to invoke <c>BeginScope</c>, and the enrichment lands on
/// <i>both</i> the <see cref="Microsoft.Extensions.Logging.ILogger"/> scope dictionary
/// and the Serilog <see cref="LogEvent"/> property bag.
/// </para>
/// <para>
/// <b>Null/empty handling.</b> Keys whose ambient value is <c>null</c> or empty are
/// not added — Serilog dashboards therefore do not pollute their template with empty
/// property slots, and downstream queries that filter on the absence of an
/// enrichment key keep working.
/// </para>
/// </remarks>
public sealed class TeamsLogEnricher : ILogEventEnricher
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">
    /// When <paramref name="logEvent"/> or <paramref name="propertyFactory"/> is null.
    /// </exception>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        var (correlationId, tenantId, userId) = TeamsLogContext.Snapshot();

        if (!string.IsNullOrEmpty(correlationId))
        {
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty(TeamsLogScope.CorrelationIdKey, correlationId));
        }

        if (!string.IsNullOrEmpty(tenantId))
        {
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty(TeamsLogScope.TenantIdKey, tenantId));
        }

        if (!string.IsNullOrEmpty(userId))
        {
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty(TeamsLogScope.UserIdKey, userId));
        }
    }
}
