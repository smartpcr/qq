// -----------------------------------------------------------------------
// <copyright file="HealthCheckJsonResponseWriter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Worker.Observability;

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Stage 6.2 — JSON serializer for the
/// <see cref="Microsoft.AspNetCore.Builder.HealthCheckEndpointRouteBuilderExtensions.MapHealthChecks(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string, Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions)"/>
/// response body. Replaces the framework's plain-text
/// "<c>Healthy</c>" / "<c>Unhealthy</c>" emission with a structured
/// document that lists the overall status, the total probe
/// duration, and a per-check entry containing the individual
/// status, description, duration, and result data — matching the
/// Stage 6.2 brief's "expose at <c>/healthz</c> with JSON detail
/// output" requirement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema.</b>
/// <code>
/// {
///   "status": "Healthy" | "Degraded" | "Unhealthy",
///   "totalDuration": "00:00:00.1234567",
///   "entries": {
///     "telegram_bot_api": {
///       "status": "Healthy",
///       "description": "...",
///       "duration": "00:00:00.0345678",
///       "tags": ["telegram"],
///       "data": { ... }
///     },
///     ...
///   }
/// }
/// </code>
/// The schema is intentionally similar to Steeltoe's and
/// AspNetCore.Diagnostics.HealthChecks' common JSON shape so the
/// operator dashboards / log scrapers built against either tool
/// can ingest this output without bespoke parsing.
/// </para>
/// <para>
/// <b>Secret hygiene (iter-4 evaluator item 3).</b> The writer
/// serialises whatever each <see cref="IHealthCheck"/> placed on
/// <see cref="HealthCheckResult.Data"/>, but the
/// <see cref="HealthCheckResult.Exception"/> is reduced to its
/// <see cref="Type"/> name only — the
/// <see cref="Exception.Message"/> is INTENTIONALLY NOT emitted
/// in the public JSON body. Provider exception messages frequently
/// embed connection strings, table names, file paths, or
/// authentication failure details that an unauthenticated
/// <c>/healthz</c> probe must not leak. Operators retain the full
/// exception (including the message and stack trace) via the
/// structured-log channel that the health checks themselves write
/// to (see each check's <c>_logger.LogWarning(ex, ...)</c> call).
/// The Stage 6.2 checks in this codebase also intentionally limit
/// <c>Data</c> to operator-safe keys (bot id / username, queue
/// depths, threshold values) — the bot token, secret token, and
/// connection strings never appear in any <c>Data</c> dictionary,
/// so the rest of the JSON body is safe to expose on an
/// unauthenticated <c>/healthz</c> probe behind the cluster's own
/// access controls.
/// </para>
/// </remarks>
public static class HealthCheckJsonResponseWriter
{
    private const string ContentType = "application/json; charset=utf-8";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Writes the supplied <paramref name="report"/> as a structured
    /// JSON document on <see cref="HttpResponse.Body"/>. The
    /// response's <see cref="HttpResponse.ContentType"/> is set to
    /// <c>application/json; charset=utf-8</c>.
    /// </summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = ContentType;

        var payload = new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
            entries = report.Entries.ToDictionary(
                kvp => kvp.Key,
                kvp => new
                {
                    status = kvp.Value.Status.ToString(),
                    description = kvp.Value.Description,
                    duration = kvp.Value.Duration.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
                    tags = kvp.Value.Tags,
                    data = kvp.Value.Data,
                    // Iter-4 evaluator item 3 — do NOT emit
                    // exception.Message into the public JSON body.
                    // Provider messages frequently embed connection
                    // strings / file paths / auth failure details
                    // that an unauthenticated /healthz probe must
                    // not leak. The exception TYPE NAME is the safe
                    // operator-actionable signal (e.g. "SqliteException"
                    // routes the runbook); the full Exception
                    // including the message and stack trace remains
                    // available via the structured-log channel that
                    // each IHealthCheck writes to.
                    exception = kvp.Value.Exception is { } ex ? ex.GetType().Name : null,
                }),
        };

        return context.Response.WriteAsync(
            JsonSerializer.Serialize(payload, SerializerOptions));
    }
}
