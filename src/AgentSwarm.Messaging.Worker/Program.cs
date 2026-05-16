namespace AgentSwarm.Messaging.Worker;

/// <summary>
/// Entry point for the AgentSwarm messaging gateway host. The host owns the
/// HTTP surface used by Slack (and other connector) inbound endpoints and
/// runs the background message-processing pipeline. Connector wiring (Slack
/// signature middleware, queues, controllers, ...) is registered by later
/// stages of the implementation plan.
/// </summary>
public class Program
{
    public static void Main(string[] args)
    {
        WebApplication app = BuildApp(args);
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
        app.Run();
    }

    /// <summary>
    /// Builds the configured <see cref="WebApplication"/> without running it.
    /// Exposed so tests can construct the host (e.g., for
    /// <c>WebApplicationFactory&lt;Program&gt;</c>) without spinning a TCP
    /// listener.
    /// </summary>
    public static WebApplication BuildApp(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.AddRouting();
        return builder.Build();
    }
}
