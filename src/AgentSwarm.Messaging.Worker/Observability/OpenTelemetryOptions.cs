// -----------------------------------------------------------------------
// <copyright file="OpenTelemetryOptions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Worker.Observability;

/// <summary>
/// Stage 6.1 — binding shape for the <c>OpenTelemetry</c>
/// configuration section consumed by
/// <see cref="OpenTelemetrySetup.AddTelegramOpenTelemetry"/>. The
/// section is optional: when absent, the host falls back to
/// safe-by-default exporter selection (console in Development,
/// OTLP-no-op in Production).
/// </summary>
public sealed class OpenTelemetryOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "OpenTelemetry";

    /// <summary>
    /// Logical service.name OTEL resource attribute. Defaults to the
    /// Worker's assembly name so dashboards group all spans/metrics
    /// from this process under a stable identifier.
    /// </summary>
    public string ServiceName { get; set; } = "AgentSwarm.Messaging.Worker";

    /// <summary>
    /// Optional service.version attribute (CI typically injects the
    /// build's git SHA or semver here).
    /// </summary>
    public string? ServiceVersion { get; set; }

    /// <summary>
    /// Whether to attach the console exporter. Defaults to <c>true</c>
    /// for Development environments via
    /// <see cref="OpenTelemetrySetup"/>, <c>false</c> otherwise. Kept
    /// configurable so the integration tests can pin the exporter
    /// regardless of the host's environment.
    /// </summary>
    public bool ConsoleExporterEnabled { get; set; }

    /// <summary>
    /// Whether to attach the OTLP (OpenTelemetry Protocol) exporter
    /// — production sidecar / collector wiring.
    /// </summary>
    public bool OtlpExporterEnabled { get; set; }

    /// <summary>
    /// OTLP endpoint (e.g. <c>http://otel-collector:4317</c> for the
    /// gRPC endpoint or <c>http://otel-collector:4318</c> for
    /// HTTP/Protobuf). When blank the SDK falls back to its built-in
    /// default (<c>http://localhost:4317</c>) which matches the
    /// docker-compose collector sidecar layout in this repo.
    /// </summary>
    public string? OtlpEndpoint { get; set; }
}
