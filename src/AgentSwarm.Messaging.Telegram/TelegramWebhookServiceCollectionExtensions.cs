using AgentSwarm.Messaging.Telegram.Webhook;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Stage 2.4 DI registrations specific to the webhook receiver pipeline.
/// Kept separate from <see cref="TelegramServiceCollectionExtensions.AddTelegram"/>
/// so test scenarios that only need the inbound pipeline (Stage 2.2)
/// can skip the ASP.NET Core dependencies, and so the Worker bootstrap
/// reads as a clear, declarative composition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetimes.</b>
/// <list type="bullet">
///   <item><description><see cref="InboundUpdateChannel"/> is a singleton:
///   the channel is the in-memory bridge between the synchronous webhook
///   endpoint (producer) and the <c>InboundUpdateDispatcher</c>
///   background consumer, and there must be exactly one instance for
///   them to communicate.</description></item>
///   <item><description><see cref="TelegramWebhookEndpoint"/> and
///   <see cref="InboundUpdateProcessor"/> are <i>scoped</i> because both
///   depend on <see cref="Abstractions.IInboundUpdateStore"/>, which is
///   scoped through <c>MessagingDbContext</c>. Registering the endpoint
///   as a singleton would fail
///   <see cref="ServiceProviderOptions.ValidateScopes"/> at startup;
///   scoped is the only lifetime that lets the framework inject the
///   per-request DbContext correctly.</description></item>
///   <item><description><see cref="TelegramWebhookSecretFilter"/> is a
///   scoped registration so it composes with the endpoint's scope.
///   <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/>
///   is singleton-resolved regardless, so the constant-time secret
///   comparison reads a consistent options snapshot.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class TelegramWebhookServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Stage 2.4 webhook receiver services (channel,
    /// processor, endpoint, secret filter, and the
    /// <see cref="TelegramWebhookRegistrationService"/> that calls
    /// <c>SetWebhook</c> at startup). Call this AFTER
    /// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/> so
    /// the <see cref="Telegram.Bot.ITelegramBotClient"/> the registration
    /// service consumes is already registered.
    /// </summary>
    public static IServiceCollection AddTelegramWebhook(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The minimal-API endpoint requires routing services even when
        // the caller used a plain HostBuilder + ConfigureWebHost (instead
        // of WebApplication.CreateBuilder which adds routing by default).
        // Calling AddRouting() unconditionally keeps the registration
        // self-contained and idempotent.
        services.AddRouting();

        services.TryAddSingleton<InboundUpdateChannel>();
        services.AddScoped<InboundUpdateProcessor>();
        services.AddScoped<TelegramWebhookEndpoint>();
        services.AddScoped<TelegramWebhookSecretFilter>();
        services.AddHostedService<TelegramWebhookRegistrationService>();

        return services;
    }
}
