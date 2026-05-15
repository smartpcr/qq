namespace AgentSwarm.Messaging.Teams.Commands;

/// <summary>
/// Canonical command-keyword constants exposed by the Stage 3.2 <see cref="CommandDispatcher"/>
/// and consumed by every <c>ICommandHandler.CommandName</c> in this namespace. Aligned with
/// <c>architecture.md</c> §2.5 (the canonical verb vocabulary) and
/// <c>implementation-plan.md</c> §3.2.
/// </summary>
/// <remarks>
/// <para>
/// Each constant is the lowercase, whitespace-normalised form a handler matches on. The
/// dispatcher performs case-insensitive longest-prefix matching against
/// <see cref="AgentSwarm.Messaging.Abstractions.CommandContext.NormalizedText"/> so an
/// inbound message of <c>"AGENT ASK build the thing"</c> still resolves to the
/// <c>"agent ask"</c> handler.
/// </para>
/// <para>
/// <b>Dispatcher does not consume <see cref="All"/>.</b> <see cref="CommandDispatcher"/>
/// builds its own ordered list from the <c>CommandName</c> of every registered
/// <c>ICommandHandler</c> (sorted by descending length, then alphabetically for stable
/// tie-breaks). <see cref="All"/> is provided here purely as a static inventory of every
/// canonical command name for documentation, help-card composition, and tests; it is
/// ordered identically to the dispatcher's runtime list so consumers that iterate it see
/// the same longest-first ordering, but the dispatcher itself never reads this property.
/// </para>
/// </remarks>
public static class CommandNames
{
    /// <summary>The <c>agent ask &lt;text&gt;</c> command — creates a new agent task.</summary>
    public const string AgentAsk = "agent ask";

    /// <summary>The <c>agent status</c> command — queries the swarm/agent status.</summary>
    public const string AgentStatus = "agent status";

    /// <summary>The <c>approve [questionId]</c> command — approves an open question.</summary>
    public const string Approve = "approve";

    /// <summary>The <c>reject [questionId]</c> command — rejects an open question.</summary>
    public const string Reject = "reject";

    /// <summary>The <c>escalate</c> command — escalates the current incident or question.</summary>
    public const string Escalate = "escalate";

    /// <summary>The <c>pause</c> command — pauses the agent bound to the current conversation.</summary>
    public const string Pause = "pause";

    /// <summary>The <c>resume</c> command — resumes a paused agent.</summary>
    public const string Resume = "resume";

    /// <summary>
    /// Every canonical command name, ordered by descending keyword length with an
    /// alphabetical tie-break — the same ordering <see cref="CommandDispatcher"/> applies
    /// when it builds its internal match list from registered handlers, so consumers that
    /// iterate this inventory (for example help-card builders or parity tests) see the
    /// same longest-first sequence the dispatcher uses.
    /// </summary>
    /// <remarks>
    /// This property is purely an inventory. <see cref="CommandDispatcher"/> does
    /// <b>not</b> iterate <see cref="All"/> at runtime; it constructs its own ordered list
    /// from the registered handler set so the dispatch path stays self-contained even if a
    /// future handler is added without a corresponding constant here.
    /// </remarks>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        // Ordering rule: OrderByDescending(length).ThenBy(name, OrdinalIgnoreCase).
        // Lengths — "agent status" (12), "agent ask" (9), "escalate" (8), "approve" (7),
        // "reject" (6), "resume" (6), "pause" (5).
        AgentStatus,
        AgentAsk,
        Escalate,
        Approve,
        Reject,
        Resume,
        Pause,
    };
}
