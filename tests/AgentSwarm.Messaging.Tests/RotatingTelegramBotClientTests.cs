// -----------------------------------------------------------------------
// <copyright file="RotatingTelegramBotClientTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AgentSwarm.Messaging.Telegram;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace AgentSwarm.Messaging.Tests;

/// <summary>
/// Stage 5.1 — pins the rotation contract called out in
/// <c>docs/stories/qq-TELEGRAM-MESSENGER-S/architecture.md:1091</c>
/// ("The refreshed token is applied to the TelegramBotClient instance
/// on the next API call"). Two scenarios:
/// <list type="number">
///   <item><description>
///   <c>ITelegramBotClient</c> resolved from the DI container after
///   <see cref="TelegramServiceCollectionExtensions.AddTelegram"/> is
///   in fact the <see cref="RotatingTelegramBotClient"/> proxy — NOT
///   the raw <see cref="TelegramBotClient"/> the
///   <see cref="TelegramBotClientFactory"/> would have produced
///   (the iter-4 evaluator flagged that the rotation type existed
///   but was dead code because the DI registration still bypassed
///   it).
///   </description></item>
///   <item><description>
///   When the proxy observes an <see cref="IOptionsMonitor{T}.OnChange"/>
///   notification with a new <see cref="TelegramOptions.BotToken"/>,
///   it rebuilds its inner <see cref="TelegramBotClient"/> so the
///   very next call uses the rotated token. The blank-token branch
///   keeps the previous inner client to survive a transient vault
///   outage (per <see cref="RotatingTelegramBotClient"/>'s safety
///   contract).
///   </description></item>
/// </list>
/// </summary>
public sealed class RotatingTelegramBotClientTests
{
    private const string InitialToken = "1111111111:AAH-initial-token-for-rotation-tests-only";
    private const string RotatedToken = "2222222222:AAH-rotated-token-for-rotation-tests-only";

    [Fact]
    public void AddTelegram_ResolvesITelegramBotClient_AsRotatingTelegramBotClient()
    {
        // Pins iter-4 evaluator Item 1: the proxy must be the
        // registered ITelegramBotClient, not the raw TelegramBotClient
        // a factory call would have produced. Without this assertion a
        // future refactor that re-routes the registration back to
        // `TelegramBotClientFactory.Create()` would silently re-break
        // rotation and no other test would notice.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(BuildConfig());
        services.AddTelegram(BuildConfig());
        using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<ITelegramBotClient>();

        resolved.Should().BeOfType<RotatingTelegramBotClient>(
            "AddTelegram (Stage 5.1) must register the rotation-aware proxy as ITelegramBotClient so vault token rotations propagate to every cached singleton consumer on the next API call (architecture.md §11 line 1091)");

        // The proxy must also be reachable as its concrete type so a
        // diagnostic / future health-check can introspect rotation
        // state (CurrentTokenForTesting etc.).
        var asConcrete = provider.GetRequiredService<RotatingTelegramBotClient>();
        asConcrete.Should().BeSameAs(resolved,
            "the ITelegramBotClient resolution must alias the RotatingTelegramBotClient singleton — registering them as separate instances would defeat rotation because the proxy that subscribes to OnChange would never receive API calls");
    }

