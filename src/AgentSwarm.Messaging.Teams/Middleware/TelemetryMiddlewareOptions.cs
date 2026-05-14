namespace AgentSwarm.Messaging.Teams.Middleware;

/// <summary>
/// Options controlling <see cref="TelemetryMiddleware"/> span enrichment.
/// </summary>
/// <remarks>
/// <para>
/// Defaults are conservative — no payload bodies are captured by default, since inbound
/// activity payloads may contain user-typed text that should not be logged at INFO/DEBUG
/// levels.
/// </para>
/// </remarks>
public sealed class TelemetryMiddlewareOptions
{
    /// <summary>
    /// When <c>true</c>, the middleware records the inbound activity body as a span
    /// attribute. Default <c>false</c> so PII / secrets in payload text are not captured by
    /// default. Operators opt in explicitly via configuration.
    /// </summary>
    public bool EnableDetailedPayloadCapture { get; set; }

    /// <summary>
    /// Activity types for which payload capture is suppressed even when
    /// <see cref="EnableDetailedPayloadCapture"/> is <c>true</c> (for example,
    /// <c>"invoke"</c> may contain sensitive Adaptive Card values). Default: empty.
    /// </summary>
    /// <remarks>
    /// Exposed as <c>string[]</c> (rather than a list type) to match the Stage 2.1
    /// canonical contract surface.
    /// </remarks>
    public string[] SensitiveActivityTypes { get; set; } = Array.Empty<string>();
}
