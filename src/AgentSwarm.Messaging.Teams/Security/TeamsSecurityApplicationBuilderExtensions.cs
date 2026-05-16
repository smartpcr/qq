using Microsoft.AspNetCore.Builder;

namespace AgentSwarm.Messaging.Teams.Security;

/// <summary>
/// <see cref="IApplicationBuilder"/> wiring helpers for the Stage 5.1 security layer.
/// Composes <see cref="TenantValidationMiddleware"/> into the ASP.NET Core HTTP pipeline
/// so every inbound bot activity (default path <c>/api/messages</c>) is tenant-validated
/// before <c>CloudAdapter.ProcessAsync</c> sees it.
/// </summary>
/// <remarks>
/// Hosts call <c>app.UseTeamsSecurity()</c> exactly once during pipeline configuration,
/// typically immediately before <c>app.UseRouting()</c> / <c>MapPost("/api/messages")</c>
/// so the rejection short-circuits HTTP 403 before any route is matched.
/// </remarks>
public static class TeamsSecurityApplicationBuilderExtensions
{
    /// <summary>
    /// Register <see cref="TenantValidationMiddleware"/> in the ASP.NET Core pipeline.
    /// The middleware is registered as a DI singleton via
    /// <see cref="TeamsSecurityServiceCollectionExtensions.AddTeamsSecurity"/>, so this
    /// extension simply slots it into the request pipeline.
    /// </summary>
    /// <param name="app">The application builder to mutate.</param>
    /// <returns>The same <paramref name="app"/> instance (fluent).</returns>
    /// <exception cref="ArgumentNullException">If <paramref name="app"/> is null.</exception>
    public static IApplicationBuilder UseTeamsSecurity(this IApplicationBuilder app)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));

        app.UseMiddleware<TenantValidationMiddleware>();
        return app;
    }
}
