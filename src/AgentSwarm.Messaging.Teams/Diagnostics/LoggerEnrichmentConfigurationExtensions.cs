using Serilog;
using Serilog.Configuration;

namespace AgentSwarm.Messaging.Teams.Diagnostics;

/// <summary>
/// First-class Serilog wiring extensions for the Stage 6.3 enrichment surface
/// (<c>implementation-plan.md</c> §6.3 step 5). Hosts compose
/// <see cref="TeamsLogEnricher"/> into a <see cref="LoggerConfiguration"/> with a
/// single fluent call — no manual <see cref="IServiceProvider"/> resolve, no manual
/// <c>.Enrich.With(new TeamsLogEnricher())</c>:
/// </summary>
/// <remarks>
/// <para>
/// <b>Standalone composition</b> (single-process bootstrap, e.g. <c>Program.cs</c>):
/// <code>
/// Log.Logger = new LoggerConfiguration()
///     .Enrich.WithTeamsContext()
///     .WriteTo.Console()
///     .CreateLogger();
/// </code>
/// </para>
/// <para>
/// <b>ASP.NET Core composition</b> (via
/// <c>Serilog.Extensions.Hosting</c> / <c>Microsoft.Extensions.Hosting.UseSerilog</c>):
/// <code>
/// builder.Host.UseSerilog((ctx, services, cfg) => cfg
///     .ReadFrom.Configuration(ctx.Configuration)
///     .Enrich.WithTeamsContext());
/// </code>
/// </para>
/// <para>
/// <b>Why this extension exists.</b> The §6.3 iter-1 evaluator review flagged that
/// merely registering <see cref="TeamsLogEnricher"/> as a DI singleton (which is what
/// <see cref="TeamsDiagnosticsServiceCollectionExtensions.AddTeamsSerilogEnricher"/>
/// does) does NOT auto-wire it into the Serilog pipeline — hosts still had to write
/// <c>.Enrich.With(sp.GetRequiredService&lt;TeamsLogEnricher&gt;())</c> by hand and an
/// omission would silently strip enrichment off every log entry. This extension
/// provides the one-line composition path so hosts cannot accidentally drop the
/// enrichment and §6.3 step 5's "on every log entry" contract is satisfied with a
/// single fluent call.
/// </para>
/// <para>
/// <b>No DI required.</b> The extension allocates the enricher inline
/// (<c>enrichment.With&lt;TeamsLogEnricher&gt;()</c>), which is safe because the
/// enricher itself is stateless — its only input is the ambient
/// <see cref="TeamsLogContext"/>. Hosts that prefer the DI-resolved singleton can
/// still pass it explicitly through <c>.Enrich.With(sp.GetRequiredService...)</c>;
/// both paths produce the identical enrichment.
/// </para>
/// </remarks>
public static class LoggerEnrichmentConfigurationExtensions
{
    /// <summary>
    /// Adds the <see cref="TeamsLogEnricher"/> to the Serilog enrichment pipeline so
    /// every <see cref="Serilog.Events.LogEvent"/> emitted inside an active
    /// <see cref="TeamsLogScope.BeginScope"/> carries the canonical
    /// <c>CorrelationId</c> / <c>TenantId</c> / <c>UserId</c> properties.
    /// </summary>
    /// <param name="enrichment">The <see cref="LoggerEnrichmentConfiguration"/> obtained
    /// from <see cref="LoggerConfiguration.Enrich"/>.</param>
    /// <returns>The parent <see cref="LoggerConfiguration"/> so further fluent calls
    /// can chain.</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="enrichment"/> is null.</exception>
    public static LoggerConfiguration WithTeamsContext(this LoggerEnrichmentConfiguration enrichment)
    {
        if (enrichment is null) throw new ArgumentNullException(nameof(enrichment));
        return enrichment.With<TeamsLogEnricher>();
    }
}
