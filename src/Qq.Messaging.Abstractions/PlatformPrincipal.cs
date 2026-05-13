namespace Qq.Messaging.Abstractions;

/// <summary>
/// Platform-level identity extracted from an inbound update.
/// Used for authorization and deduplication before mapping to <see cref="OperatorIdentity"/>.
/// </summary>
public sealed record PlatformPrincipal(
    string Platform,
    string ChatId,
    string UserId,
    string UpdateId);
