// -----------------------------------------------------------------------
// <copyright file="SlackAuthorizationServiceCollectionExtensionsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System.Collections.Generic;
using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Smoke tests for <see cref="SlackAuthorizationServiceCollectionExtensions.AddSlackAuthorization"/>.
/// Confirms the DI extension wires every dependency the filter needs and
/// honours the options-bound rejection message.
/// </summary>
public sealed class SlackAuthorizationServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSlackAuthorization_registers_filter_and_supporting_services()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Slack:Authorization:Enabled"] = "true",
                ["Slack:Authorization:RejectionMessage"] = "nope",
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSlackAuthorization(config);
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetService<SlackAuthorizationFilter>().Should().NotBeNull();
        provider.GetService<ISlackMembershipResolver>().Should().NotBeNull();
        provider.GetService<ISlackUserGroupClient>().Should().NotBeNull();
        provider.GetService<ISlackAuthorizationAuditSink>().Should().NotBeNull();

        IOptionsMonitor<SlackAuthorizationOptions> opts =
            provider.GetRequiredService<IOptionsMonitor<SlackAuthorizationOptions>>();
        opts.CurrentValue.RejectionMessage.Should().Be("nope");
    }

    [Fact]
    public void AddSlackAuthorization_supports_pre_registered_overrides_via_tryadd()
    {
        IConfiguration config = new ConfigurationBuilder().Build();

        ServiceCollection services = new();
        services.AddLogging();

        // Pre-register a custom audit sink BEFORE the extension call.
        InMemorySlackAuthorizationAuditSink override_ = new();
        services.AddSingleton<ISlackAuthorizationAuditSink>(override_);

        services.AddSlackAuthorization(config);
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISlackAuthorizationAuditSink>().Should().BeSameAs(override_,
            "the extension uses TryAdd so a pre-registered custom sink wins over the default bridge");
    }

    [Fact]
    public void Default_options_are_safe_for_production()
    {
        SlackAuthorizationOptions opts = new();
        opts.Enabled.Should().BeTrue("the security pipeline must default to enforced");
        opts.RejectionMessage.Should().Be(SlackAuthorizationOptions.DefaultRejectionMessage);
    }
}
