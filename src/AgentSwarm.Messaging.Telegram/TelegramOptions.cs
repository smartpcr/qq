namespace AgentSwarm.Messaging.Telegram;

using AgentSwarm.Messaging.Telegram.Sending;

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
    /// Mutually exclusive with <see cref="UsePolling"/> — validated by
    /// <see cref="TelegramOptionsValidator"/> at startup, which also
    /// enforces an absolute HTTPS scheme and rejects a webhook URL
    /// configured without a matching <see cref="SecretToken"/>.
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
    /// Per-(user, chat) operator bindings — the structural mapping
    /// required by the story brief's "Validate chat/user allowlist
    /// before accepting commands" + "Map Telegram chat ID to authorized
    /// human operator and tenant/workspace" rows. Each entry pins one
    /// (<see cref="TelegramOperatorBindingOptions.TelegramUserId"/>,
    /// <see cref="TelegramOperatorBindingOptions.TelegramChatId"/>)
    /// pair and supplies the
    /// <see cref="TelegramOperatorBindingOptions.TenantId"/> /
    /// <see cref="TelegramOperatorBindingOptions.WorkspaceId"/> the
    /// pipeline must use when emitting downstream events. Consumed by
    /// <see cref="Auth.ConfiguredOperatorAuthorizationService"/>; the
    /// validator (<see cref="TelegramOptionsValidator"/>) rejects
    /// blank tenant/workspace at startup.
    /// </summary>
    public List<TelegramOperatorBindingOptions> OperatorBindings { get; set; } = new();

    /// <summary>
    /// Dev/test seed used by
    /// <see cref="Swarm.StubOperatorRegistry"/> (Stage 2.7) to project a
    /// fixed set of <see cref="Core.OperatorBinding"/> rows without a
    /// database. Each entry is materialised into an
    /// <see cref="Core.OperatorBinding"/> with a deterministic
    /// <see cref="Core.OperatorBinding.Id"/> derived from
    /// (TenantId, WorkspaceId, TelegramUserId, TelegramChatId) so
    /// repeated reads yield a stable id usable as a
    /// <c>TaskOversight.OperatorBindingId</c> foreign key in
    /// fixture-driven acceptance tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Distinct from <see cref="OperatorBindings"/>.</b>
    /// <see cref="OperatorBindings"/> drives inbound authorization
    /// (<see cref="Auth.ConfiguredOperatorAuthorizationService"/>) —
    /// pinning which (user, chat) pairs may issue commands. The
    /// <see cref="DevOperators"/> list, by contrast, is the
    /// "directory" the Stage 2.7 swarm-event subscription service
    /// reads when resolving outbound routing in dev / unit-test /
    /// integration-test hosts. The two lists may overlap (and
    /// typically do in fixtures), but they may also legitimately
    /// diverge — e.g. a dev host wired to receive events for tenants
    /// that no human is permitted to message back into.
    /// </para>
    /// <para>
    /// Replaced in production by the Stage 3.4
    /// <c>PersistentOperatorRegistry</c> which reads from the
    /// <c>operator_bindings</c> table; the production registration
    /// supersedes <see cref="Swarm.StubOperatorRegistry"/> via the
    /// <c>TryAddSingleton</c> / <c>AddSingleton</c> last-wins
    /// pattern. Validator coverage: entries reuse
    /// <see cref="TelegramOperatorBindingOptions"/> so the existing
    /// <see cref="TelegramOptionsValidator"/> TenantId/WorkspaceId
    /// non-blank guard applies (added in Stage 2.7 — see
    /// <c>TelegramOptionsValidator.Validate</c>).
    /// </para>
    /// </remarks>
    public List<TelegramOperatorBindingOptions> DevOperators { get; set; } = new();

    /// <summary>
    /// Shared secret echoed by Telegram in the
    /// <c>X-Telegram-Bot-Api-Secret-Token</c> header. Validated by
    /// <c>TelegramWebhookSecretFilter</c> in Stage 2.4. Also a secret —
    /// redacted by <see cref="ToString"/>.
    /// </summary>
    public string? SecretToken { get; set; }

    /// <summary>
    /// Long-poll timeout (seconds) for the Stage 2.5
    /// <c>TelegramPollingService</c>. Telegram caps the server-side limit
    /// at 50 seconds; values must be in <c>[1, 50]</c>. Defaults to 30
    /// (the de-facto industry default that balances responsiveness
    /// against open-connection budget). Ignored unless
    /// <see cref="UsePolling"/> is <c>true</c>.
    /// </summary>
    public int PollingTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Dual-layer token-bucket rate-limit configuration consumed by
    /// <see cref="Sending.TokenBucketTelegramRateLimiter"/> and the
    /// Stage 2.3 <see cref="Sending.TelegramMessageSender"/>. Bound from
    /// the <c>Telegram:RateLimits</c> sub-section; defaults match
    /// architecture.md §10.4. Never null — the <c>= new()</c> initialiser
    /// guarantees the limiter can be constructed even when the section
    /// is omitted from configuration.
    /// </summary>
    public RateLimitOptions RateLimits { get; set; } = new();

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
             + $"SecretToken = {(string.IsNullOrEmpty(SecretToken) ? NotSetMarker : RedactedMarker)}, "
             + $"PollingTimeoutSeconds = {PollingTimeoutSeconds}"
             + " }";
    }
}
