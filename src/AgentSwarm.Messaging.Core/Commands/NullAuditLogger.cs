namespace AgentSwarm.Messaging.Core.Commands;

using AgentSwarm.Messaging.Abstractions;

/// <summary>
/// No-op <see cref="IAuditLogger"/> implementation registered by
/// default so command handlers (<see cref="DecisionCommandHandlerBase"/>
/// for <c>/approve</c> and <c>/reject</c>;
/// <see cref="HandoffCommandHandler"/> for <c>/handoff</c>) can take a
/// hard dependency on the interface without forcing every host to wire
/// up the Stage 5.3 persistent audit pipeline. Registered via
/// <see cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(Microsoft.Extensions.DependencyInjection.IServiceCollection, Type, Type)"/>
/// in the Telegram service-collection extensions, so a production
/// <c>IAuditLogger</c> registration wins by definition.
/// </summary>
/// <remarks>
/// A no-op default is preferable to a null-check at every call site:
/// the handler code stays straight-line, and operators can plug in a
/// real implementation later by registering it before
/// <c>AddTelegram(...)</c>.
/// </remarks>
public sealed class NullAuditLogger : IAuditLogger
{
    /// <inheritdoc />
    public Task LogAsync(AuditEntry entry, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task LogHumanResponseAsync(HumanResponseAuditEntry entry, CancellationToken ct) =>
        Task.CompletedTask;
}
