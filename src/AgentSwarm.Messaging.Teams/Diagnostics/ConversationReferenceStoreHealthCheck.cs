using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace AgentSwarm.Messaging.Teams.Diagnostics;

/// <summary>
/// Stage 6.3 <see cref="IHealthCheck"/> that verifies the
/// <see cref="IConversationReferenceStore"/> persistence layer is reachable AND
/// reports the active reference count across <i>all</i> tenants. Aligned with
/// <c>implementation-plan.md</c> §6.3 step 4 and test scenario:
/// "Given the database is unreachable, When <c>/health</c> is called, Then it returns
/// <c>Degraded</c> with detail <c>ConversationReferenceStore: Unhealthy</c>".
/// </summary>
/// <remarks>
/// <para>
/// The check calls <see cref="IConversationReferenceStore.CountActiveAsync"/>, a single
/// indexed <c>SELECT COUNT(*) WHERE IsActive = 1</c> query against the production SQL
/// store (per <c>SqlConversationReferenceStore.CountActiveAsync</c> in the
/// <c>AgentSwarm.Messaging.Teams.EntityFrameworkCore</c> assembly).
/// The probe is read-only, runs in O(log n) on the filtered active-index, and crosses
/// every tenant in the store — replacing the iter-1 sentinel-tenant probe whose count
/// was structurally guaranteed to be zero. Stores that have not overridden the
/// interface default return <c>-1</c>; the health check reports reachability without a
/// count in that case.
/// </para>
/// <para>
/// <b>Outcome contract.</b>
/// </para>
/// <list type="bullet">
///   <item><description>The probe completes successfully → <see cref="HealthStatus.Healthy"/>
///   with a <c>referenceCount</c> entry on the data dictionary (the real total active
///   count, or <c>"unsupported"</c> if the store returned the <c>-1</c> sentinel).</description></item>
///   <item><description>The probe throws any exception (other than cancellation by the
///   host) → <see cref="HealthStatus.Degraded"/> with the canonical description prefix
///   <c>ConversationReferenceStore: Unhealthy</c> so dashboards and the test scenario
///   in <c>implementation-plan.md</c> §6.3 can match on a stable substring.</description></item>
/// </list>
/// </remarks>
public sealed class ConversationReferenceStoreHealthCheck : IHealthCheck
{
    /// <summary>Canonical health-check name used to register and probe this check.</summary>
    public const string Name = "teams-conversation-reference-store";

    /// <summary>
    /// Description prefix returned when the store probe fails. The test scenario in
    /// <c>implementation-plan.md</c> §6.3 asserts the substring
    /// <c>ConversationReferenceStore: Unhealthy</c>; keeping the prefix as a public
    /// constant lets sibling tests reference the exact string instead of repeating it.
    /// </summary>
    public const string UnhealthyDescriptionPrefix = "ConversationReferenceStore: Unhealthy";

    private readonly IConversationReferenceStore _referenceStore;
    private readonly ILogger<ConversationReferenceStoreHealthCheck> _logger;

    /// <summary>Construct a <see cref="ConversationReferenceStoreHealthCheck"/>.</summary>
    /// <exception cref="ArgumentNullException">If any dependency is null.</exception>
    public ConversationReferenceStoreHealthCheck(
        IConversationReferenceStore referenceStore,
        ILogger<ConversationReferenceStoreHealthCheck> logger)
    {
        _referenceStore = referenceStore ?? throw new ArgumentNullException(nameof(referenceStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var data = new Dictionary<string, object>();

        try
        {
            var count = await _referenceStore
                .CountActiveAsync(cancellationToken)
                .ConfigureAwait(false);

            if (count < 0)
            {
                data["referenceCount"] = "unsupported";
                return HealthCheckResult.Healthy(
                    description: "ConversationReferenceStore: Healthy. Store reachable; aggregate count not supported by this implementation.",
                    data: data);
            }

            data["referenceCount"] = count;
            return HealthCheckResult.Healthy(
                description: $"ConversationReferenceStore: Healthy. {count} active reference(s) across all tenants.",
                data: data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ConversationReferenceStoreHealthCheck: CountActiveAsync threw — store is unreachable.");
            data["error"] = ex.GetType().FullName ?? "Exception";
            return HealthCheckResult.Degraded(
                description: $"{UnhealthyDescriptionPrefix}. {ex.Message}",
                exception: ex,
                data: data);
        }
    }
}
