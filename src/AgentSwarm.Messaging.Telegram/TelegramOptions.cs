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
    /// Stage 3.4 (iter-5 evaluator item 2) — fail-closed policy for an
    /// empty <see cref="AllowedUserIds"/> on the Tier-1 <c>/start</c>
    /// onboarding gate. The brief says onboarding "checks the
    /// allowlist first" — interpreted strictly that means an empty
    /// allowlist must DENY everyone (fail-closed). Defaults to
    /// <c>true</c> so production deployments that forget to populate
    /// the allowlist do not silently authorise every Telegram user
    /// who DMs the bot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set to <c>false</c> in dev / integration-test fixtures that
    /// rely on the prior open-by-default behaviour (and that pin
    /// the actual authorisation gate via
    /// <see cref="UserTenantMappings"/> presence — the onboarding
    /// path still denies if no mapping exists). The matrix:
    /// </para>
    /// <list type="table">
    ///   <listheader>
    ///     <term><see cref="RequireAllowlistForOnboarding"/></term>
    ///     <term><see cref="AllowedUserIds"/> populated?</term>
    ///     <term><c>/start</c> by user not in list</term>
    ///   </listheader>
    ///   <item>
    ///     <description><c>true</c> (default, production)</description>
    ///     <description>yes</description>
    ///     <description>deny</description>
    ///   </item>
    ///   <item>
    ///     <description><c>true</c> (default, production)</description>
    ///     <description>no (empty)</description>
    ///     <description>deny (FAIL-CLOSED)</description>
    ///   </item>
    ///   <item>
    ///     <description><c>false</c> (dev opt-in)</description>
    ///     <description>yes</description>
    ///     <description>deny</description>
    ///   </item>
    ///   <item>
    ///     <description><c>false</c> (dev opt-in)</description>
    ///     <description>no (empty)</description>
    ///     <description>allow through to <see cref="UserTenantMappings"/> lookup</description>
    ///   </item>
    /// </list>
    /// </remarks>
    public bool RequireAllowlistForOnboarding { get; set; } = true;

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
    /// Stage 3.4 — onboarding directory consumed by
    /// <see cref="Auth.TelegramUserAuthorizationService"/> on
    /// <c>/start</c>. Each entry maps a Telegram user id (key, as a
    /// string because JSON object keys are always strings) to an
    /// array of <see cref="TelegramUserTenantMapping"/> rows — one
    /// per workspace the operator participates in. The canonical
    /// shape is fixed by architecture.md §7.1 (lines 1042–1065):
    /// each user id key maps to a JSON ARRAY (single-workspace
    /// operators have a one-element array, multi-workspace
    /// operators have multiple elements). On <c>/start</c>, the
    /// authorization service builds an
    /// <see cref="Core.OperatorRegistration"/> from each array
    /// entry and submits the full batch via
    /// <see cref="Core.IOperatorRegistry.RegisterManyAsync"/>
    /// (Stage 3.4 iter-3 atomic upsert — every binding either
    /// commits together or is rolled back together so a
    /// <c>(OperatorAlias, TenantId)</c> unique-index collision on
    /// row N cannot leave rows 1..N-1 partially persisted). Each
    /// successful registration produces one
    /// <see cref="Core.OperatorBinding"/> row; subsequent commands
    /// trigger workspace disambiguation when multiple bindings
    /// exist for the same (user, chat) pair (architecture.md §4.3).
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
    /// <b>Distinct from <see cref="OperatorBindings"/>.</b>
    /// <see cref="OperatorBindings"/> drives the iter-5 binding-aware
    /// runtime authorization (<see cref="Auth.ConfiguredOperatorAuthorizationService"/>);
    /// <see cref="UserTenantMappings"/> drives the Stage 3.4
    /// onboarding flow. The two are kept separate because the
    /// onboarding source of truth (Tier 1 — who CAN onboard) is
    /// configuration, and the runtime source of truth (Tier 2 —
    /// what bindings DO exist) is the persistent
    /// <see cref="Core.IOperatorRegistry"/>.
    /// </para>
    /// </remarks>
    public Dictionary<string, List<TelegramUserTenantMapping>> UserTenantMappings { get; set; } = new();

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
    /// <remarks>
    /// Includes the <see cref="OperatorBindings"/> count and the
    /// <see cref="RateLimits"/> envelope (GlobalPerSecond +
    /// PerChatPerMinute) so the host startup log shows the binding-count
    /// + rate-limit envelope an on-call operator needs to validate
    /// against the current SLO. Counts only — the binding entries
    /// themselves are NOT echoed because they contain operator
    /// chat IDs which, while not credentials, should not be exposed
    /// to every log sink.
    /// </remarks>
    public override string ToString()
    {
        var allowedCount = AllowedUserIds is null ? 0 : AllowedUserIds.Count;
        var bindingCount = OperatorBindings is null ? 0 : OperatorBindings.Count;
        var rl = RateLimits ?? new RateLimitOptions();
        return "TelegramOptions { "
             + $"BotToken = {(string.IsNullOrEmpty(BotToken) ? NotSetMarker : RedactedMarker)}, "
             + $"WebhookUrl = {(string.IsNullOrEmpty(WebhookUrl) ? NotSetMarker : WebhookUrl)}, "
             + $"UsePolling = {UsePolling}, "
             + $"AllowedUserIds = [{allowedCount} ids], "
             + $"OperatorBindings = [{bindingCount} bindings], "
             + $"SecretToken = {(string.IsNullOrEmpty(SecretToken) ? NotSetMarker : RedactedMarker)}, "
             + $"PollingTimeoutSeconds = {PollingTimeoutSeconds}, "
             + $"RateLimits = {{ GlobalPerSecond = {rl.GlobalPerSecond}, GlobalBurstCapacity = {rl.GlobalBurstCapacity}, PerChatPerMinute = {rl.PerChatPerMinute}, PerChatBurstCapacity = {rl.PerChatBurstCapacity} }}"
             + " }";
    }
}
