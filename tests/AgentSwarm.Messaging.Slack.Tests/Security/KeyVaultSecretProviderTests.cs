// -----------------------------------------------------------------------
// <copyright file="KeyVaultSecretProviderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using FluentAssertions;
using Xunit;

/// <summary>
/// Stage 3.3 iter-2 evaluator item 1 regression tests for
/// <see cref="KeyVaultSecretProvider"/>: validate that the
/// <c>keyvault://</c> scheme is required, that the delegate is invoked
/// with the secret name only, and that backend failures surface as
/// <see cref="SecretNotFoundException"/> with the reference embedded
/// for triage.
/// </summary>
public sealed class KeyVaultSecretProviderTests
{
    [Fact]
    public async Task Resolves_keyvault_scheme_via_client_delegate()
    {
        StubClient client = new(_ => Task.FromResult<string?>("xoxb-bot-token"));
        KeyVaultSecretProvider provider = new(client);

        string value = await provider.GetSecretAsync("keyvault://slack-bot-token", CancellationToken.None);
        value.Should().Be("xoxb-bot-token");
        client.LastRequestedName.Should().Be(
            "slack-bot-token",
            "the provider must strip the keyvault:// prefix and pass only the secret name to the underlying client");
    }

    [Fact]
    public async Task Bare_reference_without_scheme_is_rejected()
    {
        // Configuration mistakes that drop the keyvault:// scheme must
        // not silently fall through and call the vault for the wrong
        // secret name (which could include another backend's prefix).
        StubClient client = new(_ => Task.FromResult<string?>("never"));
        KeyVaultSecretProvider provider = new(client);

        Func<Task> act = async () => await provider.GetSecretAsync("slack-bot-token", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
        client.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Missing_secret_throws_SecretNotFoundException_with_ref_in_message()
    {
        StubClient client = new(_ => Task.FromResult<string?>(null));
        KeyVaultSecretProvider provider = new(client);

        Func<Task> act = async () => await provider.GetSecretAsync("keyvault://does-not-exist", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<SecretNotFoundException>();
        ex.Which.Message.Should().Contain("keyvault://does-not-exist");
        ex.Which.SecretRef.Should().Be("keyvault://does-not-exist");
    }

    [Fact]
    public async Task Empty_vault_response_throws_SecretNotFoundException()
    {
        StubClient client = new(_ => Task.FromResult<string?>(string.Empty));
        KeyVaultSecretProvider provider = new(client);

        Func<Task> act = async () => await provider.GetSecretAsync("keyvault://blank", CancellationToken.None);
        await act.Should().ThrowAsync<SecretNotFoundException>(
            "an empty vault response is semantically the same as a missing entry -- the validator HMAC would fail anyway");
    }

    [Fact]
    public async Task Backend_failure_wraps_as_SecretNotFoundException_with_inner()
    {
        InvalidOperationException backendFailure = new("vault unreachable");
        StubClient client = new(_ => throw backendFailure);
        KeyVaultSecretProvider provider = new(client);

        Func<Task> act = async () => await provider.GetSecretAsync("keyvault://signing", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<SecretNotFoundException>();
        ex.Which.Message.Should().Contain("keyvault://signing");
        ex.Which.InnerException.Should().BeSameAs(backendFailure,
            "the original backend failure must be preserved as InnerException so triage can see the underlying cause");
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_wrapping()
    {
        StubClient client = new(_ => throw new OperationCanceledException());
        KeyVaultSecretProvider provider = new(client);

        Func<Task> act = async () => await provider.GetSecretAsync("keyvault://signing", CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "OperationCanceledException must propagate so the host can distinguish 'caller cancelled' from 'secret missing'");
    }

    [Fact]
    public void Null_client_at_construction_throws()
    {
        Action act = () => new KeyVaultSecretProvider(client: null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private sealed class StubClient : IKeyVaultSecretClient
    {
        private readonly Func<string, Task<string?>> handler;

        public StubClient(Func<string, Task<string?>> handler)
        {
            this.handler = handler;
        }

        public int CallCount { get; private set; }

        public string? LastRequestedName { get; private set; }

        public Task<string?> GetSecretAsync(string secretName, CancellationToken ct)
        {
            this.CallCount++;
            this.LastRequestedName = secretName;
            return this.handler(secretName);
        }
    }
}
