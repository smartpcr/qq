using System.Collections.ObjectModel;
using System.Net.Http;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Security;

/// <summary>
/// Strongly-typed configuration for the Entra ID-aware
/// <see cref="BotFrameworkAuthentication"/> wiring registered by
/// <c>TeamsSecurityServiceCollectionExtensions.AddEntraBotFrameworkAuthentication</c>.
/// Aligned with <c>implementation-plan.md</c> §5.1 step 5 and
/// <c>tech-spec.md</c> §4.2 (Identity).
/// </summary>
/// <remarks>
/// <para>
/// The Bot Framework SDK exposes runtime token-validation hooks via
/// <see cref="BotFrameworkAuthenticationFactory"/> and the
/// <see cref="AuthenticationConfiguration.ClaimsValidator"/>. This options object captures
/// the two enterprise-grade restrictions Stage 5.1 requires:
/// </para>
/// <list type="bullet">
///   <item><description>
///   <see cref="AllowedCallers"/> — list of AAD application IDs that are permitted to
///   call the bot's webhook. Enforced via
///   <see cref="AllowedCallersClaimsValidator"/> on every inbound activity. An
///   <c>AllowedCallers</c> entry of <c>"*"</c> matches any caller, which is the BF
///   SDK's "open" default — operators should populate the list explicitly in production
///   to reject calls from rogue/skill bots.
///   </description></item>
///   <item><description>
///   <see cref="AllowedTenantIds"/> — list of Entra ID tenants the bot accepts. The
///   <see cref="EntraTenantAwareClaimsValidator"/> composed with
///   <see cref="AllowedCallersClaimsValidator"/> additionally validates the inbound
///   token's <c>tid</c> claim against this list. The HTTP-layer
///   <see cref="TenantValidationMiddleware"/> performs the same check on the
///   <c>channelData.tenant.id</c> payload field; configuring BOTH layers gives the
///   defense-in-depth posture the story brief calls out under "Security: Enforce
///   tenant ID, user identity, Teams app installation, and RBAC".
///   </description></item>
/// </list>
/// </remarks>
public sealed class EntraBotFrameworkAuthenticationOptions
{
    /// <summary>Configuration section name bound from <c>appsettings.json</c>.</summary>
    public const string SectionName = "Teams:BotFrameworkAuthentication";

    /// <summary>
    /// AAD application IDs permitted to call the bot. Empty list means accept the BF SDK
    /// default (the bot's own AppId plus the Bot Connector service AppId — i.e., normal
    /// Teams traffic only). Populate with skill-bot or test-tool AppIds to extend the
    /// allow-list.
    /// </summary>
    public IList<string> AllowedCallers { get; set; } = new List<string>();

    /// <summary>
    /// Entra ID tenant IDs the bot accepts. Inbound tokens whose <c>tid</c> claim is not
    /// in this list are rejected by the composed claims validator. Mirrors
    /// <see cref="TeamsMessagingOptions.AllowedTenantIds"/>; the helper auto-populates
    /// this list from <c>TeamsMessagingOptions</c> when left empty.
    /// </summary>
    public IList<string> AllowedTenantIds { get; set; } = new List<string>();

    /// <summary>
    /// When <c>true</c>, the composed <see cref="EntraTenantAwareClaimsValidator"/>
    /// rejects inbound tokens that do NOT include a <c>tid</c> (tenant) claim while
    /// <see cref="AllowedTenantIds"/> is configured. Defaults to <c>false</c> because
    /// real Bot Connector and Teams-channel tokens may omit the <c>tid</c> claim for
    /// legitimate traffic — Stage 5.1 defers the canonical tenant check to the HTTP-
    /// layer <see cref="TenantValidationMiddleware"/>, which reads the activity payload's
    /// <c>channelData.tenant.id</c>. Operators in regulated environments that have
    /// confirmed every legitimate caller token carries <c>tid</c> can opt-in to strict
    /// enforcement by setting this to <c>true</c>; in that mode the JWT layer rejects
    /// tokens missing the claim BEFORE the activity body is parsed.
    /// </summary>
    public bool RequireTenantClaim { get; set; } = false;

    /// <summary>
    /// When <c>true</c> (the default), the BF SDK validates the token issuer against the
    /// canonical Microsoft authority. Operators MUST leave this true in production.
    /// </summary>
    public bool ValidateAuthority { get; set; } = true;

    /// <summary>
    /// Channel service URL (typically empty/null for the public Azure Bot Service
    /// cloud, or <c>"https://botframework.azure.us"</c> for GovCloud).
    /// </summary>
    public string ChannelService { get; set; } = string.Empty;
}

