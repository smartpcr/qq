using AgentSwarm.Messaging.Teams;
using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Tests;

public sealed class TeamsMessagingOptionsValidatorTests
{
    [Fact]
    public void Validate_ReturnsSuccess_ForFullyConfiguredOptions()
    {
        var validator = new TeamsMessagingOptionsValidator();
        var options = MakeValid();

        var result = validator.Validate(name: null, options);

        Assert.True(result.Succeeded, string.Join(";", result.Failures ?? Array.Empty<string>()));
    }

    [Fact]
    public void Defaults_HaveExpectedRetryKnobs_BeforePostConfigure()
    {
        var options = new TeamsMessagingOptions();

        Assert.Equal(5, options.MaxRetryAttempts);
        Assert.Equal(2, options.RetryBaseDelaySeconds);
        // ConnectorOptions defaults still in effect — PostConfigure has not run yet.
        Assert.Equal(3, options.RetryCount);
        Assert.Equal(1000, options.RetryDelayMs);
    }

    [Fact]
    public void PostConfigure_MirrorsRetryKnobs_IntoBaseClassFields()
    {
        var options = new TeamsMessagingOptions();
        var post = new TeamsMessagingPostConfigure();

        post.PostConfigure(name: null, options);

        // Stage 2.1 canonical mapping (TeamsMessagingPostConfigure): MaxRetryAttempts overrides
        // the base ConnectorOptions.RetryCount directly (1:1) — this is the literal mapping the
        // implementation plan describes when it says "MaxRetryAttempts / RetryBaseDelaySeconds
        // override base ConnectorOptions defaults (RetryCount=3, RetryDelayMs=1000) with
        // Teams-specific canonical values". Default MaxRetryAttempts is 5.
        Assert.Equal(5, options.RetryCount);
        Assert.Equal(2000, options.RetryDelayMs);
        Assert.Equal(5, options.DeadLetterThreshold);
    }

    [Fact]
    public void PostConfigure_HonoursBoundOverrides()
    {
        var options = new TeamsMessagingOptions { MaxRetryAttempts = 7, RetryBaseDelaySeconds = 3 };
        var post = new TeamsMessagingPostConfigure();

        post.PostConfigure(null, options);

        Assert.Equal(7, options.RetryCount);
        Assert.Equal(3000, options.RetryDelayMs);
        Assert.Equal(7, options.DeadLetterThreshold);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    public void PostConfigure_MapsRetryCount_OneToOneWith_MaxRetryAttempts(int attempts, int expectedRetryCount)
    {
        var options = new TeamsMessagingOptions { MaxRetryAttempts = attempts };
        new TeamsMessagingPostConfigure().PostConfigure(null, options);

        Assert.Equal(expectedRetryCount, options.RetryCount);
    }

    [Theory]
    [InlineData("MicrosoftAppId")]
    [InlineData("MicrosoftAppPassword")]
    [InlineData("MicrosoftAppTenantId")]
    [InlineData("BotEndpoint")]
    public void Validate_FailsWhen_RequiredStringMissing(string field)
    {
        var validator = new TeamsMessagingOptionsValidator();
        var options = MakeValid();
        typeof(TeamsMessagingOptions).GetProperty(field)!.SetValue(options, string.Empty);

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains(field, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("/api/messages")]
    [InlineData("ftp://bot.example/api/messages")]
    public void Validate_FailsWhen_BotEndpointIsNotAbsoluteHttpUri(string botEndpoint)
    {
        var validator = new TeamsMessagingOptionsValidator();
        var options = MakeValid();
        options.BotEndpoint = botEndpoint;

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("BotEndpoint", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://localhost:3978/api/messages")]
    [InlineData("https://bot.example/api/messages")]
    public void Validate_Accepts_AbsoluteHttpAndHttpsBotEndpoints(string botEndpoint)
    {
        var validator = new TeamsMessagingOptionsValidator();
        var options = MakeValid();
        options.BotEndpoint = botEndpoint;

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded, string.Join(";", result.Failures ?? Array.Empty<string>()));
    }

    [Fact]
    public void Validate_FailsWhen_AllowedTenantIdsIsEmpty()
    {
        var validator = new TeamsMessagingOptionsValidator();
        var options = MakeValid();
        options.AllowedTenantIds = new List<string>();

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("AllowedTenantIds", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_FailsWhen_AllowedTenantIdsContainsBlank()
    {
        var validator = new TeamsMessagingOptionsValidator();
        var options = MakeValid();
        options.AllowedTenantIds = new List<string> { "tenant-a", "   " };

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("[1]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(nameof(TeamsMessagingOptions.DeduplicationTtlMinutes), -1)]
    [InlineData(nameof(TeamsMessagingOptions.RetryBaseDelaySeconds), 0)]
    public void Validate_FailsWhen_NumericFieldNonPositive(string field, int value)
    {
        var validator = new TeamsMessagingOptionsValidator();
        var options = MakeValid();
        typeof(TeamsMessagingOptions).GetProperty(field)!.SetValue(options, value);

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains(field, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Allows_ZeroRateLimit_AsDisabledSentinel()
    {
        var validator = new TeamsMessagingOptionsValidator();
        var options = MakeValid();
        options.RateLimitPerTenantPerMinute = 0;

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_FailsWhen_RateLimitIsNegative()
    {
        var validator = new TeamsMessagingOptionsValidator();
        var options = MakeValid();
        options.RateLimitPerTenantPerMinute = -1;

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("RateLimitPerTenantPerMinute", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_FailsWhen_MaxRetryAttemptsNegative()
    {
        var validator = new TeamsMessagingOptionsValidator();
        var options = MakeValid();
        options.MaxRetryAttempts = -1;

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, f => f.Contains("MaxRetryAttempts", StringComparison.Ordinal));
    }

    private static TeamsMessagingOptions MakeValid() => new()
    {
        MicrosoftAppId = "app-id",
        MicrosoftAppPassword = "secret",
        MicrosoftAppTenantId = "tenant-a",
        AllowedTenantIds = new List<string> { "tenant-a" },
        BotEndpoint = "https://bot.example/api/messages",
    };
}
