namespace AgentSwarm.Messaging.Core;

using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// Handles a single slash command name. Implementations are registered in
/// DI and discovered by <see cref="CommandRouter"/> via constructor
/// injection (<c>IEnumerable&lt;ICommandHandler&gt;</c>) — each handler
/// advertises its <see cref="CommandName"/> so the router can build the
/// dispatch dictionary once at construction time. See
/// <c>implementation-plan.md</c> Stage 3.2 step 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="CommandName"/> lives on the handler.</b> The
/// alternative — registering each handler under a string key in DI — is
/// fragile (typos manifest at runtime as missing dispatch entries) and
/// duplicates the canonical names already pinned in
/// <see cref="TelegramCommands"/>. Keeping the name on the type makes the
/// "what handler maps to /approve" question answerable by `grep` and
/// detected at handler registration time.
/// </para>
/// <para>
/// <b>Role enforcement.</b> Handlers MAY assume the requesting
/// <see cref="AuthorizedOperator"/> has already passed the inbound
/// pipeline's role gate (<c>CommandRoleRequirements</c> in the Telegram
/// project) — re-checking inside the handler is permitted as
/// defense-in-depth but is not required.
/// </para>
/// </remarks>
public interface ICommandHandler
{
    /// <summary>
    /// Canonical command name (without the leading <c>/</c>) this handler
    /// services. MUST match one of the values in
    /// <see cref="TelegramCommands"/>. Comparison performed by
    /// <see cref="CommandRouter"/> is ordinal case-insensitive.
    /// </summary>
    string CommandName { get; }

    /// <summary>
    /// Execute the command. The handler is responsible for populating
    /// <see cref="CommandResult.CorrelationId"/> on the returned value;
    /// the pipeline overwrites it with the originating
    /// <c>MessengerEvent.CorrelationId</c> on the way back to the
    /// transport, but the value still flows through audit/logging code
    /// inside the handler scope, so a meaningful trace id (e.g. the
    /// originating question's <c>CorrelationId</c> for /approve) is
    /// preferred over a synthetic placeholder.
    /// </summary>
    Task<CommandResult> HandleAsync(
        ParsedCommand command,
        AuthorizedOperator @operator,
        CancellationToken ct);
}
