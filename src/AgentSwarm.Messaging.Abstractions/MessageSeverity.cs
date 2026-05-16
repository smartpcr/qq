namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Priority severity for messenger traffic.
/// Ordered so ascending sort yields the priority order
/// (Critical first, Low last) used by the outbound queue's
/// priority dispatch and by alert routing.
/// </summary>
/// <remarks>
/// Integer values are load-bearing: see architecture.md Section 3.1
/// (<c>Critical &gt; High &gt; Normal &gt; Low</c>) and the matching
/// "MessageSeverity ordering" test scenario for stage 1.2.
/// </remarks>
public enum MessageSeverity
{
    Critical = 0,
    High = 1,
    Normal = 2,
    Low = 3,
}
