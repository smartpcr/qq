using Microsoft.EntityFrameworkCore;

namespace AgentSwarm.Messaging.Slack.Entities;

/// <summary>
/// Maps a single agent task to its Slack conversation thread. One row per
/// task, with <see cref="TaskId"/> as the primary key.
/// </summary>
/// <remarks>
/// <para>
/// Stage 2.1 of
/// <c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>. Field
/// list is the canonical surface specified by architecture.md section 3.2.
/// </para>
/// <para>
/// A unique constraint on the tuple <c>(TeamId, ChannelId, ThreadTs)</c>
/// is declared at the entity level via the EF Core
/// <see cref="IndexAttribute"/> annotation below, so the constraint is
/// part of the entity contract even before Stage 2.2 wires up the
/// <c>IEntityTypeConfiguration&lt;SlackThreadMapping&gt;</c> /
/// migration. The annotation ensures the database schema enforces:
/// the same Slack thread cannot be mapped to two different tasks.
/// </para>
/// </remarks>
[Index(
    nameof(TeamId),
    nameof(ChannelId),
    nameof(ThreadTs),
    IsUnique = true,
    Name = "IX_SlackThreadMapping_TeamId_ChannelId_ThreadTs")]
public sealed class SlackThreadMapping
{
    /// <summary>
    /// Agent task identifier. Primary key.
    /// </summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>
    /// Slack workspace identifier the thread lives in.
    /// </summary>
    public string TeamId { get; set; } = string.Empty;

    /// <summary>
    /// Slack channel identifier the thread lives in.
    /// </summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>
    /// Slack timestamp (<c>ts</c>) of the root message of the thread.
    /// Stored as a string to preserve Slack's fractional-second precision
    /// exactly as returned by the Web API.
    /// </summary>
    public string ThreadTs { get; set; } = string.Empty;

    /// <summary>
    /// End-to-end correlation identifier propagated through every message
    /// in this task (FR-004).
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the agent that owns this task.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp at which the thread was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// UTC timestamp of the most recent message exchange in the thread.
    /// Updated by the inbound ingestor and outbound dispatcher.
    /// </summary>
    public DateTimeOffset LastMessageAt { get; set; }
}
