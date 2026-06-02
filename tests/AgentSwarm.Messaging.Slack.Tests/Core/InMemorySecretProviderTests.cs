// -----------------------------------------------------------------------
// <copyright file="InMemorySecretProviderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Core;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using FluentAssertions;
using Xunit;

/// <summary>
/// Stage 3.1 tests for the Core <see cref="ISecretProvider"/> contract and
/// the <see cref="InMemorySecretProvider"/> stub it ships with. These tests
/// pin the surface used by the signature validator (seed via constructor,
/// mutate via <see cref="InMemorySecretProvider.Set(string, string)"/>,
/// observe via <see cref="InMemorySecretProvider.GetSecretAsync(string, CancellationToken)"/>)
/// so future refactors cannot silently change the contract relied on by
/// <see cref="AgentSwarm.Messaging.Slack.Security.SlackSignatureValidator"/>.
/// </summary>
public sealed class InMemorySecretProviderTests
{
    [Fact]
    public async Task GetSecretAsync_returns_seeded_value_supplied_via_constructor()
    {
        Dictionary<string, string> seed = new(StringComparer.Ordinal)
        {
            ["keyvault://slack/signing"] = "shhh",
        };
        InMemorySecretProvider provider = new(seed);

        string value = await provider.GetSecretAsync("keyvault://slack/signing", CancellationToken.None);

        value.Should().Be("shhh");
    }

    [Fact]
    public async Task GetSecretAsync_returns_value_added_via_Set()
    {
        InMemorySecretProvider provider = new();
        provider.Set("env://SLACK_SIGNING_SECRET", "abc-123");

        string value = await provider.GetSecretAsync("env://SLACK_SIGNING_SECRET", CancellationToken.None);

        value.Should().Be("abc-123");
    }

    [Fact]
    public async Task GetSecretAsync_throws_SecretNotFoundException_for_unknown_ref()
    {
        InMemorySecretProvider provider = new();

        Func<Task> act = async () => await provider.GetSecretAsync("missing://ref", CancellationToken.None);

        await act.Should().ThrowAsync<SecretNotFoundException>()
            .Where(ex => ex.SecretRef == "missing://ref");
    }

    [Fact]
    public async Task GetSecretAsync_throws_ArgumentException_for_null_ref()
    {
        InMemorySecretProvider provider = new();

        Func<Task> act = async () => await provider.GetSecretAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(ex => ex.ParamName == "secretRef");
    }

    [Fact]
    public async Task GetSecretAsync_throws_ArgumentException_for_whitespace_ref()
    {
        InMemorySecretProvider provider = new();

        Func<Task> act = async () => await provider.GetSecretAsync("   ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(ex => ex.ParamName == "secretRef");
    }

    [Fact]
    public async Task GetSecretAsync_honours_cancellation()
    {
        InMemorySecretProvider provider = new();
        provider.Set("ref", "value");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Func<Task> act = async () => await provider.GetSecretAsync("ref", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetSecretAsync_is_case_sensitive_on_ref()
    {
        InMemorySecretProvider provider = new();
        provider.Set("env://Foo", "x");

        Func<Task> act = async () => await provider.GetSecretAsync("env://foo", CancellationToken.None);

        await act.Should().ThrowAsync<SecretNotFoundException>();
    }

    [Fact]
    public void Set_throws_on_null_arguments()
    {
        InMemorySecretProvider provider = new();

        Action setRefNull = () => provider.Set(null!, "v");
        Action setValueNull = () => provider.Set("k", null!);

        setRefNull.Should().Throw<ArgumentException>().WithParameterName("secretRef");
        setValueNull.Should().Throw<ArgumentNullException>().WithParameterName("value");
    }

    [Fact]
    public void Remove_returns_true_only_when_an_entry_was_removed()
    {
        InMemorySecretProvider provider = new();
        provider.Set("k", "v");

        provider.Remove("k").Should().BeTrue();
        provider.Remove("k").Should().BeFalse();
    }

    [Fact]
    public async Task Constructor_accepts_null_seed_and_yields_an_empty_store()
    {
        // Iter-3 evaluator fix: the prior revision of this fact declared
        // the test as `void` and called `act.Should().ThrowAsync<...>()`
        // without awaiting the resulting Task. That made the assertion a
        // no-op (FluentAssertions never actually invoked `act`, never
        // observed an exception, and the test passed regardless of the
        // provider's behaviour). Switching to `async Task` and `await`
        // restores the assertion's correctness AND removes the
        // CS4014/AsyncFixer "unawaited Task" warning that
        // TreatWarningsAsErrors would otherwise promote to a build
        // failure.
        InMemorySecretProvider provider = new(seed: null);

        Func<Task> act = async () => await provider.GetSecretAsync("anything", CancellationToken.None);

        await act.Should().ThrowAsync<SecretNotFoundException>();
    }

    [Fact]
    public void Constructor_with_seed_rejects_entries_with_null_values()
    {
        Dictionary<string, string> badSeed = new(StringComparer.Ordinal)
        {
            ["k"] = null!,
        };

        Action act = () => _ = new InMemorySecretProvider(badSeed);

        act.Should().Throw<ArgumentNullException>().WithParameterName("value");
    }
}
