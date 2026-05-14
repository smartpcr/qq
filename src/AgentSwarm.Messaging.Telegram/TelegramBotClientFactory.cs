// -----------------------------------------------------------------------
// <copyright file="TelegramBotClientFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Creates a configured <see cref="ITelegramBotClient"/> from validated
/// <see cref="TelegramOptions"/>. The factory deliberately performs all token reads
/// here so that nothing else in the codebase needs direct access to <c>BotToken</c>.
/// </summary>
public interface ITelegramBotClientFactory
{
    /// <summary>Creates a new <see cref="ITelegramBotClient"/> using the current options.</summary>
    ITelegramBotClient Create();
}

/// <inheritdoc cref="ITelegramBotClientFactory"/>
public sealed class TelegramBotClientFactory : ITelegramBotClientFactory
{
    private readonly IOptionsMonitor<TelegramOptions> _options;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<TelegramBotClientFactory> _logger;

    public TelegramBotClientFactory(
        IOptionsMonitor<TelegramOptions> options,
        ILogger<TelegramBotClientFactory> logger,
        IHttpClientFactory? httpClientFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClientFactory = httpClientFactory;
    }

    public ITelegramBotClient Create()
    {
        var opts = _options.CurrentValue;

        if (string.IsNullOrWhiteSpace(opts.BotToken))
        {
            // Guard: validator should have already caught this at startup, but defend in depth.
            throw new InvalidOperationException(
                "TelegramOptions.BotToken is not configured. Refusing to create Telegram bot client.");
        }

        // IMPORTANT: log the redacted options view only — never the raw token.
        _logger.LogInformation("Creating Telegram bot client. Options: {Options}", opts);

        HttpClient? httpClient = _httpClientFactory?.CreateClient("Telegram.Bot");
        return new TelegramBotClient(opts.BotToken, httpClient);
    }
}
