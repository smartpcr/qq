using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Validates <see cref="TeamsMessagingOptions"/> at startup so missing required fields
/// fail the host immediately rather than at first inbound activity. Registered as
/// <c>IValidateOptions&lt;TeamsMessagingOptions&gt;</c> in DI by the worker's
/// <c>Program.cs</c>; combined with <c>ValidateOnStart()</c> the validator runs once during
/// startup and surfaces an <see cref="OptionsValidationException"/> for invalid configuration.
/// </summary>
public sealed class TeamsMessagingOptionsValidator : IValidateOptions<TeamsMessagingOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TeamsMessagingOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail($"{nameof(TeamsMessagingOptions)} instance is null.");
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
        else if (!Uri.TryCreate(options.BotEndpoint, UriKind.Absolute, out var endpointUri)
                 || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{nameof(TeamsMessagingOptions.BotEndpoint)} must be an absolute HTTP or HTTPS URI; got '{options.BotEndpoint}'.");
        }

        if (options.AllowedTenantIds is null || options.AllowedTenantIds.Count == 0)
        {
            failures.Add($"{nameof(TeamsMessagingOptions.AllowedTenantIds)} must contain at least one entry.");
        }
        else if (options.AllowedTenantIds.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add($"{nameof(TeamsMessagingOptions.AllowedTenantIds)} must not contain blank entries.");
        }

        if (options.RateLimitPerTenantPerMinute <= 0)
        {
            failures.Add($"{nameof(TeamsMessagingOptions.RateLimitPerTenantPerMinute)} must be > 0.");
        }

        if (options.DeduplicationTtlMinutes <= 0)
        {
            failures.Add($"{nameof(TeamsMessagingOptions.DeduplicationTtlMinutes)} must be > 0.");
        }

        if (options.MaxRetryAttempts <= 0)
        {
            failures.Add($"{nameof(TeamsMessagingOptions.MaxRetryAttempts)} must be > 0.");
        }

        if (options.RetryBaseDelaySeconds <= 0)
        {
            failures.Add($"{nameof(TeamsMessagingOptions.RetryBaseDelaySeconds)} must be > 0.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
