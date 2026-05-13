using Qq.Messaging.Abstractions;

namespace Qq.Messaging.Contracts.Tests;

public class TelegramBotSettingsTests
{
    [Fact]
    public void Defaults_AreReasonableForProduction()
    {
        var settings = new TelegramBotSettings();

        Assert.Equal(TelegramReceiveMode.Webhook, settings.ReceiveMode);
        Assert.Equal(40, settings.MaxConnections);
        Assert.Contains("message", settings.AllowedUpdates);
        Assert.Contains("callback_query", settings.AllowedUpdates);
        Assert.Equal(5, settings.MaxRetryAttempts);
        Assert.Equal("TelegramBotToken", settings.BotTokenSecretName);
    }

    [Fact]
    public void WebhookUrl_IsNullByDefault_RequiresExplicitConfiguration()
    {
        var settings = new TelegramBotSettings();
        Assert.Null(settings.WebhookUrl);
    }

    [Fact]
    public void LongPollingMode_CanBeConfiguredForDev()
    {
        var settings = new TelegramBotSettings
        {
            ReceiveMode = TelegramReceiveMode.LongPolling
        };

        Assert.Equal(TelegramReceiveMode.LongPolling, settings.ReceiveMode);
    }
}
