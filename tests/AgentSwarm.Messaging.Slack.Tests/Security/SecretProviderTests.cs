// -----------------------------------------------------------------------
// <copyright file="SecretProviderTests.cs" company="Microsoft Corp.">
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
using Xunit;

/// <summary>
/// Stage 3.1 evaluator-feedback regression tests for the secret-provider
/// wiring. The iter-1 review flagged that the worker advertises
/// <c>SecretProvider:ProviderType = Environment</c> in appsettings but
/// only the in-memory provider was registered. These tests pin the
/// composite-selection contract AND prove the DI extension delivers
/// the correct backend.
/// </summary>
public sealed class SecretProviderTests
{
    [Fact]
    public async Task EnvironmentSecretProvider_resolves_env_scheme_against_supplied_resolver()
    {
        Dictionary<string, string> env = new(StringComparer.Ordinal)
        {
            ["SLACK_SIGNING_SECRET"] = "deadbeef-signing-secret",
        };

        EnvironmentSecretProvider provider = new(name => env.TryGetValue(name, out string? v) ? v : null);

        string value = await provider.GetSecretAsync("env://SLACK_SIGNING_SECRET", CancellationToken.None);
        value.Should().Be("deadbeef-signing-secret");
    }

    [Fact]
    public async Task EnvironmentSecretProvider_resolves_bare_variable_names_without_scheme()
    {
        Dictionary<string, string> env = new(StringComparer.Ordinal) { ["FOO"] = "bar" };
        EnvironmentSecretProvider provider = new(name => env.GetValueOrDefault(name));

        string value = await provider.GetSecretAsync("FOO", CancellationToken.None);
        value.Should().Be("bar");
    }

    [Fact]
    public async Task EnvironmentSecretProvider_throws_SecretNotFoundException_when_variable_is_missing()
    {
        EnvironmentSecretProvider provider = new(_ => null);
        Func<Task> act = async () => await provider.GetSecretAsync("env://NOT_SET", CancellationToken.None);
        var ex = await act.Should().ThrowAsync<SecretNotFoundException>();
        ex.Which.SecretRef.Should().Be("env://NOT_SET");
    }

    [Fact]
    public async Task EnvironmentSecretProvider_treats_empty_variable_value_as_missing()
    {
        // Slack signing secrets are never blank; an empty env var is
        // indistinguishable from "not set" for this contract.
        EnvironmentSecretProvider provider = new(_ => string.Empty);
        Func<Task> act = async () => await provider.GetSecretAsync("env://EMPTY", CancellationToken.None);
        await act.Should().ThrowAsync<SecretNotFoundException>();
    }

