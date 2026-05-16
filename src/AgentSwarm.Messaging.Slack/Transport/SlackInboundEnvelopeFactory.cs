// -----------------------------------------------------------------------
// <copyright file="SlackInboundEnvelopeFactory.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Transport;

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Builds a <see cref="SlackInboundEnvelope"/> from a buffered ASP.NET
/// Core <see cref="HttpContext"/> after Stage 3.1's signature middleware
/// and Stage 3.2's authorization filter have run. Implements the Stage
/// 4.1 normalization step that every controller
/// (<see cref="SlackEventsController"/>,
/// <see cref="SlackCommandsController"/>,
/// <see cref="SlackInteractionsController"/>) shares.
/// </summary>
/// <remarks>
/// <para>
/// The factory is the single place where the Slack-published idempotency
/// key derivations from architecture.md §3.4 are encoded:
/// </para>
/// <list type="bullet">
///   <item><description><c>event:{event_id}</c> for Events API
///   callbacks.</description></item>
///   <item><description><c>cmd:{team_id}:{user_id}:{command}:{trigger_id}</c>
///   for slash commands.</description></item>
///   <item><description><c>interact:{team_id}:{user_id}:{action_or_view_id}:{trigger_id}</c>
///   for Block Kit / view-submission interactions.</description></item>
/// </list>
/// <para>
/// Missing components fall back to a sentinel hash of the raw payload so
/// the idempotency guard still receives a stable key (rather than a
/// collision-prone <c>"event::"</c>). Stage 4.3's
/// <c>SlackIdempotencyGuard</c> is the canonical consumer of these keys.
/// </para>
/// </remarks>
internal sealed class SlackInboundEnvelopeFactory
{
    private const string EventKeyPrefix = "event:";
    private const string CommandKeyPrefix = "cmd:";
    private const string InteractionKeyPrefix = "interact:";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TimeProvider timeProvider;

    public SlackInboundEnvelopeFactory(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Reads the buffered body off <paramref name="context"/> and
    /// constructs the matching <see cref="SlackInboundEnvelope"/>. The
    /// caller selects the <paramref name="sourceType"/> via the route
    /// (Events API, slash command, or interaction).
    /// </summary>
    public async Task<SlackInboundEnvelope> CreateAsync(
        HttpContext context,
        SlackInboundSourceType sourceType,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        string body = await ReadBufferedBodyAsync(context, ct).ConfigureAwait(false);
        return this.BuildEnvelope(sourceType, body);
    }

    /// <summary>
    /// Builds a <see cref="SlackInboundEnvelope"/> from an already-read
    /// body, stamping <see cref="SlackInboundEnvelope.ReceivedAt"/> via
    /// the injected <see cref="TimeProvider"/>. Controllers that need
    /// the body string BEFORE they can call the factory (e.g., the
    /// events controller has to peek at <c>type</c> for the
    /// url_verification handshake, the commands controller has to
    /// parse the sub-command for the modal fast-path) call this
    /// instance method instead of the test-only static
    /// <see cref="Build(SlackInboundSourceType, string, DateTimeOffset)"/>
    /// so the composition root's <see cref="TimeProvider"/> registration
    /// is not dead code (Stage 4.1 iter-3 evaluator item 4).
    /// </summary>
    public SlackInboundEnvelope BuildEnvelope(SlackInboundSourceType sourceType, string body)
        => Build(sourceType, body, this.timeProvider.GetUtcNow());

    /// <summary>
    /// Pure construction overload exposed for unit tests. The buffered
    /// body and timestamp are supplied directly so the call requires no
    /// <see cref="HttpContext"/>.
    /// </summary>
    public static SlackInboundEnvelope Build(
        SlackInboundSourceType sourceType,
        string body,
        DateTimeOffset receivedAt)
    {
        return sourceType switch
        {
            SlackInboundSourceType.Event => BuildEventEnvelope(body, receivedAt),
            SlackInboundSourceType.Command => BuildCommandEnvelope(body, receivedAt),
            SlackInboundSourceType.Interaction => BuildInteractionEnvelope(body, receivedAt),
            _ => throw new ArgumentOutOfRangeException(
                nameof(sourceType),
                sourceType,
                "SlackInboundSourceType.Unspecified is not accepted by the transport factory."),
        };
    }

    /// <summary>
    /// Reads the buffered request body and rewinds the underlying
    /// stream. The Stage 3.1 signature middleware already called
    /// <see cref="HttpRequestRewindExtensions.EnableBuffering(HttpRequest)"/>;
    /// this helper is idempotent so a host that wires controllers
    /// without the signature middleware still works in test harnesses.
    /// </summary>
    public static async Task<string> ReadBufferedBodyAsync(HttpContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Body.CanSeek)
        {
            context.Request.EnableBuffering();
        }
        else
        {
            context.Request.Body.Position = 0;
        }

        using MemoryStream buffer = new();
        await context.Request.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);

