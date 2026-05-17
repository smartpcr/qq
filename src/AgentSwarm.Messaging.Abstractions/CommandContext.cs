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
    /// Identifier of the agent associated with this command's outcome — populated by
    /// the matched <see cref="ICommandHandler"/> AFTER it has resolved or created the
    /// agent context (for example, <c>ApproveRejectCommandExecutor</c> stamps the
    /// resolved <see cref="AgentQuestion.AgentId"/> here so the activity handler can
    /// thread it into the <see cref="MessengerEventTypes.AgentTaskRequest"/>
    /// <c>CommandReceived</c> audit entry per <c>tech-spec.md</c> §4.3).
    /// </summary>
    /// <remarks>
    /// This property is intentionally mutable (<c>set</c> rather than <c>init</c>) so
    /// handlers can populate it on the same context instance the activity handler
    /// already holds and reads in its post-dispatch <c>finally</c> block. Leaving it
    /// <c>null</c> is allowed — the audit row simply records the value as null, which
    /// is the correct outcome for commands that do not associate with a specific
    /// agent (e.g. bare <c>agent status</c> for swarm-wide status).
    /// </remarks>
    public string? AgentId { get; set; }

    /// <summary>
    /// Identifier of the agent task associated with this command's outcome —
    /// populated by the matched <see cref="ICommandHandler"/> after creating or
    /// resolving the task (for example, <c>AskCommandHandler</c> stamps the
    /// newly-minted task tracking ID here; <c>ApproveRejectCommandExecutor</c>
    /// stamps the resolved <see cref="AgentQuestion.TaskId"/>). Read by the
    /// activity handler in its post-dispatch audit emission so the
    /// <c>CommandReceived</c> audit row carries the task association required by
    /// <c>tech-spec.md</c> §4.3 (<c>TaskId</c> column).
    /// </summary>
    /// <remarks>
    /// Mutable for the same reason as <see cref="AgentId"/> — handlers populate it
    /// on the context instance the activity handler already holds.
    /// </remarks>
    public string? TaskId { get; set; }

    /// <summary>
    /// Handler-declared outcome for the command, surfaced to the activity handler's
    /// post-dispatch <c>CommandReceived</c> audit emission. <c>null</c> means "no
    /// explicit outcome was declared" — the activity handler then defaults to
    /// <c>AuditOutcomes.Success</c> when <see cref="ICommandDispatcher.DispatchAsync"/>
    /// returned without throwing, or <c>AuditOutcomes.Failed</c> when it threw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This property exists because handled command-level failures —
    /// "question not found", "action not in AllowedActions", "comment required",
    /// "CAS race already resolved" — are reported to the user via a reply card
    /// rather than by throwing, so a no-exception return is NOT the same as
    /// <c>Success</c> from a compliance-audit perspective. The matched
    /// <see cref="ICommandHandler"/> sets this to <c>AuditOutcomes.Rejected</c>
    /// on each early-return rejection branch so the persisted <c>AuditLog</c> row
    /// records the truthful outcome.
    /// </para>
    /// <para>
    /// Mutable (<c>set</c> rather than <c>init</c>) for the same reason as
    /// <see cref="AgentId"/> / <see cref="TaskId"/> — handlers populate it on the
    /// same context instance the activity handler holds and reads back in its
    /// post-dispatch audit emission.
    /// </para>
    /// </remarks>
    public string? Outcome { get; set; }

    /// <summary>
    /// Arguments portion of the command — the text remaining after the canonical command
    /// keyword has been stripped from <see cref="NormalizedText"/>. Populated by
    /// <see cref="ICommandDispatcher"/> immediately before invoking the matching
    /// <see cref="ICommandHandler"/> so handlers can read the body directly without
    /// re-parsing. <c>null</c> when the dispatcher has not yet run (e.g., during early
    /// pipeline validation) or when the inbound text matched no known command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For example, when <see cref="NormalizedText"/> is
    /// <c>"agent ask create e2e tests"</c> and the matched handler's
    /// <see cref="ICommandHandler.CommandName"/> is <c>"agent ask"</c>, the dispatcher sets
    /// <see cref="CommandArguments"/> to <c>"create e2e tests"</c>. For parameterless
    /// commands (e.g. bare <c>"approve"</c>), the value is the empty string.
    /// </para>
    /// <para>
    /// Stage 5.2 iter-7 — declared <c>set</c> (not <c>init</c>) so the dispatcher can stamp
    /// the parsed arguments onto the SAME context instance the activity handler holds and
    /// reads back post-dispatch. The prior <c>init</c>-only declaration forced the
    /// dispatcher into a <c>context with { CommandArguments = arguments }</c> clone that
    /// gave handlers a throwaway record — any handler mutation of
    /// <see cref="AgentId"/> / <see cref="TaskId"/> / <see cref="Outcome"/> on that clone
    /// was lost when control returned to the activity handler, silently nulling those
    /// fields in the persisted <c>CommandReceived</c> audit row. Moving to <c>set</c>
    /// eliminates the clone and makes the mutable-handler-output contract sound.
    /// </para>
    /// </remarks>
    public string? CommandArguments { get; set; }

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
}
