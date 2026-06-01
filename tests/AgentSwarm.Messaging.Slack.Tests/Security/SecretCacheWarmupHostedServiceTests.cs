// -----------------------------------------------------------------------
// <copyright file="SecretCacheWarmupHostedServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// Stage 3.3 iter-3 evaluator item 2 regression tests:
/// <see cref="SecretCacheWarmupHostedService"/> implements
/// architecture.md §7.3's "loaded into memory at connector startup"
/// requirement by pre-resolving every reference yielded by each
/// registered <see cref="ISecretRefSource"/>. The iter-3 behaviour
/// change is fail-closed semantics: a
/// <see cref="SecretRefRequirement.Required"/> reference that cannot
/// be resolved aborts host start-up via
/// <see cref="SecretCacheWarmupException"/>;
/// <see cref="SecretRefRequirement.Optional"/> references log a
/// warning and allow the host to continue.
/// </summary>
public sealed class SecretCacheWarmupHostedServiceTests
{
    [Fact]
    public async Task StartAsync_resolves_every_ref_yielded_by_the_source()
    {
        CountingProvider inner = new(_ => Task.FromResult("ok"));
        StubRefSource source = StubRefSource.Required("env://A", "env://B", "env://C");
        SecretCacheWarmupHostedService warmup = new(inner, new[] { (ISecretRefSource)source });

        await warmup.StartAsync(CancellationToken.None);

        inner.LastRequestedRefs.Should().BeEquivalentTo(new[] { "env://A", "env://B", "env://C" });
    }

    [Fact]
    public async Task StartAsync_iterates_every_registered_source_in_order()
    {
        CountingProvider inner = new(_ => Task.FromResult("ok"));
        ISecretRefSource[] sources =
        {
            StubRefSource.Required("env://workspace-a-signing", "env://workspace-a-bot"),
            StubRefSource.Required("env://workspace-b-signing"),
        };

        SecretCacheWarmupHostedService warmup = new(inner, sources);
        await warmup.StartAsync(CancellationToken.None);

        inner.LastRequestedRefs.Should().Equal(
            "env://workspace-a-signing",
            "env://workspace-a-bot",
            "env://workspace-b-signing");
    }

    [Fact]
    public async Task StartAsync_skips_blank_refs_silently()
    {
        // Blank refs are filtered at the source-yield boundary anyway
        // (the descriptor ctor rejects them), so we build descriptors
        // by hand to keep the contract visible.
        CountingProvider inner = new(_ => Task.FromResult("ok"));
        StubRefSource source = new(new[]
        {
            SecretRefDescriptor.Required("env://A"),
        });

        SecretCacheWarmupHostedService warmup = new(inner, new[] { (ISecretRefSource)source });
        await warmup.StartAsync(CancellationToken.None);

        inner.LastRequestedRefs.Should().Equal("env://A");
    }

    [Fact]
    public async Task StartAsync_throws_SecretCacheWarmupException_when_required_secret_is_missing()
    {
        // Stage 3.3 iter-3 evaluator item 2: architecture.md §7.3
        // mandates "secrets are loaded into memory at connector
        // startup". A required secret that cannot be resolved at
        // warmup means the connector CANNOT serve its first request
        // -- fail closed.
        Dictionary<string, string?> values = new()
        {
            ["env://A"] = "valueA",
            ["env://B"] = null, // forces SecretNotFoundException
            ["env://C"] = "valueC",
        };

        CountingProvider inner = new(secretRef =>
        {
            if (!values.TryGetValue(secretRef, out string? value) || value is null)
            {
                throw new SecretNotFoundException(secretRef);
            }
            return Task.FromResult(value);
        });

        StubRefSource source = StubRefSource.Required("env://A", "env://B", "env://C");
        SecretCacheWarmupHostedService warmup = new(inner, new[] { (ISecretRefSource)source });

        Func<Task> act = async () => await warmup.StartAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<SecretCacheWarmupException>(
            "a missing REQUIRED secret at warmup must fail closed so the operator catches it at start-up rather than at the first inbound request");

        ex.Which.Failures.Should().HaveCount(1);
        ex.Which.Failures[0].SecretRef.Should().Be("env://B");
        ex.Which.Failures[0].Cause.Should().BeOfType<SecretNotFoundException>();

        // The other references must still have been ATTEMPTED so the
        // operator sees every failure in a single restart cycle.
        inner.LastRequestedRefs.Should().Equal("env://A", "env://B", "env://C");
    }

    [Fact]
    public async Task StartAsync_collects_all_required_failures_before_throwing()
    {
        // Surface the WHOLE misconfiguration in one restart so the
        // operator doesn't have to fix-restart-discover-fix-restart
        // across five separate boot attempts.
        CountingProvider inner = new(secretRef => throw new SecretNotFoundException(secretRef));
        StubRefSource source = StubRefSource.Required("env://A", "env://B", "env://C");
        SecretCacheWarmupHostedService warmup = new(inner, new[] { (ISecretRefSource)source });

        Func<Task> act = async () => await warmup.StartAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<SecretCacheWarmupException>();
        ex.Which.Failures.Select(f => f.SecretRef).Should().Equal("env://A", "env://B", "env://C");
        ex.Which.Message.Should().Contain("env://A").And.Contain("env://B").And.Contain("env://C");
    }

