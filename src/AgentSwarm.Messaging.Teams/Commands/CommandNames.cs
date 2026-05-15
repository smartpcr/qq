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
/// The <see cref="All"/> list is provided as a convenience for consumers that need the
/// canonical command vocabulary in priority order (e.g. help-card builders, diagnostic
/// listings). It is <b>not</b> consumed by <see cref="CommandDispatcher"/> — the dispatcher
/// derives its own ordering from the <see cref="ICommandHandler.CommandName"/> values of
/// the DI-registered handlers, so unregistered constants in this file have no effect on
/// runtime dispatch.
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
    /// Every canonical command name, ordered by descending length with case-insensitive
    /// ordinal alphabetical tiebreaks. This mirrors the priority ordering that
    /// <see cref="CommandDispatcher"/> applies internally to its registered handler set,
    /// so consumers iterating this list see multi-word verbs ("agent status", "agent ask")
    /// before single-word ones. The dispatcher does <b>not</b> iterate this list — it
    /// builds its own ordering from the DI-registered <see cref="ICommandHandler"/>
    /// instances — but this list is the canonical reference for any non-dispatcher consumer
    /// (help cards, docs, parsers) that needs the same priority order.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        AgentStatus,
        AgentAsk,
        Escalate,
        Approve,
        Reject,
        Resume,
        Pause,
    };
}
