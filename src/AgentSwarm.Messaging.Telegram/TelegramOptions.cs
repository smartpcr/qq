namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Configuration POCO bound from the <c>Telegram</c> section of
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Secret handling.</b> <see cref="BotToken"/> and
/// <see cref="SecretToken"/> are credentials. They are read from
/// <c>IConfiguration</c> so they can flow from Azure Key Vault, environment
/// variables, or .NET user-secrets — never from a committed
/// <c>appsettings.json</c>. <see cref="ToString"/> redacts both fields
/// (<c>[REDACTED]</c> / <c>[NOT SET]</c>) so that an accidental
/// <c>ILogger.LogInformation("opts: {Opts}", options.Value)</c> cannot
/// expose the bot token in log output.
/// </para>
/// <para>
/// <b>Fail-fast at startup.</b> A missing or whitespace
/// <see cref="BotToken"/> is rejected by
/// <see cref="TelegramOptionsValidator"/> at host startup
/// (<c>ValidateOnStart()</c> is wired in
/// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/>), so the
/// process throws <c>OptionsValidationException</c> instead of starting
/// up and silently failing the first Telegram API call.
/// </para>
/// </remarks>
public sealed class TelegramOptions
{
    /// <summary>
    /// Configuration section name used by
    /// <see cref="TelegramServiceCollectionExtensions.AddTelegram"/>.
    /// </summary>
    public const string SectionName = "Telegram";

    private const string RedactedMarker = "[REDACTED]";
    private const string NotSetMarker = "[NOT SET]";

    /// <summary>
    /// Telegram Bot API token issued by BotFather. Required at startup.
    /// Sourced from Key Vault, environment variable, or user-secrets —
    /// never committed to source control. Never logged: see
    /// <see cref="ToString"/>.
    /// </summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Public HTTPS URL Telegram POSTs updates to (production mode).
    /// Mutually exclusive with <see cref="UsePolling"/> — validated by a
    /// later stage's options validator (Stage 2.5).
    /// </summary>
    public string? WebhookUrl { get; set; }

    /// <summary>
    /// When <c>true</c>, the Worker uses long polling instead of a
    /// webhook. Intended for local development and CI.
    /// </summary>
    public bool UsePolling { get; set; }

    /// <summary>
    /// Tier-1 allowlist of Telegram user IDs that may invoke
    /// <c>/start</c>. Tier-2 authorization (everything else) is binding
    /// based (Stage 5.2). Concrete <see cref="List{T}"/> so
    /// <c>IConfiguration</c> binder population is predictable; callers
    /// who need <c>O(1)</c> membership checks copy into a
    /// <see cref="HashSet{T}"/> downstream.
    /// </summary>
    public List<long> AllowedUserIds { get; set; } = new();

    /// <summary>
    /// Shared secret echoed by Telegram in the
    /// <c>X-Telegram-Bot-Api-Secret-Token</c> header. Validated by
    /// <c>TelegramWebhookSecretFilter</c> in Stage 2.4. Also a secret —
    /// redacted by <see cref="ToString"/>.
    /// </summary>
    public string? SecretToken { get; set; }

    /// <summary>
    /// Returns a diagnostic representation of this options instance with
    /// <see cref="BotToken"/> and <see cref="SecretToken"/> replaced by
    /// <c>[REDACTED]</c> / <c>[NOT SET]</c>. The actual token value is
    /// never returned by this method, so logging the options object is
    /// safe.
    /// </summary>
    public override string ToString()
    {
        var allowedCount = AllowedUserIds is null ? 0 : AllowedUserIds.Count;
        return "TelegramOptions { "
             + $"BotToken = {(string.IsNullOrEmpty(BotToken) ? NotSetMarker : RedactedMarker)}, "
             + $"WebhookUrl = {(string.IsNullOrEmpty(WebhookUrl) ? NotSetMarker : WebhookUrl)}, "
             + $"UsePolling = {UsePolling}, "
             + $"AllowedUserIds = [{allowedCount} ids], "
             + $"SecretToken = {(string.IsNullOrEmpty(SecretToken) ? NotSetMarker : RedactedMarker)}"
             + " }";
    }
}
