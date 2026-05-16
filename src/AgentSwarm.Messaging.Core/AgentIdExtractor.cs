// -----------------------------------------------------------------------
// <copyright file="AgentIdExtractor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Core;

using System;
using System.Text.Json;
using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Stage 4.2 — best-effort extraction of the logical
/// <c>AgentId</c> from an <see cref="OutboundMessage"/> so the
/// dead-letter row's <c>AgentId</c> column (e2e-scenarios.md §244)
/// can be populated without rippling a new top-level field across
/// the outbox table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source-of-truth precedence.</b>
/// <list type="number">
///   <item><description>
///     <see cref="OutboundSourceType.Question"/> →
///     deserialise <see cref="OutboundMessage.SourceEnvelopeJson"/>
///     as <see cref="AgentQuestionEnvelope"/> and read
///     <c>Question.AgentId</c>. The connector
///     (<c>TelegramMessengerConnector.SendQuestionAsync</c>)
///     always populates the envelope JSON, so this is the
///     authoritative path for the most common DLQ source.
///   </description></item>
///   <item><description>
///     <see cref="OutboundSourceType.Alert"/> →
///     deserialise <see cref="OutboundMessage.SourceEnvelopeJson"/>
///     as <see cref="MessengerMessage"/> and read
///     <see cref="MessengerMessage.AgentId"/>. The connector
///     serialises the inbound <c>MessengerMessage</c> verbatim for
///     <c>Alert</c> sends (see
///     <c>TelegramMessengerConnector.SendMessageAsync</c>).
///   </description></item>
///   <item><description>
///     Any other source type, or any deserialisation failure → null.
///     The dead-letter row's <c>AgentId</c> column is nullable so
///     this is not an error — the operator just sees a blank value
///     for source types whose envelope shape does not carry a
///     single agent identity.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Failure semantics.</b> A malformed
/// <see cref="OutboundMessage.SourceEnvelopeJson"/> must NOT block
/// the dead-letter write — the operator's audit row is strictly
/// more useful than no row at all. Any
/// <see cref="JsonException"/> is swallowed and the extractor
/// returns null.
/// </para>
/// </remarks>
public static class AgentIdExtractor
{
    /// <summary>
    /// Returns the best-effort <c>AgentId</c> for the supplied
    /// <paramref name="message"/>, or null when the envelope shape
    /// does not carry one or when JSON parsing fails.
    /// </summary>
    public static string? TryExtract(OutboundMessage message)
    {
        if (message is null) return null;
        if (string.IsNullOrWhiteSpace(message.SourceEnvelopeJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(message.SourceEnvelopeJson!);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            switch (message.SourceType)
            {
                case OutboundSourceType.Question:
                    // AgentQuestionEnvelope wraps Question; we read
                    // root.Question.AgentId without instantiating the
                    // envelope (whose setters perform default-action
                    // validation irrelevant to dead-letter extraction).
                    if (root.TryGetProperty("Question", out var questionProp)
                        && questionProp.ValueKind == JsonValueKind.Object
                        && questionProp.TryGetProperty("AgentId", out var qAgentIdProp)
                        && qAgentIdProp.ValueKind == JsonValueKind.String)
                    {
                        var qid = qAgentIdProp.GetString();
                        return string.IsNullOrWhiteSpace(qid) ? null : qid;
                    }
                    return null;

                case OutboundSourceType.Alert:
                    // MessengerMessage shape — AgentId at the root.
                    if (root.TryGetProperty("AgentId", out var aAgentIdProp)
                        && aAgentIdProp.ValueKind == JsonValueKind.String)
                    {
                        var aid = aAgentIdProp.GetString();
                        return string.IsNullOrWhiteSpace(aid) ? null : aid;
                    }
                    return null;

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            // Deliberately swallowed — see remarks. A malformed
            // envelope must not block the DLQ write; the operator
            // audit row without AgentId is strictly more useful than
            // no row at all.
            return null;
        }
    }
}
