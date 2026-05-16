// -----------------------------------------------------------------------
// <copyright file="SlackInboundPayloadParser.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System.Collections.Generic;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

/// <summary>
/// Pure parser that decodes a Slack inbound HTTP body into the canonical
/// field set <see cref="SlackEventsController"/>,
/// <see cref="SlackCommandsController"/>, and
/// <see cref="SlackInteractionsController"/> use to build a
/// <see cref="SlackInboundEnvelope"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The Stage 3.1 signature middleware buffers and rewinds the request
/// body so this parser only has to deal with strings.
/// </para>
/// <para>
/// The parser recognises every Slack inbound surface accepted by Stage
/// 4.1:
/// </para>
/// <list type="bullet">
///   <item><description>Events API callbacks (<c>application/json</c>
///   bodies with top-level <c>event_id</c>, <c>team_id</c>, and
///   <c>event</c> sub-object) -- including the
///   <c>url_verification</c> handshake which carries
///   <c>type = url_verification</c> and a <c>challenge</c>
///   string.</description></item>
///   <item><description>Slash commands
///   (<c>application/x-www-form-urlencoded</c> with <c>team_id</c>,
///   <c>channel_id</c>, <c>user_id</c>, <c>command</c>, <c>text</c>,
///   and <c>trigger_id</c> form fields).</description></item>
///   <item><description>Interactive payloads
///   (<c>application/x-www-form-urlencoded</c> with a <c>payload</c>
///   form field carrying JSON with nested <c>team.id</c>,
///   <c>channel.id</c>, <c>user.id</c>, <c>trigger_id</c>, and either
///   <c>actions[]</c> (button clicks) or <c>view.id</c>
///   (<c>view_submission</c>)).</description></item>
/// </list>
/// </remarks>
internal static class SlackInboundPayloadParser
{
    /// <summary>
    /// Slack Events API URL-verification payload discriminator
    /// (<c>type = url_verification</c>) per architecture.md §5.6.
    /// </summary>
    public const string UrlVerificationType = "url_verification";

    /// <summary>
    /// Slack Events API event-callback envelope discriminator
    /// (<c>type = event_callback</c>) per architecture.md §3.4.
    /// </summary>
    public const string EventCallbackType = "event_callback";

    /// <summary>
    /// Parses <paramref name="body"/> as an Events API callback or
    /// URL-verification handshake. Returns
    /// <see cref="SlackEventPayload.Empty"/> when the body cannot be
    /// parsed as JSON or does not match either expected shape.
    /// </summary>
    public static SlackEventPayload ParseEvent(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return SlackEventPayload.Empty;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return SlackEventPayload.Empty;
            }

            string? type = ReadStringProperty(root, "type");
            string? challenge = ReadStringProperty(root, "challenge");
            string? eventId = ReadStringProperty(root, "event_id");
            string? teamId = ReadStringProperty(root, "team_id");

            // Some workspace-level Events API callbacks carry team_id only as
            // team.id rather than as a top-level string.
            if (string.IsNullOrEmpty(teamId)
                && root.TryGetProperty("team", out JsonElement teamObj)
                && teamObj.ValueKind == JsonValueKind.Object)
            {
                teamId = ReadStringProperty(teamObj, "id");
            }

            string? channelId = null;
            string? userId = null;
            string? eventSubtype = null;

            if (root.TryGetProperty("event", out JsonElement evt) && evt.ValueKind == JsonValueKind.Object)
            {
                eventSubtype = ReadStringProperty(evt, "type");
                channelId = ReadStringProperty(evt, "channel");
                userId = ReadStringProperty(evt, "user");

                if (string.IsNullOrEmpty(channelId)
                    && evt.TryGetProperty("channel", out JsonElement evtChannelObj)
                    && evtChannelObj.ValueKind == JsonValueKind.Object)
                {
                    channelId = ReadStringProperty(evtChannelObj, "id");
                }

                if (string.IsNullOrEmpty(userId)
                    && evt.TryGetProperty("user", out JsonElement evtUserObj)
                    && evtUserObj.ValueKind == JsonValueKind.Object)
                {
                    userId = ReadStringProperty(evtUserObj, "id");
                }
            }

