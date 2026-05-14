using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Maps the Teams-specific retry knobs onto the base <c>ConnectorOptions.RetryCount</c> /
/// <c>ConnectorOptions.RetryDelayMs</c> fields after options binding completes. This keeps
/// downstream code that consumes the base contract (e.g., generic outbox retry engine) free
/// from Teams-specific naming while preserving the canonical values from
/// <c>tech-spec.md</c> §4.4.
/// </summary>
/// <remarks>
/// <para>
/// <c>RetryCount</c> is set to the full <c>MaxRetryAttempts</c> value (the canonical Teams
/// policy treats it as the TOTAL attempt budget per <c>tech-spec.md</c> §4.4). The base
/// <c>ConnectorOptions.RetryCount</c> field carries the same semantic, so no decrement is
/// applied — silently halving the documented attempt budget when the configured value is
/// small would be a contract violation.
/// </para>
/// </remarks>
public sealed class TeamsMessagingPostConfigure : IPostConfigureOptions<TeamsMessagingOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, TeamsMessagingOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        // Map Teams-specific retry vocabulary onto the base ConnectorOptions fields.
        // MaxRetryAttempts is the total attempt budget per tech-spec.md §4.4 — copy it
        // directly. DO NOT subtract one.
        options.RetryCount = options.MaxRetryAttempts;
        options.RetryDelayMs = options.RetryBaseDelaySeconds * 1000;

        // Teams canonical policy aligns DeadLetterThreshold with MaxRetryAttempts.
        options.DeadLetterThreshold = options.MaxRetryAttempts;
    }
}
