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
/// <see cref="IOperatorRegistry.RegisterManyAsync"/> (Stage 3.4 iter-3 --
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
    /// Stage 5.2 (iter-3) -- unified two-tier authorization entry
    /// point. The pipeline calls THIS overload for every inbound
    /// command regardless of whether it is <c>/start</c> or a
    /// runtime command; the implementation routes Tier 1 vs Tier 2
    /// internally based on <paramref name="commandName"/>
    /// (<c>commandName == "start"</c> => Tier 1 onboarding;
    /// otherwise => Tier 2 binding lookup). This satisfies the
    /// Stage 5.2 brief requirement that <c>commandName</c> alone
    /// drives the Tier 1/Tier 2 distinction "without requiring
    /// separate pipeline branches" while still preserving the
    /// Stage 3.4 chat-type fidelity that <see cref="OnboardAsync"/>
    /// introduced.
    /// </summary>
    /// <remarks>
    /// <para>The <paramref name="chatType"/> parameter is only
    /// consulted when <paramref name="commandName"/> equals
    /// <c>"start"</c> -- for Tier 2 lookups the chat type is
    /// already persisted on the existing <see cref="OperatorBinding"/>
    /// and the parameter is ignored.</para>
    /// <para>The default body forwards to the 4-arg
    /// <see cref="AuthorizeAsync(string,string,string?,CancellationToken)"/>
    /// overload so existing implementations that were written
    /// before Stage 5.2 continue to work; they simply ignore the
    /// chat-type token and the resulting
    /// <see cref="OperatorBinding.ChatType"/> defaults to
    /// <see cref="ChatType.Private"/>. Implementations that need
    /// chat-type fidelity (notably
    /// <see cref="AgentSwarm.Messaging.Telegram.Auth.TelegramUserAuthorizationService"/>)
    /// override this method directly.</para>
    /// </remarks>
    /// <param name="externalUserId">Telegram user id (numeric string).</param>
    /// <param name="chatId">Telegram chat id (numeric string).</param>
    /// <param name="commandName">Parsed command name (<c>"start"</c>,
    /// <c>"status"</c>, etc.); drives Tier 1 vs Tier 2 selection.</param>
    /// <param name="chatType">
    /// Raw lowercase chat-type token from
    /// <see cref="AgentSwarm.Messaging.Abstractions.MessengerEvent.ChatType"/>;
    /// only consulted on <c>commandName == "start"</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<AuthorizationResult> AuthorizeAsync(
        string externalUserId,
        string chatId,
        string? commandName,
        string? chatType,
        CancellationToken ct)
        => AuthorizeAsync(externalUserId, chatId, commandName, ct);

    /// <summary>
    /// Stage 3.4 convenience entry point retained for back-compat
    /// with callers and tests that targeted the pre-Stage-5.2
    /// onboarding API. New callers SHOULD invoke the 5-arg
    /// <see cref="AuthorizeAsync(string,string,string?,string?,CancellationToken)"/>
    /// overload directly so the same code path handles both
    /// <c>/start</c> (Tier 1) and runtime (Tier 2) commands. This
    /// method simply forwards to that unified overload with
    /// <c>commandName == "start"</c>.
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
        => AuthorizeAsync(externalUserId, chatId, "start", chatType, ct);
}
