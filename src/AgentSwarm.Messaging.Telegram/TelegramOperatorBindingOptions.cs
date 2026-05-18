namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Configuration POCO describing a single authorized operator binding.
/// Bound from the <c>Telegram:OperatorBindings</c> array in
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The story brief's "Security" row requires
/// validating BOTH the Telegram user id AND the chat id before
/// accepting a command, and the "Agent routing" row requires mapping
/// the chat id to an authorized human operator AND a tenant/workspace.
/// A flat <c>AllowedUserIds</c> list cannot express either constraint:
/// it does not pin which chats a given user may operate from, and it
/// cannot supply per-(user, chat) tenant/workspace metadata.
/// <see cref="TelegramOptions.OperatorBindings"/> is the structural
/// answer to both: each entry pins one (<see cref="TelegramUserId"/>,
/// <see cref="TelegramChatId"/>) pair and carries the
/// <see cref="TenantId"/> / <see cref="WorkspaceId"/> the pipeline
/// must use when emitting downstream events.
/// </para>
/// <para>
/// <b>Stage 5.2 iter-4 retirement note.</b>
/// <see cref="TelegramOptions.OperatorBindings"/> is now
/// <see cref="ObsoleteAttribute"/>: the iter-5
/// <c>ConfiguredOperatorAuthorizationService</c> that consumed it has
/// been deleted in favour of the registry-backed
/// <see cref="Auth.TelegramUserAuthorizationService"/> (Stage 3.4),
/// and runtime bindings now live in the persistent
/// <see cref="Core.IOperatorRegistry"/>. The same
/// <see cref="TelegramOperatorBindingOptions"/> shape is still used by
/// <see cref="TelegramOptions.DevOperators"/> (consumed by
/// <see cref="Swarm.StubOperatorRegistry"/>) so the type remains
/// non-obsolete.
/// </para>
/// <para>
/// <b>Configuration shape.</b> Bindings are an ordered array under
/// <c>Telegram:OperatorBindings</c>. Entries are validated for
/// non-blank tenant/workspace by
/// <see cref="TelegramOptionsValidator"/> at startup but have no
/// behavioural runtime consumer; deployments that need per-(user,
/// chat) authorization should configure the persistent
/// <see cref="Core.IOperatorRegistry"/> via
/// <c>Telegram:UserTenantMappings</c> + the Stage 3.4 onboarding
/// flow.
/// </para>
/// <example>
/// <code language="json">
/// "Telegram": {
///   "OperatorBindings": [
///     {
///       "TelegramUserId": 12345,
///       "TelegramChatId": 67890,
///       "TenantId": "acme",
///       "WorkspaceId": "swarm-prod",
///       "OperatorAlias": "alice",
///       "Roles": ["operator", "approver"]
///     }
///   ]
/// }
/// </code>
/// </example>
/// </remarks>
public sealed class TelegramOperatorBindingOptions
{
    /// <summary>
    /// Telegram user id this binding authorizes. Required; must be the
    /// numeric Telegram user id (matches <c>Update.Message.From.Id</c>
    /// at the wire level), not a screen name.
    /// </summary>
    public long TelegramUserId { get; set; }

    /// <summary>
    /// Telegram chat id this binding authorizes. Required; must be the
    /// numeric Telegram chat id (negative for groups/supergroups,
    /// positive for private chats -- Telegram convention). The
    /// authorization service rejects requests where the inbound chat
    /// id does not match a configured binding for the inbound user
    /// id, satisfying the "Validate chat/user allowlist before
    /// accepting commands" requirement.
    /// </summary>
    public long TelegramChatId { get; set; }

    /// <summary>
    /// Tenant id surfaced on the <see cref="Core.OperatorBinding"/>
    /// returned by the authorization service. Required and must be
    /// non-blank -- pipelines route work to a tenant boundary, so a
    /// blank tenant id would silently coalesce all bindings into one
    /// tenant. Validated at startup by
    /// <see cref="TelegramOptionsValidator"/>.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Workspace id surfaced on the <see cref="Core.OperatorBinding"/>
    /// returned by the authorization service. Required and must be
    /// non-blank for the same reason as <see cref="TenantId"/>.
    /// </summary>
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>
    /// Optional operator alias (display name) attached to audit
    /// records. When blank the authorization service falls back to
    /// <c>"user-{TelegramUserId}"</c> so the audit trail still
    /// identifies the operator.
    /// </summary>
    public string? OperatorAlias { get; set; }

    /// <summary>
    /// Optional list of role strings copied onto
    /// <see cref="Core.OperatorBinding.Roles"/>. The pipeline's command
    /// handlers consult these to enforce per-command authorization
    /// (e.g. <c>/approve</c> requires <c>"approver"</c>). Empty when
    /// not configured -- equivalent to "operator with no extra
    /// privileges".
    /// </summary>
    public List<string> Roles { get; set; } = new();
}
