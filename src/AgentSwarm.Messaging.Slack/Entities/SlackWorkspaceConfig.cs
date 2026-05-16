namespace AgentSwarm.Messaging.Slack.Entities;

/// <summary>
/// Persisted configuration for a single registered Slack workspace. One row
/// per workspace, with <see cref="TeamId"/> as the primary key.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>. Field
/// list is the canonical surface specified by architecture.md section 3.1.
/// </para>
/// <para>
/// Secrets are NEVER stored on this row. <see cref="BotTokenSecretRef"/>,
/// <see cref="SigningSecretRef"/>, and <see cref="AppLevelTokenRef"/> hold
/// secret-provider URIs (e.g., <c>keyvault://slack-bot-token</c>) that the
/// runtime secret provider (Azure Key Vault, Kubernetes secret, or
/// DPAPI-protected local store) resolves at use time.
/// </para>
/// <para>
/// Properties use <c>{ get; set; }</c> to support EF Core hydration. EF
/// configuration (column types, value converters for the string arrays,
/// indexes) is added by Stage 2.2 in a separate
/// <c>IEntityTypeConfiguration&lt;SlackWorkspaceConfig&gt;</c> class.
/// </para>
/// </remarks>
public sealed class SlackWorkspaceConfig
{
    /// <summary>
    /// Slack workspace identifier (for example <c>T0123ABCD</c>). Primary key.
    /// </summary>
    public string TeamId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable workspace name (display only, not used for routing).
    /// </summary>
    public string WorkspaceName { get; set; } = string.Empty;

    /// <summary>
    /// Secret-provider URI for the bot OAuth token used by the Slack
    /// Web API client.
    /// </summary>
    public string BotTokenSecretRef { get; set; } = string.Empty;

    /// <summary>
    /// Secret-provider URI for the signing secret used by
    /// <c>SlackSignatureValidator</c> to verify inbound request signatures.
    /// </summary>
    public string SigningSecretRef { get; set; } = string.Empty;

    /// <summary>
    /// Secret-provider URI for the Socket Mode app-level token, or
    /// <c>null</c> when this workspace runs in Events API mode.
    /// </summary>
    public string? AppLevelTokenRef { get; set; }

    /// <summary>
    /// Channel into which new agent task threads are posted by default.
    /// </summary>
    public string DefaultChannelId { get; set; } = string.Empty;

    /// <summary>
    /// Channel used when <see cref="DefaultChannelId"/> is unavailable, or
    /// <c>null</c> to disable failover.
    /// </summary>
    public string? FallbackChannelId { get; set; }

    /// <summary>
    /// Channels from which slash commands and interactions are accepted.
    /// An empty collection rejects every channel; defaults to
    /// <see cref="Array.Empty{T}"/> so the entity is non-null on insert.
    /// </summary>
    public string[] AllowedChannelIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Slack user-group IDs whose members are authorized to issue commands.
    /// An empty collection rejects every user; defaults to
    /// <see cref="Array.Empty{T}"/>.
    /// </summary>
    public string[] AllowedUserGroupIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Whether this workspace is currently active. When <c>false</c> the
    /// connector ignores inbound traffic and skips outbound dispatch.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// UTC timestamp of row creation.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// UTC timestamp of the most recent modification to this row.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
