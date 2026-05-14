using System.Collections.Concurrent;

namespace AgentSwarm.Messaging.Teams.Storage;

/// <summary>
/// In-memory <see cref="IConversationReferenceStore"/> sufficient for local development and
/// integration tests. Replaced by <c>SqlConversationReferenceStore</c> in Stage 4.1.
/// </summary>
public sealed class InMemoryConversationReferenceStore : IConversationReferenceStore
{
    private readonly ConcurrentDictionary<string, TeamsConversationReference> _byUserKey
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, TeamsConversationReference> _byInternalUserKey
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, TeamsConversationReference> _byChannelKey
        = new(StringComparer.OrdinalIgnoreCase);

    private static string UserKey(string tenantId, string aadObjectId) => $"u::{tenantId}::{aadObjectId}";

    private static string InternalUserKey(string tenantId, string internalUserId) => $"i::{tenantId}::{internalUserId}";

    private static string ChannelKey(string tenantId, string channelId) => $"c::{tenantId}::{channelId}";

    /// <inheritdoc />
    public Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct)
    {
        if (reference is null) throw new ArgumentNullException(nameof(reference));
        ct.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(reference.AadObjectId))
        {
            _byUserKey[UserKey(reference.TenantId, reference.AadObjectId!)] = reference;
        }

        if (!string.IsNullOrWhiteSpace(reference.InternalUserId))
        {
            _byInternalUserKey[InternalUserKey(reference.TenantId, reference.InternalUserId!)] = reference;
        }

        if (!string.IsNullOrWhiteSpace(reference.ChannelId))
        {
            _byChannelKey[ChannelKey(reference.TenantId, reference.ChannelId!)] = reference;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<TeamsConversationReference?> GetAsync(string tenantId, string aadObjectId, CancellationToken ct)
        => GetByAadObjectIdAsync(tenantId, aadObjectId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<TeamsConversationReference> result = _byUserKey.Values
            .Concat(_byChannelKey.Values)
            .Where(r => r.IsActive)
            .Distinct()
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _byUserKey.TryGetValue(UserKey(tenantId, aadObjectId), out var existing);
        return Task.FromResult(existing?.IsActive == true ? existing : null);
    }

    /// <inheritdoc />
    public Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _byInternalUserKey.TryGetValue(InternalUserKey(tenantId, internalUserId), out var existing);
        return Task.FromResult(existing?.IsActive == true ? existing : null);
    }

    /// <inheritdoc />
    public Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _byChannelKey.TryGetValue(ChannelKey(tenantId, channelId), out var existing);
        return Task.FromResult(existing?.IsActive == true ? existing : null);
    }

    /// <inheritdoc />
    public Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_byUserKey.TryGetValue(UserKey(tenantId, aadObjectId), out var existing))
        {
            var updated = existing with { IsActive = false, UpdatedAt = DateTimeOffset.UtcNow };
            _byUserKey[UserKey(tenantId, aadObjectId)] = updated;
            if (!string.IsNullOrWhiteSpace(existing.InternalUserId))
            {
                _byInternalUserKey[InternalUserKey(tenantId, existing.InternalUserId!)] = updated;
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_byChannelKey.TryGetValue(ChannelKey(tenantId, channelId), out var existing))
        {
            var updated = existing with { IsActive = false, UpdatedAt = DateTimeOffset.UtcNow };
            _byChannelKey[ChannelKey(tenantId, channelId)] = updated;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _byUserKey.TryGetValue(UserKey(tenantId, aadObjectId), out var existing);
        return Task.FromResult(existing?.IsActive == true);
    }

    /// <inheritdoc />
    public Task<bool> IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _byInternalUserKey.TryGetValue(InternalUserKey(tenantId, internalUserId), out var existing);
        return Task.FromResult(existing?.IsActive == true);
    }

    /// <inheritdoc />
    public Task<bool> IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _byChannelKey.TryGetValue(ChannelKey(tenantId, channelId), out var existing);
        return Task.FromResult(existing?.IsActive == true);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_byUserKey.TryRemove(UserKey(tenantId, aadObjectId), out var existing)
            && !string.IsNullOrWhiteSpace(existing.InternalUserId))
        {
            _byInternalUserKey.TryRemove(InternalUserKey(tenantId, existing.InternalUserId!), out _);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _byChannelKey.TryRemove(ChannelKey(tenantId, channelId), out _);
        return Task.CompletedTask;
    }
}
