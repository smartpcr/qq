using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Core;
using AgentSwarm.Messaging.Teams.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentSwarm.Messaging.Teams.Tests.Outbox;

/// <summary>
/// DI registration tests for <see cref="TeamsOutboxServiceCollectionExtensions.AddTeamsOutboxEngine"/>.
/// Validates that the decorator wiring (a) replaces the public
/// <see cref="IProactiveNotifier"/> / <see cref="IMessengerConnector"/> registrations with
/// the outbox-backed wrappers, (b) re-exposes the original implementations under the
/// <see cref="IInnerTeamsProactiveNotifier"/> / <see cref="IInnerTeamsMessengerConnector"/>
/// marker interfaces, (c) registers <see cref="IOutboxDispatcher"/> as
/// <see cref="TeamsOutboxDispatcher"/>, (d) honours the <see cref="OutboxOptions"/>
/// configure callback, (e) registers <see cref="OutboxRetryEngine"/> as a hosted service,
/// and (f) fails fast when the inner notifier/connector are not yet registered.
/// </summary>
public sealed class TeamsOutboxServiceCollectionExtensionsTests
{
    [Fact]
    public void AddTeamsOutboxEngine_PublicNotifierAndConnectorAreOutboxBackedDecorators()
    {
        var services = SeedBaseServices();

        services.AddTeamsOutboxEngine();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<OutboxBackedProactiveNotifier>(provider.GetRequiredService<IProactiveNotifier>());
        Assert.IsType<OutboxBackedMessengerConnector>(provider.GetRequiredService<IMessengerConnector>());
    }

    [Fact]
    public void AddTeamsOutboxEngine_InnerMarkersExposeOriginalImplementations()
    {
        var originalNotifier = new RecordingProactiveNotifier();
        var originalConnector = new RecordingMessengerConnector();

        var services = SeedBaseServices(originalNotifier, originalConnector);

        services.AddTeamsOutboxEngine();

        using var provider = services.BuildServiceProvider();

        Assert.Same(originalNotifier, provider.GetRequiredService<IInnerTeamsProactiveNotifier>().Inner);
        Assert.Same(originalConnector, provider.GetRequiredService<IInnerTeamsMessengerConnector>().Inner);
    }

    [Fact]
    public void AddTeamsOutboxEngine_RegistersDispatcherAsTeamsOutboxDispatcher()
    {
        var services = SeedBaseServices();

        services.AddTeamsOutboxEngine();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IOutboxDispatcher));
        Assert.Equal(typeof(TeamsOutboxDispatcher), descriptor.ImplementationType);
    }

    [Fact]
    public void AddTeamsOutboxEngine_RegistersRetryEngineAsHostedService()
    {
        var services = SeedBaseServices();

        services.AddTeamsOutboxEngine();

        Assert.Contains(services,
            d => d.ServiceType == typeof(IHostedService)
                 && d.ImplementationType == typeof(OutboxRetryEngine));
    }

    [Fact]
    public void AddTeamsOutboxEngine_ConfigureCallbackOverridesOptions()
    {
        var services = SeedBaseServices();

        services.AddTeamsOutboxEngine(o =>
        {
            o.PollingIntervalMs = 250;
            o.BatchSize = 7;
            o.MaxAttempts = 9;
            o.RateLimitPerSecond = 13;
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<OutboxOptions>();

        Assert.Equal(250, options.PollingIntervalMs);
        Assert.Equal(7, options.BatchSize);
        Assert.Equal(9, options.MaxAttempts);
        Assert.Equal(13, options.RateLimitPerSecond);
    }

    [Fact]
    public void AddTeamsOutboxEngine_IsIdempotentForMarkerRegistration()
    {
        var originalNotifier = new RecordingProactiveNotifier();
        var originalConnector = new RecordingMessengerConnector();
        var services = SeedBaseServices(originalNotifier, originalConnector);

        services.AddTeamsOutboxEngine();
        services.AddTeamsOutboxEngine();

        using var provider = services.BuildServiceProvider();

        // Second call must NOT re-wrap (i.e. inner must still be the original, not the
        // first-iteration decorator that the second call would have captured otherwise).
        Assert.Same(originalNotifier, provider.GetRequiredService<IInnerTeamsProactiveNotifier>().Inner);
        Assert.Same(originalConnector, provider.GetRequiredService<IInnerTeamsMessengerConnector>().Inner);
    }

    [Fact]
    public void AddTeamsOutboxEngine_ThrowsWhenInnerProactiveNotifierMissing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMessengerConnector>(new RecordingMessengerConnector());
        services.AddSingleton<IMessageOutbox>(new InMemoryRecordingOutbox());
        var store = new RecordingConversationReferenceStore();
        services.AddSingleton<IConversationReferenceStore>(store);
        services.AddSingleton<IConversationReferenceRouter>(store);

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddTeamsOutboxEngine());
        Assert.Contains("IProactiveNotifier", ex.Message);
    }

    [Fact]
    public void AddTeamsOutboxEngine_ThrowsWhenInnerMessengerConnectorMissing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProactiveNotifier>(new RecordingProactiveNotifier());
        services.AddSingleton<IMessageOutbox>(new InMemoryRecordingOutbox());
        var store = new RecordingConversationReferenceStore();
        services.AddSingleton<IConversationReferenceStore>(store);
        services.AddSingleton<IConversationReferenceRouter>(store);

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddTeamsOutboxEngine());
        Assert.Contains("IMessengerConnector", ex.Message);
    }

    private static IServiceCollection SeedBaseServices(
        RecordingProactiveNotifier? notifier = null,
        RecordingMessengerConnector? connector = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSingleton<IProactiveNotifier>(notifier ?? new RecordingProactiveNotifier());
        services.AddSingleton<IMessengerConnector>(connector ?? new RecordingMessengerConnector());

        var store = new RecordingConversationReferenceStore();
        services.AddSingleton<IConversationReferenceStore>(store);
        services.AddSingleton<IConversationReferenceRouter>(store);

        services.AddSingleton<IMessageOutbox>(new InMemoryRecordingOutbox());

        return services;
    }
}