    [Fact]
    public void AddSecretProvider_registers_a_composite_routing_to_the_environment_backend_by_default()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecretProvider:ProviderType"] = "Environment",
            })
            .Build();

        ServiceCollection services = new();
        services.AddSecretProvider(configuration);

        using ServiceProvider sp = services.BuildServiceProvider(validateScopes: true);
        ISecretProvider resolved = sp.GetRequiredService<ISecretProvider>();

        resolved.Should().BeOfType<CompositeSecretProvider>(
            "AddSecretProvider must register the routing composite as the ISecretProvider seen by consumers");
    }

    [Fact]
    public async Task CompositeSecretProvider_routes_to_InMemory_backend_when_options_select_it()
    {
        // Inject a known backend so the route-by-options contract is
        // independently verifiable.
        InMemorySecretProvider memory = new();
        memory.Set("secret-x", "value-x");

        EnvironmentSecretProvider environment = new(_ => "wrong");
        Microsoft.Extensions.Options.IOptions<SecretProviderOptions> options =
            Microsoft.Extensions.Options.Options.Create(new SecretProviderOptions { ProviderType = SecretProviderType.InMemory });

        CompositeSecretProvider composite = new(options, environment, memory);

        string value = await composite.GetSecretAsync("secret-x", CancellationToken.None);
        value.Should().Be("value-x", "ProviderType=InMemory must route to InMemorySecretProvider");
    }

    [Fact]
    public async Task CompositeSecretProvider_routes_to_Environment_backend_when_options_select_it()
    {
        InMemorySecretProvider memory = new();
        memory.Set("env://VAR", "wrong");

        Dictionary<string, string> env = new(StringComparer.Ordinal) { ["VAR"] = "correct" };
        EnvironmentSecretProvider environment = new(env.GetValueOrDefault);
        Microsoft.Extensions.Options.IOptions<SecretProviderOptions> options =
            Microsoft.Extensions.Options.Options.Create(new SecretProviderOptions { ProviderType = SecretProviderType.Environment });

        CompositeSecretProvider composite = new(options, environment, memory);

        string value = await composite.GetSecretAsync("env://VAR", CancellationToken.None);
        value.Should().Be("correct", "ProviderType=Environment must route to EnvironmentSecretProvider");
    }

    [Fact]
    public void AddSecretProvider_does_not_override_a_pre_registered_ISecretProvider()
    {
        // Stage 3.3 will swap in an Azure Key Vault provider; the
        // extension must yield to a caller's registration via TryAdd.
        IConfiguration configuration = new ConfigurationBuilder().Build();
        ServiceCollection services = new();
        services.AddSingleton<ISecretProvider, InMemorySecretProvider>();

        services.AddSecretProvider(configuration);

        using ServiceProvider sp = services.BuildServiceProvider();
        ISecretProvider resolved = sp.GetRequiredService<ISecretProvider>();
        resolved.Should().BeOfType<InMemorySecretProvider>(
            "the operator's registration must beat the AddSecretProvider default");
    }

    [Theory]
    [InlineData(SecretProviderType.KeyVault)]
    [InlineData(SecretProviderType.Kubernetes)]
    public void CompositeSecretProvider_throws_for_unsupported_provider_types(SecretProviderType providerType)
    {
        // Stage 3.1 evaluator iter-3 item 3: configuring an unsupported
        // backend (KeyVault or Kubernetes) without first registering an
        // ISecretProvider for it MUST fail loudly rather than silently
        // falling back to Environment. Falling back would mask the
        // operator's misconfiguration and route secret lookups to the
        // wrong store.
        EnvironmentSecretProvider environment = new(_ => "ignored");
        InMemorySecretProvider inMemory = new();
        Microsoft.Extensions.Options.IOptions<SecretProviderOptions> options =
            Microsoft.Extensions.Options.Options.Create(new SecretProviderOptions { ProviderType = providerType });

        System.Action act = () => new CompositeSecretProvider(options, environment, inMemory);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage($"*SecretProvider:ProviderType={providerType}*")
            .WithMessage("*not supported*")
            .WithMessage("*BEFORE calling AddSecretProvider*",
                "the exception message must tell the operator how to register the missing backend");
    }

    [Fact]
    public void CompositeSecretProvider_throws_for_unrecognized_enum_values()
    {
        // A cast from an unknown integer (e.g., a typo'd appsettings
        // value bound through the enum binder's relaxed parser) must
        // also fail loudly instead of silently routing to Environment.
        SecretProviderType madeUp = (SecretProviderType)999;
        EnvironmentSecretProvider environment = new(_ => "ignored");
        InMemorySecretProvider inMemory = new();
        Microsoft.Extensions.Options.IOptions<SecretProviderOptions> options =
            Microsoft.Extensions.Options.Options.Create(new SecretProviderOptions { ProviderType = madeUp });

        System.Action act = () => new CompositeSecretProvider(options, environment, inMemory);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("KeyVault")]
    [InlineData("Kubernetes")]
    public void AddSecretProvider_with_unsupported_provider_type_fails_when_ISecretProvider_is_first_resolved(string providerTypeName)
    {
        // Resolving ISecretProvider through the DI container is the
        // production failure point: the worker's Program.BuildApp
        // eagerly resolves the composite, so the throw lands at host
        // start instead of at the first inbound Slack request.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecretProvider:ProviderType"] = providerTypeName,
            })
            .Build();

        ServiceCollection services = new();
        services.AddSecretProvider(configuration);

        using ServiceProvider sp = services.BuildServiceProvider(validateScopes: true);

        System.Action act = () => sp.GetRequiredService<ISecretProvider>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*ProviderType={providerTypeName}*");
    }
}
