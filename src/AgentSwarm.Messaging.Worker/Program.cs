using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams;
using AgentSwarm.Messaging.Teams.Bot;
using AgentSwarm.Messaging.Teams.Cards;
using AgentSwarm.Messaging.Teams.Commands;
using AgentSwarm.Messaging.Teams.Controllers;
using AgentSwarm.Messaging.Teams.Inbound;
using AgentSwarm.Messaging.Teams.Middleware;
using AgentSwarm.Messaging.Teams.Storage;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AgentSwarm.Messaging.Worker;

/// <summary>
/// ASP.NET Core host entry point for the Teams messaging worker. Composes the connector
/// pipeline: HTTP middleware (tenant + rate limit) → controller → CloudAdapter (JWT →
/// telemetry → dedup → bot). Stage 2.1 of <c>implementation-plan.md</c>.
/// </summary>
public class Program
{
    /// <summary>Public default constructor so the type can be used as the
    /// <c>TEntryPoint</c> in <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
    public Program() { }

    /// <summary>Standard host entry point.</summary>
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        ConfigureServices(builder);
        var app = builder.Build();
        ConfigurePipeline(app);
        app.Run();
    }

    /// <summary>Register services on the supplied <see cref="WebApplicationBuilder"/>.</summary>
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));

        // Options.
        builder.Services
            .AddOptions<TeamsMessagingOptions>()
            .Bind(builder.Configuration.GetSection(TeamsMessagingOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IPostConfigureOptions<TeamsMessagingOptions>, TeamsMessagingPostConfigure>();
        builder.Services.AddSingleton<IValidateOptions<TeamsMessagingOptions>, TeamsMessagingOptionsValidator>();

        builder.Services
            .AddOptions<TelemetryMiddlewareOptions>()
            .Bind(builder.Configuration.GetSection("Telemetry"));

        // ASP.NET Core HTTP middleware classes are normally instantiated by the framework when
        // app.UseMiddleware<T>() is called, with constructor parameters resolved from the root
        // service provider. Registering them explicitly as singletons makes the dependency
        // graph visible to operators inspecting the DI container and matches the Stage 2.1
        // brief that calls for "register the middleware as a singleton".
        builder.Services.AddSingleton<TenantValidationMiddleware>();
        builder.Services.AddSingleton<RateLimitMiddleware>();

        // Bot Framework IMiddleware classes (run inside CloudAdapter) must be registered as
        // singletons so the same instances are added to the adapter middleware set.
        builder.Services.AddSingleton<TelemetryMiddleware>();
        builder.Services.AddSingleton<ActivityDeduplicationMiddleware>();

        // Bot adapter + bot.
        builder.Services.AddSingleton<BotFrameworkAuthentication>(sp =>
        {
            var teamsOptions = sp.GetRequiredService<IOptions<TeamsMessagingOptions>>().Value;
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MicrosoftAppId"] = teamsOptions.MicrosoftAppId,
                    ["MicrosoftAppPassword"] = teamsOptions.MicrosoftAppPassword,
                    ["MicrosoftAppTenantId"] = teamsOptions.MicrosoftAppTenantId,
                    ["MicrosoftAppType"] = "MultiTenant",
                })
                .Build();
            return new ConfigurationBotFrameworkAuthentication(config);
        });
        builder.Services.AddSingleton<IBotFrameworkHttpAdapter, TeamsCloudAdapter>();
        builder.Services.AddSingleton<IBot, TeamsSwarmActivityHandler>();

        // Stage 2.1 in-memory / no-op DI registrations. Each is replaced by a SQL- or
        // RBAC-backed implementation in a later stage (see the implementation plan).
        builder.Services.AddSingleton<IIdentityResolver, DefaultDenyIdentityResolver>();
        builder.Services.AddSingleton<IUserAuthorizationService, DefaultDenyAuthorizationService>();
        builder.Services.AddSingleton<IConversationReferenceStore, InMemoryConversationReferenceStore>();
        builder.Services.AddSingleton<IActivityIdStore, InMemoryActivityIdStore>();
        builder.Services.AddSingleton<IAgentQuestionStore, InMemoryAgentQuestionStore>();
        builder.Services.AddSingleton<ICardStateStore, NoOpCardStateStore>();
        builder.Services.AddSingleton<ICardActionHandler, NoOpCardActionHandler>();
        builder.Services.AddSingleton<ITeamsCardManager, NoOpCardManager>();
        builder.Services.AddSingleton<ICommandDispatcher, NoOpCommandDispatcher>();
        builder.Services.AddSingleton<ChannelInboundEventPublisher>();
        builder.Services.AddSingleton<IInboundEventPublisher>(sp => sp.GetRequiredService<ChannelInboundEventPublisher>());
        builder.Services.AddSingleton<IAuditLogger, NoOpAuditLogger>();
        builder.Services.AddSingleton<IMessageOutbox, NoOpMessageOutbox>();

        // Controller discovery — pull TeamsWebhookController out of AgentSwarm.Messaging.Teams.
        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(TeamsWebhookController).Assembly);

        // Health checks.
        builder.Services
            .AddHealthChecks()
            .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: new[] { "ready" });

        // OpenTelemetry tracing.
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("AgentSwarm.Messaging.Worker"))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddSource(TelemetryMiddleware.ActivitySourceName));
    }

    /// <summary>Configure the HTTP pipeline on the supplied <see cref="WebApplication"/>.</summary>
    public static void ConfigurePipeline(WebApplication app)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));

        // ASP.NET Core HTTP middleware — runs BEFORE CloudAdapter. Order matters:
        //   1. TenantValidationMiddleware (403 on blocked tenants)
        //   2. RateLimitMiddleware       (429 on rate-limited tenants)
        // CloudAdapter then performs JWT validation (401 on invalid tokens) and runs the
        // Bot Framework IMiddleware pipeline (TelemetryMiddleware → ActivityDeduplicationMiddleware).
        app.UseMiddleware<TenantValidationMiddleware>();
        app.UseMiddleware<RateLimitMiddleware>();

        app.MapControllers();
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
        });
    }
}
