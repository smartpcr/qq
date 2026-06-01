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

    [Fact]
    public async Task AuthorizeAsync_posts_ephemeral_via_response_url_when_responder_wired_and_rejection_occurs()
    {
        // Stage 4.2 iter-2: AC-5 leg. A Socket Mode slash command
        // rejected during async processing has no HTTP body to render
        // an ephemeral into -- the WebSocket ACK frame only carries
        // envelope_id. The authorizer MUST POST the configured
        // rejection wording to envelope.ResponseUrl via the wired
        // ISlackEphemeralResponder so the originating user still
        // sees the rejection.
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: new[] { "C-allowed" }, groups: new[] { "G1" });
        FakeAuthAuditSink sink = new();
        FakeEphemeralResponder responder = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink, responder: responder);

        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U1:/agent:trig", "T1", "C-denied", "U1") with
        {
            ResponseUrl = "https://hooks.slack.com/commands/T1/AC1",
        };

        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.Reason.Should().Be(SlackAuthorizationRejectionReason.DisallowedChannel);
        responder.Captures.Should().ContainSingle(
            "AC-5 brief: a denied-channel Socket Mode envelope MUST produce exactly one ephemeral POST to response_url so the originating user sees the rejection")
            .Which.ResponseUrl.Should().Be("https://hooks.slack.com/commands/T1/AC1");
        responder.Captures[0].Message.Should().Be(
            SlackAuthorizationOptions.DefaultRejectionMessage,
            "the authorizer MUST send the operator-configured rejection wording -- DefaultRejectionMessage when RejectionMessage is unset -- so both authorisation surfaces deliver the same user-visible text");
    }

    [Fact]
    public async Task AuthorizeAsync_does_not_post_ephemeral_when_envelope_has_no_response_url()
    {
        // Events API callbacks (and any other source without a
        // response_url) MUST NOT trigger an ephemeral POST -- there
        // is no per-invocation Slack URL to target. The audit row
        // is still written; the responder is simply not invoked.
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: new[] { "C-allowed" }, groups: new[] { "G1" });
        FakeAuthAuditSink sink = new();
        FakeEphemeralResponder responder = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink, responder: responder);

        SlackInboundEnvelope envelope = BuildEnvelope("event:Ev1", "T1", "C-denied", "U1");
        // BuildEnvelope leaves ResponseUrl null.

        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        sink.Records.Should().ContainSingle();
        responder.Captures.Should().BeEmpty(
            "envelopes without a response_url (Events API, Socket Mode events) MUST NOT trigger an ephemeral POST -- there is no Slack-supported user-reply target");
    }

    [Fact]
    public async Task AuthorizeAsync_does_not_throw_when_ephemeral_responder_fails()
    {
        // The responder contract is best-effort -- a transient
        // HTTP failure inside the responder MUST NOT escalate into
        // an authorization-level exception, because the rejection
        // is the terminal disposition and the audit row is already
        // durable.
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: new[] { "C-allowed" }, groups: new[] { "G1" });
        FakeAuthAuditSink sink = new();
        FakeEphemeralResponder responder = new() { ThrowOnSend = new InvalidOperationException("transport down") };
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink, responder: responder);

        SlackInboundEnvelope envelope = BuildEnvelope("cmd:T1:U1:/agent:trig", "T1", "C-denied", "U1") with
        {
            ResponseUrl = "https://hooks.slack.com/commands/T1/AC1",
        };

        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.Reason.Should().Be(SlackAuthorizationRejectionReason.DisallowedChannel);
        sink.Records.Should().ContainSingle(
            "the rejection audit row MUST be persisted even when the ephemeral responder fails -- audit is the durable record, the ephemeral is best-effort");
    }

    [Fact]
    public async Task AuthorizeAsync_populates_command_text_from_socket_mode_json_payload()
    {
        // Stage 4.2 iter-2: the rejected_auth audit row's
        // command_text MUST carry the verbatim slash command an
        // operator needs to triage the rejection. Socket Mode
        // slash_commands frames deliver JSON bodies (not form-
        // encoded), so the audit-field helper's JSON branch is the
        // one exercised here.
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: new[] { "C-allowed" }, groups: new[] { "G1" });
        FakeAuthAuditSink sink = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink);

        const string SocketModeJson = "{\"team_id\":\"T1\",\"channel_id\":\"C-denied\",\"user_id\":\"U1\","
            + "\"command\":\"/agent\",\"text\":\"ask generate implementation plan\","
            + "\"trigger_id\":\"trig\",\"response_url\":\"https://hooks.slack.com/commands/T1/AC1\"}";

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "cmd:T1:U1:/agent:trig",
            SourceType: SlackInboundSourceType.Command,
            TeamId: "T1",
            ChannelId: "C-denied",
            UserId: "U1",
            RawPayload: SocketModeJson,
            TriggerId: "trig",
            ReceivedAt: DateTimeOffset.UtcNow);

        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        sink.Records.Should().ContainSingle();
        sink.Records[0].CommandText.Should().Be(
            "/agent ask generate implementation plan",
            "the rejected_auth row's command_text MUST be extracted via SlackInboundEnvelopeAuditFields.Extract, which auto-detects JSON-shaped Socket Mode payloads; a null command_text would leak the synthetic 'ingestor://' marker into the row and break audit triage");
    }

    private static SlackInboundAuthorizer BuildAuthorizer(
        FakeWorkspaceStore store,
        FakeMembershipResolver resolver,
        FakeAuthAuditSink sink,
        bool enabled = true,
        ISlackEphemeralResponder? responder = null)
    {
        SlackAuthorizationOptions opts = new() { Enabled = enabled };
        IOptionsMonitor<SlackAuthorizationOptions> monitor = new TestOptionsMonitor<SlackAuthorizationOptions>(opts);
        if (responder is null)
        {
            return new SlackInboundAuthorizer(
                store,
                resolver,
                sink,
                monitor,
                NullLogger<SlackInboundAuthorizer>.Instance,
                TimeProvider.System);
        }

        return new SlackInboundAuthorizer(
            store,
            resolver,
            sink,
            monitor,
            NullLogger<SlackInboundAuthorizer>.Instance,
            TimeProvider.System,
            responder);
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

    private sealed class FakeEphemeralResponder : ISlackEphemeralResponder
    {
        private readonly ConcurrentQueue<EphemeralCapture> captures = new();

        public Exception? ThrowOnSend { get; init; }

        public IReadOnlyList<EphemeralCapture> Captures => this.captures.ToArray();

        public Task SendEphemeralAsync(string? responseUrl, string message, CancellationToken ct)
        {
            this.captures.Enqueue(new EphemeralCapture(responseUrl, message));
            if (this.ThrowOnSend is not null)
            {
                throw this.ThrowOnSend;
            }

            return Task.CompletedTask;
        }

        public sealed record EphemeralCapture(string? ResponseUrl, string Message);
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
