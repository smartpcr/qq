using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Tests;

/// <summary>
/// Pins the canonical mapping rules implemented by <see cref="TeamsMessagingPostConfigure"/>
/// — the post-configure step that mirrors Teams-facing retry knobs into the inherited
/// <see cref="ConnectorOptions"/> fields after configuration binding completes. These rules
/// are referenced directly by Stage 4.4 of the implementation plan; pinning them here means
/// any future drift breaks loudly at test time rather than silently changing retry behavior.
/// </summary>
public sealed class TeamsMessagingPostConfigureTests
{
    [Fact]
    public void PostConfigure_MirrorsMaxRetryAttemptsIntoRetryCount()
    {
        var options = new TeamsMessagingOptions { MaxRetryAttempts = 7 };
        var subject = new TeamsMessagingPostConfigure();

        subject.PostConfigure(name: null, options);

        Assert.Equal(7, options.RetryCount);
    }

    [Fact]
    public void PostConfigure_MirrorsRetryBaseDelaySecondsIntoRetryDelayMs()
    {
        var options = new TeamsMessagingOptions { RetryBaseDelaySeconds = 4 };
        var subject = new TeamsMessagingPostConfigure();

        subject.PostConfigure(name: null, options);

        Assert.Equal(4 * 1000, options.RetryDelayMs);
    }

    [Fact]
    public void PostConfigure_MirrorsMaxRetryAttemptsIntoDeadLetterThreshold()
    {
        var options = new TeamsMessagingOptions { MaxRetryAttempts = 9 };
        var subject = new TeamsMessagingPostConfigure();

        subject.PostConfigure(name: null, options);

        Assert.Equal(9, options.DeadLetterThreshold);
    }

    [Fact]
    public void PostConfigure_NegativeMaxRetryAttempts_ClampedToZero_OnRetryCountOnly()
    {
        // Defensive: ConnectorOptions.RetryCount must never go negative or downstream
        // exponential-backoff calculations underflow. The post-configure clamps RetryCount to
        // zero on negative input; DeadLetterThreshold preserves the (negative) input value
        // so a separate validator can flag the misconfiguration.
        var options = new TeamsMessagingOptions { MaxRetryAttempts = -3 };
        var subject = new TeamsMessagingPostConfigure();

        subject.PostConfigure(name: null, options);

        Assert.Equal(0, options.RetryCount);
        Assert.Equal(-3, options.DeadLetterThreshold);
    }

    [Fact]
    public void PostConfigure_DefaultsApplied_When_NoOverridesSupplied()
    {
        // Exercise the canonical defaults: 5 attempts, 2 s base delay → RetryCount=5,
        // RetryDelayMs=2000, DeadLetterThreshold=5.
        var options = new TeamsMessagingOptions();
        var subject = new TeamsMessagingPostConfigure();

        subject.PostConfigure(name: null, options);

        Assert.Equal(5, options.RetryCount);
        Assert.Equal(2000, options.RetryDelayMs);
        Assert.Equal(5, options.DeadLetterThreshold);
    }

    [Fact]
    public void PostConfigure_NullOptions_NoThrow()
    {
        // Defensive: matches the implementation's null-guard so a null options instance
        // (e.g. during framework probing) does not crash startup.
        var subject = new TeamsMessagingPostConfigure();

        var ex = Record.Exception(() => subject.PostConfigure(name: null, options: null!));

        Assert.Null(ex);
    }
}
