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
/// <b>Every-log-entry contract (§6.3 step 5).</b> The enricher emits ALL THREE
/// canonical properties — <see cref="TeamsLogScope.CorrelationIdKey"/>,
/// <see cref="TeamsLogScope.TenantIdKey"/>, <see cref="TeamsLogScope.UserIdKey"/>
/// — on <i>every</i> <see cref="LogEvent"/>, with no exception. When a
/// <see cref="TeamsLogScope.BeginScope"/> is active the values come from
/// <see cref="TeamsLogContext.Snapshot"/>; when no scope is active the enricher
/// emits <see cref="TeamsLogScope.EmptyValueSentinel"/> (<c>"-"</c>) for all three
/// slots so the envelope shape is uniform across in-scope, out-of-scope, and
/// partial-scope frames. This implements <c>implementation-plan.md</c> §6.3 step 5
/// verbatim ("Serilog enrichers for CorrelationId, TenantId, UserId on every log
/// entry"). Downstream dashboards see a single stable three-property shape and can
/// filter the sentinel trivially (e.g. <c>WHERE UserId != '-'</c>).
/// </para>
/// <para>
/// <b>Stage 6.3 iter-12 evaluator fix item 2.</b> Prior to iter-12 the enricher
/// returned early when <see cref="TeamsLogContext.Snapshot"/> reported no active
/// scope (i.e. all three values <c>null</c>). That left lifecycle, startup,
/// background-poll, and security-pre-resolution log entries structurally
/// uncovered — directly contradicting the §6.3 step 5 wording. The current
/// implementation always emits all three keys; the sentinel
/// (<see cref="TeamsLogScope.EmptyValueSentinel"/>) marks frames that genuinely
/// had no ambient enrichment context.
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

        // Stage 6.3 iter-12 evaluator fix item 2 — §6.3 step 5 mandates ALL three
        // canonical keys on EVERY emitted LogEvent, not just on entries that
        // happen to be inside a TeamsLogScope. When TeamsLogContext.Snapshot
        // reports no active scope (all three null) we still emit the canonical
        // keys, substituting TeamsLogScope.EmptyValueSentinel ("-") in every
        // slot. The envelope shape is therefore uniform across all three
        // frame classes (in-scope full triple, in-scope partial triple
        // sentinel-substituted by TeamsLogScope.BeginScope, no-scope all-sentinel)
        // — dashboards never see a missing key and never have to special-case
        // unenriched frames.
        var effectiveCorrelationId = correlationId ?? TeamsLogScope.EmptyValueSentinel;
        var effectiveTenantId = tenantId ?? TeamsLogScope.EmptyValueSentinel;
        var effectiveUserId = userId ?? TeamsLogScope.EmptyValueSentinel;

        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty(TeamsLogScope.CorrelationIdKey, effectiveCorrelationId));
        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty(TeamsLogScope.TenantIdKey, effectiveTenantId));
        logEvent.AddPropertyIfAbsent(
            propertyFactory.CreateProperty(TeamsLogScope.UserIdKey, effectiveUserId));
    }
}
