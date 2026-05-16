using AgentSwarm.Messaging.Slack.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentSwarm.Messaging.Slack.Tests.Configuration;

/// <summary>
/// Stage 2.1 options-binding tests for <see cref="SlackConnectorOptions"/>.
/// Implements the explicit brief scenario:
/// <c>Given configuration JSON with Slack:MaxWorkspaces = 10, When
/// SlackConnectorOptions is resolved from DI, Then MaxWorkspaces
/// equals 10.</c>
/// </summary>
public sealed class SlackConnectorOptionsTests
{
    [Fact]
    public void Defaults_match_documented_baseline()
    {
        SlackConnectorOptions options = new();

        options.MaxWorkspaces.Should().Be(15,
            because: "the brief and tech-spec.md §5.2 say MaxWorkspaces defaults to 15");
        options.MembershipCacheTtlMinutes.Should().Be(5,
            because: "appsettings.json sets the default cache TTL to 5 minutes");

        options.Retry.Should().NotBeNull();
        options.Retry.MaxAttempts.Should().Be(5);
        options.Retry.InitialDelayMilliseconds.Should().Be(200);
        options.Retry.MaxDelaySeconds.Should().Be(30);

        options.RateLimits.Should().NotBeNull();
        options.RateLimits.Tier1.Should().NotBeNull();
        options.RateLimits.Tier2.Should().NotBeNull();
        options.RateLimits.Tier3.Should().NotBeNull();
        options.RateLimits.Tier4.Should().NotBeNull();
        options.RateLimits.Tier2.Scope.Should().Be(SlackRateLimitScope.Channel,
            because: "architecture.md §2.12: chat.postMessage (Tier 2) is per-channel");

        // Stage 6.3 evaluator iter-1 item #3 regression: the shipped
        // Tier 2 default MUST honour Slack's "~1 message per second per
        // channel" ceiling for chat.postMessage (= 60 rpm). Earlier
        // iterations defaulted to 20 rpm, which throttled outbound
        // posts to ~1 message every 3 seconds and silently capped
        // agents below the Slack-documented limit. Operators can still
        // override via Slack:RateLimits:Tier2:RequestsPerMinute when
        // they want to be more conservative, but the SHIPPED default
        // must meet the published ceiling.
        options.RateLimits.Tier2.RequestsPerMinute.Should().BeGreaterOrEqualTo(60,
            because: "Stage 6.3 evaluator item #3: Slack chat.postMessage ~1 msg/sec/channel = 60 rpm");
    }

    [Fact]
    public void SectionName_constant_is_Slack_so_appsettings_section_name_is_pinned()
    {
        SlackConnectorOptions.SectionName.Should().Be("Slack");
    }

    [Fact]
    public void DI_resolution_binds_MaxWorkspaces_from_configuration()
    {
        // Brief scenario: Given Slack:MaxWorkspaces = 10, When resolved
        // from DI, Then MaxWorkspaces equals 10.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Slack:MaxWorkspaces"] = "10",
            })
            .Build();

        ServiceCollection services = new();
        services.AddSlackConnectorOptions(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        SlackConnectorOptions options = provider.GetRequiredService<IOptions<SlackConnectorOptions>>().Value;

        options.MaxWorkspaces.Should().Be(10);
    }

    [Fact]
    public void DI_resolution_binds_nested_retry_and_membership_cache_settings()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Slack:MembershipCacheTtlMinutes"] = "7",
                ["Slack:Retry:MaxAttempts"] = "9",
                ["Slack:Retry:InitialDelayMilliseconds"] = "1000",
                ["Slack:Retry:MaxDelaySeconds"] = "60",
            })
            .Build();

        ServiceCollection services = new();
        services.AddSlackConnectorOptions(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        SlackConnectorOptions options = provider.GetRequiredService<IOptions<SlackConnectorOptions>>().Value;

        options.MembershipCacheTtlMinutes.Should().Be(7);
        options.Retry.MaxAttempts.Should().Be(9);
        options.Retry.InitialDelayMilliseconds.Should().Be(1000);
        options.Retry.MaxDelaySeconds.Should().Be(60);
    }

    [Fact]
    public void DI_resolution_binds_rate_limit_tiers()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Slack:RateLimits:Tier2:RequestsPerMinute"] = "60",
                ["Slack:RateLimits:Tier2:BurstCapacity"] = "12",
                ["Slack:RateLimits:Tier2:Scope"] = "Workspace",
                ["Slack:RateLimits:Tier4:RequestsPerMinute"] = "120",
            })
            .Build();

        ServiceCollection services = new();
        services.AddSlackConnectorOptions(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        SlackConnectorOptions options = provider.GetRequiredService<IOptions<SlackConnectorOptions>>().Value;

        options.RateLimits.Tier2.RequestsPerMinute.Should().Be(60);
        options.RateLimits.Tier2.BurstCapacity.Should().Be(12);
        options.RateLimits.Tier2.Scope.Should().Be(SlackRateLimitScope.Workspace);
        options.RateLimits.Tier4.RequestsPerMinute.Should().Be(120);
        // Untouched tiers retain their defaults.
        options.RateLimits.Tier1.RequestsPerMinute.Should().Be(1);
        options.RateLimits.Tier3.RequestsPerMinute.Should().Be(50);
    }

    [Fact]
    public void DI_resolution_with_empty_configuration_uses_POCO_defaults()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        ServiceCollection services = new();
        services.AddSlackConnectorOptions(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        SlackConnectorOptions options = provider.GetRequiredService<IOptions<SlackConnectorOptions>>().Value;

        options.MaxWorkspaces.Should().Be(15);
        options.MembershipCacheTtlMinutes.Should().Be(5);
    }

    [Fact]
    public void AddSlackConnectorOptions_rejects_null_services()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        Action act = () => SlackConnectorServiceCollectionExtensions
            .AddSlackConnectorOptions(null!, configuration);
        act.Should().Throw<ArgumentNullException>().WithParameterName("services");
    }

    [Fact]
    public void AddSlackConnectorOptions_rejects_null_configuration()
    {
        ServiceCollection services = new();
        Action act = () => services.AddSlackConnectorOptions(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("configuration");
    }
}
