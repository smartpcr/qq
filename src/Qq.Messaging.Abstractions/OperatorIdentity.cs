namespace Qq.Messaging.Abstractions;

/// <summary>
/// Identifies an authorized human operator within a tenant and workspace.
/// </summary>
public sealed record OperatorIdentity(
    string OperatorId,
    string TenantId,
    string WorkspaceId,
    string DisplayName);
