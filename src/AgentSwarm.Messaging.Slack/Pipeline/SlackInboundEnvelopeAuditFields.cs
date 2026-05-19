// -----------------------------------------------------------------------
// <copyright file="SlackInboundEnvelopeAuditFields.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Collections.Generic;
using System.Text.Json;
using AgentSwarm.Messaging.Slack.Transport;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

/// <summary>
/// Helper that extracts the audit-record fields the operator
/// attachment / story "Audit" requirement mandates -- specifically
/// the human-readable command text, the originating Slack thread
/// timestamp, and the originating message timestamp -- from a
/// <see cref="SlackInboundEnvelope"/>'s raw payload without
/// allocating an entire parsed payload graph the audit pipeline
/// does not otherwise need.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>,
/// added in iter 6 to close evaluator item #3 ("Inbound audit rows
/// intentionally leave CommandText, ResponsePayload, ThreadTs, and
/// ConversationId null").
/// </para>
/// <para>
/// The extractor is intentionally tolerant of malformed payloads:
/// every accessor catches <see cref="JsonException"/> and returns
/// <see langword="null"/> so a single bad envelope cannot crash the
/// audit pipeline. Field-of-interest extraction is per source type:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="SlackInboundSourceType.Command"/> --
///       <c>CommandText</c> = <c>"{command} {text}"</c> (the verbatim
///       slash-command invocation); <c>ThreadTs</c> /
///       <c>MessageTs</c> = <see langword="null"/> because Slack
///       slash commands never originate inside a thread.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="SlackInboundSourceType.Interaction"/> --
///       <c>CommandText</c> = the action's <c>action_id</c> or the
///       submission's <c>view.id</c>; <c>ThreadTs</c> /
///       <c>MessageTs</c> = the <c>container.thread_ts</c> /
///       <c>container.message_ts</c> a Block Kit button click
///       carries when the message lives inside a thread.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="SlackInboundSourceType.Event"/> --
///       <c>CommandText</c> = the inner <c>event.text</c> when
///       present (for <c>app_mention</c>) and falls back to the
///       <c>event.type</c> string for other Events API callbacks;
///       <c>ThreadTs</c> / <c>MessageTs</c> = <c>event.thread_ts</c>
///       / <c>event.ts</c> when present.
///     </description>
///   </item>
/// </list>
/// </remarks>
internal readonly record struct SlackInboundEnvelopeAuditFields(
    string? CommandText,
    string? ThreadTs,
    string? MessageTs)
{
    /// <summary>
    /// All-null instance returned when the raw payload is missing or
    /// cannot be parsed.
    /// </summary>
    public static SlackInboundEnvelopeAuditFields Empty { get; } =
        new(CommandText: null, ThreadTs: null, MessageTs: null);

    /// <summary>
    /// Extracts the audit fields from <paramref name="envelope"/>'s
    /// raw payload. Returns <see cref="Empty"/> for envelopes whose
    /// raw payload is missing or unparseable.
    /// </summary>
    public static SlackInboundEnvelopeAuditFields Extract(SlackInboundEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.IsNullOrEmpty(envelope.RawPayload))
        {
            return Empty;
        }

        try
        {
            return envelope.SourceType switch
            {
                SlackInboundSourceType.Command => ExtractCommand(envelope.RawPayload),
                SlackInboundSourceType.Interaction => ExtractInteraction(envelope.RawPayload),
                SlackInboundSourceType.Event => ExtractEvent(envelope.RawPayload),
                _ => Empty,
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return Empty;
        }
    }

    private static SlackInboundEnvelopeAuditFields ExtractCommand(string raw)
    {
        IDictionary<string, StringValues> fields = QueryHelpers.ParseQuery(raw);
        string? command = GetFormValue(fields, "command");
        string? text = GetFormValue(fields, "text");

        string? commandText = (command, text) switch
        {
            (null, null) => null,
            (null, _) => text,
            (_, null) => command,
            _ => string.IsNullOrEmpty(text) ? command : $"{command} {text}",
        };

        return new SlackInboundEnvelopeAuditFields(
            CommandText: commandText,
            ThreadTs: null,
            MessageTs: null);
    }

    private static SlackInboundEnvelopeAuditFields ExtractInteraction(string raw)
    {
        IDictionary<string, StringValues> fields = QueryHelpers.ParseQuery(raw);
        if (!fields.TryGetValue("payload", out StringValues payloadValues) || StringValues.IsNullOrEmpty(payloadValues))
        {
            return Empty;
        }

        return ExtractInteractionJson(payloadValues.ToString());
    }

    private static SlackInboundEnvelopeAuditFields ExtractInteractionJson(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        string? commandText = null;
        if (root.TryGetProperty("actions", out JsonElement actions)
            && actions.ValueKind == JsonValueKind.Array
            && actions.GetArrayLength() > 0
            && actions[0].ValueKind == JsonValueKind.Object)
        {
            commandText = ReadStringProperty(actions[0], "action_id");
        }

        if (string.IsNullOrEmpty(commandText)
            && root.TryGetProperty("view", out JsonElement view)
            && view.ValueKind == JsonValueKind.Object)
        {
            commandText = ReadStringProperty(view, "id");
        }

        string? threadTs = null;
        string? messageTs = null;
        if (root.TryGetProperty("container", out JsonElement container) && container.ValueKind == JsonValueKind.Object)
        {
            threadTs = ReadStringProperty(container, "thread_ts");
            messageTs = ReadStringProperty(container, "message_ts");
        }

        // Some Block Kit payloads also expose thread_ts at the root
        // (legacy interactive shortcuts); fall through to that when
        // the container did not carry it.
        if (string.IsNullOrEmpty(threadTs))
        {
            threadTs = ReadStringProperty(root, "thread_ts");
        }

        return new SlackInboundEnvelopeAuditFields(
            CommandText: commandText,
            ThreadTs: threadTs,
            MessageTs: messageTs);
    }

    private static SlackInboundEnvelopeAuditFields ExtractEvent(string raw)
    {
        using JsonDocument doc = JsonDocument.Parse(raw);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        if (!root.TryGetProperty("event", out JsonElement evt) || evt.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        string? text = ReadStringProperty(evt, "text");
        string? subtype = ReadStringProperty(evt, "type");
        string? commandText = string.IsNullOrEmpty(text) ? subtype : text;

        string? threadTs = ReadStringProperty(evt, "thread_ts");
        string? messageTs = ReadStringProperty(evt, "ts");

        return new SlackInboundEnvelopeAuditFields(
            CommandText: commandText,
            ThreadTs: threadTs,
            MessageTs: messageTs);
    }

    private static string? GetFormValue(IDictionary<string, StringValues> fields, string key)
    {
        return fields.TryGetValue(key, out StringValues values) && !StringValues.IsNullOrEmpty(values)
            ? values.ToString()
            : null;
    }

    private static string? ReadStringProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
