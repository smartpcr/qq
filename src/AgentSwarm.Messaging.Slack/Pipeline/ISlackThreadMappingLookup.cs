// -----------------------------------------------------------------------
// <copyright file="ISlackThreadMappingLookup.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Pipeline;

using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;

/// <summary>
/// Narrow lookup contract Stage 5.3's
/// <see cref="SlackInteractionHandler"/> uses to resolve the
/// <see cref="SlackThreadMapping.CorrelationId"/> for an interactive
/// payload's parent message. Decoupled from the full Stage 6.2
/// <c>ISlackThreadManager</c> (which also writes mappings) so the
/// interaction handler only depends on the read surface and so a
/// Stage 5.3-only test host does not have to wire the entire thread
/// lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// Architecture.md §5.2 step 6 says <c>CorrelationId</c> "is resolved
/// from <c>SlackThreadMapping</c> via the message's
/// <c>thread_ts</c>". The lookup is keyed on
/// <c>(team_id, channel_id, thread_ts)</c> -- the same unique
/// constraint the Stage 2.2
/// <see cref="Persistence.SlackThreadMappingConfiguration"/> declares
/// on the persistence table.
/// </para>
/// </remarks>
internal interface ISlackThreadMappingLookup
{
    /// <summary>
    /// Returns the <see cref="SlackThreadMapping"/> whose
    /// <c>(TeamId, ChannelId, ThreadTs)</c> matches the supplied
    /// coordinates, or <see langword="null"/> when no mapping exists
    /// (e.g., the click landed on a message that was not anchored to
    /// an agent task -- the handler falls back to the envelope's
    /// idempotency key as the correlation id).
    /// </summary>
    Task<SlackThreadMapping?> LookupAsync(
        string teamId,
        string? channelId,
        string? threadTs,
        CancellationToken ct);
}

/// <summary>
/// Default <see cref="ISlackThreadMappingLookup"/> that always returns
/// <see langword="null"/>. Used when the host has not yet wired the
/// EF-backed lookup (Stage 6.2). The
/// <see cref="SlackInteractionHandler"/> degrades gracefully -- it
/// falls back to <c>envelope.IdempotencyKey</c> for the correlation id
/// so the audit trail still carries a deterministic anchor.
/// </summary>
internal sealed class NullSlackThreadMappingLookup : ISlackThreadMappingLookup
{
    public Task<SlackThreadMapping?> LookupAsync(
        string teamId,
        string? channelId,
        string? threadTs,
        CancellationToken ct) => Task.FromResult<SlackThreadMapping?>(null);
}
