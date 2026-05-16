namespace AgentSwarm.Messaging.Teams.Tests.Diagnostics;

/// <summary>
/// Serializes the telemetry-listener tests because <see cref="System.Diagnostics.Metrics.MeterListener"/>
/// observes measurements from <i>any</i> live <see cref="System.Diagnostics.Metrics.Meter"/>
/// with a matching name within the process, and
/// <see cref="System.Diagnostics.ActivityListener"/> sees activities from any matching
/// <see cref="System.Diagnostics.ActivitySource"/>. Running these classes in parallel
/// would cause one test's listener to pick up another test's measurements / spans.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TeamsTelemetryCollection
{
    public const string Name = "TeamsTelemetryListeners";
}
