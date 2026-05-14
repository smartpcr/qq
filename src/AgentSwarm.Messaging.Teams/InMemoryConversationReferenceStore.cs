using System.Collections.Concurrent;

namespace AgentSwarm.Messaging.Teams;

/// <summary>
/// In-memory <see cref="IConversationReferenceStore"/> implementation backed by a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>. Registered as a singleton in Stage 2.1
/// for local development and integration tests; replaced by the SQL-backed implementation in
/// Stage 4.1.
/// </summary>
/// <remarks>
/// <para>
/// The store maintains a single dictionary keyed by <see cref="TeamsConversationReference.Id"/>;
/// lookups by tenant + AAD object ID, tenant + internal user ID, and tenant + channel ID
/// scan the dictionary on demand. This is acceptable for the in-memory variant because the
/// expected cardinality during local development is small; the SQL replacement uses indexed
/// columns for each lookup path.
/// </para>
/// <para>
/// Active-state lookups (<see cref="GetByAadObjectIdAsync"/>, <see cref="GetByInternalUserIdAsync"/>,
/// <see cref="GetByChannelIdAsync"/>) filter on <see cref="TeamsConversationReference.IsActive"/>
/// per the contract. The <see cref="IsActiveByInternalUserIdAsync"/> overload distinguishes
/// "inactive" from "missing" — it returns <c>false</c> in both cases because the
/// installation gate (Stage 5.1) treats both as non-sendable targets.
/// </para>
/// </remarks>
public sealed class InMemoryConversationReferenceStore : IConversationReferenceStore
{
    private readonly ConcurrentDictionary<string, TeamsConversationReference> _byId = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveOrUpdateAsync(TeamsConversationReference reference, CancellationToken ct)
    {
        if (reference is null) throw new ArgumentNullException(nameof(reference));
        ct.ThrowIfCancellationRequested();

        // Upsert keyed by the natural identity so the same scope produces a single row even
        // when Bot Framework returns a different surrogate ID on re-installation.
        var existing = LocateByNaturalKey(reference);
        if (existing is not null)
        {
            _byId[existing.Id] = reference with { Id = existing.Id };
        }
        else
        {
            _byId[reference.Id] = reference;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<TeamsConversationReference?> GetAsync(string referenceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(referenceId)) throw new ArgumentException("Reference ID must be non-empty.", nameof(referenceId));
        ct.ThrowIfCancellationRequested();

        _byId.TryGetValue(referenceId, out var value);
        return Task.FromResult(value);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TeamsConversationReference>> GetAllActiveAsync(string tenantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID must be non-empty.", nameof(tenantId));
        ct.ThrowIfCancellationRequested();

        var matches = _byId.Values
            .Where(r => string.Equals(r.TenantId, tenantId, StringComparison.Ordinal) && r.IsActive)
            .ToArray();
        return Task.FromResult<IReadOnlyList<TeamsConversationReference>>(matches);
    }

    /// <inheritdoc />
    public Task<TeamsConversationReference?> GetByAadObjectIdAsync(string tenantId, string aadObjectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID must be non-empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(aadObjectId)) throw new ArgumentException("AAD object ID must be non-empty.", nameof(aadObjectId));
        ct.ThrowIfCancellationRequested();

        var match = _byId.Values.FirstOrDefault(r =>
            r.IsActive &&
            string.Equals(r.TenantId, tenantId, StringComparison.Ordinal) &&
            string.Equals(r.AadObjectId, aadObjectId, StringComparison.Ordinal));
        return Task.FromResult(match);
    }

    /// <inheritdoc />
    public Task<TeamsConversationReference?> GetByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID must be non-empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(internalUserId)) throw new ArgumentException("Internal user ID must be non-empty.", nameof(internalUserId));
        ct.ThrowIfCancellationRequested();

