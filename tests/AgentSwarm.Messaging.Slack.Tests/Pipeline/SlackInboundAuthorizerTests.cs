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
    public async Task RejectAsync_stamps_verbatim_command_text_on_rejected_auth_record_for_disallowed_channel()
    {
        // Iter-7 evaluator item #2 (STRUCTURAL unit-level pin): the
        // pipeline-side authorizer MUST persist the verbatim slash
        // command on the rejected_auth audit record so an operator can
        // triage a denied-channel rejection without falling back to
        // raw payload inspection (story FR-008 "Audit": persist
        // ... command text). Iter-6 evaluator flagged this with
        // "SlackInboundAuthorizer.RejectAsync sets CommandText: null"
        // -- the production fix routes via
        // SlackInboundEnvelopeAuditFields.Extract, but the only direct
        // proof at the unit-test level was an integration assertion.
        // This unit test pins the contract at the seam itself: if a
        // future refactor regresses the field-extraction call back to
        // null, this test fails sharply with a one-line message rather
        // than relying on the integration suite to catch it.
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: new[] { "C-allowed" }, groups: new[] { "G1" });
        FakeAuthAuditSink sink = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink);

        // Slack delivers slash commands as application/x-www-form-urlencoded.
        // The factory normalises that into envelope.RawPayload verbatim;
        // the authorizer's helper re-parses it to reconstruct CommandText.
        string rawCommandPayload =
            "team_id=T1&channel_id=C-other&user_id=U1"
            + "&command=%2Fagent"
            + "&text=ask+generate+implementation+plan+for+persistence+failover"
            + "&trigger_id=trig&response_url=https%3A%2F%2Fhooks.slack.com%2Fcommands%2FT1%2F1%2Fxxx";

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "cmd:T1:U1:/agent:trig",
            SourceType: SlackInboundSourceType.Command,
            TeamId: "T1",
            ChannelId: "C-other",
            UserId: "U1",
            RawPayload: rawCommandPayload,
            TriggerId: "trig",
            ReceivedAt: DateTimeOffset.UtcNow);

        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.Reason.Should().Be(SlackAuthorizationRejectionReason.DisallowedChannel);

        sink.Records.Should().ContainSingle();
        SlackAuthorizationAuditRecord rejected = sink.Records[0];
        rejected.Outcome.Should().Be(SlackAuthorizationAuditRecord.RejectedAuthOutcome);
        rejected.Reason.Should().Be(SlackAuthorizationRejectionReason.DisallowedChannel);

        rejected.CommandText.Should().NotBeNullOrEmpty(
            "story FR-008 Audit requires command_text on EVERY audit row including rejected_auth; "
            + "the iter-7 fix routes RejectAsync through SlackInboundEnvelopeAuditFields.Extract -- "
            + "if a regression passes CommandText: null again, this assertion catches it at the unit-test level "
            + "before any integration test even runs");
        rejected.CommandText!.Should().Contain("/agent",
            "the rejected_auth row's command_text MUST carry the literal slash-command prefix; "
            + "Slack URL-encodes it as %2Fagent in the form-encoded body and the extractor MUST decode it");
        rejected.CommandText.Should().Contain("ask generate implementation plan for persistence failover",
            "the rejected_auth row's command_text MUST carry the verbatim slash-command TEXT "
            + "an operator triaging the rejection needs the full prompt the user attempted, "
            + "not a synthetic 'ingestor://' marker that hides the prompt behind a path discriminator");
    }

    [Fact]
    public async Task RejectAsync_stamps_action_id_on_rejected_auth_record_for_interaction_envelope()
    {
        // Companion unit test to the disallowed-channel command above:
        // when the rejected envelope is a Block Kit interaction (not a
        // slash command), the helper's "Interaction" branch extracts
        // the action_id from the payload's actions[0]. This locks the
        // helper's other branch so a regression that breaks one source
        // type cannot pass while the other still works.
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: new[] { "C-allowed" }, groups: new[] { "G1" });
        FakeAuthAuditSink sink = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink);

        const string interactionJson = """
        {"type":"block_actions","team":{"id":"T1"},"user":{"id":"U1"},"channel":{"id":"C-other"},"actions":[{"action_id":"act-approve","value":"approve"}]}
        """;
        string rawInteractionPayload = "payload=" + Uri.EscapeDataString(interactionJson);

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "int:T1:U1:act-approve",
            SourceType: SlackInboundSourceType.Interaction,
            TeamId: "T1",
            ChannelId: "C-other",
            UserId: "U1",
            RawPayload: rawInteractionPayload,
            TriggerId: null,
            ReceivedAt: DateTimeOffset.UtcNow);

        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.Reason.Should().Be(SlackAuthorizationRejectionReason.DisallowedChannel);

        sink.Records.Should().ContainSingle();
        SlackAuthorizationAuditRecord rejected = sink.Records[0];
        rejected.CommandText.Should().Be(
            "act-approve",
            "the rejected_auth row's command_text MUST carry the Block Kit action_id for interaction envelopes "
            + "so operators can pivot on which decision a user attempted in a denied channel "
            + "(SlackInboundEnvelopeAuditFields.Extract Interaction branch)");
    }

    [Fact]
    public async Task RejectAsync_stamps_action_id_on_rejected_auth_record_for_socket_mode_json_interaction_envelope()
    {
        // Iter-9 evaluator item #1 (STRUCTURAL unit-level pin): the
        // Socket Mode interactive transport stamps the raw Block Kit
        // JSON onto envelope.RawPayload verbatim (Transport/
        // SlackSocketModePayloadNormalizer.cs:168), with no
        // surrounding "payload=" form-field wrapper -- that wrapper
        // only exists on the HTTP transport. Iter-8 evaluator flagged
        // that SlackInboundEnvelopeAuditFields.ExtractInteraction
        // only knew the form-wrapped shape, so any pipeline-side
        // rejected Socket Mode button click / modal submission lost
        // its CommandText / action_id in the rejected_auth audit row.
        // This unit test pins the auto-detect branch (JSON vs form)
        // at the seam itself: if a future refactor regresses the
        // JSON-detection path back to form-only, the assertion below
        // fails sharply with the action_id field empty.
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: new[] { "C-allowed" }, groups: new[] { "G1" });
        FakeAuthAuditSink sink = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink);

        // Socket Mode delivers the same JSON shape directly -- no
        // "payload=" prefix, no URL-encoding -- because the Socket
        // Mode WebSocket frame's payload is already JSON.
        const string rawSocketModeInteractionJson = """
        {"type":"block_actions","team":{"id":"T1"},"user":{"id":"U1"},"channel":{"id":"C-other"},"container":{"type":"message","thread_ts":"1700000001.000100","message_ts":"1700000002.000200"},"actions":[{"action_id":"act-socket-approve","value":"approve"}]}
        """;

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "interact:T1:U1:act-socket-approve:trig",
            SourceType: SlackInboundSourceType.Interaction,
            TeamId: "T1",
            ChannelId: "C-other",
            UserId: "U1",
            RawPayload: rawSocketModeInteractionJson,
            TriggerId: null,
            ReceivedAt: DateTimeOffset.UtcNow);

        SlackInboundAuthorizationResult result = await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        result.IsAuthorized.Should().BeFalse();
        result.Reason.Should().Be(SlackAuthorizationRejectionReason.DisallowedChannel);

        sink.Records.Should().ContainSingle();
        SlackAuthorizationAuditRecord rejected = sink.Records[0];
        rejected.Outcome.Should().Be(SlackAuthorizationAuditRecord.RejectedAuthOutcome);
        rejected.CommandText.Should().Be(
            "act-socket-approve",
            "the Socket Mode transport stamps raw Block Kit JSON onto envelope.RawPayload without the HTTP "
            + "transport's 'payload=' form wrapper; the audit-field extractor MUST auto-detect that JSON shape "
            + "and pull actions[0].action_id directly, otherwise every pipeline-side rejected Socket Mode "
            + "button click silently drops its action_id in the rejected_auth row (story FR-008 Audit gap)");
    }

    [Fact]
    public async Task RejectAsync_posts_ephemeral_via_response_url_when_envelope_carries_it()
    {
        // Iter-5 evaluator item #1 (STRUCTURAL unit-level pin): the
        // pipeline-side authorizer MUST POST the ephemeral rejection
        // message via the envelope's response_url on rejection. The
        // integration test asserts the same via MockSlackWebApi, but
        // a unit test makes the contract immune to integration-suite
        // flakes and pins the wiring at the seam itself.
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: new[] { "C-allowed" }, groups: new[] { "G1" });
        FakeAuthAuditSink sink = new();
        CapturingEphemeralResponder responder = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink, responder: responder);

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "cmd:T1:U1:/agent:trig",
            SourceType: SlackInboundSourceType.Command,
            TeamId: "T1",
            ChannelId: "C-other",
            UserId: "U1",
            RawPayload: "team_id=T1&command=%2Fagent&text=ask",
            TriggerId: "trig",
            ReceivedAt: DateTimeOffset.UtcNow)
        {
            ResponseUrl = "https://hooks.slack.com/commands/T1/1/xxx",
        };

        await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        responder.Sent.Should().ContainSingle();
        (string? url, string message) = responder.Sent[0];
        url.Should().Be(
            "https://hooks.slack.com/commands/T1/1/xxx",
            "the authorizer MUST POST to the envelope's verbatim response_url -- this is the only Slack-supported "
            + "channel for delivering a user-visible ephemeral after the HTTP / WebSocket ACK has already been sent");
        message.Should().Be(
            SlackAuthorizationOptions.DefaultRejectionMessage,
            "the operator-configured rejection wording MUST be delivered (the sync MVC filter and the async authorizer "
            + "share the same message text via SlackAuthorizationOptions.RejectionMessage)");
    }

    [Fact]
    public async Task RejectAsync_does_not_post_ephemeral_when_envelope_has_no_response_url()
    {
        // Companion to the test above: Events API callbacks never
        // carry a response_url, so the authorizer's ephemeral leg
        // MUST short-circuit cleanly without throwing. The audit row
        // is still the only durable footprint in this case.
        FakeWorkspaceStore stores = FakeWorkspaceStore.WithSingleEnabled("T1", channels: new[] { "C-allowed" }, groups: new[] { "G1" });
        FakeAuthAuditSink sink = new();
        CapturingEphemeralResponder responder = new();
        SlackInboundAuthorizer authorizer = BuildAuthorizer(stores, FakeMembershipResolver.Yes(), sink, responder: responder);

        SlackInboundEnvelope envelope = new(
            IdempotencyKey: "event:T1:Ev1",
            SourceType: SlackInboundSourceType.Event,
            TeamId: "T1",
            ChannelId: "C-other",
            UserId: "U1",
            RawPayload: """{"event":{"type":"app_mention","text":"hi","ts":"1.1"}}""",
            TriggerId: null,
            ReceivedAt: DateTimeOffset.UtcNow);

        envelope.ResponseUrl.Should().BeNull("Events API callbacks have no response_url by construction");

        await authorizer.AuthorizeAsync(envelope, CancellationToken.None);

        responder.Sent.Should().BeEmpty(
            "the authorizer MUST short-circuit ephemeral delivery when response_url is null "
            + "-- otherwise it would crash or spuriously try to POST to a missing endpoint");
        sink.Records.Should().ContainSingle("the rejected_auth audit row is the only durable footprint when no response_url exists");
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
        bool enabled = true,
        CapturingEphemeralResponder? responder = null)
    {
        SlackAuthorizationOptions opts = new() { Enabled = enabled };
        IOptionsMonitor<SlackAuthorizationOptions> monitor = new TestOptionsMonitor<SlackAuthorizationOptions>(opts);
        return new SlackInboundAuthorizer(
            store,
            resolver,
            sink,
            monitor,
            responder ?? new CapturingEphemeralResponder(),
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

    private sealed class CapturingEphemeralResponder : ISlackEphemeralResponder
    {
        private readonly ConcurrentQueue<(string? ResponseUrl, string Message)> sent = new();

        public IReadOnlyList<(string? ResponseUrl, string Message)> Sent => this.sent.ToArray();

        public Task SendEphemeralAsync(string? responseUrl, string message, CancellationToken ct)
        {
            this.sent.Enqueue((responseUrl, message));
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
