// -----------------------------------------------------------------------
// <copyright file="SlackMembershipResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Security;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="ISlackMembershipResolver"/>. Calls Slack's
/// <c>usergroups.users.list</c> via <see cref="ISlackUserGroupClient"/>
/// for each allowed user-group, caching the returned member-set per
/// (<c>team_id</c>, <c>user_group_id</c>) tuple with a TTL drawn from
/// <see cref="SlackConnectorOptions.MembershipCacheTtlMinutes"/>
/// (default 5 minutes per the Stage 3.2 implementation-plan brief).
/// </summary>
/// <remarks>
/// <para>
/// Membership snapshots are kept as case-sensitive
/// <see cref="HashSet{T}"/> instances so the membership check is O(1).
/// Cache expiry is enforced lazily on read; an entry whose
/// <see cref="DateTimeOffset"/> has passed is fetched again from Slack
/// before the membership check runs.
/// </para>
/// <para>
/// A per-key <see cref="SemaphoreSlim"/> protects against the "thundering
/// herd" effect: when two concurrent requests miss the same cache key,
/// only one outbound Slack call is made and the second request reuses
/// the freshly-cached result.
/// </para>
/// <para>
/// Iteration order over <paramref name="allowedUserGroupIds"/> is
/// preserved: the resolver short-circuits on the first group whose
/// membership set contains the user, so a well-ordered allow-list
/// (most-likely group first) minimises Slack API calls.
/// </para>
/// </remarks>
public sealed class SlackMembershipResolver : ISlackMembershipResolver
{
    private readonly ISlackUserGroupClient client;
    private readonly IOptionsMonitor<SlackConnectorOptions> optionsMonitor;
    private readonly ILogger<SlackMembershipResolver> logger;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<MembershipKey, CacheEntry> cache;
    private readonly ConcurrentDictionary<MembershipKey, SemaphoreSlim> locks;

    /// <summary>
    /// Initializes a new resolver with the supplied client and options.
    /// </summary>
    public SlackMembershipResolver(
        ISlackUserGroupClient client,
        IOptionsMonitor<SlackConnectorOptions> optionsMonitor,
        ILogger<SlackMembershipResolver> logger,
        TimeProvider? timeProvider = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.cache = new ConcurrentDictionary<MembershipKey, CacheEntry>();
        this.locks = new ConcurrentDictionary<MembershipKey, SemaphoreSlim>();
    }

    /// <inheritdoc />
    public async Task<bool> IsUserInAnyAllowedGroupAsync(
        string teamId,
        string userId,
        IReadOnlyCollection<string> allowedUserGroupIds,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(teamId))
        {
            throw new ArgumentException("Team id must be supplied.", nameof(teamId));
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        if (allowedUserGroupIds is null || allowedUserGroupIds.Count == 0)
        {
            return false;
        }

        TimeSpan ttl = ResolveTtl(this.optionsMonitor.CurrentValue);
        DateTimeOffset now = this.timeProvider.GetUtcNow();

        foreach (string rawGroupId in allowedUserGroupIds)
        {
            if (string.IsNullOrWhiteSpace(rawGroupId))
            {
                continue;
            }

            string userGroupId = rawGroupId;
            MembershipKey key = new(teamId, userGroupId);

            HashSet<string> members = await this
                .GetOrFetchMembersAsync(key, ttl, now, ct)
                .ConfigureAwait(false);

            if (members.Contains(userId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Clears every cached entry. Exposed so operator tooling (and
    /// integration tests) can force a refresh without restarting the
    /// host. Not on <see cref="ISlackMembershipResolver"/> because the
    /// interface keeps the public surface narrow.
    /// </summary>
    public void InvalidateCache()
    {
        this.cache.Clear();
    }

    private static TimeSpan ResolveTtl(SlackConnectorOptions options)
    {
        int minutes = options.MembershipCacheTtlMinutes;
        if (minutes <= 0)
        {
            // A zero or negative TTL would defeat the cache. Fall back
            // to the documented 5-minute default rather than turning
            // every check into a Slack call.
            minutes = 5;
        }

        return TimeSpan.FromMinutes(minutes);
    }

    private async Task<HashSet<string>> GetOrFetchMembersAsync(
        MembershipKey key,
        TimeSpan ttl,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (this.cache.TryGetValue(key, out CacheEntry? cached) && cached.ExpiresAt > now)
        {
            return cached.Members;
        }

        SemaphoreSlim gate = this.locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the gate; another caller may
            // have populated the entry while we were waiting.
            DateTimeOffset reCheckNow = this.timeProvider.GetUtcNow();
            if (this.cache.TryGetValue(key, out CacheEntry? after) && after.ExpiresAt > reCheckNow)
            {
                return after.Members;
            }

            IReadOnlyCollection<string> fetched;
            try
            {
                fetched = await this.client
                    .ListUserGroupMembersAsync(key.TeamId, key.UserGroupId, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (SlackMembershipResolutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.logger.LogError(
                    ex,
                    "Slack usergroups.users.list call failed for team {TeamId} group {UserGroupId}.",
                    key.TeamId,
                    key.UserGroupId);
                throw new SlackMembershipResolutionException(
                    key.TeamId,
                    key.UserGroupId,
                    $"Failed to resolve members of user group '{key.UserGroupId}' in team '{key.TeamId}'.",
                    ex);
            }

            HashSet<string> snapshot = new(fetched ?? Array.Empty<string>(), StringComparer.Ordinal);
            DateTimeOffset expiresAt = this.timeProvider.GetUtcNow().Add(ttl);
            CacheEntry entry = new(snapshot, expiresAt);
            this.cache[key] = entry;
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record CacheEntry(HashSet<string> Members, DateTimeOffset ExpiresAt);

    private readonly record struct MembershipKey(string TeamId, string UserGroupId);
}
