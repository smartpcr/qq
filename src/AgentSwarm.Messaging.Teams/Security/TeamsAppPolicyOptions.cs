using System.Collections.ObjectModel;

namespace AgentSwarm.Messaging.Teams.Security;

/// <summary>
/// Strongly typed configuration for the Teams app installation policy. Aligned with
/// <c>implementation-plan.md</c> §5.1 step 7 and <c>tech-spec.md</c> §5.1 R-5.
/// </summary>
/// <remarks>
/// <para>
/// The policy options drive operator-facing deployment posture rather than runtime
/// behaviour: <see cref="RequireAdminConsent"/> and <see cref="BlockSideloading"/> are
/// declarative flags consumed by the deployment checklist (and the health check at
/// startup) to assert that the corresponding admin-consent / app-catalog policy is in
/// place. Microsoft Graph permissions are explicitly NOT required — proactive messaging
/// uses <c>BotAdapter.ContinueConversationAsync</c> with the bot's own
/// <c>MicrosoftAppId</c>, not Graph endpoints (per <c>tech-spec.md</c> §5.1 R-5).
/// </para>
/// </remarks>
public sealed class TeamsAppPolicyOptions
{
    /// <summary>Configuration section bound from <c>appsettings.json</c> in <c>Program.cs</c>.</summary>
    public const string SectionName = "Teams:AppPolicy";

    /// <summary>
    /// When <c>true</c> (the default), the deployment is required to be admin-consented in
    /// the Entra ID tenant. The <c>TeamsAppPolicyHealthCheck</c> includes this in its
    /// health summary so operators see a clear signal in service health dashboards when
    /// the production policy expects admin consent.
    /// </summary>
    public bool RequireAdminConsent { get; set; } = true;

    /// <summary>
    /// Catalog scopes from which the Teams app is permitted to be installed. The supported
    /// values mirror the Teams admin centre app setup policy:
    /// <c>"organization"</c> (app catalog) and <c>"personal"</c> (personal scope).
    /// Defaults to organization-only (the conservative production posture).
    /// </summary>
    public IList<string> AllowedAppCatalogScopes { get; set; }
        = new List<string> { OrganizationScope };

    /// <summary>
    /// When <c>true</c> (the default), uploaded / sideloaded Teams app packages are
    /// disallowed. Operators MUST configure the Teams admin center app permission policy
    /// to block "custom apps uploaded by users". The flag is asserted in the deployment
    /// checklist and surfaced in the health check description.
    /// </summary>
    public bool BlockSideloading { get; set; } = true;

    /// <summary>Canonical scope name for the org-wide Teams app catalog.</summary>
    public const string OrganizationScope = "organization";

    /// <summary>Canonical scope name for the personal app catalog.</summary>
    public const string PersonalScope = "personal";

    /// <summary>The set of canonical scope names accepted by <see cref="AllowedAppCatalogScopes"/>.</summary>
    public static IReadOnlyCollection<string> SupportedScopes { get; }
        = new ReadOnlyCollection<string>(new List<string> { OrganizationScope, PersonalScope });

    /// <summary>
    /// Returns <c>true</c> when the supplied scope is in <see cref="AllowedAppCatalogScopes"/>;
    /// case-insensitive.
    /// </summary>
    public bool IsScopeAllowed(string scope)
    {
        if (string.IsNullOrEmpty(scope) || AllowedAppCatalogScopes is null)
        {
            return false;
        }

        return AllowedAppCatalogScopes.Contains(scope, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validate the configuration at startup. Returns the collection of validation errors;
    /// an empty list means the options are well-formed. Used by
    /// <c>TeamsAppPolicyHealthCheck</c> and the operator-facing
    /// <see cref="Microsoft.Extensions.Options.IValidateOptions{TOptions}"/> registration.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (AllowedAppCatalogScopes is null || AllowedAppCatalogScopes.Count == 0)
        {
            errors.Add($"{nameof(AllowedAppCatalogScopes)} must contain at least one scope " +
                       $"({string.Join("/", SupportedScopes)}).");
        }
        else
        {
            foreach (var scope in AllowedAppCatalogScopes)
            {
                if (!SupportedScopes.Contains(scope, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"{nameof(AllowedAppCatalogScopes)} contains unsupported scope " +
                               $"'{scope}'. Supported scopes: [{string.Join(", ", SupportedScopes)}].");
                }
            }
        }

        return errors;
    }
}
