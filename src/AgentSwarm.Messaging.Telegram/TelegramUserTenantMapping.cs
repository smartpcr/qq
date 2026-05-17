namespace AgentSwarm.Messaging.Telegram;

/// <summary>
/// Stage 3.4 — single workspace entry under
/// <see cref="TelegramOptions.UserTenantMappings"/>. Carries the
/// fields required to construct an
/// <see cref="AgentSwarm.Messaging.Core.OperatorRegistration"/>
/// (and ultimately an <see cref="AgentSwarm.Messaging.Core.OperatorBinding"/>)
/// at <c>/start</c> time.
/// </summary>
/// <remarks>
/// <para>
/// Bound from the array under each Telegram user-id key in
/// <c>Telegram:UserTenantMappings</c> per architecture.md §7.1
/// (lines 1042–1065). The canonical JSON shape is:
/// </para>
/// <example>
/// <code language="json">
/// "Telegram": {
///   "UserTenantMappings": {
///     "12345": [
///       { "TenantId": "acme", "WorkspaceId": "factory-1", "Roles": ["Operator", "Approver"], "OperatorAlias": "@alice" }
///     ],
///     "67890": [
///       { "TenantId": "acme", "WorkspaceId": "factory-2", "Roles": ["Operator"], "OperatorAlias": "@bob" },
///       { "TenantId": "acme", "WorkspaceId": "factory-3", "Roles": ["Operator"], "OperatorAlias": "@bob-f3" }
///     ]
///   }
/// }
/// </code>
/// </example>
/// <para>
/// Most operators have a single workspace entry; operators overseeing
/// multiple workspaces have one entry per workspace. The
/// <see cref="Auth.TelegramUserAuthorizationService"/> builds an
/// <see cref="AgentSwarm.Messaging.Core.OperatorRegistration"/>
/// from each array entry on <c>/start</c> and submits the full
/// batch via
/// <see cref="AgentSwarm.Messaging.Core.IOperatorRegistry.RegisterManyAsync"/>
/// (Stage 3.4 iter-3 atomic upsert — the whole batch commits in
/// one transaction or rolls back together, so a
/// <c>(OperatorAlias, TenantId)</c> unique-index collision on row N
/// cannot leave rows 1..N-1 partially persisted). Each successful
/// registration produces one
/// <see cref="AgentSwarm.Messaging.Core.OperatorBinding"/> row.
/// Subsequent commands trigger workspace disambiguation via inline
/// keyboard when multiple bindings exist for the same (user, chat)
/// pair (per architecture.md §4.3).
/// </para>
/// <para>
/// <b>Distinct from <see cref="TelegramOperatorBindingOptions"/>.</b>
/// <see cref="TelegramOperatorBindingOptions"/> pins a static
/// (user, chat) → tenant/workspace authorization (the iter-5 binding-
/// aware authz model used by
/// <see cref="Auth.ConfiguredOperatorAuthorizationService"/>).
/// <see cref="TelegramUserTenantMapping"/>, by contrast, is the
/// <b>onboarding directory</b> consulted only at <c>/start</c> time
/// to source the tenant/workspace/roles/alias for a new
/// <see cref="AgentSwarm.Messaging.Core.OperatorBinding"/>. The two
/// configuration shapes intentionally do not share a class because
/// the two flows have different lookup keys: <c>OperatorBindings</c>
/// is indexed by <c>(userId, chatId)</c> for hot-path runtime
/// authorization; <c>UserTenantMappings</c> is indexed by
/// <c>userId</c> only because the chat id only becomes known at
/// <c>/start</c> time.
/// </para>
/// </remarks>
public sealed class TelegramUserTenantMapping
{
    /// <summary>
    /// Tenant id assigned to the operator's
    /// <see cref="AgentSwarm.Messaging.Core.OperatorBinding"/> on
    /// <c>/start</c>. Required.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Workspace id assigned to the operator's
    /// <see cref="AgentSwarm.Messaging.Core.OperatorBinding"/> on
    /// <c>/start</c>. Required.
    /// </summary>
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>
    /// Roles attached to the resulting
    /// <see cref="AgentSwarm.Messaging.Core.OperatorBinding.Roles"/>.
    /// Empty list permitted ("operator with no extra privileges").
    /// </summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>
    /// Operator handle (e.g. <c>@alice</c>) attached to the
    /// resulting
    /// <see cref="AgentSwarm.Messaging.Core.OperatorBinding.OperatorAlias"/>.
    /// Used by <c>/handoff @alias</c> resolution within the tenant.
    /// </summary>
    public string OperatorAlias { get; set; } = string.Empty;
}
