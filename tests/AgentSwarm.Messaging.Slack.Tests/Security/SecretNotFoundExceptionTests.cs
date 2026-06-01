// -----------------------------------------------------------------------
// <copyright file="SecretNotFoundExceptionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using FluentAssertions;
using FluentAssertions.Specialized;
using Xunit;

/// <summary>
/// Stage 3.3 brief Test Scenario: "Missing secret throws descriptive
/// error -- Given a secret ref that does not resolve, When
/// <c>GetSecretAsync</c> is called, Then a
/// <see cref="SecretNotFoundException"/> is thrown with the ref in
/// the message."
/// </summary>
public sealed class SecretNotFoundExceptionTests
{
    [Fact]
    public async Task EnvironmentSecretProvider_includes_the_ref_in_the_exception_message()
    {
        EnvironmentSecretProvider provider = new(_ => null);

        Func<Task> act = async () =>
            await provider.GetSecretAsync("env://SLACK_BOT_TOKEN_MISSING", CancellationToken.None);

        ExceptionAssertions<SecretNotFoundException> ex =
            await act.Should().ThrowAsync<SecretNotFoundException>();

        ex.Which.SecretRef.Should().Be("env://SLACK_BOT_TOKEN_MISSING");
        ex.Which.Message.Should().Contain(
            "env://SLACK_BOT_TOKEN_MISSING",
            "operators triage missing-secret failures by grepping logs for the ref URI");
    }

    [Fact]
    public async Task InMemorySecretProvider_includes_the_ref_in_the_exception_message()
    {
        InMemorySecretProvider provider = new();

        Func<Task> act = async () =>
            await provider.GetSecretAsync("keyvault://not-seeded", CancellationToken.None);

        ExceptionAssertions<SecretNotFoundException> ex =
            await act.Should().ThrowAsync<SecretNotFoundException>();

        ex.Which.SecretRef.Should().Be("keyvault://not-seeded");
        ex.Which.Message.Should().Contain("keyvault://not-seeded");
    }

    [Fact]
    public async Task CompositeSecretProvider_propagates_SecretNotFoundException_with_the_ref()
    {
        // The composite is the type production code resolves through DI;
        // the test pins that the descriptive error survives the cache
        // and routing layer.
        InMemorySecretProvider memory = new();
        EnvironmentSecretProvider env = new(_ => null);
        Microsoft.Extensions.Options.IOptions<SecretProviderOptions> options =
            Microsoft.Extensions.Options.Options.Create(
                new SecretProviderOptions
                {
                    ProviderType = SecretProviderType.InMemory,
                    RefreshIntervalMinutes = 60,
                });

        CompositeSecretProvider composite = new(options, env, memory);

        Func<Task> act = async () =>
            await composite.GetSecretAsync("env://NOT_THERE", CancellationToken.None);

        ExceptionAssertions<SecretNotFoundException> ex =
            await act.Should().ThrowAsync<SecretNotFoundException>();

        ex.Which.SecretRef.Should().Be("env://NOT_THERE");
        ex.Which.Message.Should().Contain("env://NOT_THERE");
    }

    [Fact]
    public void Constructor_with_inner_exception_preserves_both_ref_and_cause()
    {
        // The "with cause" overload is used by future remote-vault
        // backends to attach the underlying network error; the
        // descriptive message contract must hold for that path too.
        InvalidOperationException cause = new("vault unreachable");
        SecretNotFoundException ex = new("keyvault://slack-bot-token", cause);

        ex.SecretRef.Should().Be("keyvault://slack-bot-token");
        ex.Message.Should().Contain("keyvault://slack-bot-token");
        ex.InnerException.Should().BeSameAs(cause);
    }
}
