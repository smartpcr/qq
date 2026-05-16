using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams.Security;

/// <summary>
/// <see cref="IValidateOptions{TOptions}"/> implementation that surfaces
/// <see cref="TeamsAppPolicyOptions.Validate"/> errors during host startup. Registered by
/// <see cref="TeamsSecurityServiceCollectionExtensions.AddTeamsSecurity"/> alongside the
/// <c>ValidateOnStart()</c> hook so misconfigured policies fail the host bootstrap
/// rather than waiting for the first health-check tick.
/// </summary>
public sealed class TeamsAppPolicyOptionsValidator : IValidateOptions<TeamsAppPolicyOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TeamsAppPolicyOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail(
                "TeamsAppPolicyOptions instance is null; cannot validate.");
        }

        var errors = options.Validate();
        if (errors is null || errors.Count == 0)
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(errors);
    }
}
