namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Cross-platform authorisation contract for inbound commands. Resolves the
/// caller's identity, the originating chat, and (where the platform has the
/// notion) the parent group/guild/team into an <see cref="AuthorizationResult"/>
/// the pipeline gate uses to allow or reject the command. See architecture.md
/// Section 4.5 and FR-006 in
/// <c>.forge-attachments/agent_swarm_messenger_user_stories.md</c>.
/// </summary>
/// <remarks>
/// The Discord implementation looks up the
/// <see cref="AgentSwarm.Messaging.Abstractions.GuildBinding"/> matching
/// <c>(platformGroupId, chatId)</c>, validates that the binding is active, and
/// checks the caller's roles against
/// <see cref="AgentSwarm.Messaging.Abstractions.GuildBinding.AllowedRoleIds"/>
/// (or against the per-command override in
/// <see cref="AgentSwarm.Messaging.Abstractions.GuildBinding.CommandRestrictions"/>
/// when present for <paramref name="commandName"/>).
/// </remarks>
public interface IUserAuthorizationService
{
    /// <summary>
    /// Authorises a single inbound command invocation.
    /// </summary>
    /// <param name="externalUserId">
    /// Platform-native user id of the caller (Discord user snowflake stringified,
    /// Telegram user id, Slack member id, AAD object id for Teams). Required.
    /// </param>
    /// <param name="platformGroupId">
    /// Platform-native parent-group id when the platform has one (Discord guild
    /// id stringified, Slack team id, Teams tenant id). <see langword="null"/>
    /// for platforms without a group concept (e.g. Telegram private chats),
    /// where the implementation falls back to <paramref name="chatId"/>-only
    /// resolution.
    /// </param>
    /// <param name="chatId">
    /// Platform-native channel/chat/conversation id the command was issued in.
    /// Required.
    /// </param>
    /// <param name="commandName">
    /// Logical command name (the slash subcommand; e.g. <c>"ask"</c>,
    /// <c>"approve"</c>). Used to consult per-command role overrides.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuthorizationResult> AuthorizeAsync(
        string externalUserId,
        string? platformGroupId,
        string chatId,
        string commandName,
        CancellationToken ct);
}
