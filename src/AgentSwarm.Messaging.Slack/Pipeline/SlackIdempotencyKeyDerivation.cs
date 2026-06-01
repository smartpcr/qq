// -----------------------------------------------------------------------
// <copyright file="SlackIdempotencyKeyDerivation.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System;
using System.Globalization;
using AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Pure, testable encoding of the architecture.md §3.4 idempotency
/// key derivation rules. The Stage 4.1
/// <see cref="SlackInboundEnvelopeFactory"/> already produces keys
/// in this format; this helper exists so Stage 4.3 tests (and any
/// future consumer that needs to recompute a key for an out-of-band
/// envelope, e.g. a DLQ replay tool) have a single authoritative
/// derivation point.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4.3 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>:
/// implementation step 3 mandates "implement idempotency key
/// derivation per architecture.md section 3.4". The factory does the
/// transport-side derivation; this helper pins the contract for the
/// async ingestor's tests so an evolving factory cannot silently
/// drift away from the architecture-specified key shape.
/// </para>
/// <para>
/// All three derivation rules use case-sensitive concatenation; Slack
/// identifiers (<c>team_id</c>, <c>user_id</c>, <c>channel_id</c>,
/// <c>event_id</c>, <c>trigger_id</c>, <c>action_id</c>, <c>view_id</c>)
/// are already case-stable, so no normalisation is applied.
/// </para>
/// </remarks>
internal static class SlackIdempotencyKeyDerivation
{
    /// <summary>Prefix applied to Events API keys.</summary>
    public const string EventPrefix = "event:";

    /// <summary>Prefix applied to slash-command keys.</summary>
    public const string CommandPrefix = "cmd:";

    /// <summary>Prefix applied to interaction keys.</summary>
    public const string InteractionPrefix = "interact:";

    /// <summary>
    /// Builds the Events API key <c>event:{eventId}</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when
    /// <paramref name="eventId"/> is null or empty.</exception>
    public static string ForEvent(string eventId)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);
        return EventPrefix + eventId;
    }

    /// <summary>
    /// Builds the slash-command key
    /// <c>cmd:{teamId}:{userId}:{command}:{triggerId}</c>.
    /// </summary>
    public static string ForCommand(string teamId, string userId, string command, string triggerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentException.ThrowIfNullOrEmpty(command);
        ArgumentException.ThrowIfNullOrEmpty(triggerId);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1}:{2}:{3}:{4}",
            CommandPrefix,
            teamId,
            userId,
            command,
            triggerId);
    }

    /// <summary>
    /// Builds the interaction key
    /// <c>interact:{teamId}:{userId}:{actionOrViewId}:{triggerId}</c>.
    /// </summary>
    public static string ForInteraction(string teamId, string userId, string actionOrViewId, string triggerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentException.ThrowIfNullOrEmpty(actionOrViewId);
        ArgumentException.ThrowIfNullOrEmpty(triggerId);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1}:{2}:{3}:{4}",
            InteractionPrefix,
            teamId,
            userId,
            actionOrViewId,
            triggerId);
    }
}
