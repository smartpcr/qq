// -----------------------------------------------------------------------
// <copyright file="SlackAuditPayloadSerializer.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Persistence;

using System;
using System.Text.Json;

/// <summary>
/// Stage 7.1 helper that serialises arbitrary <see cref="object"/>
/// payloads (typically pre-rendered Slack view JSON trees produced by
/// <c>ISlackMessageRenderer</c>) into the bounded UTF-8 JSON form
/// that <see cref="Entities.SlackAuditEntry.ResponsePayload"/> stores.
/// </summary>
/// <remarks>
/// <para>
/// The story "Audit" requirement mandates that the response payload
/// is persisted alongside the team / channel / thread / user / command
/// fields. The transport-layer modal-open audit recorders
/// (<see cref="Transport.SlackModalAuditRecorder"/>,
/// <c>SlackDirectApiClient</c>) feed the Slack <c>view</c> object
/// through this serialiser before stamping it onto the audit row so
/// the payload is queryable through
/// <see cref="ISlackAuditLogger.QueryAsync"/>.
/// </para>
/// <para>
/// Output is capped at <see cref="MaxPayloadChars"/> (16,000 UTF-16
/// code units, matching <see cref="string.Length"/> semantics) so a
/// pathologically large modal cannot bloat the audit table. When the
/// serialised representation exceeds the cap, the truncated prefix is
/// followed by a literal <c>"...&lt;truncated&gt;"</c> marker so an
/// operator can still see the shape and length without scanning the
/// entire blob.
/// </para>
/// <para>
/// Serialisation failures (cyclic graphs, unsupported types) fall
/// back to a sentinel JSON object that captures the exception type
/// name; the audit pipeline must never throw because of a payload it
/// cannot serialise.
/// </para>
/// </remarks>
public static class SlackAuditPayloadSerializer
{
    /// <summary>
    /// Maximum length, in <see cref="string.Length"/> units (UTF-16
    /// code units, i.e. characters), of the JSON written to
    /// <see cref="Entities.SlackAuditEntry.ResponsePayload"/>.
    /// Matches the persistence column's 16&#160;000-character cap.
    /// Note: this is a character cap, not a byte cap; multi-byte
    /// content (e.g. CJK) may serialise to more UTF-8 bytes than this
    /// value, but the audit column is <c>NVARCHAR(MAX)</c> so no
    /// downstream byte limit is exceeded.
    /// </summary>
    public const int MaxPayloadChars = 16_000;

    /// <summary>Suffix appended to a truncated payload.</summary>
    public const string TruncationMarker = "\"...<truncated>\"";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        MaxDepth = 32,
    };

    /// <summary>
    /// Serialises <paramref name="payload"/> to JSON, returning
    /// <see langword="null"/> if the payload is <see langword="null"/>.
    /// </summary>
    /// <param name="payload">Object to serialise (typically a Slack
    /// view JSON tree).</param>
    /// <returns>JSON representation, possibly truncated.</returns>
    public static string? Serialize(object? payload)
    {
        if (payload is null)
        {
            return null;
        }

        if (payload is string s)
        {
            return Truncate(s);
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(payload, SerializerOptions);
        }
        catch (Exception ex)
        {
            json = JsonSerializer.Serialize(new
            {
                serializationError = ex.GetType().Name,
                message = ex.Message,
                payloadType = payload.GetType().FullName,
            });
        }

        return Truncate(json);
    }

    private static string Truncate(string value)
    {
        if (value.Length <= MaxPayloadChars)
        {
            return value;
        }

        return string.Concat(
            value.AsSpan(0, MaxPayloadChars - TruncationMarker.Length),
            TruncationMarker.AsSpan());
    }
}
