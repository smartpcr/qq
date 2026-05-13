using Qq.Messaging.Abstractions;

namespace Qq.Messaging.Contracts;

/// <summary>
/// Maps a Telegram chat/user pair to an authorized <see cref="OperatorIdentity"/>.
/// </summary>
public sealed record TelegramOperatorMapping(
    long ChatId,
    long UserId,
    OperatorIdentity Operator);
