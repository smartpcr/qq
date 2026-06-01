// -----------------------------------------------------------------------
// <copyright file="AzureKeyVaultSecretClientTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using AgentSwarm.Messaging.Core.Secrets;
using Azure.Identity;
using FluentAssertions;
using Xunit;

/// <summary>
/// Stage 3.3 iter-3 evaluator item 1 regression tests for the
/// auto-registered <see cref="AzureKeyVaultSecretClient"/> adapter.
/// The adapter is what makes
/// <c>AddSecretProvider(IConfiguration)</c> self-sufficient for
/// <see cref="SecretProviderType.KeyVault"/>: we can't make a real
/// Key Vault round-trip in a unit test, but we CAN pin the
/// constructor validation contract so a misconfigured URI fails
/// loudly at construction time.
/// </summary>
public sealed class AzureKeyVaultSecretClientTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_string_rejects_blank_VaultUri_with_remediation_message(string? vaultUri)
    {
        Action act = () => new AzureKeyVaultSecretClient(vaultUri!);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Key Vault URI*non-empty*");
    }

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("vault.example.com")] // missing scheme -- not absolute
    public void Ctor_string_rejects_non_absolute_VaultUri(string vaultUri)
    {
        Action act = () => new AzureKeyVaultSecretClient(vaultUri);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*valid absolute URI*");
    }

    [Fact]
    public void Ctor_string_accepts_valid_absolute_VaultUri()
    {
        // Construction must succeed even though we can't authenticate
        // against the URI in the unit-test environment -- the adapter
        // defers all network IO to GetSecretAsync. This is what makes
        // AddSecretProvider safe to call from a worker host with no
        // Azure credentials configured (provided the operator never
        // sets ProviderType=KeyVault).
        Action act = () => new AzureKeyVaultSecretClient("https://example-vault.vault.azure.net");
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_uri_credential_rejects_null_args()
    {
        Action nullUri = () => new AzureKeyVaultSecretClient((Uri)null!, new DefaultAzureCredential());
        nullUri.Should().Throw<ArgumentNullException>();

        Action nullCredential = () => new AzureKeyVaultSecretClient(new Uri("https://example.vault.azure.net"), null!);
        nullCredential.Should().Throw<ArgumentNullException>();
    }
}
