namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Shared guard for <c>CorrelationId</c>/trace-id fields across the messaging
/// contracts. The story acceptance criterion "All messages include
/// trace/correlation ID" requires every transport-bearing contract to carry
/// a non-null, non-empty, non-whitespace identifier; this helper centralizes
/// the throw shape so every record reports the violation in the same way.
/// </summary>
/// <remarks>
/// Throws <see cref="ArgumentNullException"/> when the supplied value is
/// <c>null</c>, and <see cref="ArgumentException"/> when it is empty or
/// consists entirely of whitespace. The <paramref name="paramName"/>
/// argument is propagated to the exception so consumers can surface the
/// offending property without re-deriving it.
/// <para>The class is <c>public</c> so the same guard is applied uniformly
/// across Abstractions records (e.g. <see cref="MessengerMessage"/>,
/// <see cref="AgentQuestion"/>, <see cref="SwarmCommand"/>,
/// <see cref="HumanDecisionEvent"/>, <see cref="OutboundMessage"/>) and
/// Core records (e.g. <c>AgentSwarm.Messaging.Core.TaskOversight</c>) — the
/// "All messages include trace/correlation ID" acceptance criterion is
/// undermined the moment any single contract lets an empty or whitespace
/// trace id slip through, so the helper is shared rather than duplicated.</para>
/// </remarks>
public static class CorrelationIdValidation
{
    public static string Require(string? value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value!;
    }
}
