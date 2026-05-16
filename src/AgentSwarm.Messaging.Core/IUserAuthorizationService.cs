namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Performs two-tier authorization for inbound messenger commands.
/// </summary>
/// <remarks>
/// <para>Lives in <c>AgentSwarm.Messaging.Core</c> because its return type
/// <see cref="AuthorizationResult"/> carries a list of
/// <see cref="OperatorBinding"/> records, which are Core-level persistence
/// projections.</para>
/// <para>When <c>commandName == "start"</c>, the service performs <b>Tier 1
/// (onboarding)</b> authorization: it checks the static allowlist (e.g.
/// <c>Telegram:AllowedUserIds</c>) and, if the user is allowed, registers
/// every workspace binding under the user's
/// <c>Telegram:UserTenantMappings</c> entry as one atomic batch via
/// <see cref="IOperatorRegistry.RegisterManyAsync"/> (Stage 3.4 iter-3 —
/// replaces the prior per-row <see cref="IOperatorRegistry.RegisterAsync"/>
/// loop so a unique-index collision on row N rolls back rows 1..N-1
/// atomically). Stage 3.4 callers SHOULD prefer <see cref="OnboardAsync"/>
/// for the onboarding path so the real Telegram <c>Update.Message.Chat.Type</c>
/// flows into the new bindings instead of defaulting to
/// <see cref="ChatType.Private"/>.</para>
/// <para>For any other <c>commandName</c> (including <c>null</c>), the
/// service performs <b>Tier 2 (runtime)</b> authorization by calling
/// <see cref="IOperatorRegistry.GetBindingsAsync"/> and populating
/// <see cref="AuthorizationResult.Bindings"/>.</para>
/// <para>The pipeline then resolves cardinality and either rejects, dispatches
/// directly, or prompts the operator for workspace disambiguation.</para>
/// </remarks>
public interface IUserAuthorizationService
{
    Task<AuthorizationResult> AuthorizeAsync(
        string externalUserId,
        string chatId,
        string? commandName,
        CancellationToken ct);

    /// <summary>
    /// Stage 3.4 — Tier 1 onboarding entry point that additionally
    /// carries the raw Telegram chat-type token (one of
    /// <c>"private"</c>, <c>"group"</c>, <c>"supergroup"</c>,
    /// <c>"channel"</c>) so the freshly-created
    /// <see cref="OperatorBinding"/> records the actual chat kind.
    /// The pipeline calls this method instead of
    /// <see cref="AuthorizeAsync"/> when the parsed command is
    /// <c>/start</c>; older implementations that have not been
    /// updated for Stage 3.4 inherit the default body below, which
    /// forwards to <see cref="AuthorizeAsync"/> with <c>"start"</c>
    /// so their existing onboarding behaviour is preserved (binding
    /// is still created, <see cref="OperatorBinding.ChatType"/>
    /// defaults to <see cref="ChatType.Private"/>).
    /// </summary>
    /// <param name="externalUserId">Telegram user id (numeric string).</param>
    /// <param name="chatId">Telegram chat id (numeric string).</param>
    /// <param name="chatType">
    /// Raw lowercase chat-type token from
    /// <see cref="AgentSwarm.Messaging.Abstractions.MessengerEvent.ChatType"/>;
    /// <see langword="null"/> when the inbound transport could not
    /// determine the chat kind (in which case the implementation
    /// chooses a documented default).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuthorizationResult> OnboardAsync(
        string externalUserId,
        string chatId,
        string? chatType,
        CancellationToken ct)
        => AuthorizeAsync(externalUserId, chatId, "start", ct);
}
