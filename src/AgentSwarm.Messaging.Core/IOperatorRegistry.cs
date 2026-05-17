namespace AgentSwarm.Messaging.Core;

using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Registry of operator-to-workspace bindings. Used by the
/// authorization service, the <c>/handoff</c> alias resolver, alert
/// fallback routing, runtime allowlist checks, and the Stage 2.7
/// swarm-event subscription service (which enumerates active tenants
/// and per-tenant bindings for routing). See architecture.md §4.3.
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
    /// Atomically register a batch of <see cref="OperatorRegistration"/>
    /// rows under a single persistence transaction. Used by the
    /// <c>/start</c> onboarding flow when an operator's
    /// <c>Telegram:UserTenantMappings</c> entry has multiple workspace
    /// bindings: if ANY row fails (unique-index violation, transient
    /// DB error), ALL prior inserts in the batch MUST roll back so the
    /// operator is never left in a partial onboarding state.
    /// </summary>
    /// <remarks>
    /// Added in Stage 3.4 iter-3 (evaluator item 2). The default
    /// implementation iterates <see cref="RegisterAsync"/> and is
    /// suitable for the in-memory <c>StubOperatorRegistry</c> (where
    /// rollback semantics do not apply because every call is
    /// independent). The persistent EF Core implementation
    /// (<c>PersistentOperatorRegistry</c>) overrides this to wrap
    /// all inserts in a single <c>IDbContextTransaction</c>.
    /// </remarks>
    async Task RegisterManyAsync(
        IReadOnlyList<OperatorRegistration> registrations,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        for (var i = 0; i < registrations.Count; i++)
        {
            await RegisterAsync(registrations[i], ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fast-path check: returns <c>true</c> when at least one active
    /// binding exists for the (user, chat) pair. Used by the runtime
    /// allowlist gate.
    /// </summary>
    Task<bool> IsAuthorizedAsync(long telegramUserId, long chatId, CancellationToken ct);

    /// <summary>
    /// All distinct tenant ids that currently host at least one active
    /// <see cref="OperatorBinding"/>. Used by the Stage 2.7
    /// <c>SwarmEventSubscriptionService</c> to enumerate the tenants it
    /// must call <see cref="ISwarmCommandBus.SubscribeAsync(string, CancellationToken)"/>
    /// against at startup (per implementation-plan.md Stage 2.7
    /// "resolve active tenant IDs from <c>IOperatorRegistry</c>").
    /// </summary>
    /// <remarks>
    /// Added in Stage 2.7. Originally <see cref="IOperatorRegistry"/>
    /// declared six methods per architecture.md §4.3 (Stage 1.3 surface).
    /// The Swarm Event Ingress Service requires a way to discover the
    /// active tenant boundary BEFORE any inbound event has been received,
    /// so this enumeration is hung on the registry alongside the
    /// per-user/per-workspace queries rather than introduced as a
    /// separate side abstraction.
    /// </remarks>
    Task<IReadOnlyList<string>> GetActiveTenantsAsync(CancellationToken ct);

    /// <summary>
    /// All active bindings within a tenant boundary, across every
    /// workspace and chat. Used by the Stage 2.7
    /// <c>SwarmEventSubscriptionService</c> when an
    /// <see cref="AgentStatusUpdateEvent"/> has no
    /// <c>ITaskOversightRepository</c> assignment — the service then
    /// broadcasts the status update to every active operator within
    /// the tenant being processed (per implementation-plan.md Stage 2.7
    /// "broadcast to all active operators").
    /// </summary>
    /// <remarks>
    /// Added in Stage 2.7 alongside <see cref="GetActiveTenantsAsync"/>.
    /// Tenant-scoped (not workspace-scoped) because
    /// <see cref="AgentStatusUpdateEvent"/> does not carry a
    /// <c>WorkspaceId</c> — the only routing scope available at
    /// broadcast time is the tenant the subscription stream belongs to.
    /// </remarks>
    Task<IReadOnlyList<OperatorBinding>> GetByTenantAsync(string tenantId, CancellationToken ct);
}
