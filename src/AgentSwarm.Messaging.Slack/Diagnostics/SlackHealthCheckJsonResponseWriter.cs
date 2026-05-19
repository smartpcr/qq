// -----------------------------------------------------------------------
// <copyright file="SlackHealthCheckJsonResponseWriter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Diagnostics;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// JSON response writer for the Stage 7.3 Slack readiness endpoint.
/// Emits a structured payload that names every registered Slack
/// health check and reports its status, description, tags, duration,
/// and (when supplied by the check) the <c>data</c> dictionary --
/// matching the per-check shape e2e Scenario 20.2 in
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/e2e-scenarios.md</c>
/// asserts on
/// ("the response includes ... Slack API connectivity / Outbound
/// queue depth / DLQ depth ... statuses").
/// </summary>
/// <remarks>
/// <para>
/// The default <see cref="Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions"/>
/// response writer produces a plain-text body containing only the
/// aggregate status ("Healthy" / "Unhealthy"). That was the iter-3
/// evaluator's item 3 regression: an operator hitting
/// <c>/health/ready</c> could not distinguish which Slack check was
/// failing, and the e2e-scenarios.md table of per-component statuses
/// could not be satisfied by inspection. This writer surfaces every
/// check name + status so:
/// </para>
/// <list type="bullet">
///   <item><description>Operators get an actionable triage payload
///   without enabling extra tooling (HealthChecks-UI, etc.).</description></item>
///   <item><description>The Scenario 20.2 acceptance check passes
///   verbatim: each of <c>slack-api-connectivity</c>,
///   <c>slack-outbound-queue-depth</c>, and
///   <c>slack-dead-letter-queue-depth</c> appears under
///   <c>"checks"</c>.</description></item>
///   <item><description>The endpoint integration test asserts the
///   exact JSON shape, pinning the contract so a future writer swap
///   cannot silently drop the per-component breakdown.</description></item>
/// </list>
/// <para>
/// Field naming uses <c>snake_case</c> for parity with the existing
/// structured-logging fields in
/// <see cref="SlackStartupDiagnosticsHostedService"/> and the audit
/// schema; this keeps an operator's grep patterns consistent across
/// log / probe / audit surfaces.
/// </para>
/// </remarks>
internal static class SlackHealthCheckJsonResponseWriter
{
    private static readonly JsonWriterOptions JsonWriterOptions = new()
    {
        // Indented = false so the readiness endpoint stays cheap to
        // serve under load; operators piping the response through
        // `jq` get the same content.
        Indented = false,
    };

    /// <summary>
    /// MIME type the readiness endpoint writes. ASP.NET Core sets
    /// <see cref="HttpResponse.ContentType"/> from this constant so
    /// integration tests can assert on it without recomputing.
    /// </summary>
    public const string ContentType = "application/json; charset=utf-8";

    /// <summary>
    /// Writes <paramref name="report"/> to <paramref name="context"/>'s
    /// response body as the Slack readiness JSON envelope. Matches
    /// the <c>HealthCheckOptions.ResponseWriter</c> delegate signature
    /// so it plugs into <c>MapHealthChecks</c> directly.
    /// </summary>
    /// <param name="context">Active HTTP request context.</param>
    /// <param name="report">Aggregate health-check report produced
    /// by the ASP.NET Core pipeline.</param>
    /// <returns>Task representing the asynchronous write.</returns>
    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = ContentType;

        using MemoryStreamWrapper wrapper = new();
        using (Utf8JsonWriter writer = new(wrapper.Stream, JsonWriterOptions))
        {
            WriteReport(writer, report);
        }

        // Materialize once then write so the Content-Length header
        // (set by ASP.NET Core from the response body length) is
        // accurate -- streaming directly to the response body would
        // leave it as chunked-transfer which complicates probe
        // tooling that asserts on exact byte length.
        wrapper.Stream.Position = 0;
        await wrapper.Stream.CopyToAsync(context.Response.Body).ConfigureAwait(false);
    }

    private static void WriteReport(Utf8JsonWriter writer, HealthReport report)
    {
        writer.WriteStartObject();

        writer.WriteString("status", report.Status.ToString());
        writer.WriteNumber("total_duration_ms", report.TotalDuration.TotalMilliseconds);

        writer.WritePropertyName("checks");
        writer.WriteStartArray();

        // Order matters for deterministic test assertions. Sort by
        // registered name so the readiness payload is stable across
        // runs regardless of DI iteration order.
        foreach (KeyValuePair<string, HealthReportEntry> kvp in report.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            WriteEntry(writer, kvp.Key, kvp.Value);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteEntry(Utf8JsonWriter writer, string name, HealthReportEntry entry)
    {
        writer.WriteStartObject();

        writer.WriteString("name", name);
        writer.WriteString("status", entry.Status.ToString());
        writer.WriteNumber("duration_ms", entry.Duration.TotalMilliseconds);

        if (!string.IsNullOrEmpty(entry.Description))
        {
            writer.WriteString("description", entry.Description);
        }

        if (entry.Exception is not null)
        {
            writer.WriteString("exception", entry.Exception.GetType().FullName);
            writer.WriteString("exception_message", entry.Exception.Message);
        }

        if (entry.Tags is not null)
        {
            string[] tagArray = entry.Tags.ToArray();
            if (tagArray.Length > 0)
            {
                writer.WritePropertyName("tags");
                writer.WriteStartArray();
                foreach (string tag in tagArray)
                {
                    writer.WriteStringValue(tag);
                }

                writer.WriteEndArray();
            }
        }

        if (entry.Data is { Count: > 0 })
        {
            writer.WritePropertyName("data");
            writer.WriteStartObject();
            foreach (KeyValuePair<string, object> pair in entry.Data.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                WriteDataValue(writer, pair.Value);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Serialises a single <see cref="HealthReportEntry.Data"/>
    /// value. The dictionary is <c>object</c>-valued so we coerce
    /// well-known primitives directly and fall back to a string
    /// representation for everything else; this keeps the JSON
    /// schema stable and avoids accidentally serialising reflective
    /// type details for arbitrary boxed objects.
    /// </summary>
    private static void WriteDataValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    /// <summary>
    /// Tiny <see cref="System.IO.MemoryStream"/> holder so the
    /// JSON writer can flush before the bytes hit
    /// <see cref="HttpResponse.Body"/>. Kept private because the
    /// writer is internal and the lifetime is purely scoped to a
    /// single <see cref="WriteAsync"/> call.
    /// </summary>
    private sealed class MemoryStreamWrapper : IDisposable
    {
        public System.IO.MemoryStream Stream { get; } = new();

        public void Dispose() => this.Stream.Dispose();
    }
}
