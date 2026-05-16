using System.Collections.Concurrent;
using AgentSwarm.Messaging.Abstractions;

namespace AgentSwarm.Messaging.Teams.Security;

/// <summary>
/// In-memory <see cref="IUserDirectory"/> seeded by configuration. Used as the default DI
/// registration; operators replace with an LDAP/Graph/SCIM-backed implementation when one
/// is available.
/// </summary>
public sealed class StaticUserDirectory : IUserDirectory
{
    private readonly ConcurrentDictionary<string, UserIdentity> _byAadObjectId
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Add or replace a directory entry. Thread-safe; calling this concurrently from
    /// multiple threads always leaves the directory in a well-formed state and the last
    /// write for the same AAD object ID wins.
    /// </summary>
    public void Add(UserIdentity identity)
    {
        if (identity is null) throw new ArgumentNullException(nameof(identity));
        if (string.IsNullOrWhiteSpace(identity.AadObjectId))
        {
            throw new ArgumentException("UserIdentity.AadObjectId is required.", nameof(identity));
        }

        _byAadObjectId[identity.AadObjectId] = identity;
    }

    /// <inheritdoc />
    public Task<UserIdentity?> LookupAsync(string aadObjectId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(aadObjectId))
        {
            return Task.FromResult<UserIdentity?>(null);
        }

        _byAadObjectId.TryGetValue(aadObjectId, out var identity);
        return Task.FromResult(identity);
    }

    /// <summary>Returns a snapshot of every directory entry.</summary>
    public IReadOnlyCollection<UserIdentity> Entries => _byAadObjectId.Values.ToList().AsReadOnly();
}
