using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Telegram.Pipeline;
using AgentSwarm.Messaging.Telegram.Pipeline.Stubs;
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
    /// <see cref="TelegramBotClientFactory"/>, a singleton
    /// <see cref="ITelegramBotClient"/> backed by the factory, the
    /// Stage 2.2 <see cref="ITelegramUpdatePipeline"/>, and the Stage 2.2
    /// in-memory stub implementations of the inbound pipeline's
    /// dependencies.
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
    /// <b>Stage 2.2 stubs.</b> Concrete implementations of
    /// <see cref="ICommandParser"/>, <see cref="ICommandRouter"/>,
    /// <see cref="ICallbackHandler"/>, <see cref="IDeduplicationService"/>,
    /// <see cref="IPendingQuestionStore"/>, and
    /// <see cref="IPendingDisambiguationStore"/> are intentionally
    /// <i>stubs</i> here — they let the inbound pipeline run end-to-end
    /// before Phase 3 (command processing, including the
    /// <c>CallbackQueryHandler</c> that consumes
    /// <see cref="IPendingDisambiguationStore.TakeAsync"/> for workspace
    /// disambiguation) and Phase 4 (deduplication) register the
    /// production replacements via additional <c>services.AddXxx()</c>
    /// calls. Re-registering an interface in a later phase replaces
    /// the stub via standard <see cref="IServiceCollection"/> last-wins
    /// semantics.
    /// </para>
    /// <para>
    /// <b><see cref="TimeProvider"/>.</b> Registered via
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService}(IServiceCollection, TService)"/>
    /// so tests can pre-register a fake <see cref="TimeProvider"/>
    /// (used by <see cref="TelegramUpdatePipeline"/> to compute the
    /// <see cref="PendingDisambiguation.ExpiresAt"/> TTL deterministically)
    /// without losing to the production default of
    /// <see cref="TimeProvider.System"/>.
    /// </para>
    /// <para>
    /// <b>Authorization service is NOT stubbed.</b>
    /// <c>IUserAuthorizationService</c> (in the Core project) is a Phase 4
    /// concern and is registered separately. Resolving
    /// <see cref="ITelegramUpdatePipeline"/> before that registration
    /// will fail at first activation; this is intentional so missing
    /// authorization is a loud bootstrap failure rather than a silent
    /// allow-everything stub.
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

        // Stage 2.2 inbound pipeline + stubs. Stubs are singletons so their
        // in-memory state (dedup set, pending questions, pending workspace
        // disambiguations) survives across pipeline invocations within the
        // same process.
        services.AddSingleton<IDeduplicationService, InMemoryDeduplicationService>();
        services.AddSingleton<IPendingQuestionStore, InMemoryPendingQuestionStore>();
        services.AddSingleton<IPendingDisambiguationStore, InMemoryPendingDisambiguationStore>();
        services.AddSingleton<ICommandParser, StubCommandParser>();
        services.AddSingleton<ICommandRouter, StubCommandRouter>();
        services.AddSingleton<ICallbackHandler, StubCallbackHandler>();
        // TimeProvider.System is the production default; tests register a
        // FakeTimeProvider via TryAddSingleton-replacement before AddTelegram.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ITelegramUpdatePipeline, TelegramUpdatePipeline>();

        return services;
    }
}
