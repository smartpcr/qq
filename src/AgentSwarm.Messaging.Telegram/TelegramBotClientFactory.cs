// -----------------------------------------------------------------------
// <copyright file="TelegramBotClientFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Net.Http;
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
    /// <summary>
    /// Named <see cref="HttpClient"/> registration used by the factory and
    /// referenced from <see cref="TelegramServiceCollectionExtensions.AddTelegram"/>.
    /// </summary>
    public const string HttpClientName = "Telegram.Bot";

    private readonly IOptions<TelegramOptions> _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public TelegramBotClientFactory(
        IOptions<TelegramOptions> options,
        IHttpClientFactory httpClientFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public ITelegramBotClient Create()
    {
        var opts = _options.Value;

        if (string.IsNullOrWhiteSpace(opts.BotToken))
        {
            // Guard: validator should have already caught this at startup, but defend in depth.
            throw new InvalidOperationException(
                "Telegram:BotToken is not configured. Refusing to create Telegram bot client.");
        }

        HttpClient httpClient = _httpClientFactory.CreateClient(HttpClientName);
        return new TelegramBotClient(opts.BotToken, httpClient);
    }
}
