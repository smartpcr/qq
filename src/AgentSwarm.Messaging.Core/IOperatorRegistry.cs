namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Six-method registry of operator-to-workspace bindings. Used by the
/// authorization service, the <c>/handoff</c> alias resolver, alert
/// fallback routing, and runtime allowlist checks. See architecture.md §4.3.
/// </summary>
public interface IOperatorRegistry
{
    /// <summary>
    /// Returns <b>all</b> active bindings matching the (user, chat) pair —
    /// one per workspace. Callers handle cardinality: zero → unauthorized;
    /// one → use directly; multiple → prompt the operator for workspace
    /// disambiguation.
    /// </summary>
    Task<IReadOnlyList<OperatorBinding>> GetBindingsAsync(
        long telegramUserId,
        long chatId,
        CancellationToken ct);

    /// <summary>
    /// All bindings for the user across every chat. Used by administrative
    /// queries and <c>/status</c> across workspaces.
    /// </summary>
    Task<IReadOnlyList<OperatorBinding>> GetAllBindingsAsync(
        long telegramUserId,
        CancellationToken ct);

    /// <summary>
    /// Tenant-scoped alias resolution backed by the
    /// <c>UNIQUE (OperatorAlias, TenantId)</c> constraint — prevents
    /// cross-tenant mis-resolution from <c>/handoff @alias</c>.
    /// </summary>
    Task<OperatorBinding?> GetByAliasAsync(
        string operatorAlias,
        string tenantId,
        CancellationToken ct);

    /// <summary>
    /// All active bindings for a workspace. Used by alert fallback routing
    /// (§5.6) when no <c>TaskOversight</c> row exists for the alert's
    /// <c>TaskId</c> — the first active binding receives the alert.
    /// </summary>
    Task<IReadOnlyList<OperatorBinding>> GetByWorkspaceAsync(
        string workspaceId,
        CancellationToken ct);

    /// <summary>
    /// Create a new <see cref="OperatorBinding"/> row from the supplied
    /// <see cref="OperatorRegistration"/>. Called by the <c>/start</c>
    /// onboarding flow.
    /// </summary>
    Task RegisterAsync(OperatorRegistration registration, CancellationToken ct);

    /// <summary>
    /// Fast-path check: returns <c>true</c> when at least one active
    /// binding exists for the (user, chat) pair. Used by the runtime
    /// allowlist gate.
    /// </summary>
    Task<bool> IsAuthorizedAsync(long telegramUserId, long chatId, CancellationToken ct);
}
