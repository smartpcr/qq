using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Factory that produces a configured <see cref="ITelegramBotClient"/>
/// from the validated <see cref="TelegramOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> The factory is registered as a singleton in
/// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/> and is
/// expected to back a singleton <see cref="ITelegramBotClient"/>.
/// <c>Telegram.Bot</c>'s client is thread-safe and reuses the underlying
/// <see cref="HttpClient"/>, so a single instance is shared across all
/// outbound senders and inbound receivers.
/// </para>
/// <para>
/// <b>HttpClient management.</b> The factory pulls a named
/// <see cref="HttpClient"/> from <see cref="IHttpClientFactory"/> rather
/// than instantiating one directly. This lets later stages add policy
/// handlers (Polly retry, telemetry, etc.) via
/// <c>services.AddHttpClient(...)</c> chaining without touching this
/// factory.
/// </para>
/// <para>
/// <b>Token source.</b> Per the Stage 2.1 brief, the bot token is read
/// from <see cref="IOptions{TOptions}"/> of <see cref="TelegramOptions"/>,
/// which in turn is bound from <c>IConfiguration</c> — so the value can
/// arrive via Azure Key Vault, environment variable, or
/// <c>dotnet user-secrets</c>. <see cref="TelegramOptionsValidator"/>
/// guarantees the token is non-empty at host startup; this factory
/// re-checks defensively to give a clear runtime error if the validator
/// is ever bypassed (for example, when callers construct the factory
/// outside of <c>AddTelegram</c>).
/// </para>
/// </remarks>
public sealed class TelegramBotClientFactory
{
    /// <summary>
    /// Logical name used when requesting the <see cref="HttpClient"/>
    /// from <see cref="IHttpClientFactory"/>. Stable so later stages can
    /// attach policy handlers via
    /// <c>services.AddHttpClient(TelegramBotClientFactory.HttpClientName)</c>.
    /// </summary>
    public const string HttpClientName = "TelegramBotClient";

    private readonly IOptions<TelegramOptions> _options;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Creates the factory.
    /// </summary>
    /// <param name="options">Validated Telegram options.</param>
    /// <param name="httpClientFactory">Named <see cref="HttpClient"/>
    /// factory.</param>
    public TelegramBotClientFactory(
        IOptions<TelegramOptions> options,
        IHttpClientFactory httpClientFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClientFactory = httpClientFactory
            ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    /// <summary>
    /// Builds the configured <see cref="ITelegramBotClient"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <see cref="TelegramOptions.BotToken"/> is null, empty, or
    /// whitespace. In normal startup this is unreachable because the
    /// options validator throws first; the check is here so that direct
    /// callers (tests, ad-hoc tooling) get a clear error rather than a
    /// confusing <c>ArgumentException</c> from inside
    /// <c>Telegram.Bot</c>.
    /// </exception>
    public ITelegramBotClient Create()
    {
        var token = _options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Cannot create ITelegramBotClient: Telegram:BotToken is not configured. "
                + "Verify that AddTelegram(IConfiguration) was called and that the token "
                + "is supplied via Key Vault, environment variable, or user-secrets.");
        }

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        return new TelegramBotClient(token, httpClient);
    }
}
