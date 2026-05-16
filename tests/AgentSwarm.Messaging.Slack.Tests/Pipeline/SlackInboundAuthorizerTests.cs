// -----------------------------------------------------------------------
// <copyright file="SlackInboundAuthorizerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Pipeline;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Pipeline;
using AgentSwarm.Messaging.Slack.Security;
using AgentSwarm.Messaging.Slack.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 4.3 tests for <see cref="SlackInboundAuthorizer"/>.
/// </summary>
public sealed class SlackInboundAuthorizerTests
{
    [Fact]
    public async Task AuthorizeAsync_returns_authorized_on_happy_path_and_does_not_audit()
    {
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: new[] { "C1" }, groups: new[] { "G1" });
        FakeMembershipResolver resolver = FakeMembershipResolver.Yes();
        FakeAuthAuditSink sink = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, resolver, sink);

        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U1:/agent:trig", "T1", "C1", "U1");
        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeTrue();
        result.Workspace.Should().NotBeNull();
        result.Workspace!.TeamId.Should().Be("T1");
        sink.Records.Should().BeEmpty("happy path must not write a rejection audit row");
    }

    [Fact]
    public async Task AuthorizeAsync_returns_unauthorized_authorized_when_options_disabled()
    {
        FakeWorkspaceStore stores = new(); // no workspaces registered -- doesn't matter
        FakeMembershipResolver resolver = FakeMembershipResolver.Yes();
        FakeAuthAuditSink sink = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, resolver, sink, enabled: false);

        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U1:/agent:trig", "T1", "C1", "U1");
        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeTrue("the Enabled escape hatch matches SlackAuthorizationFilter behaviour");
        sink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task AuthorizeAsync_rejects_missing_team_id()
    {
        FakeWorkspaceStore stores = new();
        FakeAuthAuditSink sink = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink);

        SlackInboundEnvelope envelope = BuildEnvelope("event:Ev1", string.Empty, "C1", "U1");
        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.Reason.Should().Be(SlackAuthorizationRejectionReason.MissingTeamId);
        sink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackAuthorizationRejectionReason.MissingTeamId);
    }

    [Fact]
    public async Task AuthorizeAsync_rejects_unknown_workspace()
    {
        FakeWorkspaceStore stores = new();
        FakeAuthAuditSink sink = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink);

        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T-unknown:U:/agent:trig", "T-unknown", "C1", "U1");
        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.Reason.Should().Be(SlackAuthorizationRejectionReason.UnknownWorkspace);
        sink.Records.Should().ContainSingle();
    }

    [Fact]
    public async Task AuthorizeAsync_rejects_disabled_workspace()
    {
        SlackWorkspaceConfig disabled = new()
        {
            TeamId = "T1",
            Enabled = false,
            AllowedChannelIds = new[] { "C1" },
            AllowedUserGroupIds = new[] { "G1" },
        };
        FakeWorkspaceStore stores = new();
        stores.Add(disabled);

        FakeAuthAuditSink sink = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink);

        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U1:/agent:trig", "T1", "C1", "U1");
        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.Reason.Should().Be(SlackAuthorizationRejectionReason.UnknownWorkspace);
    }

    [Fact]
    public async Task AuthorizeAsync_rejects_disallowed_channel()
    {
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: new[] { "C-allowed" }, groups: new[] { "G1" });
        FakeAuthAuditSink sink = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink);

        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U1:/agent:trig", "T1", "C-other", "U1");
        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.Reason.Should().Be(SlackAuthorizationRejectionReason.DisallowedChannel);
    }

    [Fact]
    public async Task AuthorizeAsync_rejects_empty_allow_list_as_deny_all()
    {
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: Array.Empty<string>(), groups: new[] { "G1" });
        FakeAuthAuditSink sink = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink);

        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U1:/agent:trig", "T1", "C1", "U1");
        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.Reason.Should().Be(SlackAuthorizationRejectionReason.DisallowedChannel);
    }

    [Fact]
    public async Task AuthorizeAsync_rejects_missing_user_id()
    {
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: new[] { "C1" }, groups: new[] { "G1" });
        FakeAuthAuditSink sink = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink);

        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1::/agent:trig", "T1", "C1", string.Empty);
        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.Reason.Should().Be(SlackAuthorizationRejectionReason.UserNotInAllowedGroup);
    }

    [Fact]
    public async Task AuthorizeAsync_rejects_user_not_in_any_allowed_group()
    {
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: new[] { "C1" }, groups: new[] { "G1" });
        FakeAuthAuditSink sink = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.No(), sink);

        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U-stranger:/agent:trig", "T1", "C1", "U-stranger");
        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.Reason.Should().Be(SlackAuthorizationRejectionReason.UserNotInAllowedGroup);
    }

    [Fact]
    public async Task AuthorizeAsync_surfaces_membership_resolution_failure_as_controlled_rejection()
    {
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: new[] { "C1" }, groups: new[] { "G1" });
        FakeMembershipResolver resolver = FakeMembershipResolver.Throws(
            new SlackMembershipResolutionException("T1", "G1", "timeout"));
        FakeAuthAuditSink sink = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, resolver, sink);

        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U1:/agent:trig", "T1", "C1", "U1");
        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.Reason.Should().Be(SlackAuthorizationRejectionReason.MembershipResolutionFailed,
            "the security pipeline fails closed on usergroups.users.list outages");
        sink.Records.Should().ContainSingle();
    }

    private static SlackInboundAuthorizer BuildAuthorizer(
        FakeWorkspaceStore store,
        FakeMembershipResolver resolver,
        FakeAuthAuditSink sink,
        bool enabled = true)
    {
        SlackAuthorizationOptions opts = new() { Enabled = enabled };
        IOptionsMonitor<SlackAuthorizationOptions> monitor = new TestOptionsMonitor<SlackAuthorizationOptions>(opts);
        return new SlackInboundAuthorizer(
            store,
            resolver,
            sink,
            monitor,
            NullLogger<SlackInboundAuthorizer>.Instance,
            TimeProvider.System);
    }

    private static SlackInboundEnvelope BuildEnvelope(string key, string team, string? channel, string user) => new(
        IdempotencyKey: key,
        SourceType: SlackInboundSourceType.Command,
        TeamId: team,
        ChannelId: channel,
        UserId: user,
        RawPayload: $"team_id={team}&user_id={user}&command=/agent",
        TriggerId: "trig",
        ReceivedAt: DateTimeOffset.UtcNow);

    private sealed class FakeWorkspaceStore : ISlackWorkspaceConfigStore
    {
        private readonly Dictionary<string, SlackWorkspaceConfig> rows = new(StringComparer.Ordinal);

        public void Add(SlackWorkspaceConfig config) => this.rows[config.TeamId] = config;

        public static FakeWorkspaceStore WithSingleEnabled(string teamId, string[] channels, string[] groups)
        {
            FakeWorkspaceStore store = new();
            store.Add(new SlackWorkspaceConfig
            {
                TeamId = teamId,
                Enabled = true,
                AllowedChannelIds = channels,
                AllowedUserGroupIds = groups,
            });
            return store;
        }

        public Task<SlackWorkspaceConfig?> GetByTeamIdAsync(string? teamId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(teamId) || !this.rows.TryGetValue(teamId, out SlackWorkspaceConfig? cfg))
            {
                return Task.FromResult<SlackWorkspaceConfig?>(null);
            }

            return Task.FromResult<SlackWorkspaceConfig?>(cfg.Enabled ? cfg : null);
        }

        public Task<IReadOnlyCollection<SlackWorkspaceConfig>> GetAllEnabledAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyCollection<SlackWorkspaceConfig>>(System.Linq.Enumerable.ToArray(this.rows.Values));
    }

    private sealed class FakeMembershipResolver : ISlackMembershipResolver
    {
        private readonly Func<Task<bool>> behaviour;

        private FakeMembershipResolver(Func<Task<bool>> behaviour)
        {
            this.behaviour = behaviour;
        }

        public static FakeMembershipResolver Yes() => new(() => Task.FromResult(true));

        public static FakeMembershipResolver No() => new(() => Task.FromResult(false));

        public static FakeMembershipResolver Throws(Exception ex) => new(() => throw ex);

        public Task<bool> IsUserInAnyAllowedGroupAsync(
            string teamId, string userId, IReadOnlyCollection<string> allowedUserGroupIds, CancellationToken ct)
            => this.behaviour();
    }

    private sealed class FakeAuthAuditSink : ISlackAuthorizationAuditSink
    {
        private readonly ConcurrentQueue<SlackAuthorizationAuditRecord> records = new();

        public IReadOnlyList<SlackAuthorizationAuditRecord> Records => this.records.ToArray();

        public Task WriteAsync(SlackAuthorizationAuditRecord record, CancellationToken ct)
        {
            this.records.Enqueue(record);
            return Task.CompletedTask;
        }
    }

    private sealed class TestOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
        where TOptions : class
    {
        public TestOptionsMonitor(TOptions value)
        {
            this.CurrentValue = value;
        }

        public TOptions CurrentValue { get; }

        public TOptions Get(string? name) => this.CurrentValue;

        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}
