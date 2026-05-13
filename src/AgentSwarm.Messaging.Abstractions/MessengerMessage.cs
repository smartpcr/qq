using System.ComponentModel.DataAnnotations;

namespace AgentSwarm.Messaging.Abstractions;

/// <summary>
/// An outbound message produced by an agent and routed through a messenger connector
/// (Teams / Slack / Discord / Telegram). Immutable by construction.
/// </summary>
public sealed record MessengerMessage
{
    /// <summary>Stable id assigned by the producer (typically a GUID).</summary>
    [Required(AllowEmptyStrings = false)]
    public string MessageId { get; init; } = string.Empty;

    /// <summary>Messenger-specific conversation identifier the message was posted to.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ConversationId { get; init; } = string.Empty;

    /// <summary>Id of the agent that produced the message.</summary>
    [Required(AllowEmptyStrings = false)]
    public string AgentId { get; init; } = string.Empty;

    /// <summary>Id of the originating task that triggered the message.</summary>
    [Required(AllowEmptyStrings = false)]
    public string TaskId { get; init; } = string.Empty;

    /// <summary>Correlation id stitching all events for one logical interaction.</summary>
    [Required(AllowEmptyStrings = false)]
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>Plain-text body. Connectors may wrap / format this for the channel.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Body { get; init; } = string.Empty;

    /// <summary>UTC timestamp the message was created.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Severity bucket — see <see cref="MessageSeverities"/>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Severity { get; init; } = MessageSeverities.Info;
}
