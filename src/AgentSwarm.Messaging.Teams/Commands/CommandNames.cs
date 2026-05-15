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
/// The <see cref="All"/> list is ordered longest-first to support the dispatcher's
/// longest-prefix selection — <c>"agent ask"</c> wins over a hypothetical short alias like
/// <c>"ask"</c>.
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
    /// Every canonical command name in longest-first order. The dispatcher iterates this
    /// list when performing longest-prefix matching so multi-word verbs ("agent ask",
    /// "agent status") win over any single-word alias.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        AgentAsk,
        AgentStatus,
        Approve,
        Reject,
        Escalate,
        Pause,
        Resume,
    };
}
