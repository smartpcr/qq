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

        return ValidateOptionsResult.Success;
    }
}
