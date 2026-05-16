namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Marker type that anchors the assembly so reflection-based scanners
/// (e.g., <c>typeof(AssemblyMarker).Assembly</c>) have a stable handle.
/// The canonical messaging contracts (IMessengerConnector, AgentQuestion,
/// HumanDecisionEvent, ...) are introduced in Stage 1.2.
/// </summary>
internal static class AssemblyMarker
{
}
