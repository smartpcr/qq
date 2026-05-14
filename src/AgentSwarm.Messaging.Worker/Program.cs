using System.Diagnostics.CodeAnalysis;
using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Teams;
using AgentSwarm.Messaging.Teams.Controllers;
using AgentSwarm.Messaging.Teams.Middleware;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Options;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Options binding
// ---------------------------------------------------------------------------
builder.Services
    .AddOptions<TeamsMessagingOptions>()
    .Bind(builder.Configuration.GetSection("Teams"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<TeamsMessagingOptions>, TeamsMessagingOptionsValidator>();
builder.Services.AddSingleton<IPostConfigureOptions<TeamsMessagingOptions>, TeamsMessagingPostConfigure>();

builder.Services
    .AddOptions<TelemetryMiddlewareOptions>()
    .Bind(builder.Configuration.GetSection("Telemetry"));

// ---------------------------------------------------------------------------
// Bot Framework authentication + adapter
// ---------------------------------------------------------------------------
// ConfigurationBotFrameworkAuthentication reads MicrosoftAppId / MicrosoftAppPassword /
// MicrosoftAppType / MicrosoftAppTenantId from the supplied IConfiguration. We point it at
// the "Teams" section so the same keys serve TeamsMessagingOptions and the auth pipeline.
builder.Services.AddSingleton<BotFrameworkAuthentication>(sp =>
    new ConfigurationBotFrameworkAuthentication(builder.Configuration.GetSection("Teams")));

builder.Services.AddSingleton<TelemetryMiddleware>();
builder.Services.AddSingleton<ActivityDeduplicationMiddleware>();

// ASP.NET Core HTTP middleware — registered as singletons per Stage 2.1. Both implement
// Microsoft.AspNetCore.Http.IMiddleware so the framework resolves them from DI on every
// request via IMiddlewareFactory (the default factory honors the registered lifetime).
// They run in the ASP.NET Core pipeline BEFORE CloudAdapter; see app.UseMiddleware calls
// below.
builder.Services.AddSingleton<TenantValidationMiddleware>();
builder.Services.AddSingleton<RateLimitMiddleware>();

builder.Services.AddSingleton<CloudAdapter>(sp =>
{
    var auth = sp.GetRequiredService<BotFrameworkAuthentication>();
    var logger = sp.GetRequiredService<ILogger<CloudAdapter>>();
    var adapter = new CloudAdapter(auth, logger);
    // Bot Framework middleware pipeline (executes inside CloudAdapter.ProcessAsync after
    // JWT validation, before the activity handler):
    //   TelemetryMiddleware → ActivityDeduplicationMiddleware → TeamsSwarmActivityHandler
    adapter.Use(sp.GetRequiredService<TelemetryMiddleware>());
    adapter.Use(sp.GetRequiredService<ActivityDeduplicationMiddleware>());
    adapter.OnTurnError = async (turnContext, exception) =>
    {
        // Surface failures to the operator log. Stage 5.2 will additionally emit a
        // ConnectorFailure audit record via IAuditLogger; this stage keeps the handler
        // minimal so the host never throws an unhandled exception out of CloudAdapter.
        logger.LogError(
            exception,
            "Unhandled exception in Teams activity handler for activity {ActivityId} (type={ActivityType}).",
            turnContext.Activity?.Id,
            turnContext.Activity?.Type);
        try
        {
            await turnContext.SendActivityAsync(
                "The bot encountered an unexpected error. Operators have been notified.")
                .ConfigureAwait(false);
        }
        catch (Exception sendEx)
        {
            logger.LogWarning(sendEx, "Failed to send error reply to Teams.");
        }
    };
    return adapter;
});
builder.Services.AddSingleton<IBotFrameworkHttpAdapter>(sp => sp.GetRequiredService<CloudAdapter>());

builder.Services.AddTransient<IBot, TeamsSwarmActivityHandler>();

// ---------------------------------------------------------------------------
// Teams connector singletons — in-memory stubs / no-ops for Stage 2.1
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<IConversationReferenceStore, InMemoryConversationReferenceStore>();
builder.Services.AddSingleton<IAgentQuestionStore, InMemoryAgentQuestionStore>();
builder.Services.AddSingleton<ICardStateStore, NoOpCardStateStore>();
builder.Services.AddSingleton<ICardActionHandler, NoOpCardActionHandler>();
builder.Services.AddSingleton<ITeamsCardManager, NoOpCardManager>();
builder.Services.AddSingleton<IActivityIdStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<TeamsMessagingOptions>>().Value;
    var logger = sp.GetRequiredService<ILogger<InMemoryActivityIdStore>>();
    return new InMemoryActivityIdStore(opts.DeduplicationTtlMinutes, logger);
});

// Inbound publisher (Channel-backed) — both the IInboundEventPublisher abstraction and the
// concrete type are exposed so the Stage 2.3 connector can consume the reader directly.
builder.Services.AddSingleton<ChannelInboundEventPublisher>();
builder.Services.AddSingleton<IInboundEventPublisher>(sp =>
    sp.GetRequiredService<ChannelInboundEventPublisher>());

// Identity + RBAC: default-deny stubs (Stage 1.2); Stage 5.1 swaps these out.
builder.Services.AddSingleton<IIdentityResolver, DefaultDenyIdentityResolver>();
builder.Services.AddSingleton<IUserAuthorizationService, DefaultDenyAuthorizationService>();

// Command dispatcher: no-op (Stage 3.2 provides the concrete dispatcher).
builder.Services.AddSingleton<ICommandDispatcher, NoOpCommandDispatcher>();

// Outbox: in-memory no-op (Stage 4.2 provides the persistent outbox).
builder.Services.AddSingleton<IMessageOutbox, NoOpMessageOutbox>();

// Audit logger: no-op (Stage 5.2 provides the SQL-backed logger).
builder.Services.AddSingleton<IAuditLogger, NoOpAuditLogger>();

// ---------------------------------------------------------------------------
// MVC controllers (Teams webhook lives in AgentSwarm.Messaging.Teams)
// ---------------------------------------------------------------------------
builder.Services
    .AddControllers()
    .AddApplicationPart(typeof(TeamsWebhookController).Assembly);

// ---------------------------------------------------------------------------
// Health checks + OpenTelemetry tracing
// ---------------------------------------------------------------------------
builder.Services.AddHealthChecks();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(rb => rb.AddService(serviceName: "AgentSwarm.Messaging.Worker"))
    .WithTracing(tb => tb
        .AddSource(TelemetryMiddleware.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation());

var app = builder.Build();

// ---------------------------------------------------------------------------
// ASP.NET Core HTTP pipeline
//   TenantValidationMiddleware (HTTP 403 on disallowed tenant)
//     → RateLimitMiddleware    (HTTP 429 on quota exceeded)
//       → CloudAdapter         (JWT 401, then Bot Framework middleware pipeline)
// ---------------------------------------------------------------------------
app.UseMiddleware<TenantValidationMiddleware>();
app.UseMiddleware<RateLimitMiddleware>();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapHealthChecks("/ready");

app.Run();

/// <summary>
/// Worker host entry-point partial class — exposed publicly so test projects can use
/// <c>WebApplicationFactory&lt;Program&gt;</c> for integration testing.
/// </summary>
[SuppressMessage("Design", "CA1052:Static holder types should be Static", Justification = "Program serves as the WebApplicationFactory entry point.")]
public partial class Program
{
}
