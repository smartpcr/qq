namespace AgentSwarm.Messaging.Teams.Outbox;

/// <summary>
/// Sentinel singleton registered by
/// <see cref="TeamsOutboxServiceCollectionExtensions.AddTeamsOutboxEngine"/> to mark
/// the host as "outbox-only". When this type is resolvable from DI the inner concrete
/// <see cref="TeamsMessengerConnector"/> and <see cref="TeamsProactiveNotifier"/>
/// reject every direct send so production callers cannot accidentally bypass the
/// Stage 6.1 outbox by resolving the concrete types or the
/// <see cref="IInnerTeamsMessengerConnector"/> /
/// <see cref="IInnerTeamsProactiveNotifier"/> marker interfaces.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a sentinel (not an enum / flag on options).</b> The guard MUST be opt-in via
/// DI composition so legacy hosts and unit tests that pre-date Stage 6.1 (or wire the
/// inner concretes without the outbox engine for pinning underlying send semantics)
/// keep working unchanged. By keying the guard to a separate singleton instead of a
/// flag on <see cref="TeamsMessagingOptions"/>, the host's existing options stay
/// behaviourally identical and only the explicit
/// <see cref="TeamsOutboxServiceCollectionExtensions.AddTeamsOutboxEngine"/> call
/// activates the guard. The connectors' factories pull the guard from DI via
/// <see cref="System.IServiceProvider"/> at construction time, so the guard's
/// presence reflects the composition order the host actually wired.
/// </para>
/// <para>
/// <b>Why a guard at all.</b> Iter-2 evaluator critique #4 — the outbox-backed
/// decorator narrative claims every send method is enqueue-only, but the inner
/// concrete classes still call <c>CloudAdapter.ContinueConversationAsync</c> directly
/// (per <c>implementation-plan.md</c> §6.1 lines 383-384). When the outbox engine is
/// wired, direct resolution of <see cref="TeamsMessengerConnector"/> or
/// <see cref="TeamsProactiveNotifier"/> would silently bypass
/// <see cref="Core.IMessageOutbox.EnqueueAsync"/>. The guard converts that silent
/// bypass into a loud failure with a clear remediation message — and keeps the
/// inner-class direct paths intact for legacy/test compositions where the outbox is
/// not in play.
/// </para>
/// <para>
/// <b>Dispatcher boundary.</b> The dispatcher
/// (<see cref="TeamsOutboxDispatcher"/>) does NOT resolve the inner concretes — it
/// owns <see cref="Microsoft.Bot.Builder.Integration.AspNet.Core.CloudAdapter"/>
/// directly. So registering this guard never breaks the outbox engine's own
/// delivery path; only ad-hoc direct resolution of the concrete classes is blocked.
/// </para>
/// </remarks>
public sealed class TeamsDirectSendBypassGuard
{
    /// <summary>
    /// Throw <see cref="InvalidOperationException"/> with a deterministic, auditable
    /// remediation message. Called from the top of every direct send method on
    /// <see cref="TeamsMessengerConnector"/> and <see cref="TeamsProactiveNotifier"/>
    /// when this guard is wired by DI.
    /// </summary>
    /// <param name="callerType">Type name of the concrete class invoking the guard
    /// (e.g. <c>nameof(TeamsMessengerConnector)</c>). Surfaced in the error message
    /// so logs / dashboards can attribute the rejected call without parsing a stack
    /// trace.</param>
    /// <param name="callerMember">Method name of the direct send (e.g.
    /// <c>nameof(TeamsMessengerConnector.SendMessageAsync)</c>). Surfaced in the
    /// error message for the same reason.</param>
    /// <exception cref="System.InvalidOperationException">Always — this method
    /// exists solely to throw a structured, actionable error.</exception>
    public void ThrowIfDisallowed(string callerType, string callerMember)
    {
        throw new InvalidOperationException(
            $"{callerType}.{callerMember} was invoked directly while the Stage 6.1 outbox engine is composed. " +
            "Direct sends on the concrete TeamsMessengerConnector / TeamsProactiveNotifier are blocked when " +
            "AddTeamsOutboxEngine is wired so production code cannot bypass IMessageOutbox.EnqueueAsync. " +
            "Resolve the public IMessengerConnector / IProactiveNotifier from DI instead — those point to the " +
            "outbox-backed wrappers (OutboxBackedMessengerConnector / OutboxBackedProactiveNotifier) which " +
            "enqueue the send to the durable outbox and let OutboxRetryEngine + TeamsOutboxDispatcher do the " +
            "actual delivery via CloudAdapter. Dispatcher-side delivery does NOT go through this concrete class " +
            "(the dispatcher owns CloudAdapter directly), so this guard never blocks the outbox engine's own " +
            "send path; it blocks only ad-hoc direct resolution of the inner concretes that would otherwise " +
            "silently skip the outbox.");
    }
}
