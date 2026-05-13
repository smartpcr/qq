namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Carrier passed from the messenger-specific activity handler into <see cref="ICommandDispatcher"/>
/// (Stage 2.2 → Stage 3.2). The contract is defined here in <c>AgentSwarm.Messaging.Abstractions</c>
/// so that the activity handler can route parsed commands without taking a dependency on the
/// concrete <c>CommandDispatcher</c> implementation that lands in Stage 3.2.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TurnContext"/> is typed as <see cref="object"/> deliberately. The Abstractions
/// assembly is platform-agnostic and must not depend on <c>Microsoft.Bot.Builder</c>; the
/// concrete Teams dispatcher casts this back to <c>Microsoft.Bot.Builder.ITurnContext</c>
/// when it needs to reply through the Bot Framework. Connectors for other messengers (Slack,
/// Discord, Telegram) place their own turn-context types in this slot.
/// </para>
/// <para>
/// Per <c>implementation-plan.md</c> §2.2 — Stage 2.2's <c>OnMessageActivityAsync</c> is the
/// sole location that calls <c>Activity.RemoveMentionText</c> to normalize the message text.
/// The dispatcher (and downstream handlers) operate on <see cref="NormalizedText"/> only and
/// must not perform any further <c>@mention</c> stripping.
/// </para>
/// </remarks>
public sealed record CommandContext
{
    /// <summary>
    /// Mention-stripped message text, ready for keyword parsing. Required.
    /// </summary>
    public required string NormalizedText { get; init; }

    /// <summary>
    /// The resolved internal user identity for the inbound activity, as produced by
    /// <see cref="IIdentityResolver.ResolveAsync"/>. <c>null</c> when identity resolution has
    /// not yet run (for example, during early pipeline validation tests).
    /// </summary>
    public UserIdentity? ResolvedIdentity { get; init; }

    /// <summary>
    /// End-to-end trace identifier propagated from the originating activity. Optional at the
    /// contract level so that callers may stamp it via correlating middleware before
    /// dispatching.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// The messenger-specific turn context object (for Teams, an
    /// <c>ITurnContext&lt;IMessageActivity&gt;</c>). Typed as <see cref="object"/> here so the
    /// Abstractions assembly remains platform-agnostic — the concrete dispatcher casts back
    /// to the messenger's native turn-context type before invoking reply APIs.
    /// </summary>
    public object? TurnContext { get; init; }

    /// <summary>
    /// Conversation identifier for the inbound activity. Populated by the activity handler so
    /// downstream handlers can correlate replies and queries (for example,
    /// <see cref="IAgentQuestionStore.GetOpenByConversationAsync"/>) without re-extracting it
    /// from the messenger-specific turn context.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Inbound activity identifier (for example, the Teams activity ID). Useful for audit
    /// logging and deduplication downstream of the dispatcher.
    /// </summary>
    public string? ActivityId { get; init; }
}
