using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;

namespace AgentSwarm.Messaging.Teams.Diagnostics;

/// <summary>
/// Stage 6.3 <see cref="IStartupFilter"/> that auto-wires the canonical Teams
/// <c>/health</c>, <c>/health/live</c>, and <c>/health/ready</c> endpoints into ANY
/// ASP.NET Core host that has called
/// <see cref="TeamsDiagnosticsServiceCollectionExtensions.AddTeamsDiagnostics"/>,
/// without requiring the host to also call
/// <see cref="TeamsHealthCheckEndpointRouteBuilderExtensions.MapTeamsHealthChecks"/>
/// from its <c>Configure</c> delegate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (iter-3 evaluator feedback item 2).</b> The first cut of the
/// §6.3 endpoint surface left <see cref="TeamsHealthCheckEndpointRouteBuilderExtensions.MapTeamsHealthChecks"/>
/// as an opt-in helper. Reviewers correctly noted that <c>AddTeamsDiagnostics()</c>
/// would compose the health checks but still leave the <c>/health</c> route
/// dependent on undocumented host wiring — so the §6.3 scenario
/// ("<i>When <c>/health</c> is called…</i>") would not be satisfied by default. An
/// <see cref="IStartupFilter"/> closes that gap: when registered in DI, the ASP.NET
/// Core hosting infrastructure invokes the filter while building the request
/// pipeline, so the health endpoints are added to the route table without the host
/// touching <c>Configure</c>. Hosts that prefer manual control over endpoint
/// composition can pass <c>autoMapHealthEndpoints: false</c> to
/// <c>AddTeamsDiagnostics</c> (see overload) and the filter is not registered.
/// </para>
/// <para>
/// <b>Pipeline placement.</b> The filter's <see cref="Configure"/> calls
/// <see cref="EndpointRoutingApplicationBuilderExtensions.UseRouting"/> + a
/// <see cref="EndpointRoutingApplicationBuilderExtensions.UseEndpoints"/> block
/// BEFORE the host's own pipeline (the <c>next</c> delegate). UseRouting is
/// idempotent under ASP.NET Core's dedup — calling it twice is the standard
/// pattern for sandwich middleware between routing and endpoints, and the host's
/// subsequent <c>UseRouting</c> call (if any) is a no-op for matching purposes.
/// The endpoints registered here use the canonical predicate from
/// <see cref="TeamsHealthCheckEndpointRouteBuilderExtensions.TeamsTag"/>, so any
/// health checks the host added under a different tag namespace continue to be
/// invisible to the Teams readiness route.
/// </para>
/// <para>
/// <b>Test-host safety.</b> Hosts built with <c>Microsoft.AspNetCore.TestHost</c>
/// can opt out of the auto-mapping by passing <c>autoMapHealthEndpoints: false</c>
/// to <c>AddTeamsDiagnostics</c> — this is exactly what the explicit-mapping
/// integration tests do so the test exercises only the call site under test.
/// </para>
/// </remarks>
internal sealed class TeamsHealthCheckEndpointStartupFilter : IStartupFilter
{
    private readonly string _pathPrefix;

    public TeamsHealthCheckEndpointStartupFilter(string pathPrefix)
    {
        if (string.IsNullOrWhiteSpace(pathPrefix))
        {
            throw new ArgumentException("Path prefix must be non-empty.", nameof(pathPrefix));
        }
        if (!pathPrefix.StartsWith('/'))
        {
            throw new ArgumentException("Path prefix must start with '/'.", nameof(pathPrefix));
        }
        _pathPrefix = pathPrefix;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // UseRouting is safe to call before the host's own UseRouting — ASP.NET
            // Core's routing middleware is idempotent on the request side, and the
            // routing graph is shared across all UseEndpoints blocks in the
            // pipeline. Calling UseRouting + UseEndpoints HERE (before the host's
            // pipeline) makes /health, /health/live, /health/ready discoverable
            // even on hosts that never call MapTeamsHealthChecks manually.
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapTeamsHealthChecks(_pathPrefix);
            });

            next(app);
        };
    }
}
