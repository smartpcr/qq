// -----------------------------------------------------------------------
// <copyright file="SlackDeadLetterEntry.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Queues;

using System;
using AgentSwarm.Messaging.Slack.Transport;

/// <summary>
/// Represents a Slack message that has exhausted its retry budget and
/// has been moved to the durable dead-letter queue. The entry preserves
/// the original payload (inbound or outbound) along with enough metadata
/// for an operator to triage, replay, or discard the message.
/// </summary>
/// <remarks>
/// Declared <c>internal</c> to match the visibility of the dead-letter
/// queue contract (<see cref="ISlackDeadLetterQueue"/>) and the inbound
/// /outbound envelope types it carries. The Slack test assembly observes
/// it through <c>[assembly: InternalsVisibleTo]</c>.
/// </remarks>
internal sealed record SlackDeadLetterEntry
{
    /// <summary>
    /// Gets the stable identifier assigned to this dead-letter entry.
    /// </summary>
    public required Guid EntryId { get; init; }

    /// <summary>
    /// Gets the queue side (inbound or outbound) the failed payload
    /// originated from. This discriminator determines the runtime type
    /// of <see cref="Payload"/>.
    /// </summary>
    public required SlackDeadLetterSource Source { get; init; }

    /// <summary>
    /// Gets the human-readable reason the message was dead-lettered.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Gets the .NET type name of the terminal exception, when available.
    /// </summary>
    public string? ExceptionType { get; init; }

    /// <summary>
    /// Gets the number of delivery or processing attempts performed
    /// before the message was moved to the dead-letter queue.
    /// </summary>
    public required int AttemptCount { get; init; }

    /// <summary>
    /// Gets the UTC timestamp of the first failed attempt.
    /// </summary>
    public required DateTimeOffset FirstFailedAt { get; init; }

    /// <summary>
    /// Gets the UTC timestamp at which the message was finally
    /// dead-lettered (the last failed attempt).
    /// </summary>
    public required DateTimeOffset DeadLetteredAt { get; init; }

    /// <summary>
    /// Gets the end-to-end correlation identifier propagated from the
    /// originating message, per FR-004.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Gets the failed payload. The runtime type is either
    /// <see cref="SlackInboundEnvelope"/> or
    /// <see cref="SlackOutboundEnvelope"/>, indicated by
    /// <see cref="Source"/>. Prefer <see cref="AsInbound"/> or
    /// <see cref="AsOutbound"/> for type-checked access instead of
    /// casting directly.
    /// </summary>
    public required object Payload { get; init; }

    /// <summary>
    /// Returns <see cref="Payload"/> cast to <see cref="SlackInboundEnvelope"/>
    /// when <see cref="Source"/> is <see cref="SlackDeadLetterSource.Inbound"/>.
    /// </summary>
    /// <returns>The inbound envelope payload.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="Source"/> is not
    /// <see cref="SlackDeadLetterSource.Inbound"/>.
    /// </exception>
    public SlackInboundEnvelope AsInbound() =>
        this.Source == SlackDeadLetterSource.Inbound
            ? (SlackInboundEnvelope)this.Payload
            : throw new InvalidOperationException(
                $"Cannot read Payload as {nameof(SlackInboundEnvelope)}: Source is {this.Source}, not {nameof(SlackDeadLetterSource.Inbound)}.");

    /// <summary>
    /// Returns <see cref="Payload"/> cast to <see cref="SlackOutboundEnvelope"/>
    /// when <see cref="Source"/> is <see cref="SlackDeadLetterSource.Outbound"/>.
    /// </summary>
    /// <returns>The outbound envelope payload.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="Source"/> is not
    /// <see cref="SlackDeadLetterSource.Outbound"/>.
    /// </exception>
    public SlackOutboundEnvelope AsOutbound() =>
        this.Source == SlackDeadLetterSource.Outbound
            ? (SlackOutboundEnvelope)this.Payload
            : throw new InvalidOperationException(
                $"Cannot read Payload as {nameof(SlackOutboundEnvelope)}: Source is {this.Source}, not {nameof(SlackDeadLetterSource.Outbound)}.");
}