        if (context.Request.Body.CanSeek)
        {
            context.Request.Body.Position = 0;
        }

        return Utf8NoBom.GetString(buffer.ToArray());
    }

    private static SlackInboundEnvelope BuildEventEnvelope(string body, DateTimeOffset receivedAt)
    {
        SlackEventPayload payload = SlackInboundPayloadParser.ParseEvent(body);
        string idempotencyKey = DeriveEventKey(payload, body);
        return new SlackInboundEnvelope(
            IdempotencyKey: idempotencyKey,
            SourceType: SlackInboundSourceType.Event,
            TeamId: payload.TeamId ?? string.Empty,
            ChannelId: NullIfEmpty(payload.ChannelId),
            UserId: payload.UserId ?? string.Empty,
            RawPayload: body,
            TriggerId: null,
            ReceivedAt: receivedAt);
    }

    private static SlackInboundEnvelope BuildCommandEnvelope(string body, DateTimeOffset receivedAt)
    {
        SlackCommandPayload payload = SlackInboundPayloadParser.ParseCommand(body);
        string idempotencyKey = DeriveCommandKey(payload, body);
        return new SlackInboundEnvelope(
            IdempotencyKey: idempotencyKey,
            SourceType: SlackInboundSourceType.Command,
            TeamId: payload.TeamId ?? string.Empty,
            ChannelId: NullIfEmpty(payload.ChannelId),
            UserId: payload.UserId ?? string.Empty,
            RawPayload: body,
            TriggerId: NullIfEmpty(payload.TriggerId),
            ReceivedAt: receivedAt);
    }

    private static SlackInboundEnvelope BuildInteractionEnvelope(string body, DateTimeOffset receivedAt)
    {
        SlackInteractionPayload payload = SlackInboundPayloadParser.ParseInteraction(body);
        string idempotencyKey = DeriveInteractionKey(payload, body);
        return new SlackInboundEnvelope(
            IdempotencyKey: idempotencyKey,
            SourceType: SlackInboundSourceType.Interaction,
            TeamId: payload.TeamId ?? string.Empty,
            ChannelId: NullIfEmpty(payload.ChannelId),
            UserId: payload.UserId ?? string.Empty,
            RawPayload: body,
            TriggerId: NullIfEmpty(payload.TriggerId),
            ReceivedAt: receivedAt);
    }

    private static string DeriveEventKey(SlackEventPayload payload, string body)
    {
        if (!string.IsNullOrEmpty(payload.EventId))
        {
            return EventKeyPrefix + payload.EventId;
        }

        // Fallback: hash of the raw body. Slack's at-least-once retry
        // contract guarantees event_id is repeated across retries; if a
        // payload arrives without an event_id at all (e.g., a
        // malformed test fixture) we degrade to body-hash dedup so
        // the idempotency guard still has a stable key to insert.
        return EventKeyPrefix + HashFallback(body);
    }

    private static string DeriveCommandKey(SlackCommandPayload payload, string body)
    {
        string team = payload.TeamId ?? string.Empty;
        string user = payload.UserId ?? string.Empty;
        string cmd = payload.Command ?? string.Empty;
        string trigger = payload.TriggerId ?? HashFallback(body);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1}:{2}:{3}:{4}",
            CommandKeyPrefix,
            team,
            user,
            cmd,
            trigger);
    }

    private static string DeriveInteractionKey(SlackInteractionPayload payload, string body)
    {
        string team = payload.TeamId ?? string.Empty;
        string user = payload.UserId ?? string.Empty;
        string actionOrView = payload.ActionOrViewId ?? HashFallback(body);
        string trigger = payload.TriggerId ?? actionOrView;

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1}:{2}:{3}:{4}",
            InteractionKeyPrefix,
            team,
            user,
            actionOrView,
            trigger);
    }

    private static string HashFallback(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return "empty";
        }

        // SHA-256 hex of the raw body. Used only when Slack omitted the
        // expected idempotency token; the resulting key remains stable
        // across retries because Slack repeats the same body verbatim.
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Utf8NoBom.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
