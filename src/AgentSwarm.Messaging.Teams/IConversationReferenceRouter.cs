namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Narrow companion lookup contract for resolving a stored
/// <see cref="TeamsConversationReference"/> from a Bot Framework
/// <c>ConversationId</c> alone. Co-located in the Teams project rather than added to
/// <see cref="IConversationReferenceStore"/> so the canonical store interface (defined in
/// <c>implementation-plan.md</c> §2.1 and <c>architecture.md</c> §4.2) is not widened beyond
/// its documented surface — that contract intentionally exposes only natural-key lookups
/// (<c>(TenantId, AadObjectId)</c>, <c>(TenantId, InternalUserId)</c>,
/// <c>(TenantId, ChannelId)</c>) plus the GUID primary-key getter, none of which are
/// reachable from a bare <c>MessengerMessage</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a separate interface:</b> <c>MessengerMessage</c> (defined in Stage 1.1)
/// carries no tenant or user identity — its only routing field is the Bot Framework
/// <c>ConversationId</c>. <c>TeamsMessengerConnector.SendMessageAsync</c>
/// therefore cannot use any natural-key lookup on <see cref="IConversationReferenceStore"/>
/// to resolve the proactive reference; it depends on this companion interface instead.
/// Splitting the lookup off (rather than adding a method to
/// <see cref="IConversationReferenceStore"/>) keeps the canonical store contract aligned
/// with the planning docs while still giving the connector a clean composition point.
/// </para>
/// <para>
/// <b>Implementation expectation:</b> Stage 2.1's <c>InMemoryConversationReferenceStore</c>
/// and Stage 4.1's <c>SqlConversationReferenceStore</c> SHOULD implement BOTH
/// <see cref="IConversationReferenceStore"/> and this interface so a single DI registration
/// covers both contracts. Tests can also implement only this interface in isolation.
/// </para>
/// <para>
/// <b>Forward path:</b> a future Stage 1 contract revision could either add a
/// <c>TenantId</c> field to <c>MessengerMessage</c> or introduce a destination-URI parser,
/// at which point this companion interface can be retired and
/// <see cref="IConversationReferenceStore"/> would still not need a
/// <c>GetByConversationIdAsync</c> overload.
/// </para>
/// </remarks>
public interface IConversationReferenceRouter
{
    /// <summary>
    /// <b>Legacy tenantless lookup.</b> Look up a stored conversation reference whose Bot
    /// Framework <c>ConversationId</c> matches <paramref name="conversationId"/>. Returns
    /// <c>null</c> when no reference exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bot Framework <c>ConversationId</c> values are generated server-side and the
    /// platform does NOT guarantee uniqueness across Entra ID tenants. A row with the same
    /// <c>ConversationId</c> could exist in two distinct tenants and this method would
    /// return whichever the database surfaces first — that's a cross-tenant isolation gap.
    /// </para>
    /// <para>
    /// <b>Production callers SHOULD invoke the tenant-aware overload</b>
    /// <see cref="GetByConversationIdAsync(string, string, CancellationToken)"/> instead.
    /// This tenantless variant is retained for back-compat with consumers that have not yet
    /// been migrated and for test doubles that don't model multi-tenant data.
    /// </para>
    /// </remarks>
    /// <param name="conversationId">Bot Framework <c>ConversationId</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching reference, or <c>null</c>.</returns>
    Task<TeamsConversationReference?> GetByConversationIdAsync(string conversationId, CancellationToken ct);

    /// <summary>
    /// <b>Tenant-aware lookup.</b> Look up the stored conversation reference whose
    /// Bot Framework <c>ConversationId</c> matches <paramref name="conversationId"/> AND
    /// whose <c>TenantId</c> matches <paramref name="tenantId"/>. Returns <c>null</c> when
    /// no matching reference exists in the supplied tenant — including when a reference
    /// with the same <c>ConversationId</c> exists but in a DIFFERENT tenant. This is the
    /// FR-006 multi-tenant-isolation contract: a tenant cannot resolve a sibling tenant's
    /// conversation reference even if they happen to share a Bot Framework conversation ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default interface implementation post-filters the result of the legacy
    /// tenantless lookup. It is correct for test doubles and any single-row store, but
    /// real stores SHOULD override with a server-side tenant filter so cross-tenant rows
    /// are excluded by the index seek rather than discovered and discarded in memory.
    /// <c>SqlConversationReferenceStore</c> (in the EntityFrameworkCore assembly) provides
    /// such an override against the <c>IX_ConversationReferences_ConversationId</c>
    /// filtered index.
    /// </para>
    /// </remarks>
    /// <param name="tenantId">Entra ID tenant that scopes the lookup.</param>
    /// <param name="conversationId">Bot Framework <c>ConversationId</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching reference (active or inactive — store-specific), or <c>null</c>.</returns>
    async Task<TeamsConversationReference?> GetByConversationIdAsync(string tenantId, string conversationId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new ArgumentException("Tenant ID must be a non-empty string.", nameof(tenantId));
        }

        if (string.IsNullOrEmpty(conversationId))
        {
            throw new ArgumentException("Conversation ID must be a non-empty string.", nameof(conversationId));
        }

        var hit = await GetByConversationIdAsync(conversationId, ct).ConfigureAwait(false);

        // Post-filter by tenant. A cross-tenant row that happens to share the
        // ConversationId returns null — which is the correct multi-tenant isolation
        // behavior even when the underlying store doesn't enforce it server-side.
        return hit is not null && string.Equals(hit.TenantId, tenantId, StringComparison.Ordinal)
            ? hit
            : null;
    }
}
