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

    /// <summary>
    /// Arguments portion of the command — the text remaining after the canonical command
    /// keyword has been stripped from <see cref="NormalizedText"/>. Populated by
    /// <see cref="ICommandDispatcher"/> immediately before invoking the matching
    /// <see cref="ICommandHandler"/> so handlers can read the body directly without
    /// re-parsing. <c>null</c> when the dispatcher has not yet run (e.g., during early
    /// pipeline validation) or when the inbound text matched no known command.
    /// </summary>
    /// <remarks>
    /// For example, when <see cref="NormalizedText"/> is
    /// <c>"agent ask create e2e tests"</c> and the matched handler's
    /// <see cref="ICommandHandler.CommandName"/> is <c>"agent ask"</c>, the dispatcher sets
    /// <see cref="CommandArguments"/> to <c>"create e2e tests"</c>. For parameterless
    /// commands (e.g. bare <c>"approve"</c>), the value is the empty string.
    /// </remarks>
    public string? CommandArguments { get; init; }

    /// <summary>
    /// Origination hint set by inbound producers that already know the canonical
    /// <see cref="MessengerEventSources"/> value to stamp on the published
    /// <see cref="MessengerEvent.Source"/>. Used by the Stage 3.4 message-extension path
    /// (<c>MessageExtensionHandler</c>) so the resulting <see cref="CommandEvent"/> carries
    /// <see cref="MessengerEventSources.MessageAction"/> regardless of the underlying
    /// conversation type. When <c>null</c>, downstream handlers fall back to deriving the
    /// source from the turn context (<c>PersonalChat</c> vs <c>TeamChannel</c>) per
    /// <c>architecture.md</c> §3.1.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// When <c>true</c>, downstream <see cref="ICommandHandler"/> implementations skip
    /// sending an outbound chat reply through <see cref="TurnContext"/>. Set by inbound
    /// producers that own the user-facing response themselves — for example, the Stage 3.4
    /// message-extension handler returns its confirmation card via the
    /// <c>MessagingExtensionActionResponse</c> invoke reply rather than a channel-thread
    /// message (per <c>architecture.md</c> §2.15 and <c>e2e-scenarios.md</c> §Message
    /// Actions: "message extensions return a confirmation card response, not a channel
    /// thread reply"). Defaults to <c>false</c> so existing call sites (
    /// <c>OnMessageActivityAsync</c>) continue to receive their per-handler reply card.
    /// </summary>
    public bool SuppressReply { get; init; }

    /// <summary>
    /// Entra ID tenant identifier scoping the inbound activity. Stamped by the
    /// messenger-specific activity handler (for Teams,
    /// <c>TeamsSwarmActivityHandler</c> reads it from
    /// <c>TeamsChannelData.Tenant.Id</c>) so downstream command handlers can push it
    /// onto the canonical Stage 6.3 <c>TeamsLogScope</c>
    /// <c>TenantId</c> enrichment key without re-deriving it from the
    /// messenger-specific <see cref="TurnContext"/>. The Abstractions
    /// <see cref="CommandContext"/> remains platform-agnostic — only the producer
    /// knows where to read the tenant from for its messenger (cref omitted because
    /// <c>TeamsLogScope</c> lives in the Teams-specific assembly, which Abstractions
    /// cannot reference without inverting the dependency graph).
    /// </summary>
    /// <remarks>
    /// Stage 6.3 iter-6 evaluator feedback items 1 + 3 — required so every command
    /// handler's <c>TeamsLogScope.BeginScope</c> call carries all three canonical
    /// enrichment keys (<c>CorrelationId</c>, <c>TenantId</c>, <c>UserId</c>) and
    /// so the structural-coverage tests can assert the tenant without instantiating
    /// a Bot Framework <c>ITurnContext</c> mock.
    /// </remarks>
    public string? TenantId { get; init; }
}
