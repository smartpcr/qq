// -----------------------------------------------------------------------
// <copyright file="TelegramChatTypeParser.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Telegram.Auth;

using System;
using AgentSwarm.Messaging.Core;

/// <summary>
/// Stage 3.4 — parses the raw lowercase chat-type token surfaced on
/// <see cref="AgentSwarm.Messaging.Abstractions.MessengerEvent.ChatType"/>
/// (one of <c>"private"</c>, <c>"group"</c>, <c>"supergroup"</c>,
/// <c>"channel"</c>) into the Core
/// <see cref="ChatType"/> enum used by
/// <see cref="OperatorBinding"/> / <see cref="OperatorRegistration"/>.
/// </summary>
/// <remarks>
/// <para>
/// The conversion table matches the
/// <c>Telegram.Bot.Types.Enums.ChatType</c> enum surfaced by the
/// <see cref="Webhook.TelegramUpdateMapper.FormatChatType"/> formatter:
/// <list type="bullet">
///   <item><description><c>"private"</c> → <see cref="ChatType.Private"/></description></item>
///   <item><description><c>"group"</c> → <see cref="ChatType.Group"/></description></item>
///   <item><description><c>"supergroup"</c> → <see cref="ChatType.Supergroup"/></description></item>
///   <item><description><c>"channel"</c> → <see cref="ChatType.Supergroup"/>
///   (channels are a broadcast surface — they share the multi-member
///   threat model with supergroups so the binding's authorization
///   semantics match.)</description></item>
///   <item><description>Anything else (including <see langword="null"/>
///   / blank) → <see cref="ChatType.Private"/>. The default matches
///   the historical
///   <see cref="ConfiguredOperatorAuthorizationService"/> convention
///   and the e2e-scenarios "private chat operator" baseline so a
///   connector that has not been updated to populate
///   <c>MessengerEvent.ChatType</c> continues to onboard private-chat
///   operators correctly.</description></item>
/// </list>
/// </para>
/// <para>
/// Lives in <c>AgentSwarm.Messaging.Telegram.Auth</c> because the
/// only consumer is
/// <see cref="TelegramUserAuthorizationService"/>; future connectors
/// that need the same translation will surface their own
/// platform-specific parser rather than coupling to this one.
/// </para>
/// </remarks>
internal static class TelegramChatTypeParser
{
    /// <summary>
    /// Parses <paramref name="raw"/> into the corresponding
    /// <see cref="ChatType"/>; returns <see cref="ChatType.Private"/>
    /// when the token is <see langword="null"/>, blank, or
    /// unrecognized (see remarks for the conversion table).
    /// </summary>
    public static ChatType ParseOrDefault(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ChatType.Private;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "private" => ChatType.Private,
            "group" => ChatType.Group,
            "supergroup" => ChatType.Supergroup,
            "channel" => ChatType.Supergroup,
            _ => ChatType.Private,
        };
    }
}