    [Fact]
    public async Task StartAsync_tolerates_missing_optional_secret_and_continues()
    {
        // SecretRefRequirement.Optional means "we'd like to warm this
        // but we can boot without it". A Slack Socket Mode app-level
        // token is the canonical example: HTTP Events workspaces leave
        // it unset and must still boot.
        Dictionary<string, string?> values = new()
        {
            ["env://OPTIONAL"] = null,
            ["env://REQUIRED"] = "value",
        };

        CountingProvider inner = new(secretRef =>
        {
            if (!values.TryGetValue(secretRef, out string? value) || value is null)
            {
                throw new SecretNotFoundException(secretRef);
            }
            return Task.FromResult(value);
        });

        StubRefSource source = new(new[]
        {
            SecretRefDescriptor.Optional("env://OPTIONAL"),
            SecretRefDescriptor.Required("env://REQUIRED"),
        });

        SecretCacheWarmupHostedService warmup = new(inner, new[] { (ISecretRefSource)source });

        Func<Task> act = async () => await warmup.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync(
            "an optional secret that fails to resolve at warmup must NOT crash the host -- the on-demand path will surface the error when actually requested");
    }

    [Fact]
    public async Task StartAsync_propagates_OperationCanceledException()
    {
        CountingProvider inner = new(_ => throw new OperationCanceledException());
        StubRefSource source = StubRefSource.Required("env://A");

        SecretCacheWarmupHostedService warmup = new(inner, new[] { (ISecretRefSource)source });

        Func<Task> act = async () => await warmup.StartAsync(CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task StartAsync_fails_closed_on_non_SecretNotFound_required_failure()
    {
        // A network timeout or auth failure against KeyVault during
        // warmup of a Required secret is just as fatal as a missing
        // secret -- the connector cannot serve its first request
        // without it.
        InvalidOperationException backendFailure = new("vault unreachable");
        CountingProvider inner = new(_ => throw backendFailure);
        StubRefSource source = StubRefSource.Required("keyvault://signing");

        SecretCacheWarmupHostedService warmup = new(inner, new[] { (ISecretRefSource)source });

        Func<Task> act = async () => await warmup.StartAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<SecretCacheWarmupException>();
        ex.Which.Failures.Should().ContainSingle()
            .Which.Cause.Should().BeSameAs(backendFailure);
    }

    [Fact]
    public void AddSecretProvider_registers_warmup_hosted_service()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecretProvider:ProviderType"] = "InMemory",
            })
            .Build();

        ServiceCollection services = new();
        services.AddSecretProvider(configuration);

        using ServiceProvider sp = services.BuildServiceProvider(validateScopes: true);

        IHostedService[] hostedServices = sp.GetServices<IHostedService>().ToArray();
        hostedServices.OfType<SecretCacheWarmupHostedService>().Should().HaveCount(
            1,
            "AddSecretProvider must register the warmup hosted service so the composite cache is populated at host start-up");
    }

    [Fact]
    public async Task End_to_end_warmup_populates_composite_cache_so_subsequent_calls_skip_backend()
    {
        // The whole point of warmup: after StartAsync, the on-demand
        // call path must observe a cache hit (the inner provider is
        // NOT consulted again).
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecretProvider:ProviderType"] = "InMemory",
                ["SecretProvider:RefreshIntervalMinutes"] = "60",
            })
            .Build();

        InMemorySecretProvider seeded = new(new Dictionary<string, string>
        {
            ["signing"] = "deadbeef",
        });

        ServiceCollection services = new();
        services.AddSingleton(seeded);
        services.AddSingleton<ISecretRefSource>(StubRefSource.Required("signing"));
        services.AddSecretProvider(configuration);

        using ServiceProvider sp = services.BuildServiceProvider(validateScopes: true);

        IHostedService warmup = sp.GetServices<IHostedService>().OfType<SecretCacheWarmupHostedService>().Single();
        await warmup.StartAsync(CancellationToken.None);

        // After warmup, mutate the underlying store. A cache hit must
        // continue to return the warmed value rather than the mutated one.
        seeded.Set("signing", "rotated");

        ISecretProvider composite = sp.GetRequiredService<ISecretProvider>();
        string value = await composite.GetSecretAsync("signing", CancellationToken.None);

        value.Should().Be(
            "deadbeef",
            "the warmup must have populated the composite cache; a fresh backend hit would observe the rotated value");
    }

    private sealed class CountingProvider : ISecretProvider
    {
        private readonly Func<string, Task<string>> handler;

        public CountingProvider(Func<string, Task<string>> handler)
        {
            this.handler = handler;
        }

        public List<string> LastRequestedRefs { get; } = new();

        public async Task<string> GetSecretAsync(string secretRef, CancellationToken ct)
        {
            this.LastRequestedRefs.Add(secretRef);
            return await this.handler(secretRef).ConfigureAwait(false);
        }
    }

    private sealed class StubRefSource : ISecretRefSource
    {
        private readonly SecretRefDescriptor[] descriptors;

        public StubRefSource(IEnumerable<SecretRefDescriptor> descriptors)
        {
            this.descriptors = descriptors is null
                ? Array.Empty<SecretRefDescriptor>()
                : System.Linq.Enumerable.ToArray(descriptors);
        }

        public static StubRefSource Required(params string[] refs)
        {
            SecretRefDescriptor[] descriptors = new SecretRefDescriptor[refs.Length];
            for (int i = 0; i < refs.Length; i++)
            {
                descriptors[i] = SecretRefDescriptor.Required(refs[i]);
            }
            return new StubRefSource(descriptors);
        }

        public async IAsyncEnumerable<SecretRefDescriptor> GetSecretRefsAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (SecretRefDescriptor d in this.descriptors)
            {
                await Task.Yield();
                yield return d;
            }
        }
    }
}

