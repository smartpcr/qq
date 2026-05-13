namespace AgentSwarm.Messaging.Core;

/// <summary>
/// Outcome of <see cref="IUserAuthorizationService.AuthorizeAsync"/>.
/// Callers handle cardinality: zero bindings → unauthorized rejection; one
/// binding → construct <c>AuthorizedOperator</c> directly; multiple
/// bindings → present workspace disambiguation per the multi-workspace
/// e2e flow.
/// </summary>
/// <remarks>
/// Lives in <c>AgentSwarm.Messaging.Core</c> alongside
/// <see cref="OperatorBinding"/> per implementation-plan.md Stage 1.3 and
/// architecture.md §4.11.
/// </remarks>
public sealed record AuthorizationResult
{
    public required bool IsAuthorized { get; init; }

    /// <summary>
    /// All active bindings for the user/chat pair (empty when unauthorized).
    /// Supports multi-workspace disambiguation per architecture.md §4.3.
    /// </summary>
    public IReadOnlyList<OperatorBinding> Bindings { get; init; } =
        Array.Empty<OperatorBinding>();

    /// <summary>Human-readable rejection reason; <c>null</c> on success.</summary>
    public string? DenialReason { get; init; }
}
