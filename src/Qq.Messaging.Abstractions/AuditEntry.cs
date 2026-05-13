namespace Qq.Messaging.Abstractions;

/// <summary>
/// An immutable audit record for every human↔agent interaction.
/// </summary>
public sealed record AuditEntry(
    string EntryId,
    string MessageId,
    string OperatorId,
    string? AgentId,
    DateTimeOffset Timestamp,
    string CorrelationId,
    MessageDirection Direction,
    string? PlatformChatId,
    string? PlatformUserId,
    string? PlatformUpdateId,
    string Payload);
