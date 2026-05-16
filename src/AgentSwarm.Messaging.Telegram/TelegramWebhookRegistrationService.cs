using AgentSwarm.Messaging.Telegram.Webhook;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// <see cref="IHostedService"/> that calls
/// <see cref="TelegramBotClientExtensions.SetWebhook"/> on host startup
/// (when <see cref="TelegramOptions.WebhookUrl"/> is configured and
/// <see cref="TelegramOptions.UsePolling"/> is <c>false</c>) and
/// <see cref="TelegramBotClientExtensions.DeleteWebhook"/> on shutdown.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mode awareness.</b> Polling mode (<see cref="TelegramOptions.UsePolling"/>
/// = <c>true</c>) is mutually exclusive with the webhook per
/// architecture.md §7.1 and implementation-plan.md §209. The
/// registration runs only in webhook mode; in polling mode it is a
/// no-op so Stage 2.5's polling service can call
/// <c>DeleteWebhook</c> itself.
/// </para>
/// <para>
/// <b>Secret token propagation.</b>
/// <see cref="TelegramOptions.SecretToken"/> is passed to
/// <see cref="TelegramBotClientExtensions.SetWebhook"/> so Telegram
/// echoes it in the <c>X-Telegram-Bot-Api-Secret-Token</c> header on
/// every webhook POST. The <see cref="Webhook.TelegramWebhookSecretFilter"/>
/// validates it on the receiving end.
/// </para>
/// <para>
/// <b>Allowed updates.</b> Subscribes to the update types this Stage
/// supports: <c>Message</c> (slash commands and text replies) and
/// <c>CallbackQuery</c> (inline-button answers). Subscribing to a
/// narrow set keeps Telegram from delivering edited-message /
/// channel-post traffic that the pipeline classifies as
/// <c>EventType.Unknown</c> anyway.
/// </para>
/// </remarks>
public sealed class TelegramWebhookRegistrationService : IHostedService
{
    private static readonly UpdateType[] AllowedUpdates =
    {
        UpdateType.Message,
        UpdateType.CallbackQuery,
    };

    private readonly ITelegramBotClient _bot;
    private readonly IOptionsMonitor<TelegramOptions> _options;
    private readonly ILogger<TelegramWebhookRegistrationService> _logger;

    public TelegramWebhookRegistrationService(
        ITelegramBotClient bot,
        IOptionsMonitor<TelegramOptions> options,
        ILogger<TelegramWebhookRegistrationService> logger)
    {
        _bot = bot ?? throw new ArgumentNullException(nameof(bot));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;
        if (opts.UsePolling || string.IsNullOrWhiteSpace(opts.WebhookUrl))
        {
            _logger.LogInformation(
                "TelegramWebhookRegistrationService skipped: UsePolling={UsePolling} WebhookUrlSet={WebhookUrlSet}",
                opts.UsePolling,
                !string.IsNullOrWhiteSpace(opts.WebhookUrl));
            return;
        }

        _logger.LogInformation(
            "Registering Telegram webhook. WebhookUrl={WebhookUrl} AllowedUpdates={AllowedUpdates}",
            opts.WebhookUrl,
            AllowedUpdates);

        await _bot.SetWebhook(
            url: opts.WebhookUrl!,
            allowedUpdates: AllowedUpdates,
            dropPendingUpdates: false,
            secretToken: opts.SecretToken,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;
        if (opts.UsePolling || string.IsNullOrWhiteSpace(opts.WebhookUrl))
        {
            return;
        }

        try
        {
            await _bot.DeleteWebhook(dropPendingUpdates: false, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation("Unregistered Telegram webhook on shutdown.");
        }
        catch (Exception ex)
        {
            // Shutdown best-effort — the next process can re-register.
            _logger.LogWarning(ex, "Failed to delete Telegram webhook on shutdown.");
        }
    }
}
