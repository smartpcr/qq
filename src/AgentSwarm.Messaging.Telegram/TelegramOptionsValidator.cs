using Microsoft.Extensions.Options;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// <see cref="IValidateOptions{TOptions}"/> implementation that rejects
/// a missing or whitespace <see cref="TelegramOptions.BotToken"/> at host
/// startup. Registered alongside <c>.ValidateOnStart()</c> in
/// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/> so the
/// process throws <c>OptionsValidationException</c> during
/// <c>IHost.StartAsync</c> rather than failing later at the first
/// Telegram API call.
/// </summary>
internal sealed class TelegramOptionsValidator : IValidateOptions<TelegramOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TelegramOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail(
                "Telegram options are not configured. Add a 'Telegram' section to configuration.");
        }

        if (string.IsNullOrWhiteSpace(options.BotToken))
        {
            return ValidateOptionsResult.Fail(
                "Telegram:BotToken must be configured. Set it via Azure Key Vault, "
                + "the TELEGRAM__BOTTOKEN environment variable, or "
                + "'dotnet user-secrets set Telegram:BotToken <token>'. "
                + "Never commit the token to source control.");
        }

        // Stage 2.5: long-polling (dev) and webhook (prod) modes are mutually
        // exclusive. Running both would cause Telegram to return HTTP 409
        // from getUpdates while the webhook is registered, and would also
        // double-deliver updates if both happened to be active. Reject the
        // conflict at host startup so the configuration error is loud
        // rather than a runtime mystery.
        if (options.UsePolling && !string.IsNullOrWhiteSpace(options.WebhookUrl))
        {
            return ValidateOptionsResult.Fail(
                "Telegram:UsePolling and Telegram:WebhookUrl are mutually exclusive. "
                + "Set Telegram:UsePolling=true for local development (long polling) "
                + "OR set Telegram:WebhookUrl for production (webhook delivery), but not both. "
                + $"Current values: UsePolling=true, WebhookUrl='{options.WebhookUrl}'.");
        }

        // Stage 2.5: PollingTimeoutSeconds must respect Telegram's
        // server-side cap (max 50s; values <= 0 would translate to "no
        // long-poll" and burn the API quota with tight requests).
        if (options.UsePolling && (options.PollingTimeoutSeconds < 1 || options.PollingTimeoutSeconds > 50))
        {
            return ValidateOptionsResult.Fail(
                "Telegram:PollingTimeoutSeconds must be in the range [1, 50] when "
                + "Telegram:UsePolling is true. "
                + $"Configured value: {options.PollingTimeoutSeconds}.");
        }

        return ValidateOptionsResult.Success;
    }
}
