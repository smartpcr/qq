// -----------------------------------------------------------------------
// <copyright file="SlackInboundIdentityExtractor.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

/// <summary>
/// Helper used by <see cref="SlackAuthorizationFilter"/> to extract the
/// three identity fields enforced by Stage 3.2's ACL (<c>team_id</c>,
/// <c>channel_id</c>, <c>user_id</c>) plus the raw slash-command text
/// from a Slack inbound HTTP request.
/// </summary>
/// <remarks>
/// <para>
/// Stage 3.2 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>.
/// The Stage 3.1 <see cref="SlackSignatureValidator"/> already buffers the
/// request body via <c>HttpRequest.EnableBuffering</c> and rewinds it on
/// success, so the filter can re-read the same bytes without disturbing
/// model binding. This extractor is the single place where the
/// JSON / form / nested-payload shape variations are parsed.
/// </para>
/// <para>
/// The extractor recognises the three Slack inbound surfaces:
/// </para>
/// <list type="bullet">
///   <item><description>Events API callbacks (<c>application/json</c>):
///   top-level <c>team_id</c>; <c>event.channel</c> / <c>event.user</c>
///   for channel-scoped events.</description></item>
///   <item><description>Slash commands
///   (<c>application/x-www-form-urlencoded</c>): top-level
///   <c>team_id</c>, <c>channel_id</c>, <c>user_id</c>, <c>text</c>,
///   and <c>command</c> form fields.</description></item>
///   <item><description>Interactions
///   (<c>application/x-www-form-urlencoded</c>): a <c>payload</c> form
///   field carrying a JSON document with nested <c>team.id</c>,
///   <c>channel.id</c>, and <c>user.id</c>.</description></item>
/// </list>
/// </remarks>
internal static class SlackInboundIdentityExtractor
{
    private const int DefaultReadLimitBytes = 1 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Buffers <paramref name="context"/>'s request body (which the
    /// signature middleware has already enabled buffering for) and
    /// parses out the three ACL fields plus the slash-command text.
    /// </summary>
    /// <param name="context">Inbound HTTP context.</param>
    /// <param name="maxBufferedBodyBytes">
    /// Defensive upper bound on the bytes read from the body. Defaults
    /// to 1&nbsp;MiB to mirror
    /// <see cref="AgentSwarm.Messaging.Slack.Configuration.SlackSignatureOptions.MaxBufferedBodyBytes"/>.
    /// </param>
    public static async Task<SlackInboundIdentity> ExtractAsync(
        HttpContext context,
        int maxBufferedBodyBytes = DefaultReadLimitBytes)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Body.CanSeek)
        {
            context.Request.EnableBuffering(bufferThreshold: 32 * 1024, bufferLimit: maxBufferedBodyBytes);
        }

        string body = await ReadBodyAsync(context.Request, maxBufferedBodyBytes, context.RequestAborted)
            .ConfigureAwait(false);

        return Parse(body, context.Request.ContentType);
    }

    /// <summary>
    /// Pure parser exposed for unit tests. Accepts the verbatim request
    /// body and content type and returns the parsed identity record.
    /// </summary>
    public static SlackInboundIdentity Parse(string body, string? contentType)
    {
        if (string.IsNullOrEmpty(body))
        {
            return SlackInboundIdentity.Empty;
        }

        string trimmed = body.AsSpan().TrimStart().ToString();
        if (trimmed.StartsWith('{'))
        {
            return ParseFromJson(trimmed);
        }

        return ParseFromForm(body, contentType);
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        using MemoryStream buffer = new();
        await request.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
        }

        if (buffer.Length > maxBytes)
        {
            // Belt-and-braces: in practice the signature middleware
            // already enforces MaxBufferedBodyBytes. If a host wires
            // the authorization filter without the signature middleware
            // (e.g., an integration test) we still refuse to materialise
            // an unbounded string.
            return string.Empty;
        }

        return Utf8NoBom.GetString(buffer.ToArray());
    }

    private static SlackInboundIdentity ParseFromJson(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return SlackInboundIdentity.Empty;
            }

            string? teamId = ReadStringProperty(root, "team_id");
            if (string.IsNullOrEmpty(teamId)
                && root.TryGetProperty("team", out JsonElement teamObj)
                && teamObj.ValueKind == JsonValueKind.Object)
            {
                teamId = ReadStringProperty(teamObj, "id");
            }

            // Stage 4.1 iter-2 evaluator item 1: capture the top-level
            // payload discriminator so SlackAuthorizationFilter can
            // recognise view_submission modals (which Slack delivers
            // with no channel context) and let them through the
            // channel ACL while still enforcing workspace + user-group
            // ACL. The value is the raw Slack `type` field --
            // url_verification, event_callback, block_actions,
            // view_submission, etc.
            string? payloadType = ReadStringProperty(root, "type");

            string? channelId = null;
            string? userId = null;

            // Top-level channel/user objects (interactions JSON payloads).
            if (root.TryGetProperty("channel", out JsonElement channelObj)
                && channelObj.ValueKind == JsonValueKind.Object)
            {
                channelId = ReadStringProperty(channelObj, "id");
            }

            if (root.TryGetProperty("user", out JsonElement userObj)
                && userObj.ValueKind == JsonValueKind.Object)
            {
                userId = ReadStringProperty(userObj, "id");
            }

            // Event-callback envelope nests channel / user inside
            // "event" (sometimes as a bare string, sometimes as an
            // object). Prefer the nested values only when the top-level
            // ones were absent.
            if (root.TryGetProperty("event", out JsonElement evt) && evt.ValueKind == JsonValueKind.Object)
            {
                if (string.IsNullOrEmpty(teamId))
                {
                    teamId = ReadStringProperty(evt, "team");
                }

                channelId ??= ReadStringProperty(evt, "channel");
                userId ??= ReadStringProperty(evt, "user");

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

            return new SlackInboundIdentity(teamId, channelId, userId, CommandText: null)
            {
                PayloadType = payloadType,
            };
        }
        catch (JsonException)
        {
            return SlackInboundIdentity.Empty;
        }
    }

    private static SlackInboundIdentity ParseFromForm(string body, string? contentType)
    {
        _ = contentType;
        IDictionary<string, StringValues> fields = QueryHelpers.ParseQuery(body);

        // Interactions wrap the JSON inside a "payload" field; commands
        // carry team_id / channel_id / user_id directly.
        if (fields.TryGetValue("payload", out StringValues payloadValues)
            && !StringValues.IsNullOrEmpty(payloadValues))
        {
            SlackInboundIdentity fromPayload = ParseFromJson(payloadValues.ToString());
            if (!fromPayload.IsEmpty)
            {
                return fromPayload;
            }
        }

        string? teamId = GetFormValue(fields, "team_id");
        string? channelId = GetFormValue(fields, "channel_id");
        string? userId = GetFormValue(fields, "user_id");
        string? commandText = BuildCommandText(fields);

        return new SlackInboundIdentity(teamId, channelId, userId, commandText);
    }

    private static string? GetFormValue(IDictionary<string, StringValues> fields, string key)
    {
        return fields.TryGetValue(key, out StringValues values) && !StringValues.IsNullOrEmpty(values)
            ? values.ToString()
            : null;
    }

    private static string? BuildCommandText(IDictionary<string, StringValues> fields)
    {
        string? command = GetFormValue(fields, "command");
        string? text = GetFormValue(fields, "text");

        if (string.IsNullOrEmpty(command) && string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (string.IsNullOrEmpty(command))
        {
            return text;
        }

        if (string.IsNullOrEmpty(text))
        {
            return command;
        }

        return FormattableString.Invariant($"{command} {text}");
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
/// Identity fields extracted from a Slack inbound payload. Every field is
/// nullable because not every Slack surface carries every value (e.g.,
/// workspace-level events have no channel, Events API callbacks have no
/// slash-command text).
/// </summary>
/// <param name="TeamId">Slack <c>team_id</c> when present.</param>
/// <param name="ChannelId">Slack <c>channel_id</c> when present.</param>
/// <param name="UserId">Slack <c>user_id</c> when present.</param>
/// <param name="CommandText">
/// Raw slash-command text (the combined <c>command</c> + <c>text</c>
/// form fields), or <see langword="null"/> for non-command payloads.
/// </param>
internal readonly record struct SlackInboundIdentity(
    string? TeamId,
    string? ChannelId,
    string? UserId,
    string? CommandText)
{
    /// <summary>
    /// Optional raw Slack payload discriminator (<c>type</c>) -- e.g.,
    /// <c>url_verification</c>, <c>event_callback</c>,
    /// <c>block_actions</c>, <c>view_submission</c>. Populated by the
    /// JSON parser path (Events API callbacks and interaction
    /// payloads); form-encoded slash commands leave it
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Stage 4.1 iter-2 evaluator item 1:
    /// <see cref="SlackAuthorizationFilter"/> reads
    /// <c>PayloadType == "view_submission"</c> to skip the channel
    /// ACL on modal submissions, which Slack delivers without a
    /// <c>channel.id</c> by design (architecture.md §5.3).
    /// </remarks>
    public string? PayloadType { get; init; }

    /// <summary>An identity record with every field <see langword="null"/>.</summary>
    public static SlackInboundIdentity Empty { get; } = new(null, null, null, null);

    /// <summary>True when every field is <see langword="null"/> or empty.</summary>
    public bool IsEmpty =>
        string.IsNullOrEmpty(this.TeamId)
        && string.IsNullOrEmpty(this.ChannelId)
        && string.IsNullOrEmpty(this.UserId)
        && string.IsNullOrEmpty(this.CommandText);
}
