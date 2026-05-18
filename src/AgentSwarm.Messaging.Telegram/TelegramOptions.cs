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
/// variables, or .NET user-secrets -- never from a committed
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
    /// Sourced from Key Vault, environment variable, or user-secrets --
    /// never committed to source control. Never logged: see
    /// <see cref="ToString"/>.
    /// </summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Public HTTPS URL Telegram POSTs updates to (production mode).
    /// Mutually exclusive with <see cref="UsePolling"/> -- validated by
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
    /// <c>/start</c>. <b>Stage 5.2 single source of truth for
    /// onboarding authorization</b> per the implementation-plan brief:
    /// when this list is empty OR does not contain the inbound user,
    /// <c>/start</c> is rejected and no <see cref="Core.OperatorBinding"/>
    /// is created. There is no escape hatch -- a production deployment
    /// that forgets to populate <c>AllowedUserIds</c> rejects every
    /// onboarding attempt (fail-closed by construction). Tier-2
    /// authorization (every command other than <c>/start</c>) is
    /// binding-based via the persistent
    /// <see cref="Core.IOperatorRegistry"/>. Concrete
    /// <see cref="List{T}"/> so <c>IConfiguration</c> binder population
    /// is predictable; callers who need <c>O(1)</c> membership checks
    /// copy into a <see cref="HashSet{T}"/> downstream.
    /// </summary>
    public List<long> AllowedUserIds { get; set; } = new();

    /// <summary>
    /// <b>[Obsolete -- Stage 5.2 retirement.]</b> Per-(user, chat)
    /// static binding directory previously consumed by the iter-5
    /// <c>ConfiguredOperatorAuthorizationService</c> (deleted in
    /// Stage 5.2 iter-4). Retained as a config-shape no-op so existing
    /// <c>appsettings.json</c> / Key Vault deployments that still ship
    /// an empty <c>Telegram:OperatorBindings</c> array do not break at
    /// startup; the field has NO behavioural consumer in production.
    /// The runtime source of truth for operator bindings is now
    /// <see cref="Core.IOperatorRegistry.GetBindingsAsync"/> (backed by
    /// the persistent <c>operator_bindings</c> table); onboarding
    /// directory is <see cref="UserTenantMappings"/> consumed by
    /// <see cref="Auth.TelegramUserAuthorizationService"/>.
    /// <see cref="TelegramOptionsValidator"/> still validates the
    /// shape (blank TenantId/WorkspaceId rejected) so a stray entry is
    /// reported at startup rather than silently ignored.
    /// </summary>
    [Obsolete(
        "Stage 5.2 retired ConfiguredOperatorAuthorizationService. " +
        "Runtime bindings now live in the persistent IOperatorRegistry; " +
        "configure onboarding via Telegram:UserTenantMappings instead. " +
        "This property is a config-shape no-op retained for backwards compatibility.",
        error: false)]
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
    /// <b>Distinct from the obsolete <see cref="OperatorBindings"/>.</b>
    /// <see cref="OperatorBindings"/> was the iter-5 in-memory
    /// authorization directory (consumed by the now-deleted
    /// <c>ConfiguredOperatorAuthorizationService</c> -- see Stage 5.2
    /// iter-4 retirement) and is retained only as a config-shape
    /// no-op for backwards compatibility. The
    /// <see cref="DevOperators"/> list, by contrast, is the
    /// "directory" the Stage 2.7 swarm-event subscription service
    /// reads when resolving outbound routing in dev / unit-test /
    /// integration-test hosts. Production runtime bindings live in
    /// the persistent <see cref="Core.IOperatorRegistry"/>; this
    /// list is the dev-time stand-in.
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
    /// non-blank guard applies (added in Stage 2.7 -- see
    /// <c>TelegramOptionsValidator.Validate</c>).
    /// </para>
    /// </remarks>
    public List<TelegramOperatorBindingOptions> DevOperators { get; set; } = new();

    /// <summary>
    /// Stage 3.4 -- onboarding directory consumed by
    /// <see cref="Auth.TelegramUserAuthorizationService"/> on
    /// <c>/start</c>. Each entry maps a Telegram user id (key, as a
    /// string because JSON object keys are always strings) to an
    /// array of <see cref="TelegramUserTenantMapping"/> rows -- one
    /// per workspace the operator participates in. The canonical
    /// shape is fixed by architecture.md section 7.1 (lines 1042-1065):
    /// each user id key maps to a JSON ARRAY (single-workspace
    /// operators have a one-element array, multi-workspace
    /// operators have multiple elements). On <c>/start</c>, the
    /// authorization service builds an
    /// <see cref="Core.OperatorRegistration"/> from each array
    /// entry and submits the full batch via
    /// <see cref="Core.IOperatorRegistry.RegisterManyAsync"/>
    /// (Stage 3.4 iter-3 atomic upsert -- every binding either
    /// commits together or is rolled back together so a
    /// <c>(OperatorAlias, TenantId)</c> unique-index collision on
    /// row N cannot leave rows 1..N-1 partially persisted). Each
    /// successful registration produces one
    /// <see cref="Core.OperatorBinding"/> row; subsequent commands
    /// trigger workspace disambiguation when multiple bindings
    /// exist for the same (user, chat) pair (architecture.md section 4.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a dictionary keyed on the Telegram user id (and not a
    /// flat list).</b> The <c>/start</c> handler's lookup key is the
    /// Telegram user id only (the chat id only becomes known when
    /// the <c>/start</c> Update is received), so a dictionary lookup
    /// is the natural shape; a flat list would require an O(N) scan
    /// per <c>/start</c>. The key is a <see cref="string"/> rather
    /// than a <see cref="long"/> because <c>IConfiguration</c> binds
    /// JSON object keys as strings and the binder for
    /// <c>Dictionary&lt;long, T&gt;</c> would silently drop keys
    /// containing non-numeric characters; surfacing the key as a
    /// string lets the authorization service validate the format
    /// once at <c>/start</c> time with a clear error.
    /// </para>
    /// <para>
    /// <b>Distinct from the obsolete <see cref="OperatorBindings"/>.</b>
    /// <see cref="OperatorBindings"/> was the iter-5 in-memory
    /// binding directory consumed by the now-retired
    /// <c>ConfiguredOperatorAuthorizationService</c> (Stage 5.2 iter-4
    /// retirement). <see cref="UserTenantMappings"/>, by contrast, is
    /// the Stage 3.4 onboarding directory consumed by
    /// <see cref="Auth.TelegramUserAuthorizationService"/>. The
    /// onboarding source of truth (Tier 1 -- who CAN onboard) is
    /// configuration; the runtime source of truth (Tier 2 -- what
    /// bindings DO exist) is the persistent
    /// <see cref="Core.IOperatorRegistry"/>.
    /// </para>
    /// </remarks>
    public Dictionary<string, List<TelegramUserTenantMapping>> UserTenantMappings { get; set; } = new();

    /// <summary>
    /// Shared secret echoed by Telegram in the
    /// <c>X-Telegram-Bot-Api-Secret-Token</c> header. Validated by
    /// <c>TelegramWebhookSecretFilter</c> in Stage 2.4. Also a secret --
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
    /// architecture.md section 10.4. Never null -- the <c>= new()</c> initialiser
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
