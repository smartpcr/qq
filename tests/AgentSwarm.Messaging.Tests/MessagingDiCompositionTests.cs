using AgentSwarm.Messaging.Abstractions;
using AgentSwarm.Messaging.Persistence;
using AgentSwarm.Messaging.Telegram;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Pins the iter-2 evaluator's item 2 + DI follow-up: when both
/// <see cref="ServiceCollectionExtensions.AddMessagingPersistence"/> and
/// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/> are wired into
/// the same container, the resolved <see cref="IMessageIdTracker"/> must be the
/// durable <see cref="PersistentMessageIdTracker"/>, not the in-memory fallback.
/// </summary>
public class MessagingDiCompositionTests
{
    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:BotToken"] = "1234567:test-token",
                ["ConnectionStrings:MessagingDb"] = "Data Source=:memory:",
            })
            .Build();
    }

    [Fact]
    public void IMessageIdTracker_IsPersistent_WhenBothExtensionsWired_PersistenceFirst()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = BuildConfiguration();
        services.AddMessagingPersistence(config);
        services.AddTelegram(config);

        var provider = services.BuildServiceProvider();
        var tracker = provider.GetRequiredService<IMessageIdTracker>();

        tracker.Should().BeOfType<PersistentMessageIdTracker>(
            "AddTelegram registers IMessageIdTracker via TryAddSingleton — when AddMessagingPersistence has already added PersistentMessageIdTracker (via AddSingleton), the persistent registration wins regardless of extension call order");
    }

    [Fact]
    public void IMessageIdTracker_IsPersistent_WhenBothExtensionsWired_TelegramFirst()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = BuildConfiguration();
        services.AddTelegram(config);
        services.AddMessagingPersistence(config);

        var provider = services.BuildServiceProvider();
        var tracker = provider.GetRequiredService<IMessageIdTracker>();

        tracker.Should().BeOfType<PersistentMessageIdTracker>(
            "even when AddTelegram is called first (registering InMemoryMessageIdTracker via TryAddSingleton), AddMessagingPersistence's explicit AddSingleton registration overrides the in-memory fallback because the last-in-wins resolution rule applies once both registrations exist");
    }

    [Fact]
    public void IMessageIdTracker_IsInMemoryFallback_WhenOnlyTelegramWired()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = BuildConfiguration();
        services.AddTelegram(config);

        var provider = services.BuildServiceProvider();
        var tracker = provider.GetRequiredService<IMessageIdTracker>();

        tracker.GetType().Name.Should().Be("InMemoryMessageIdTracker",
            "without AddMessagingPersistence, the dev/test fallback is the in-memory tracker registered by AddTelegram via TryAddSingleton");
    }
}
