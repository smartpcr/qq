using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// DI registration extensions for the Telegram messenger connector.
/// </summary>
public static class TelegramServiceCollectionExtensions
{
    /// <summary>
    /// Registers Telegram options binding (<see cref="TelegramOptions"/>),
    /// the fail-fast validator (<see cref="TelegramOptionsValidator"/>),
    /// the named <see cref="HttpClient"/>, the
    /// <see cref="TelegramBotClientFactory"/>, and a singleton
    /// <see cref="ITelegramBotClient"/> backed by the factory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Options are bound from the <c>Telegram</c> section of
    /// <paramref name="configuration"/>. <c>.ValidateOnStart()</c> wires
    /// the validator into <c>IHost.StartAsync</c> so a missing
    /// <see cref="TelegramOptions.BotToken"/> throws
    /// <see cref="OptionsValidationException"/> before the worker begins
    /// accepting traffic.
    /// </para>
    /// <para>
    /// The <see cref="ITelegramBotClient"/> registration is a singleton
    /// (the underlying <c>Telegram.Bot</c> client is thread-safe and
    /// reuses the <see cref="HttpClient"/>), so all senders share the
    /// same instance.
    /// </para>
    /// <para>
    /// Stage 2.3 additions: <see cref="RateLimitOptions"/> bound from
    /// <c>Telegram:RateLimits</c>; an in-memory
    /// <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>
    /// for callback-data resolution (replace with
    /// <c>AddStackExchangeRedisCache()</c> in production);
    /// <see cref="ITelegramRateLimiter"/> + <see cref="IDelayProvider"/>
    /// for the proactive dual token-bucket limiter;
    /// <see cref="ITelegramApiClient"/> as the testable wrapper around
    /// <see cref="ITelegramBotClient"/>'s extension-method send surface;
    /// <see cref="IMessageIdTracker"/> for post-send correlation
    /// tracking; and <see cref="IMessageSender"/> ↔
    /// <see cref="TelegramMessageSender"/> as the outbound contract
    /// consumed by <c>OutboundQueueProcessor</c> (Stage 4.1).
    /// </para>
    /// </remarks>
    /// <param name="services">DI container.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddTelegram(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<TelegramOptions>, TelegramOptionsValidator>();

        services.AddHttpClient(TelegramBotClientFactory.HttpClientName);

        services.AddSingleton<TelegramBotClientFactory>();
        services.AddSingleton<ITelegramBotClient>(sp =>
            sp.GetRequiredService<TelegramBotClientFactory>().Create());

        // Stage 2.3 — outbound message sender wiring.
        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(
                $"{TelegramOptions.SectionName}:{RateLimitOptions.SectionName}"))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();

        // First stage that writes to IDistributedCache — production swaps
        // for AddStackExchangeRedisCache() via configuration. The
        // CallbackQueryHandler (Stage 3.3) consumes these entries.
        services.AddDistributedMemoryCache();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IDelayProvider, TaskDelayProvider>();
        services.AddSingleton<ITelegramRateLimiter, TokenBucketRateLimiter>();
        services.AddSingleton<ITelegramApiClient, TelegramBotApiClient>();
        // TryAddSingleton — the production registration belongs to
        // AgentSwarm.Messaging.Persistence's PersistentMessageIdTracker
        // (registered via AddMessagingPersistence with explicit
        // AddSingleton). When only AddTelegram is wired (dev/local/test
        // without Persistence), the in-memory implementation here
        // becomes the active tracker.
        services.TryAddSingleton<IMessageIdTracker, InMemoryMessageIdTracker>();
        services.AddSingleton<TelegramMessageSender>();
        services.AddSingleton<IMessageSender>(sp =>
            sp.GetRequiredService<TelegramMessageSender>());

        return services;
    }
}
