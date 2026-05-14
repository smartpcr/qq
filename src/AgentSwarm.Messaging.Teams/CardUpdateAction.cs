namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// Describes the update to apply to a previously-sent Adaptive Card. Aligned with
/// <c>architecture.md</c> §4.1.1.
/// </summary>
public enum CardUpdateAction
{
    /// <summary>Replace the card body with an "answered" disposition (decision recorded).</summary>
    MarkAnswered,

    /// <summary>Replace the card body with an "expired" disposition (deadline elapsed).</summary>
    MarkExpired,

    /// <summary>Replace the card body with a "cancelled" disposition (operator-initiated).</summary>
    MarkCancelled,
}
