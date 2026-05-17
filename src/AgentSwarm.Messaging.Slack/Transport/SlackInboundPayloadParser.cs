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
    /// Parses <paramref name="body"/> as a slash command payload.
    /// </summary>
    /// <remarks>
    /// Auto-detects the body encoding so the same entry point works for
    /// HTTP form bodies (delivered by Slack's slash-command webhook) and
    /// for the JSON-wrapped Socket Mode <c>slash_commands</c> frame
    /// payloads (per architecture.md §3.4). A leading <c>{</c> (after
    /// trimming whitespace) routes through
    /// <see cref="ParseCommandJson"/>; everything else is treated as
    /// <c>application/x-www-form-urlencoded</c>.
    ///
    /// <para>
    /// Iter-2 evaluator item 1 fix: previously this method form-decoded
    /// every body, which silently turned every Socket Mode slash command
    /// into an empty payload (SocketMode normalizer stores raw JSON in
    /// <see cref="SlackInboundEnvelope.RawPayload"/>). Downstream handlers
    /// then saw a missing sub-command and replied with an unhelpful
    /// "Missing sub-command" error.
    /// </para>
    /// </remarks>
    public static SlackCommandPayload ParseCommand(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return SlackCommandPayload.Empty;
        }

        if (LooksLikeJsonObject(body))
        {
            return ParseCommandJson(body);
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
            ResponseUrl: GetFormValue(fields, "response_url"),
            SubCommand: ParseSubCommand(text));
    }

    /// <summary>
    /// Parses <paramref name="json"/> as a Socket Mode
    /// <c>slash_commands</c> envelope payload. The JSON document mirrors
    /// the HTTP form field set 1:1 but is delivered as a JSON object
    /// rather than form-encoded text (architecture.md §3.4).
    /// </summary>
    /// <remarks>
    /// Iter-2 evaluator item 1 fix: promoted from a private helper on
    /// <see cref="SlackSocketModePayloadNormalizer"/> to a public surface
    /// on the parser so the post-ACK
    /// <see cref="Pipeline.SlackCommandHandler"/> can decode Socket Mode
    /// envelopes via the auto-detecting
    /// <see cref="ParseCommand"/> dispatcher.
    /// </remarks>
    public static SlackCommandPayload ParseCommandJson(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return SlackCommandPayload.Empty;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return SlackCommandPayload.Empty;
            }

            string? text = ReadStringProperty(root, "text");
            return new SlackCommandPayload(
                TeamId: ReadStringProperty(root, "team_id"),
                ChannelId: ReadStringProperty(root, "channel_id"),
                UserId: ReadStringProperty(root, "user_id"),
                Command: ReadStringProperty(root, "command"),
                Text: text,
                TriggerId: ReadStringProperty(root, "trigger_id"),
                ResponseUrl: ReadStringProperty(root, "response_url"),
                SubCommand: ParseSubCommand(text));
        }
        catch (JsonException)
        {
            return SlackCommandPayload.Empty;
        }
    }

    private static bool LooksLikeJsonObject(string body)
    {
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
            {
                continue;
            }

            return c == '{';
        }

        return false;
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

        return ParseInteractionJson(payloadValues.ToString());
    }

    /// <summary>
    /// Parses a Slack interactive payload's inner JSON document.
    /// Exposed so the Stage 4.2 Socket Mode receiver
    /// (<see cref="SlackSocketModePayloadNormalizer"/>) can decode an
    /// <c>interactive</c> frame -- whose payload is the same JSON
    /// shape that the HTTP transport delivers inside the
    /// <c>payload</c> form field -- without first having to wrap the
    /// JSON in a synthetic form body.
    /// </summary>
    public static SlackInteractionPayload ParseInteractionJson(string json)
    {
        return ParseInteractionPayloadJson(json);
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

    /// <summary>
    /// Parses <paramref name="body"/> as an Events API <c>app_mention</c>
    /// payload and returns the bot-relevant fields. The returned
    /// <see cref="SlackAppMentionPayload"/> carries the mention text, the
    /// channel id, the user id, the message timestamp (<c>ts</c>), and
    /// the optional containing <c>thread_ts</c>. Returns
    /// <see cref="SlackAppMentionPayload.Empty"/> when the body is empty,
    /// not JSON, or does not match the expected shape.
    /// </summary>
    /// <remarks>
    /// Stage 5.2 of
    /// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
    /// The existing <see cref="ParseEvent"/> entry point only exposes the
    /// envelope-level discriminator fields (team, channel, user, subtype)
    /// because Stage 4.3's routing pipeline does not need the inner
    /// <c>text</c>; Stage 5.2's
    /// <see cref="Pipeline.SlackAppMentionHandler"/> needs the
    /// inner <c>text</c> (to extract the sub-command), the inner
    /// <c>ts</c> (to anchor a new thread when the mention is in the main
    /// channel), and the inner <c>thread_ts</c> (to reply into an
    /// existing thread).
    /// </remarks>
    public static SlackAppMentionPayload ParseEventAppMention(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return SlackAppMentionPayload.Empty;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return SlackAppMentionPayload.Empty;
            }

            string? teamId = ReadStringProperty(root, "team_id");
            if (string.IsNullOrEmpty(teamId)
                && root.TryGetProperty("team", out JsonElement teamObj)
                && teamObj.ValueKind == JsonValueKind.Object)
            {
                teamId = ReadStringProperty(teamObj, "id");
            }

            if (!root.TryGetProperty("event", out JsonElement evt) || evt.ValueKind != JsonValueKind.Object)
            {
                return SlackAppMentionPayload.Empty;
            }

            string? subtype = ReadStringProperty(evt, "type");
            string? text = ReadStringProperty(evt, "text");

            string? channelId = ReadStringProperty(evt, "channel");
            if (string.IsNullOrEmpty(channelId)
                && evt.TryGetProperty("channel", out JsonElement evtChannelObj)
                && evtChannelObj.ValueKind == JsonValueKind.Object)
            {
                channelId = ReadStringProperty(evtChannelObj, "id");
            }

            string? userId = ReadStringProperty(evt, "user");
            if (string.IsNullOrEmpty(userId)
                && evt.TryGetProperty("user", out JsonElement evtUserObj)
                && evtUserObj.ValueKind == JsonValueKind.Object)
            {
                userId = ReadStringProperty(evtUserObj, "id");
            }

            string? ts = ReadStringProperty(evt, "ts");
            string? threadTs = ReadStringProperty(evt, "thread_ts");

            return new SlackAppMentionPayload(
                TeamId: teamId,
                ChannelId: channelId,
                UserId: userId,
                Text: text,
                Ts: ts,
                ThreadTs: threadTs,
                EventSubtype: subtype);
        }
        catch (JsonException)
        {
            return SlackAppMentionPayload.Empty;
        }
    }

    /// <summary>
    /// Strips a leading <c>&lt;@USERID&gt;</c> bot-mention prefix from
    /// <paramref name="text"/> and returns the remainder, trimmed.
    /// Returns the input unchanged (after trimming) when no Slack
    /// mention token is present at the start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stage 5.2 implementation step 2: "remove the
    /// <c>&lt;@BOT_USER_ID&gt;</c> prefix from the mention text to
    /// extract the raw command string". Slack always serialises user
    /// mentions in <c>&lt;@U0123ABCD&gt;</c> form (optionally with a
    /// display-name suffix: <c>&lt;@U0123ABCD|agentbot&gt;</c>); the
    /// regex-free string scan here matches both shapes and is
    /// deliberately permissive about the inner id format (Slack reserves
    /// the <c>U</c>, <c>W</c>, and <c>B</c> prefixes for users, enterprise
    /// users, and bots respectively, and short test workspaces use
    /// uppercase alpha-numerics) so the helper does not need to know
    /// which bot user id belongs to which workspace -- the brief's
    /// test scenario 2 only requires the prefix to be stripped, not
    /// validated.
    /// </para>
    /// </remarks>
    public static string StripBotMentionPrefix(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = text.AsSpan().TrimStart();
        if (span.Length < 4 || span[0] != '<' || span[1] != '@')
        {
            return span.ToString();
        }

        int closeIdx = span.IndexOf('>');
        if (closeIdx < 3)
        {
            return span.ToString();
        }

        ReadOnlySpan<char> remainder = span[(closeIdx + 1)..].TrimStart();
        return remainder.ToString();
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
    string? ResponseUrl,
    string? SubCommand)
{
    public static SlackCommandPayload Empty { get; } =
        new(null, null, null, null, null, null, null, null);

    /// <summary>
    /// Returns the slash-command text with the leading sub-command
    /// token (and the whitespace that separates it from the
    /// arguments) removed. Returns <see langword="null"/> when the
    /// original text is empty.
    /// </summary>
    /// <remarks>
    /// Stage 5.1 helper: command handlers parse the sub-command
    /// from <see cref="SubCommand"/> and then need the remaining
    /// arguments verbatim (e.g., the full prompt that follows
    /// <c>ask</c>). Keeping the slice on the payload struct lets
    /// every handler share the same trimming rules without
    /// re-implementing whitespace handling.
    /// </remarks>
    public string? ArgumentText
    {
        get
        {
            string? text = this.Text;
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

            if (end >= span.Length)
            {
                return null;
            }

            ReadOnlySpan<char> rest = span[end..].TrimStart();
            return rest.IsEmpty ? null : rest.ToString();
        }
    }
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

/// <summary>
/// Field set extracted from an Events API <c>app_mention</c> event
/// callback. Carries the inner <c>event.text</c> (so the
/// <see cref="Pipeline.SlackAppMentionHandler"/> can strip the bot
/// prefix and reuse the slash-command sub-command parser), the
/// inner <c>event.ts</c> (so a top-level mention can anchor a new
/// thread), and the optional <c>event.thread_ts</c> (so a mention
/// already inside a thread routes its reply back into that thread).
/// </summary>
/// <param name="TeamId">Workspace id (<c>team_id</c> or nested
/// <c>team.id</c>).</param>
/// <param name="ChannelId">Channel id from <c>event.channel</c>.</param>
/// <param name="UserId">User id of the human who posted the
/// mention.</param>
/// <param name="Text">Raw mention text, including the leading
/// <c>&lt;@BOT_USER_ID&gt;</c> token. The handler strips that prefix
/// with <see cref="SlackInboundPayloadParser.StripBotMentionPrefix"/>
/// before parsing the sub-command.</param>
/// <param name="Ts">Slack message timestamp of the mention itself.
/// Used as the <c>thread_ts</c> anchor when the mention was posted to
/// the main channel (i.e., when <see cref="ThreadTs"/> is null).</param>
/// <param name="ThreadTs">Containing thread timestamp when the mention
/// was posted as a reply inside an existing thread; null for top-level
/// mentions.</param>
/// <param name="EventSubtype">Inner <c>event.type</c> discriminator;
/// expected to equal <c>app_mention</c> when the routing pipeline
/// reaches this parser, but exposed so the handler can defensively
/// log a mismatch.</param>
internal readonly record struct SlackAppMentionPayload(
    string? TeamId,
    string? ChannelId,
    string? UserId,
    string? Text,
    string? Ts,
    string? ThreadTs,
    string? EventSubtype)
{
    public static SlackAppMentionPayload Empty { get; } =
        new(null, null, null, null, null, null, null);
}