            return new SlackEventPayload(
                Type: type,
                Challenge: challenge,
                EventId: eventId,
                TeamId: teamId,
                ChannelId: channelId,
                UserId: userId,
                EventSubtype: eventSubtype);
        }
        catch (JsonException)
        {
            return SlackEventPayload.Empty;
        }
    }

    /// <summary>
    /// Parses <paramref name="body"/> as a slash command form payload.
    /// </summary>
    public static SlackCommandPayload ParseCommand(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return SlackCommandPayload.Empty;
        }

        IDictionary<string, StringValues> fields = QueryHelpers.ParseQuery(body);

        string? command = GetFormValue(fields, "command");
        string? text = GetFormValue(fields, "text");

        return new SlackCommandPayload(
            TeamId: GetFormValue(fields, "team_id"),
            ChannelId: GetFormValue(fields, "channel_id"),
            UserId: GetFormValue(fields, "user_id"),
            Command: command,
            Text: text,
            TriggerId: GetFormValue(fields, "trigger_id"),
            SubCommand: ParseSubCommand(text));
    }

    /// <summary>
    /// Parses <paramref name="body"/> as an interactive Block Kit /
    /// view-submission payload. Slack wraps the JSON document in a
    /// <c>payload</c> form field; the inner JSON carries
    /// <c>type</c>, <c>team.id</c>, <c>channel.id</c>, <c>user.id</c>,
    /// <c>trigger_id</c>, and either <c>actions[]</c> or
    /// <c>view.id</c>.
    /// </summary>
    public static SlackInteractionPayload ParseInteraction(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return SlackInteractionPayload.Empty;
        }

        IDictionary<string, StringValues> fields = QueryHelpers.ParseQuery(body);
        if (!fields.TryGetValue("payload", out StringValues payloadValues) || StringValues.IsNullOrEmpty(payloadValues))
        {
            return SlackInteractionPayload.Empty;
        }

        return ParseInteractionPayloadJson(payloadValues.ToString());
    }

    /// <summary>
    /// Parses a Slack slash-command <c>text</c> field into its leading
    /// sub-command token (e.g., <c>"ask"</c>, <c>"review"</c>). Returns
    /// <see langword="null"/> when the text is empty or the first token
    /// cannot be extracted.
    /// </summary>
    public static string? ParseSubCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        ReadOnlySpan<char> span = text.AsSpan().TrimStart();
        if (span.IsEmpty)
        {
            return null;
        }

        int end = 0;
        while (end < span.Length && !char.IsWhiteSpace(span[end]))
        {
            end++;
        }

        return end == 0 ? null : span[..end].ToString().ToLowerInvariant();
    }

    private static SlackInteractionPayload ParseInteractionPayloadJson(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return SlackInteractionPayload.Empty;
            }

            string? type = ReadStringProperty(root, "type");
            string? triggerId = ReadStringProperty(root, "trigger_id");

            string? teamId = null;
            if (root.TryGetProperty("team", out JsonElement teamObj) && teamObj.ValueKind == JsonValueKind.Object)
            {
                teamId = ReadStringProperty(teamObj, "id");
            }

            string? channelId = null;
            if (root.TryGetProperty("channel", out JsonElement channelObj) && channelObj.ValueKind == JsonValueKind.Object)
            {
                channelId = ReadStringProperty(channelObj, "id");
            }

            string? userId = null;
            if (root.TryGetProperty("user", out JsonElement userObj) && userObj.ValueKind == JsonValueKind.Object)
            {
                userId = ReadStringProperty(userObj, "id");
            }

            string? actionOrViewId = null;
            if (root.TryGetProperty("actions", out JsonElement actions)
                && actions.ValueKind == JsonValueKind.Array
                && actions.GetArrayLength() > 0)
            {
                JsonElement firstAction = actions[0];
                if (firstAction.ValueKind == JsonValueKind.Object)
                {
                    actionOrViewId = ReadStringProperty(firstAction, "action_id");
                }
            }

            if (string.IsNullOrEmpty(actionOrViewId)
                && root.TryGetProperty("view", out JsonElement view)
                && view.ValueKind == JsonValueKind.Object)
            {
                actionOrViewId = ReadStringProperty(view, "id");
            }

            return new SlackInteractionPayload(
                Type: type,
                TeamId: teamId,
                ChannelId: channelId,
                UserId: userId,
                TriggerId: triggerId,
                ActionOrViewId: actionOrViewId);
        }
        catch (JsonException)
        {
            return SlackInteractionPayload.Empty;
        }
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

/// <summary>
/// Canonical field set extracted from an Events API HTTP body.
/// </summary>
internal readonly record struct SlackEventPayload(
    string? Type,
    string? Challenge,
    string? EventId,
    string? TeamId,
    string? ChannelId,
    string? UserId,
    string? EventSubtype)
{
    public static SlackEventPayload Empty { get; } =
        new(null, null, null, null, null, null, null);

    /// <summary>True for the Events API URL-verification handshake.</summary>
    public bool IsUrlVerification =>
        string.Equals(this.Type, SlackInboundPayloadParser.UrlVerificationType, StringComparison.Ordinal)
        && !string.IsNullOrEmpty(this.Challenge);
}

/// <summary>
/// Canonical field set extracted from a slash-command form body.
/// </summary>
internal readonly record struct SlackCommandPayload(
    string? TeamId,
    string? ChannelId,
    string? UserId,
    string? Command,
    string? Text,
    string? TriggerId,
    string? SubCommand)
{
    public static SlackCommandPayload Empty { get; } =
        new(null, null, null, null, null, null, null);
}

/// <summary>
/// Canonical field set extracted from a Block Kit / view-submission
/// interactive payload.
/// </summary>
internal readonly record struct SlackInteractionPayload(
    string? Type,
    string? TeamId,
    string? ChannelId,
    string? UserId,
    string? TriggerId,
    string? ActionOrViewId)
{
    public static SlackInteractionPayload Empty { get; } =
        new(null, null, null, null, null, null);
}
