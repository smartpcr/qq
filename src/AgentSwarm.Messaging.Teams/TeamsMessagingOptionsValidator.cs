using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Startup-time validator that ensures every required field on
/// <see cref="TeamsMessagingOptions"/> has been populated. A missing value produces an
/// <see cref="OptionsValidationException"/> on the first read of the options instance — which
/// happens at host startup when DI resolves <c>IOptions&lt;TeamsMessagingOptions&gt;</c>.
/// </summary>
/// <remarks>
/// Aligned with Stage 2.1 test scenario "Missing config fails startup — Given
/// <c>MicrosoftAppId</c> is not configured, When the host starts, Then it throws
/// <c>OptionsValidationException</c>." Wired via
/// <c>services.AddSingleton&lt;IValidateOptions&lt;TeamsMessagingOptions&gt;, TeamsMessagingOptionsValidator&gt;()</c>
/// in <c>Program.cs</c>, then activated by <c>OptionsBuilder.ValidateOnStart()</c>.
/// </remarks>
public sealed class TeamsMessagingOptionsValidator : IValidateOptions<TeamsMessagingOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TeamsMessagingOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Teams options object is null.");
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.MicrosoftAppId))
        {
            failures.Add($"{nameof(TeamsMessagingOptions.MicrosoftAppId)} is required.");
        }

        if (string.IsNullOrWhiteSpace(options.MicrosoftAppPassword))
        {
            failures.Add($"{nameof(TeamsMessagingOptions.MicrosoftAppPassword)} is required.");
        }

        if (string.IsNullOrWhiteSpace(options.MicrosoftAppTenantId))
        {
            failures.Add($"{nameof(TeamsMessagingOptions.MicrosoftAppTenantId)} is required.");
        }

        if (string.IsNullOrWhiteSpace(options.BotEndpoint))
        {
            failures.Add($"{nameof(TeamsMessagingOptions.BotEndpoint)} is required.");
        }
        else if (!Uri.TryCreate(options.BotEndpoint, UriKind.Absolute, out var botEndpointUri)
            || (botEndpointUri.Scheme != Uri.UriSchemeHttp && botEndpointUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add(
                $"{nameof(TeamsMessagingOptions.BotEndpoint)} must be an absolute http(s) URI (got '{options.BotEndpoint}').");
        }

        if (options.AllowedTenantIds is null || options.AllowedTenantIds.Count == 0)
        {
            failures.Add(
                $"{nameof(TeamsMessagingOptions.AllowedTenantIds)} must contain at least one tenant ID.");
        }
        else
        {
            for (var i = 0; i < options.AllowedTenantIds.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(options.AllowedTenantIds[i]))
                {
                    failures.Add(
                        $"{nameof(TeamsMessagingOptions.AllowedTenantIds)}[{i}] must be non-empty.");
                }
            }
        }

        if (options.RateLimitPerTenantPerMinute < 0)
        {
            failures.Add(
                $"{nameof(TeamsMessagingOptions.RateLimitPerTenantPerMinute)} must be non-negative (got {options.RateLimitPerTenantPerMinute}; use 0 to disable rate limiting).");
        }

        if (options.DeduplicationTtlMinutes <= 0)
        {
            failures.Add(
                $"{nameof(TeamsMessagingOptions.DeduplicationTtlMinutes)} must be positive (got {options.DeduplicationTtlMinutes}).");
        }

        if (options.MaxRetryAttempts < 0)
        {
            failures.Add(
                $"{nameof(TeamsMessagingOptions.MaxRetryAttempts)} must be non-negative (got {options.MaxRetryAttempts}).");
        }

        if (options.RetryBaseDelaySeconds <= 0)
        {
            failures.Add(
                $"{nameof(TeamsMessagingOptions.RetryBaseDelaySeconds)} must be positive (got {options.RetryBaseDelaySeconds}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
