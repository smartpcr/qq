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
    /// Look up a stored conversation reference whose Bot Framework
    /// <c>ConversationId</c> matches <paramref name="conversationId"/>. Returns
    /// <c>null</c> when no reference exists.
    /// </summary>
    /// <param name="conversationId">Bot Framework <c>ConversationId</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching reference, or <c>null</c>.</returns>
    Task<TeamsConversationReference?> GetByConversationIdAsync(string conversationId, CancellationToken ct);
}
