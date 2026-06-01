// -----------------------------------------------------------------------
// <copyright file="SlackMembershipResolverTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 3.2 tests for <see cref="SlackMembershipResolver"/>. The brief's
/// fourth scenario states: <em>Given a resolver with a 5-minute TTL,
/// When the same user group is queried twice within TTL, Then the Slack
/// API is called only once.</em> The cases below pin that behaviour and
/// the surrounding contract:
/// <list type="bullet">
///   <item><description>Cache hit within TTL avoids the API call.</description></item>
///   <item><description>Cache miss after TTL expiry triggers a fresh fetch.</description></item>
///   <item><description>Concurrent callers for the same key single-flight to one fetch.</description></item>
///   <item><description>Underlying client failure surfaces as
///   <see cref="SlackMembershipResolutionException"/> so the filter can fail closed.</description></item>
///   <item><description>Empty / whitespace inputs short-circuit to <c>false</c>.</description></item>
/// </list>
/// </summary>
public sealed class SlackMembershipResolverTests
{
    private const string TeamId = "T0123ABCD";
    private const string UserId = "U7777BETA";
    private const string GroupOne = "S1111";
    private const string GroupTwo = "S2222";

    [Fact]
    public async Task Cache_hit_within_ttl_avoids_repeated_slack_api_calls()
    {
        // Brief scenario: with a 5-minute TTL, two queries within the
        // window must call Slack only once.
        CountingClient client = new();
        client.SetMembers(TeamId, GroupOne, UserId);

        FixedTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_714_410_000));
        SlackMembershipResolver resolver = Build(client, ttlMinutes: 5, clock);

        bool first = await resolver.IsUserInAnyAllowedGroupAsync(TeamId, UserId, new[] { GroupOne }, default);
        clock.Advance(TimeSpan.FromMinutes(2));
        bool second = await resolver.IsUserInAnyAllowedGroupAsync(TeamId, UserId, new[] { GroupOne }, default);
        clock.Advance(TimeSpan.FromMinutes(2));
        bool third = await resolver.IsUserInAnyAllowedGroupAsync(TeamId, UserId, new[] { GroupOne }, default);

        first.Should().BeTrue();
        second.Should().BeTrue();
        third.Should().BeTrue();
        client.CallCount.Should().Be(1,
            "the resolver must serve the second and third checks from the in-memory cache, not from Slack");
    }

    [Fact]
    public async Task Cache_expiry_triggers_fresh_fetch_from_slack()
    {
        CountingClient client = new();
        client.SetMembers(TeamId, GroupOne, UserId);

        FixedTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_714_410_000));
        SlackMembershipResolver resolver = Build(client, ttlMinutes: 5, clock);

        await resolver.IsUserInAnyAllowedGroupAsync(TeamId, UserId, new[] { GroupOne }, default);
        clock.Advance(TimeSpan.FromMinutes(6));
        await resolver.IsUserInAnyAllowedGroupAsync(TeamId, UserId, new[] { GroupOne }, default);

        client.CallCount.Should().Be(2,
            "the second query lands outside the 5-minute TTL window, so the resolver must refresh from Slack");
    }

    [Fact]
    public async Task Different_user_groups_are_cached_independently()
    {
        CountingClient client = new();
        client.SetMembers(TeamId, GroupOne, "U_alice");
        client.SetMembers(TeamId, GroupTwo, UserId);

        FixedTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_714_410_000));
        SlackMembershipResolver resolver = Build(client, ttlMinutes: 5, clock);

        // First call: misses Group1 (Alice not Beta), then hits Group2.
        bool authorized = await resolver
            .IsUserInAnyAllowedGroupAsync(TeamId, UserId, new[] { GroupOne, GroupTwo }, default);
        authorized.Should().BeTrue();
        client.CallCount.Should().Be(2,
            "the first call should fetch both groups because neither has been cached yet");

        // Second call: both cached, no additional API calls.
        bool stillAuthorized = await resolver
            .IsUserInAnyAllowedGroupAsync(TeamId, UserId, new[] { GroupOne, GroupTwo }, default);
        stillAuthorized.Should().BeTrue();
        client.CallCount.Should().Be(2,
            "the second call must reuse both cached snapshots without touching the API");
    }

    [Fact]
    public async Task First_matching_group_short_circuits_subsequent_lookups()
    {
        CountingClient client = new();
        client.SetMembers(TeamId, GroupOne, UserId);

        // Group2 deliberately not configured: if the resolver were to
        // continue past Group1 the lookup would still succeed against
        // the empty default, but client.CallCount would increment.
        FixedTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_714_410_000));
        SlackMembershipResolver resolver = Build(client, ttlMinutes: 5, clock);

        bool authorized = await resolver
            .IsUserInAnyAllowedGroupAsync(TeamId, UserId, new[] { GroupOne, GroupTwo }, default);

        authorized.Should().BeTrue();
        client.CallCount.Should().Be(1,
            "once the first group confirms membership the resolver must stop calling Slack for the remaining groups");
    }

    [Fact]
    public async Task Empty_allowed_groups_returns_false_without_slack_call()
    {
        CountingClient client = new();
        FixedTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_714_410_000));
        SlackMembershipResolver resolver = Build(client, ttlMinutes: 5, clock);

        bool authorized = await resolver
            .IsUserInAnyAllowedGroupAsync(TeamId, UserId, Array.Empty<string>(), default);

        authorized.Should().BeFalse();
        client.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Whitespace_user_id_returns_false_without_slack_call()
    {
        CountingClient client = new();
        FixedTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_714_410_000));
        SlackMembershipResolver resolver = Build(client, ttlMinutes: 5, clock);

        bool authorized = await resolver
            .IsUserInAnyAllowedGroupAsync(TeamId, "  ", new[] { GroupOne }, default);

        authorized.Should().BeFalse();
        client.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Underlying_client_failure_is_wrapped_in_resolution_exception()
    {
        CountingClient client = new();
        client.ThrowOn(GroupOne, new InvalidOperationException("Slack 503"));
        FixedTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_714_410_000));
        SlackMembershipResolver resolver = Build(client, ttlMinutes: 5, clock);

        Func<Task> act = () => resolver
            .IsUserInAnyAllowedGroupAsync(TeamId, UserId, new[] { GroupOne }, default);

        SlackMembershipResolutionException ex = (await act.Should()
            .ThrowAsync<SlackMembershipResolutionException>()).Which;
        ex.TeamId.Should().Be(TeamId);
        ex.UserGroupId.Should().Be(GroupOne);
        ex.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task Concurrent_callers_for_same_key_single_flight_to_one_fetch()
    {
        // Single-flight guarantee: 16 concurrent callers all asking the
        // same (team, group) tuple should only trigger ONE Slack call.
        SlowClient client = new();
        client.SetMembers(TeamId, GroupOne, UserId);
        FixedTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_714_410_000));
        SlackMembershipResolver resolver = Build(client, ttlMinutes: 5, clock);

        Task<bool>[] tasks = new Task<bool>[16];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() =>
                resolver.IsUserInAnyAllowedGroupAsync(TeamId, UserId, new[] { GroupOne }, default));
        }

        client.ReleaseAll();
        bool[] outcomes = await Task.WhenAll(tasks);
        outcomes.Should().AllBeEquivalentTo(true);
        client.CallCount.Should().Be(1,
            "the per-key single-flight gate must coalesce concurrent misses into a single Slack call");
    }

    [Fact]
    public async Task Invalidate_cache_forces_next_lookup_to_refetch()
    {
        CountingClient client = new();
        client.SetMembers(TeamId, GroupOne, UserId);
        FixedTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_714_410_000));
        SlackMembershipResolver resolver = Build(client, ttlMinutes: 5, clock);

        await resolver.IsUserInAnyAllowedGroupAsync(TeamId, UserId, new[] { GroupOne }, default);
        client.CallCount.Should().Be(1);

        resolver.InvalidateCache();
        await resolver.IsUserInAnyAllowedGroupAsync(TeamId, UserId, new[] { GroupOne }, default);
        client.CallCount.Should().Be(2,
            "InvalidateCache must drop every cached entry so the next query refetches from Slack");
    }

    [Fact]
    public async Task Non_positive_ttl_falls_back_to_five_minute_default()
    {
        CountingClient client = new();
        client.SetMembers(TeamId, GroupOne, UserId);
        FixedTimeProvider clock = new(DateTimeOffset.FromUnixTimeSeconds(1_714_410_000));
        SlackMembershipResolver resolver = Build(client, ttlMinutes: 0, clock);

        await resolver.IsUserInAnyAllowedGroupAsync(TeamId, UserId, new[] { GroupOne }, default);
        clock.Advance(TimeSpan.FromMinutes(4));
        await resolver.IsUserInAnyAllowedGroupAsync(TeamId, UserId, new[] { GroupOne }, default);

        client.CallCount.Should().Be(1,
            "a misconfigured TTL of 0 must degrade to the documented 5-minute default, not to no caching at all");
    }

    private static SlackMembershipResolver Build(
        ISlackUserGroupClient client,
        int ttlMinutes,
        TimeProvider clock)
    {
        StaticOptionsMonitor<SlackConnectorOptions> options = new(new SlackConnectorOptions
        {
            MembershipCacheTtlMinutes = ttlMinutes,
        });
        return new SlackMembershipResolver(client, options, NullLogger<SlackMembershipResolver>.Instance, clock);
    }

    private sealed class CountingClient : ISlackUserGroupClient
    {
        private readonly ConcurrentDictionary<(string, string), HashSet<string>> members = new();
        private readonly ConcurrentDictionary<string, Exception> errors = new();
        private int callCount;

        public int CallCount => this.callCount;

        public void SetMembers(string teamId, string userGroupId, params string[] userIds)
        {
            this.members[(teamId, userGroupId)] = new HashSet<string>(userIds, StringComparer.Ordinal);
        }

        public void ThrowOn(string userGroupId, Exception ex) => this.errors[userGroupId] = ex;

        public Task<IReadOnlyCollection<string>> ListUserGroupMembersAsync(
            string teamId,
            string userGroupId,
            CancellationToken ct)
        {
            Interlocked.Increment(ref this.callCount);
            if (this.errors.TryGetValue(userGroupId, out Exception? ex))
            {
                return Task.FromException<IReadOnlyCollection<string>>(ex);
            }

            IReadOnlyCollection<string> snapshot = this.members.TryGetValue((teamId, userGroupId), out HashSet<string>? set)
                ? new List<string>(set)
                : (IReadOnlyCollection<string>)Array.Empty<string>();
            return Task.FromResult(snapshot);
        }
    }

    private sealed class SlowClient : ISlackUserGroupClient
    {
        private readonly ConcurrentDictionary<(string, string), HashSet<string>> members = new();
        private readonly TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;

        public int CallCount => this.callCount;

        public void SetMembers(string teamId, string userGroupId, params string[] userIds)
        {
            this.members[(teamId, userGroupId)] = new HashSet<string>(userIds, StringComparer.Ordinal);
        }

        public void ReleaseAll() => this.gate.TrySetResult(true);

        public async Task<IReadOnlyCollection<string>> ListUserGroupMembersAsync(
            string teamId,
            string userGroupId,
            CancellationToken ct)
        {
            Interlocked.Increment(ref this.callCount);
            await this.gate.Task.ConfigureAwait(false);
            if (this.members.TryGetValue((teamId, userGroupId), out HashSet<string>? set))
            {
                return new List<string>(set);
            }

            return Array.Empty<string>();
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => this.utcNow;

        public void Advance(TimeSpan delta) => this.utcNow = this.utcNow.Add(delta);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        private readonly T value;

        public StaticOptionsMonitor(T value)
        {
            this.value = value;
        }

        public T CurrentValue => this.value;

        public T Get(string? name) => this.value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
