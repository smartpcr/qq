// -----------------------------------------------------------------------
// <copyright file="CompositeSecretProviderCachingTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 3.3 caching contract for <see cref="CompositeSecretProvider"/>.
/// Implementation-plan §Stage 3.3 / architecture.md §7.3 require
/// "secrets are loaded into memory at connector startup and refreshed
/// on a configurable interval (default 1 hour)". These tests pin:
/// <list type="bullet">
///   <item>cache hits within the TTL skip the backend call,</item>
///   <item>cache misses after the TTL re-resolve from the backend,</item>
///   <item>distinct secret refs are cached independently,</item>
///   <item>failed lookups do NOT poison the cache,</item>
///   <item>the appsettings default (60 minutes) is honoured by
///         <see cref="SecretProviderServiceCollectionExtensions.AddSecretProvider"/>.</item>
/// </list>
/// </summary>
public sealed class CompositeSecretProviderCachingTests
{
    [Fact]
    public async Task GetSecretAsync_within_refresh_interval_does_not_call_inner_provider_twice()
    {
        // Brief Test Scenario: "Given CompositeSecretProvider with 1-hour
        // refresh, When the same secret ref is resolved twice within
        // 1 hour, Then the underlying provider is called only once."
        CountingSecretProvider inner = new(("env://SLACK_BOT_TOKEN", "xoxb-test"));
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));

        CompositeSecretProvider composite = new(
            inner,
            refreshInterval: TimeSpan.FromHours(1),
            timeProvider: clock);

        string first = await composite.GetSecretAsync("env://SLACK_BOT_TOKEN", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(59));
        string second = await composite.GetSecretAsync("env://SLACK_BOT_TOKEN", CancellationToken.None);

        first.Should().Be("xoxb-test");
        second.Should().Be("xoxb-test");
        inner.GetCallCount("env://SLACK_BOT_TOKEN").Should().Be(
            1,
            "the second lookup within the 1-hour TTL must be served from cache");
    }

    [Fact]
    public async Task GetSecretAsync_after_refresh_interval_re_resolves_from_inner_provider()
    {
        CountingSecretProvider inner = new(("env://SLACK_BOT_TOKEN", "xoxb-test"));
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));

        CompositeSecretProvider composite = new(
            inner,
            refreshInterval: TimeSpan.FromHours(1),
            timeProvider: clock);

        _ = await composite.GetSecretAsync("env://SLACK_BOT_TOKEN", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(1));
        _ = await composite.GetSecretAsync("env://SLACK_BOT_TOKEN", CancellationToken.None);

        inner.GetCallCount("env://SLACK_BOT_TOKEN").Should().Be(
            2,
            "a lookup AFTER the TTL elapses must re-hit the backend so rotated secrets are picked up");
    }

    [Fact]
    public async Task GetSecretAsync_caches_each_secret_ref_independently()
    {
        CountingSecretProvider inner = new(
            ("env://A", "value-a"),
            ("env://B", "value-b"));
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 5, 16, 12, 0, 0, TimeSpan.Zero));

        CompositeSecretProvider composite = new(inner, TimeSpan.FromHours(1), clock);

        string a1 = await composite.GetSecretAsync("env://A", CancellationToken.None);
        string b1 = await composite.GetSecretAsync("env://B", CancellationToken.None);
        string a2 = await composite.GetSecretAsync("env://A", CancellationToken.None);
        string b2 = await composite.GetSecretAsync("env://B", CancellationToken.None);

        a1.Should().Be("value-a");
        a2.Should().Be("value-a");
        b1.Should().Be("value-b");
        b2.Should().Be("value-b");

        inner.GetCallCount("env://A").Should().Be(1, "env://A must be cached after the first call");
        inner.GetCallCount("env://B").Should().Be(1, "env://B must be cached independently of env://A");
    }

    [Fact]
    public async Task GetSecretAsync_when_refresh_interval_is_zero_disables_caching()
    {
        // RefreshIntervalMinutes = 0 in appsettings disables the cache
        // entirely. This is the escape hatch for tests and for
        // environments that want to read every secret on every request.
        CountingSecretProvider inner = new(("env://X", "value"));
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);

        CompositeSecretProvider composite = new(inner, TimeSpan.Zero, clock);

        _ = await composite.GetSecretAsync("env://X", CancellationToken.None);
        _ = await composite.GetSecretAsync("env://X", CancellationToken.None);
        _ = await composite.GetSecretAsync("env://X", CancellationToken.None);

        inner.GetCallCount("env://X").Should().Be(
            3,
            "with caching disabled every call must reach the backend");
    }

    [Fact]
    public async Task GetSecretAsync_failed_lookup_is_not_cached()
    {
        // A SecretNotFoundException from the inner provider must NOT be
        // remembered: a transient outage or a yet-to-be-set environment
        // variable would otherwise wedge the cache for an hour.
        CountingSecretProvider inner = new();
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);

        CompositeSecretProvider composite = new(inner, TimeSpan.FromHours(1), clock);

        Func<Task> firstAttempt = async () =>
            await composite.GetSecretAsync("env://NOT_SET", CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<SecretNotFoundException>();

        // Now seed the value and retry; the cache must NOT have stored
        // the failure so the second attempt succeeds.
        inner.Seed("env://NOT_SET", "now-here");

        string resolved = await composite.GetSecretAsync("env://NOT_SET", CancellationToken.None);
        resolved.Should().Be("now-here");
        inner.GetCallCount("env://NOT_SET").Should().Be(
            2,
            "failed lookups must NOT be cached; the second attempt must hit the backend again");
    }

    [Fact]
    public async Task GetSecretAsync_throws_for_null_or_whitespace_secret_ref()
    {
        CountingSecretProvider inner = new();
        FakeTimeProvider clock = new(DateTimeOffset.UtcNow);

        CompositeSecretProvider composite = new(inner, TimeSpan.FromHours(1), clock);

        Func<Task> nullRef = async () =>
            await composite.GetSecretAsync(null!, CancellationToken.None);
        Func<Task> whitespaceRef = async () =>
            await composite.GetSecretAsync("   ", CancellationToken.None);

        await nullRef.Should().ThrowAsync<ArgumentException>();
        await whitespaceRef.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void AddSecretProvider_default_refresh_interval_is_one_hour()
    {
        // architecture.md §7.3: "refreshed on a configurable interval
        // (default 1 hour)". The appsettings binder must honour that
        // default when the section is missing entirely.
        IConfiguration configuration = new ConfigurationBuilder().Build();

        ServiceCollection services = new();
        services.AddSecretProvider(configuration);

        using ServiceProvider sp = services.BuildServiceProvider();
        ISecretProvider resolved = sp.GetRequiredService<ISecretProvider>();
        CompositeSecretProvider composite = resolved.Should().BeOfType<CompositeSecretProvider>().Subject;

        composite.RefreshInterval.Should().Be(
            TimeSpan.FromHours(1),
            "the default RefreshIntervalMinutes (60) must materialize as a 1-hour TTL");
    }

    [Fact]
    public void AddSecretProvider_honours_configured_refresh_interval()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecretProvider:ProviderType"] = "Environment",
                ["SecretProvider:RefreshIntervalMinutes"] = "15",
            })
            .Build();

        ServiceCollection services = new();
        services.AddSecretProvider(configuration);

        using ServiceProvider sp = services.BuildServiceProvider();
        CompositeSecretProvider composite = sp.GetRequiredService<ISecretProvider>()
            .Should().BeOfType<CompositeSecretProvider>().Subject;

        composite.RefreshInterval.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void AddSecretProvider_zero_refresh_interval_disables_cache()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecretProvider:RefreshIntervalMinutes"] = "0",
            })
            .Build();

        ServiceCollection services = new();
        services.AddSecretProvider(configuration);

        using ServiceProvider sp = services.BuildServiceProvider();
        CompositeSecretProvider composite = sp.GetRequiredService<ISecretProvider>()
            .Should().BeOfType<CompositeSecretProvider>().Subject;

        composite.RefreshInterval.Should().Be(
            TimeSpan.Zero,
            "RefreshIntervalMinutes=0 must materialize as TimeSpan.Zero so the cache is bypassed");
    }

    [Fact]
    public void AddSecretProvider_negative_refresh_interval_fails_options_validation()
    {
        // The bind-time validator rejects negative values so a typo in
        // appsettings ("-1") never silently disables caching with an
        // unobservable side effect.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecretProvider:RefreshIntervalMinutes"] = "-5",
            })
            .Build();

        ServiceCollection services = new();
        services.AddSecretProvider(configuration);

        using ServiceProvider sp = services.BuildServiceProvider();

        Action act = () => sp.GetRequiredService<IOptions<SecretProviderOptions>>().Value.GetHashCode();
        act.Should().Throw<OptionsValidationException>();
    }

    /// <summary>
    /// Counting <see cref="ISecretProvider"/> stub: returns seeded values
    /// from an internal dictionary and tracks per-ref invocation counts
    /// so cache-hit/miss behaviour is independently observable.
    /// </summary>
    private sealed class CountingSecretProvider : ISecretProvider
    {
        private readonly Dictionary<string, string> values;
        private readonly Dictionary<string, int> callCounts;

        public CountingSecretProvider(params (string Ref, string Value)[] seed)
        {
            this.values = new Dictionary<string, string>(StringComparer.Ordinal);
            this.callCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach ((string r, string v) in seed)
            {
                this.values[r] = v;
            }
        }

        public void Seed(string secretRef, string value)
        {
            this.values[secretRef] = value;
        }

        public int GetCallCount(string secretRef)
        {
            return this.callCounts.TryGetValue(secretRef, out int n) ? n : 0;
        }

        public Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
        {
            this.callCounts[secretRef] = this.GetCallCount(secretRef) + 1;
            if (this.values.TryGetValue(secretRef, out string? v))
            {
                return Task.FromResult(v);
            }

            throw new SecretNotFoundException(secretRef);
        }
    }

    /// <summary>
    /// Minimal <see cref="TimeProvider"/> double whose UTC clock is
    /// advanced explicitly by tests. Only <see cref="GetUtcNow"/> is
    /// overridden; the other members fall back to the base
    /// implementation which is sufficient for the caching contract.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;

        public FakeTimeProvider(DateTimeOffset initialUtcNow)
        {
            this.utcNow = initialUtcNow;
        }

        public override DateTimeOffset GetUtcNow() => this.utcNow;

        public void Advance(TimeSpan by)
        {
            this.utcNow = this.utcNow.Add(by);
        }
    }
}
