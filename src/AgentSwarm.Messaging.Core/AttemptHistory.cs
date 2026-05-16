// -----------------------------------------------------------------------
// <copyright file="AttemptHistory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

/// <summary>
/// Stage 4.2 iter-2 evaluator item 1 — helper that maintains the
/// per-attempt failure log accumulated on
/// <c>OutboundMessage.AttemptHistoryJson</c> and projected into the
/// dead-letter ledger's <c>AttemptTimestamps</c> + <c>ErrorHistory</c>
/// columns (architecture.md §3.1 lines 386–388). Centralising the
/// JSON shape here keeps the producer side
/// (<c>PersistentOutboundQueue.MarkFailedAsync</c> +
/// <c>InMemoryOutboundQueue.MarkFailedAsync</c>) and the consumer side
/// (<c>OutboundQueueProcessor</c> projecting into
/// <see cref="FailureReason"/>) in sync — a divergent shape between
/// the two would surface as either a JSON parse error at dead-letter
/// time or a silently-empty audit log.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire format.</b> A JSON array of objects, each with shape
/// <c>{ "attempt": int, "timestamp": ISO-8601, "error": string,
/// "httpStatus": int? }</c>. The architecture's example is
/// <c>["2026-05-11T18:00:00Z","2026-05-11T18:00:02Z", ...]</c> for
/// <c>AttemptTimestamps</c> — that array is computed as the projection
/// of <c>timestamp</c> out of the per-attempt entries kept here so the
/// outbox does not have to store the same data twice.
/// </para>
/// <para>
/// <b>Bounded growth.</b> The history is capped at 100 entries — any
/// further appends drop the OLDEST entry to keep the column from
/// growing unboundedly on a poison row. <c>MaxAttempts</c> rarely
/// exceeds 10 in practice but the cap exists as a defence-in-depth
/// guard for a future loop bug that would otherwise turn a single row
/// into a multi-MB column.
/// </para>
/// </remarks>
public static class AttemptHistory
{
    /// <summary>The canonical "no history yet" JSON literal.</summary>
    public const string Empty = "[]";

    /// <summary>
    /// Cap on the per-row history length. See the type's remarks.
    /// </summary>
    public const int MaxEntries = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,

        // Architecture.md §3.1 line 388 fixes the on-wire shape as
        // {"attempt","timestamp","error","httpStatus"} — lowercase
        // first letter on each key. CamelCase keeps the producer
        // (System.Text.Json serialise on Append) and consumer
        // (operator audit screen JSON.parse) using the same key
        // names. PropertyNameCaseInsensitive=true ensures deserialise
        // round-trips a legacy PascalCase row if any landed in dev.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Append a single attempt entry to <paramref name="existingJson"/>
    /// and return the resulting JSON array. <paramref name="existingJson"/>
    /// may be <see langword="null"/>, empty, whitespace, or
    /// <see cref="Empty"/>; in all three cases the result is a single-
    /// element array. A malformed prior payload (parse error) is
    /// quietly replaced with a single-element array so a poison row
    /// cannot block the new write.
    /// </summary>
    public static string Append(
        string? existingJson,
        int attempt,
        DateTimeOffset timestamp,
        string error,
        int? httpStatus)
    {
        ArgumentNullException.ThrowIfNull(error);

        var entries = TryParse(existingJson);
        entries.Add(new AttemptEntry(attempt, timestamp, error, httpStatus));

        if (entries.Count > MaxEntries)
        {
            entries.RemoveRange(0, entries.Count - MaxEntries);
        }

        return JsonSerializer.Serialize(entries, JsonOptions);
    }

    /// <summary>
    /// Project the timestamp list out of <paramref name="historyJson"/>
    /// for the dead-letter ledger's <c>AttemptTimestamps</c> column.
    /// Returns <see cref="Empty"/> when no entries are present.
    /// </summary>
    public static string ProjectTimestamps(string? historyJson)
    {
        var entries = TryParse(historyJson);
        if (entries.Count == 0)
        {
            return Empty;
        }

        var timestamps = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            timestamps.Add(entry.Timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        }
        return JsonSerializer.Serialize(timestamps, JsonOptions);
    }

    /// <summary>
    /// Re-emit <paramref name="historyJson"/> in the canonical
    /// <c>ErrorHistory</c> shape (architecture.md §3.1 line 388 —
    /// <c>{"attempt": int, "timestamp": DateTimeOffset, "error": string,
    /// "httpStatus": int?}</c>). The producer side already writes this
    /// shape; this method exists as the named "consumer" gate so a
    /// future refactor that decouples the in-memory representation
    /// from the wire format has a single seam to touch.
    /// </summary>
    public static string ProjectErrorHistory(string? historyJson)
    {
        var entries = TryParse(historyJson);
        if (entries.Count == 0)
        {
            return Empty;
        }
        return JsonSerializer.Serialize(entries, JsonOptions);
    }

    private static List<AttemptEntry> TryParse(string? historyJson)
    {
        if (string.IsNullOrWhiteSpace(historyJson))
        {
            return new List<AttemptEntry>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<AttemptEntry>>(historyJson, JsonOptions);
            return parsed ?? new List<AttemptEntry>();
        }
        catch (JsonException)
        {
            // Malformed prior payload — pretend it was empty so the
            // new append still lands. The old data is unrecoverable
            // either way and a parse-throw would block the dead-
            // letter path on a poison row.
            return new List<AttemptEntry>();
        }
    }

    /// <summary>
    /// Wire-shape entry for a single failed attempt — see the type's
    /// remarks for the canonical JSON shape.
    /// </summary>
    /// <param name="Attempt">1-based attempt number.</param>
    /// <param name="Timestamp">UTC instant of the failure.</param>
    /// <param name="Error">Free-form error description.</param>
    /// <param name="HttpStatus">Optional HTTP status code observed (Telegram Bot API only).</param>
    public sealed record AttemptEntry(
        int Attempt,
        DateTimeOffset Timestamp,
        string Error,
        int? HttpStatus);
}