    [Fact]
    public void Proxy_RebuildsInnerClient_When_BotToken_Rotates_Via_IOptionsMonitor()
    {
        // Direct unit test of the rotation behavior:
        // 1. Construct the proxy against a controllable OptionsMonitor.
        // 2. Force lazy build by reading BotId (parsed from token,
        //    no API call required).
        // 3. Trigger an OnChange with a new token.
        // 4. Force a second build by reading BotId again.
        // 5. Assert the inner client's cached token reflects the
        //    rotation — proving the next API call would carry the
        //    new credential.
        var monitor = new ControllableOptionsMonitor<TelegramOptions>(
            new TelegramOptions { BotToken = InitialToken });
        using var proxy = new RotatingTelegramBotClient(
            monitor,
            NullLogger<RotatingTelegramBotClient>.Instance);

        var firstBotId = proxy.BotId;
        firstBotId.Should().Be(ParseBotId(InitialToken),
            "BotId is parsed from the token by TelegramBotClient's ctor, so resolving it forces the first lazy Rebuild()");
        proxy.CurrentTokenForTesting.Should().Be(InitialToken,
            "after the first BotId read the inner client must hold the initial token");

        monitor.TriggerChange(new TelegramOptions { BotToken = RotatedToken });

        proxy.CurrentTokenForTesting.Should().Be(RotatedToken,
            "OnChange with a different non-blank token must rebuild the inner client eagerly so the next API call uses the rotated credential (architecture.md §11 line 1091)");
        proxy.BotId.Should().Be(ParseBotId(RotatedToken),
            "the proxy's BotId now forwards to the rebuilt inner client which captured the rotated token at construction time");
    }

    [Fact]
    public void Proxy_KeepsCachedClient_When_OnChange_Fires_With_Blank_Token()
    {
        // Defends the safety contract documented on
        // RotatingTelegramBotClient.OnOptionsChanged: a Key Vault
        // refresh that returns blank is treated as a vault outage,
        // NOT a rotation — the proxy must keep using the previously-
        // cached client. Without this guard, a flaky vault could
        // cause every outbound call to throw between blank-fire
        // and the next successful refresh.
        var monitor = new ControllableOptionsMonitor<TelegramOptions>(
            new TelegramOptions { BotToken = InitialToken });
        using var proxy = new RotatingTelegramBotClient(
            monitor,
            NullLogger<RotatingTelegramBotClient>.Instance);

        // Prime the inner client.
        _ = proxy.BotId;
        proxy.CurrentTokenForTesting.Should().Be(InitialToken);

        monitor.TriggerChange(new TelegramOptions { BotToken = string.Empty });
        proxy.CurrentTokenForTesting.Should().Be(InitialToken,
            "OnChange with a blank token must NOT tear down the cached client — the operator-facing validator at startup is the source of truth for blank-token rejection, and a transient blank from a vault refresh would otherwise cause an outage");

        monitor.TriggerChange(new TelegramOptions { BotToken = "   \t  " });
        proxy.CurrentTokenForTesting.Should().Be(InitialToken,
            "whitespace-only is also treated as a transient blank, not a rotation");
    }

    private static IConfigurationRoot BuildConfig()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telegram:BotToken"] = InitialToken,
            })
            .Build();

    private static long ParseBotId(string token)
    {
        var colon = token.IndexOf(':');
        return long.Parse(token.AsSpan(0, colon), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Minimal <see cref="IOptionsMonitor{T}"/> that tracks listeners
    /// and supports a manual <see cref="TriggerChange(T)"/> call so
    /// the test can simulate a configuration reload without spinning
    /// up a real reloadable configuration source. The behaviour
    /// matches the production <c>OptionsMonitor&lt;T&gt;</c> closely
    /// enough that the proxy's <c>OnChange</c> hook fires the same
    /// way it would in production.
    /// </summary>
    private sealed class ControllableOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly ConcurrentDictionary<Guid, Action<T, string?>> _listeners = new();
        private T _current;

        public ControllableOptionsMonitor(T initial)
        {
            _current = initial;
        }

        public T CurrentValue => _current;

        public T Get(string? name) => _current;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            var key = Guid.NewGuid();
            _listeners[key] = listener;
            return new Unsubscriber(_listeners, key);
        }

        public void TriggerChange(T newValue)
        {
            _current = newValue;
            foreach (var listener in _listeners.Values)
            {
                listener(newValue, null);
            }
        }

        private sealed class Unsubscriber : IDisposable
        {
            private readonly ConcurrentDictionary<Guid, Action<T, string?>> _bag;
            private readonly Guid _key;

            public Unsubscriber(ConcurrentDictionary<Guid, Action<T, string?>> bag, Guid key)
            {
                _bag = bag;
                _key = key;
            }

            public void Dispose() => _bag.TryRemove(_key, out _);
        }
    }
}
