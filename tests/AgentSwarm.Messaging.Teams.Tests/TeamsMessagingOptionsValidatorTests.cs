using AgentSwarm.Messaging.Teams;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class TeamsMessagingOptionsValidatorTests
{
    private static TeamsMessagingOptions ValidOptions() => new()
    {
        MicrosoftAppId = "00000000-0000-0000-0000-000000000001",
        MicrosoftAppPassword = "secret",
        MicrosoftAppTenantId = "00000000-0000-0000-0000-000000000002",
        BotEndpoint = "https://bot.example.com/api/messages",
        AllowedTenantIds = new List<string> { "00000000-0000-0000-0000-000000000002" },
        RateLimitPerTenantPerMinute = 100,
        DeduplicationTtlMinutes = 10,
        MaxRetryAttempts = 5,
        RetryBaseDelaySeconds = 2,
    };

    [Fact]
    public void Validate_Returns_Success_When_All_Fields_Present()
    {
        var validator = new TeamsMessagingOptionsValidator();
        var result = validator.Validate(name: null, ValidOptions());
        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? Array.Empty<string>()));
    }

    [Fact]
    public void Validate_Fails_When_MicrosoftAppId_Missing()
    {
        var opts = ValidOptions();
        opts.MicrosoftAppId = string.Empty;
        var result = new TeamsMessagingOptionsValidator().Validate(null, opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("MicrosoftAppId"));
    }

    [Fact]
    public void Validate_Fails_When_BotEndpoint_Missing()
    {
        var opts = ValidOptions();
        opts.BotEndpoint = string.Empty;
        var result = new TeamsMessagingOptionsValidator().Validate(null, opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("BotEndpoint"));
    }

    [Fact]
    public void Validate_Fails_When_BotEndpoint_Not_Http_Or_Https()
    {
        var opts = ValidOptions();
        opts.BotEndpoint = "ftp://bot.example.com";
        var result = new TeamsMessagingOptionsValidator().Validate(null, opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("BotEndpoint"));
    }

    [Fact]
    public void Validate_Fails_When_AllowedTenantIds_Empty()
    {
        var opts = ValidOptions();
        opts.AllowedTenantIds = new List<string>();
        var result = new TeamsMessagingOptionsValidator().Validate(null, opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("AllowedTenantIds"));
    }

    [Fact]
    public void Validate_Fails_When_AllowedTenantIds_Has_Blank_Entry()
    {
        var opts = ValidOptions();
        opts.AllowedTenantIds = new List<string> { string.Empty };
        var result = new TeamsMessagingOptionsValidator().Validate(null, opts);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Validate_Fails_When_MaxRetryAttempts_NonPositive()
    {
        var opts = ValidOptions();
        opts.MaxRetryAttempts = 0;
        var result = new TeamsMessagingOptionsValidator().Validate(null, opts);
        Assert.True(result.Failed);
    }

    [Fact]
    public void PostConfigure_Maps_MaxRetryAttempts_Onto_RetryCount_Without_Subtracting_One()
    {
        var opts = ValidOptions();
        opts.MaxRetryAttempts = 5;
        opts.RetryBaseDelaySeconds = 2;
        new TeamsMessagingPostConfigure().PostConfigure(null, opts);

        // Canonical Teams policy: MaxRetryAttempts is the total attempt budget — copy it
        // directly onto RetryCount without subtracting one.
        Assert.Equal(5, opts.RetryCount);
        Assert.Equal(2000, opts.RetryDelayMs);
        Assert.Equal(5, opts.DeadLetterThreshold);
    }
}