        var match = _byId.Values.FirstOrDefault(r =>
            r.IsActive &&
            string.Equals(r.TenantId, tenantId, StringComparison.Ordinal) &&
            string.Equals(r.InternalUserId, internalUserId, StringComparison.Ordinal));
        return Task.FromResult(match);
    }

    /// <inheritdoc />
    public Task<TeamsConversationReference?> GetByChannelIdAsync(string tenantId, string channelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID must be non-empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel ID must be non-empty.", nameof(channelId));
        ct.ThrowIfCancellationRequested();

        var match = _byId.Values.FirstOrDefault(r =>
            r.IsActive &&
            string.Equals(r.TenantId, tenantId, StringComparison.Ordinal) &&
            string.Equals(r.ChannelId, channelId, StringComparison.Ordinal));
        return Task.FromResult(match);
    }

    /// <inheritdoc />
    public Task<bool> IsActiveAsync(string tenantId, string aadObjectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID must be non-empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(aadObjectId)) throw new ArgumentException("AAD object ID must be non-empty.", nameof(aadObjectId));
        ct.ThrowIfCancellationRequested();

        var match = _byId.Values.Any(r =>
            r.IsActive &&
            string.Equals(r.TenantId, tenantId, StringComparison.Ordinal) &&
            string.Equals(r.AadObjectId, aadObjectId, StringComparison.Ordinal));
        return Task.FromResult(match);
    }

    /// <inheritdoc />
    public Task<bool> IsActiveByInternalUserIdAsync(string tenantId, string internalUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID must be non-empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(internalUserId)) throw new ArgumentException("Internal user ID must be non-empty.", nameof(internalUserId));
        ct.ThrowIfCancellationRequested();

        var match = _byId.Values.Any(r =>
            r.IsActive &&
            string.Equals(r.TenantId, tenantId, StringComparison.Ordinal) &&
            string.Equals(r.InternalUserId, internalUserId, StringComparison.Ordinal));
        return Task.FromResult(match);
    }

    /// <inheritdoc />
    public Task<bool> IsActiveByChannelAsync(string tenantId, string channelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID must be non-empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel ID must be non-empty.", nameof(channelId));
        ct.ThrowIfCancellationRequested();

        var match = _byId.Values.Any(r =>
            r.IsActive &&
            string.Equals(r.TenantId, tenantId, StringComparison.Ordinal) &&
            string.Equals(r.ChannelId, channelId, StringComparison.Ordinal));
        return Task.FromResult(match);
    }

    /// <inheritdoc />
    public Task MarkInactiveAsync(string tenantId, string aadObjectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID must be non-empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(aadObjectId)) throw new ArgumentException("AAD object ID must be non-empty.", nameof(aadObjectId));
        ct.ThrowIfCancellationRequested();

        foreach (var pair in _byId)
        {
            var r = pair.Value;
            if (string.Equals(r.TenantId, tenantId, StringComparison.Ordinal) &&
                string.Equals(r.AadObjectId, aadObjectId, StringComparison.Ordinal))
            {
                _byId[pair.Key] = r with { IsActive = false, UpdatedAt = DateTimeOffset.UtcNow };
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkInactiveByChannelAsync(string tenantId, string channelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID must be non-empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel ID must be non-empty.", nameof(channelId));
        ct.ThrowIfCancellationRequested();

        foreach (var pair in _byId)
        {
            var r = pair.Value;
            if (string.Equals(r.TenantId, tenantId, StringComparison.Ordinal) &&
                string.Equals(r.ChannelId, channelId, StringComparison.Ordinal))
            {
                _byId[pair.Key] = r with { IsActive = false, UpdatedAt = DateTimeOffset.UtcNow };
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string tenantId, string aadObjectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID must be non-empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(aadObjectId)) throw new ArgumentException("AAD object ID must be non-empty.", nameof(aadObjectId));
        ct.ThrowIfCancellationRequested();

        var keys = _byId
            .Where(p =>
                string.Equals(p.Value.TenantId, tenantId, StringComparison.Ordinal) &&
                string.Equals(p.Value.AadObjectId, aadObjectId, StringComparison.Ordinal))
            .Select(p => p.Key)
            .ToArray();

        foreach (var key in keys)
        {
            _byId.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteByChannelAsync(string tenantId, string channelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("Tenant ID must be non-empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("Channel ID must be non-empty.", nameof(channelId));
        ct.ThrowIfCancellationRequested();

        var keys = _byId
            .Where(p =>
                string.Equals(p.Value.TenantId, tenantId, StringComparison.Ordinal) &&
                string.Equals(p.Value.ChannelId, channelId, StringComparison.Ordinal))
            .Select(p => p.Key)
            .ToArray();

        foreach (var key in keys)
        {
            _byId.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    private TeamsConversationReference? LocateByNaturalKey(TeamsConversationReference candidate)
    {
        return _byId.Values.FirstOrDefault(r =>
            string.Equals(r.TenantId, candidate.TenantId, StringComparison.Ordinal) &&
            ((!string.IsNullOrEmpty(candidate.AadObjectId) &&
              string.Equals(r.AadObjectId, candidate.AadObjectId, StringComparison.Ordinal)) ||
             (!string.IsNullOrEmpty(candidate.ChannelId) &&
              string.Equals(r.ChannelId, candidate.ChannelId, StringComparison.Ordinal))));
    }
}
