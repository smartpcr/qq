// -----------------------------------------------------------------------
// <copyright file="SlackAuthorizationServiceCollectionExtensionsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.Collections.Generic;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Smoke tests for <see cref="SlackAuthorizationServiceCollectionExtensions.AddSlackAuthorization"/>.
/// Confirms the DI extension wires every dependency the filter needs,
/// honours the options-bound rejection message, and -- critically --
/// derives its URL path scope from the SAME <see cref="SlackSignatureOptions.PathPrefix"/>
/// the upstream HMAC middleware consumes (the iter-2 evaluator called
/// the lack of this property a configurable-prefix authorization bypass).
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

    [Fact]
    public void SlackAuthorizationOptions_no_longer_exposes_PathPrefix()
    {
        // Regression pin (evaluator iter-2 follow-up): a separate
        // Slack:Authorization:PathPrefix is an authorization-bypass
        // footgun. The property must not exist on the options class
        // so operators cannot configure it to drift away from
        // Slack:Signature:PathPrefix.
        typeof(SlackAuthorizationOptions)
            .GetProperty("PathPrefix")
            .Should().BeNull(
                "the iter-2 evaluator flagged a separate Slack:Authorization:PathPrefix as a configurable-prefix authorization bypass; this iteration removes the property entirely so divergence from Slack:Signature:PathPrefix is impossible by construction");
    }

    [Fact]
    public void AddSlackAuthorization_path_scope_follows_Slack_Signature_PathPrefix_when_only_signature_is_configured()
    {
        // Evaluator iter-2 follow-up: the scenario was "operator
        // changes ONLY Slack:Signature:PathPrefix to /slack-gateway,
        // signature validation moves, but the authorization filter
        // bypasses /slack-gateway because the old
        // Slack:Authorization:PathPrefix still says /api/slack."
        // With the shared option that mismatch is impossible:
        // AddSlackAuthorization binds SlackSignatureOptions from the
        // SAME configuration root, so the filter sees the operator's
        // value automatically.
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Note: the operator did NOT set Slack:Authorization:PathPrefix
                // (it does not exist anymore).
                ["Slack:Signature:PathPrefix"] = "/slack-gateway",
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSlackAuthorization(config);
        using ServiceProvider provider = services.BuildServiceProvider();

        IOptionsMonitor<SlackSignatureOptions> signatureMonitor =
            provider.GetRequiredService<IOptionsMonitor<SlackSignatureOptions>>();
        signatureMonitor.CurrentValue.PathPrefix.Should().Be("/slack-gateway",
            "AddSlackAuthorization must bind SlackSignatureOptions from the shared configuration root so the filter and the HMAC middleware ALWAYS read the same PathPrefix");

        // The filter resolves cleanly with that prefix in place.
        provider.GetRequiredService<SlackAuthorizationFilter>().Should().NotBeNull(
            "AddSlackAuthorization must also bind SlackSignatureOptions defensively so the filter can resolve even when AddSlackSignatureValidation has not been called");
    }

    [Fact]
    public void AddSlackSignatureValidation_rejects_signature_path_prefix_without_leading_slash_at_startup()
    {
        // Evaluator iter-2 follow-up: tighten PathPrefix validation
        // so values like 'api/slack' (no leading '/') fail at startup
        // instead of at the first request when 'new PathString(...)'
        // throws. The validation now also covers
        // SlackAuthorizationFilter's path scope by transitivity --
        // both components share SlackSignatureOptions.PathPrefix.
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Slack:Signature:PathPrefix"] = "api/slack", // missing leading '/'
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSlackSignatureValidation(config);
        using ServiceProvider provider = services.BuildServiceProvider();

        Action act = () => provider.GetRequiredService<IOptionsMonitor<SlackSignatureOptions>>().CurrentValue.PathPrefix.ToString();
        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*PathPrefix*",
                "values without a leading '/' would otherwise fail at request time when 'new PathString(...)' rejects them; the validator must fail closed at startup instead");
    }

    [Fact]
    public void AddSlackSignatureValidation_rejects_blank_signature_path_prefix_at_startup()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Slack:Signature:PathPrefix"] = "   ",
            })
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSlackSignatureValidation(config);
        using ServiceProvider provider = services.BuildServiceProvider();

        Action act = () => provider.GetRequiredService<IOptionsMonitor<SlackSignatureOptions>>().CurrentValue.PathPrefix.ToString();
        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*PathPrefix*");
    }
}
