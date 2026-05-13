namespace AgentSwarm.Messaging.Abstractions.Tests;

/// <summary>
/// Stage 1.2 test scenario: "Options defaults — Given a default <see cref="ConnectorOptions"/>,
/// When instantiated, Then <c>RetryCount</c> is 3 and <c>RetryDelayMs</c> is 1000."
/// </summary>
public sealed class ConnectorOptionsDefaultsTests
{
    [Fact]
    public void DefaultRetryCount_Is_3()
    {
        var options = new ConnectorOptions();

        Assert.Equal(3, options.RetryCount);
    }

    [Fact]
    public void DefaultRetryDelayMs_Is_1000()
    {
        var options = new ConnectorOptions();

        Assert.Equal(1000, options.RetryDelayMs);
    }

    [Fact]
    public void DefaultMaxConcurrency_IsPositive()
    {
        var options = new ConnectorOptions();

        Assert.True(options.MaxConcurrency >= 1);
    }

    [Fact]
    public void DefaultDeadLetterThreshold_IsPositive()
    {
        var options = new ConnectorOptions();

        Assert.True(options.DeadLetterThreshold >= 1);
    }

    [Fact]
    public void Derived_OverridesOfBaseDefaults_ArePreserved()
    {
        // Concrete connectors (for example TeamsMessagingOptions) override the base defaults
        // with platform-specific canonical values per tech-spec.md §4.4 — verify the base
        // contract supports this via property setters.
        var options = new ConnectorOptions
        {
            RetryCount = 5,
            RetryDelayMs = 2000,
            MaxConcurrency = 8,
            DeadLetterThreshold = 7,
        };

        Assert.Equal(5, options.RetryCount);
        Assert.Equal(2000, options.RetryDelayMs);
        Assert.Equal(8, options.MaxConcurrency);
        Assert.Equal(7, options.DeadLetterThreshold);
    }
}