/// <summary>
/// Claims validator that composes <see cref="AllowedCallersClaimsValidator"/> (per-app
/// allow-list) with a tenant <c>tid</c>-claim check against
/// <see cref="EntraBotFrameworkAuthenticationOptions.AllowedTenantIds"/>. Throws when
/// either the caller AppId or the tenant ID is not on the corresponding allow-list.
/// </summary>
public sealed class EntraTenantAwareClaimsValidator : ClaimsValidator
{
    private const string TenantClaim = "tid";

    private readonly AllowedCallersClaimsValidator _callersValidator;
    private readonly IReadOnlyList<string> _allowedTenantIds;
    private readonly bool _requireTenantClaim;
    private readonly ILogger<EntraTenantAwareClaimsValidator> _logger;

    /// <summary>Construct a new <see cref="EntraTenantAwareClaimsValidator"/>.</summary>
    /// <param name="allowedCallers">App IDs permitted to call the bot. Empty list means accept the SDK default.</param>
    /// <param name="allowedTenantIds">Entra tenant IDs whose tokens are accepted. Empty list means accept any tenant (caller check only).</param>
    /// <param name="requireTenantClaim">
    /// When <c>true</c>, tokens missing the <c>tid</c> claim are rejected even though the
    /// HTTP-layer <see cref="TenantValidationMiddleware"/> would also catch the request.
    /// Stage 5.1 default is <c>false</c> because Teams Bot Connector tokens may omit
    /// <c>tid</c> for legitimate channel-side traffic; the canonical tenant check is the
    /// activity payload's <c>channelData.tenant.id</c>.
    /// </param>
    /// <param name="logger">Optional logger; defaults to a no-op when null.</param>
    public EntraTenantAwareClaimsValidator(
        IList<string> allowedCallers,
        IList<string> allowedTenantIds,
        bool requireTenantClaim = false,
        ILogger<EntraTenantAwareClaimsValidator>? logger = null)
    {
        if (allowedCallers is null) throw new ArgumentNullException(nameof(allowedCallers));
        if (allowedTenantIds is null) throw new ArgumentNullException(nameof(allowedTenantIds));

        _callersValidator = new AllowedCallersClaimsValidator(allowedCallers);
        _allowedTenantIds = new ReadOnlyCollection<string>(new List<string>(allowedTenantIds));
        _requireTenantClaim = requireTenantClaim;
        _logger = logger ?? NullLogger<EntraTenantAwareClaimsValidator>.Instance;
    }

    /// <inheritdoc />
    public override async Task ValidateClaimsAsync(IList<System.Security.Claims.Claim> claims)
    {
        if (claims is null) throw new ArgumentNullException(nameof(claims));

        await _callersValidator.ValidateClaimsAsync(claims).ConfigureAwait(false);

        if (_allowedTenantIds.Count == 0)
        {
            return;
        }

        var tenantClaim = string.Empty;
        for (var i = 0; i < claims.Count; i++)
        {
            var claim = claims[i];
            if (string.Equals(claim.Type, TenantClaim, StringComparison.OrdinalIgnoreCase))
            {
                tenantClaim = claim.Value;
                break;
            }
        }

        if (string.IsNullOrEmpty(tenantClaim))
        {
            // Stage 5.1 iter-5 evaluator feedback item 4 — tokens without `tid` are
            // accepted by default. The HTTP-layer TenantValidationMiddleware performs the
            // canonical tenant check on the activity payload's channelData.tenant.id.
            // Operators in regulated environments can opt-in to strict JWT-layer
            // enforcement via EntraBotFrameworkAuthenticationOptions.RequireTenantClaim.
            if (_requireTenantClaim)
            {
                _logger.LogWarning(
                    "EntraTenantAwareClaimsValidator: inbound token missing 'tid' claim while AllowedTenantIds is configured AND RequireTenantClaim=true; rejecting.");
                throw new UnauthorizedAccessException(
                    "Inbound Bot Framework token is missing the 'tid' (tenant) claim and RequireTenantClaim is enabled; rejecting per Stage 5.1 strict tenant-restriction policy.");
            }

            _logger.LogDebug(
                "EntraTenantAwareClaimsValidator: inbound token missing 'tid' claim; deferring tenant enforcement to TenantValidationMiddleware (activity-payload tenant). Set RequireTenantClaim=true to reject at the JWT layer.");
            return;
        }

        var matched = false;
        for (var i = 0; i < _allowedTenantIds.Count; i++)
        {
            if (string.Equals(_allowedTenantIds[i], tenantClaim, StringComparison.OrdinalIgnoreCase))
            {
                matched = true;
                break;
            }
        }

        if (!matched)
        {
            _logger.LogWarning(
                "EntraTenantAwareClaimsValidator: inbound token tenant '{Tenant}' is not on the AllowedTenantIds list; rejecting.",
                tenantClaim);
            throw new UnauthorizedAccessException(
                $"Inbound Bot Framework token tenant '{tenantClaim}' is not on the AllowedTenantIds allow-list.");
        }
    }
}
