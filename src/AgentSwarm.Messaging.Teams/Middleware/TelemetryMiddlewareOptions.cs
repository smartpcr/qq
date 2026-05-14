namespace AgentSwarm.Messaging.Teams.Middleware;

/// <summary>
/// Configuration knobs for <see cref="TelemetryMiddleware"/>. Bound via DI; injectable for
/// tests so the middleware can be exercised with detailed-payload capture enabled.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SensitiveActivityTypes"/> is intentionally typed as a <see cref="string"/> array
/// (the public contract required by Stage 2.1 of the implementation plan) so it presents an
/// immutable shape at the public surface. Internal lookups copy the array into a
/// case-insensitive set to avoid O(n) scans per activity.
/// </para>
/// </remarks>
public sealed class TelemetryMiddlewareOptions
{
    /// <summary>
    /// When <see langword="true"/>, the middleware records sanitized payload attributes on
    /// the span. When <see langword="false"/> (default) only structural metadata (activity
    /// type, IDs, tenant) is captured.
    /// </summary>
    public bool EnableDetailedPayloadCapture { get; set; }

    /// <summary>
    /// Activity types for which payload capture is redacted (the span still records
    /// activity-type, IDs, and tenant). Defaults to an empty array per Stage 2.1. Typed as
    /// <see cref="string"/>[] to match the canonical Stage 2.1 contract shape.
    /// </summary>
    public string[] SensitiveActivityTypes { get; set; } = Array.Empty<string>();
}
