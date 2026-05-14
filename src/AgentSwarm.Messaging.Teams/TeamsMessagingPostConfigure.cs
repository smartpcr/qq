using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// <see cref="IPostConfigureOptions{TOptions}"/> implementation that mirrors the
/// Teams-specific retry knobs (<see cref="TeamsMessagingOptions.MaxRetryAttempts"/> and
/// <see cref="TeamsMessagingOptions.RetryBaseDelaySeconds"/>) into the inherited
/// <see cref="AgentSwarm.Messaging.Abstractions.ConnectorOptions"/> fields AFTER configuration
/// binding completes.
/// </summary>
/// <remarks>
/// <para>
/// Without this post-configure step, a configuration override of <c>Teams:MaxRetryAttempts</c>
/// would leave <see cref="AgentSwarm.Messaging.Abstractions.ConnectorOptions.RetryCount"/> at
/// its base-class default — the constructor mapping would already have run with the unbound
/// values. Hooking the mapping at the post-configure stage guarantees the mirror is computed
/// from the FINAL bound values regardless of whether the configuration overrides any subset
/// of the fields.
/// </para>
/// <para>
/// Mapping rules — per the Stage 2.1 implementation-plan literal wording
/// "<c>MaxRetryAttempts</c> and <c>RetryBaseDelaySeconds</c> override the base-class
/// <c>ConnectorOptions</c> defaults (<c>RetryCount = 3</c>, <c>RetryDelayMs = 1000</c>) with
/// Teams-specific canonical values per <c>tech-spec.md</c> §4.4":
/// <list type="bullet">
///   <item><see cref="AgentSwarm.Messaging.Abstractions.ConnectorOptions.RetryCount"/> = <c>MaxRetryAttempts</c> (Stage 2.1 canonical literal mapping; <see cref="TeamsMessagingOptions.MaxRetryAttempts"/> is the public Teams-facing knob and overrides the base-class field directly).</item>
///   <item><see cref="AgentSwarm.Messaging.Abstractions.ConnectorOptions.RetryDelayMs"/> = <c>RetryBaseDelaySeconds * 1000</c>.</item>
///   <item><see cref="AgentSwarm.Messaging.Abstractions.ConnectorOptions.DeadLetterThreshold"/> = <c>MaxRetryAttempts</c> (after <c>MaxRetryAttempts</c> consecutive failures the entry is dead-lettered).</item>
/// </list>
/// </para>
/// </remarks>
public sealed class TeamsMessagingPostConfigure : IPostConfigureOptions<TeamsMessagingOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, TeamsMessagingOptions options)
    {
        if (options is null) return;

        // Canonical Stage 2.1 mapping: TeamsMessagingOptions.MaxRetryAttempts overrides
        // ConnectorOptions.RetryCount directly (the implementation plan describes them as a
        // single Teams-canonical value, not "total attempts vs additional retries"). A test
        // pins this mapping so any future revisit must change both surfaces in lockstep.
        options.RetryCount = options.MaxRetryAttempts < 0 ? 0 : options.MaxRetryAttempts;
        options.RetryDelayMs = options.RetryBaseDelaySeconds * 1000;
        options.DeadLetterThreshold = options.MaxRetryAttempts;
    }
}
