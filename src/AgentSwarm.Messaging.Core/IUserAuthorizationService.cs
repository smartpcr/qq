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
/// <c>Telegram:AllowedUserIds</c>) and, if the user is allowed, registers a
/// new <see cref="OperatorBinding"/> via <see cref="IOperatorRegistry.RegisterAsync"/>.</para>
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
}
