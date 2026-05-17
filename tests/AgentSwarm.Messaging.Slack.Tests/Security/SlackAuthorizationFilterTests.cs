// -----------------------------------------------------------------------
// <copyright file="SlackAuthorizationFilterTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Stage 3.2 acceptance tests for <see cref="SlackAuthorizationFilter"/>.
/// Each scenario implements one of the four bullets in the
/// implementation-plan brief
/// (<c>docs/stories/qq-SLACK-MESSENGER-SUPP/implementation-plan.md</c>
/// §Stage 3.2):
/// <list type="bullet">
///   <item><description>Authorized request passes -- the filter calls
///   <c>next()</c> and stamps the resolved identity on
///   <see cref="HttpContext.Items"/>.</description></item>
///   <item><description>Unknown workspace rejected -- HTTP 200 ephemeral
///   message and an audit record with reason
///   <see cref="SlackAuthorizationRejectionReason.UnknownWorkspace"/>.</description></item>
///   <item><description>Disallowed channel rejected -- HTTP 200 ephemeral
///   message.</description></item>
///   <item><description>Membership cache respects TTL -- covered separately
///   in <see cref="SlackMembershipResolverTests"/>.</description></item>
/// </list>
/// Supplementary facts pin the supporting failure modes (missing team_id,
/// disabled workspace, missing user_id, membership-lookup failure, the
/// master-switch toggle, and the events-API ephemeral-body contract).
/// </summary>
public sealed class SlackAuthorizationFilterTests
{
    private const string TeamId = "T0123ABCD";
    private const string ChannelId = "C9999ALPHA";
    private const string UserId = "U7777BETA";
    private const string AllowedGroup = "S1234GAMMA";
    private const string CommandsPath = "/api/slack/commands";
    private const string EventsPath = "/api/slack/events";
    private const string InteractionsPath = "/api/slack/interactions";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task Authorized_request_passes_and_calls_next()
    {
        // Brief scenario: Given a request from an allowed workspace,
        // allowed channel, and a user in an allowed group, When the
        // filter runs, Then the request proceeds.
        Harness h = Harness.Build(allowedChannels: new[] { ChannelId }, allowedGroups: new[] { AllowedGroup });
        h.UserGroupClient.SetMembers(TeamId, AllowedGroup, UserId);

        ActionExecutingContext ctx = BuildCommandContext(
            teamId: TeamId, channelId: ChannelId, userId: UserId,
            command: "/agent", text: "ask write a plan");

        bool nextInvoked = false;
        await h.Filter.OnActionExecutionAsync(ctx, () =>
        {
            nextInvoked = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        nextInvoked.Should().BeTrue("a passing ACL must invoke the next pipeline step");
        ctx.Result.Should().BeNull("the filter must not short-circuit an authorized request");
        h.AuditSink.Records.Should().BeEmpty("the happy path produces no rejection audit");

        ctx.HttpContext.Items[SlackAuthorizationFilter.AuthorizedItemKey].Should().Be(true,
            "downstream stages read the AuthorizedItemKey marker to skip re-checking the ACL");
        ctx.HttpContext.Items[SlackAuthorizationFilter.ChannelIdItemKey].Should().Be(ChannelId);
        ctx.HttpContext.Items[SlackAuthorizationFilter.UserIdItemKey].Should().Be(UserId);
        ctx.HttpContext.Items[SlackAuthorizationFilter.WorkspaceItemKey].Should().BeOfType<SlackWorkspaceConfig>()
            .Which.TeamId.Should().Be(TeamId);
    }

    [Fact]
    public async Task Unknown_workspace_rejected_with_ephemeral_message_and_audit_entry()
    {
        // Brief scenario: Given a request with a team_id not in
        // SlackWorkspaceConfig, When the filter runs, Then an ephemeral
        // error message is returned with audit entry
        // outcome = rejected_auth.
        Harness h = Harness.Build(allowedChannels: new[] { ChannelId }, allowedGroups: new[] { AllowedGroup });

        ActionExecutingContext ctx = BuildCommandContext(
            teamId: "T_NOPE", channelId: ChannelId, userId: UserId,
            command: "/agent", text: "ask hello");

        bool nextInvoked = false;
        await h.Filter.OnActionExecutionAsync(ctx, () =>
        {
            nextInvoked = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        nextInvoked.Should().BeFalse("a rejected request must short-circuit the pipeline");
        AssertEphemeral200(ctx.Result, SlackAuthorizationOptions.DefaultRejectionMessage);

        SlackAuthorizationAuditRecord record = h.AuditSink.Records.Should().ContainSingle().Subject;
        record.Reason.Should().Be(SlackAuthorizationRejectionReason.UnknownWorkspace);
        record.Outcome.Should().Be(SlackAuthorizationAuditRecord.RejectedAuthOutcome);
        record.TeamId.Should().Be("T_NOPE",
            "the brief requires the audit row to include team_id even when the team is unknown");
        record.ChannelId.Should().Be(ChannelId);
        record.UserId.Should().Be(UserId);
    }

    [Fact]
    public async Task Disabled_workspace_rejected_with_unknown_workspace_reason()
    {
        Harness h = Harness.Build(
            allowedChannels: new[] { ChannelId },
            allowedGroups: new[] { AllowedGroup },
            enabled: false);

        ActionExecutingContext ctx = BuildCommandContext(
            teamId: TeamId, channelId: ChannelId, userId: UserId,
            command: "/agent", text: "ask hello");

        await h.Filter.OnActionExecutionAsync(ctx, () => Task.FromResult<ActionExecutedContext>(null!));

        AssertEphemeral200(ctx.Result, SlackAuthorizationOptions.DefaultRejectionMessage);
        h.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackAuthorizationRejectionReason.UnknownWorkspace,
                "a workspace whose Enabled flag is false is indistinguishable from an unknown workspace to the security pipeline");
    }

    [Fact]
    public async Task Disallowed_channel_rejected_with_ephemeral_message()
    {
        // Brief scenario: Given a valid workspace but channel_id not in
        // AllowedChannelIds, When the filter runs, Then an ephemeral
        // error message is returned.
        Harness h = Harness.Build(allowedChannels: new[] { "C_OTHER" }, allowedGroups: new[] { AllowedGroup });

        ActionExecutingContext ctx = BuildCommandContext(
            teamId: TeamId, channelId: ChannelId, userId: UserId,
            command: "/agent", text: "ask hello");

        bool nextInvoked = false;
        await h.Filter.OnActionExecutionAsync(ctx, () =>
        {
            nextInvoked = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        nextInvoked.Should().BeFalse();
        AssertEphemeral200(ctx.Result, SlackAuthorizationOptions.DefaultRejectionMessage);
        h.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackAuthorizationRejectionReason.DisallowedChannel);
    }

    [Fact]
    public async Task User_not_in_allowed_group_rejected_with_ephemeral_message()
    {
        Harness h = Harness.Build(allowedChannels: new[] { ChannelId }, allowedGroups: new[] { AllowedGroup });

        // Group exists in Slack but does NOT contain the requesting user.
        h.UserGroupClient.SetMembers(TeamId, AllowedGroup, "U_someone_else");

        ActionExecutingContext ctx = BuildCommandContext(
            teamId: TeamId, channelId: ChannelId, userId: UserId,
            command: "/agent", text: "ask hello");

        await h.Filter.OnActionExecutionAsync(ctx, () => Task.FromResult<ActionExecutedContext>(null!));

        AssertEphemeral200(ctx.Result, SlackAuthorizationOptions.DefaultRejectionMessage);
        h.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackAuthorizationRejectionReason.UserNotInAllowedGroup);
    }

    [Fact]
    public async Task Membership_resolution_failure_is_fail_closed_with_membership_failed_reason()
    {
        // Defense-in-depth: when Slack's API is unavailable the filter
        // must "fail closed" -- a controlled rejection, not a 500
        // bubbled up to the request pipeline, and not (worse) a silent
        // pass.
        Harness h = Harness.Build(allowedChannels: new[] { ChannelId }, allowedGroups: new[] { AllowedGroup });
        h.UserGroupClient.ThrowOn(AllowedGroup, new InvalidOperationException("Slack outage"));

        ActionExecutingContext ctx = BuildCommandContext(
            teamId: TeamId, channelId: ChannelId, userId: UserId,
            command: "/agent", text: "ask hello");

        await h.Filter.OnActionExecutionAsync(ctx, () => Task.FromResult<ActionExecutedContext>(null!));

        AssertEphemeral200(ctx.Result, SlackAuthorizationOptions.DefaultRejectionMessage);
        h.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackAuthorizationRejectionReason.MembershipResolutionFailed);
    }

    [Fact]
    public async Task Missing_team_id_rejected_with_missing_team_reason()
    {
        Harness h = Harness.Build(allowedChannels: new[] { ChannelId }, allowedGroups: new[] { AllowedGroup });

        ActionExecutingContext ctx = BuildCommandContext(
            teamId: null, channelId: ChannelId, userId: UserId,
            command: "/agent", text: "ask hello");

        await h.Filter.OnActionExecutionAsync(ctx, () => Task.FromResult<ActionExecutedContext>(null!));

        AssertEphemeral200(ctx.Result, SlackAuthorizationOptions.DefaultRejectionMessage);
        h.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackAuthorizationRejectionReason.MissingTeamId);
    }

    [Fact]
    public async Task Empty_allowed_user_groups_rejects_user_not_in_any_group()
    {
        // SlackWorkspaceConfig docstring: an empty allow-list rejects
        // every user (deny-all by default).
        Harness h = Harness.Build(
            allowedChannels: new[] { ChannelId },
            allowedGroups: Array.Empty<string>());

        ActionExecutingContext ctx = BuildCommandContext(
            teamId: TeamId, channelId: ChannelId, userId: UserId,
            command: "/agent", text: "ask hello");

        await h.Filter.OnActionExecutionAsync(ctx, () => Task.FromResult<ActionExecutedContext>(null!));

        AssertEphemeral200(ctx.Result, SlackAuthorizationOptions.DefaultRejectionMessage);
        h.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackAuthorizationRejectionReason.UserNotInAllowedGroup);
    }

    [Fact]
    public async Task Disabled_master_switch_skips_filter_entirely()
    {
        Harness h = Harness.Build(
            allowedChannels: new[] { "C_OTHER" },
            allowedGroups: new[] { AllowedGroup },
            options: new SlackAuthorizationOptions { Enabled = false });

        ActionExecutingContext ctx = BuildCommandContext(
            teamId: TeamId, channelId: ChannelId, userId: UserId,
            command: "/agent", text: "ask hello");

        bool nextInvoked = false;
        await h.Filter.OnActionExecutionAsync(ctx, () =>
        {
            nextInvoked = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        nextInvoked.Should().BeTrue();
        ctx.Result.Should().BeNull();
        h.AuditSink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Url_verification_marker_short_circuits_filter()
    {
        // The Stage 3.1 signature middleware stamps
        // UrlVerificationItemKey for the Events API url_verification
        // handshake; the filter must NOT reject those requests because
        // they intentionally lack team_id / channel / user.
        Harness h = Harness.Build(allowedChannels: new[] { ChannelId }, allowedGroups: new[] { AllowedGroup });
        ActionExecutingContext ctx = BuildJsonContext(EventsPath,
            @"{""type"":""url_verification"",""challenge"":""abc123""}");
        ctx.HttpContext.Items[SlackSignatureValidator.UrlVerificationItemKey] = true;

        bool nextInvoked = false;
        await h.Filter.OnActionExecutionAsync(ctx, () =>
        {
            nextInvoked = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        nextInvoked.Should().BeTrue("url_verification must pass through the filter unmolested");
        ctx.Result.Should().BeNull();
        h.AuditSink.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Events_path_rejection_returns_ephemeral_body_with_200()
    {
        // Per the Stage 3.2 brief: "Return an ephemeral Slack error
        // message for rejected requests (Slack endpoints must return
        // HTTP 200; rejection is communicated in the response body
        // as an ephemeral message)." That contract applies to every
        // inbound Slack surface, including Events API callbacks --
        // even though Slack does not render ephemeral bodies for
        // events, emitting the same {response_type:"ephemeral",text:...}
        // shape on every path gives audit consumers and Stage 4.x
        // retry/replay tooling a uniform rejection body to parse.
        Harness h = Harness.Build(allowedChannels: new[] { "C_OTHER" }, allowedGroups: new[] { AllowedGroup });

        string body = @"{""team_id"":""T0123ABCD"",""event"":{""type"":""app_mention"",""channel"":""C9999ALPHA"",""user"":""U7777BETA""}}";
        ActionExecutingContext ctx = BuildJsonContext(EventsPath, body);

        await h.Filter.OnActionExecutionAsync(ctx, () => Task.FromResult<ActionExecutedContext>(null!));

        ContentResult result = ctx.Result.Should().BeOfType<ContentResult>().Subject;
        result.StatusCode.Should().Be((int)HttpStatusCode.OK,
            "events API endpoints must ack with HTTP 200 even on rejection so Slack does not retry indefinitely");
        result.ContentType.Should().StartWith("application/json");
        result.Content.Should().Contain("\"response_type\":\"ephemeral\"")
            .And.Contain("\"text\":\"");
        h.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackAuthorizationRejectionReason.DisallowedChannel);
    }

    [Fact]
    public async Task Interaction_payload_field_is_parsed_for_team_channel_and_user_ids()
    {
        // Interactions arrive as application/x-www-form-urlencoded with
        // the entire JSON payload wrapped in a `payload` form field.
        Harness h = Harness.Build(allowedChannels: new[] { ChannelId }, allowedGroups: new[] { AllowedGroup });
        h.UserGroupClient.SetMembers(TeamId, AllowedGroup, UserId);

        string payloadJson = @"{""team"":{""id"":""T0123ABCD""},""channel"":{""id"":""C9999ALPHA""},""user"":{""id"":""U7777BETA""},""actions"":[{""value"":""approve""}]}";
        string formBody = "payload=" + Uri.EscapeDataString(payloadJson);
        ActionExecutingContext ctx = BuildFormContext(InteractionsPath, formBody);

        bool nextInvoked = false;
        await h.Filter.OnActionExecutionAsync(ctx, () =>
        {
            nextInvoked = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        nextInvoked.Should().BeTrue();
        h.AuditSink.Records.Should().BeEmpty();
        ctx.HttpContext.Items[SlackAuthorizationFilter.UserIdItemKey].Should().Be(UserId);
    }

    [Fact]
    public async Task Filter_reuses_workspace_stamped_by_signature_middleware()
    {
        // Stage 3.1 stamps the resolved workspace on Items[WorkspaceItemKey]
        // so the filter can avoid a redundant ISlackWorkspaceConfigStore
        // round-trip on the hot path.
        Harness h = Harness.Build(allowedChannels: new[] { ChannelId }, allowedGroups: new[] { AllowedGroup });
        h.UserGroupClient.SetMembers(TeamId, AllowedGroup, UserId);

        ActionExecutingContext ctx = BuildCommandContext(
            teamId: TeamId, channelId: ChannelId, userId: UserId,
            command: "/agent", text: "ask hello");
        SlackWorkspaceConfig stamped = (await h.WorkspaceStore.GetByTeamIdAsync(TeamId, CancellationToken.None))!;
        ctx.HttpContext.Items[SlackAuthorizationFilter.WorkspaceItemKey] = stamped;

        // Counter the store so we can detect re-lookups.
        h.WorkspaceStore.LookupCount.Should().Be(1,
            "the initial GetByTeamIdAsync above primes the harness with one lookup");
        await h.Filter.OnActionExecutionAsync(ctx, () => Task.FromResult<ActionExecutedContext>(null!));

        h.WorkspaceStore.LookupCount.Should().Be(1,
            "the filter must reuse the workspace stamped on HttpContext.Items rather than re-querying the store");
    }

    [Fact]
    public async Task Rejection_message_falls_back_to_default_when_options_value_is_empty()
    {
        // An operator who misconfigures Slack:Authorization:RejectionMessage
        // to an empty string must not produce an empty Slack reply --
        // the filter substitutes the documented default text.
        Harness h = Harness.Build(
            allowedChannels: new[] { "C_OTHER" },
            allowedGroups: new[] { AllowedGroup },
            options: new SlackAuthorizationOptions { Enabled = true, RejectionMessage = "  " });

        ActionExecutingContext ctx = BuildCommandContext(
            teamId: TeamId, channelId: ChannelId, userId: UserId,
            command: "/agent", text: "ask hello");

        await h.Filter.OnActionExecutionAsync(ctx, () => Task.FromResult<ActionExecutedContext>(null!));

        ContentResult result = ctx.Result.Should().BeOfType<ContentResult>().Subject;
        result.Content.Should().Contain(SlackAuthorizationOptions.DefaultRejectionMessage,
            "empty/whitespace operator overrides must degrade to the default rejection text");
    }

    [Fact]
    public async Task Non_slack_path_bypasses_filter_when_default_prefix_is_active()
    {
        // Evaluator iter-1 item 1: the filter is registered as a
        // global MVC filter so future Slack controllers inherit the
        // gate. Without path scoping every non-Slack MVC endpoint
        // (admin APIs, cache invalidation, future controllers
        // unrelated to Slack) would be rejected as MissingTeamId
        // because their bodies have no team_id. Default
        // PathPrefix='/api/slack' mirrors SlackSignatureOptions.PathPrefix
        // and short-circuits non-Slack paths to next() with no audit
        // record and no rejection result.
        Harness h = Harness.Build(allowedChannels: new[] { ChannelId }, allowedGroups: new[] { AllowedGroup });

        // An admin endpoint with no Slack identity fields whatsoever.
        ActionExecutingContext ctx = BuildJsonContext("/api/admin/cache-invalidate", @"{""scope"":""all""}");

        bool nextInvoked = false;
        await h.Filter.OnActionExecutionAsync(ctx, () =>
        {
            nextInvoked = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        nextInvoked.Should().BeTrue("non-Slack paths must pass through the filter without enforcement");
        ctx.Result.Should().BeNull("the filter must not short-circuit a non-Slack request");
        h.AuditSink.Records.Should().BeEmpty("no audit record should be emitted for non-Slack paths -- they are out of scope for the Slack ACL");
        h.WorkspaceStore.LookupCount.Should().Be(0, "the filter must not parse the body or look up a workspace for non-Slack paths");
    }

    [Fact]
    public async Task Custom_prefix_scopes_enforcement_to_matching_paths_only()
    {
        // Operators can override Slack:Authorization:PathPrefix to
        // mount the connector under a non-default URL (e.g.,
        // '/slack-gateway'). Inside the prefix the ACL enforces;
        // outside it the filter is a no-op.
        Harness h = Harness.Build(
            allowedChannels: new[] { ChannelId },
            allowedGroups: new[] { AllowedGroup },
            options: new SlackAuthorizationOptions
            {
                Enabled = true,
                PathPrefix = "/slack-gateway",
            });
        h.UserGroupClient.SetMembers(TeamId, AllowedGroup, UserId);

        // (a) A path UNDER the configured prefix -- enforced.
        ActionExecutingContext inScope = BuildCommandContext(
            teamId: TeamId, channelId: ChannelId, userId: UserId,
            command: "/agent", text: "ask");
        inScope.HttpContext.Request.Path = "/slack-gateway/commands";

        bool inScopeNext = false;
        await h.Filter.OnActionExecutionAsync(inScope, () =>
        {
            inScopeNext = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });
        inScopeNext.Should().BeTrue("an in-scope path with a valid identity must pass through");
        inScope.HttpContext.Items[SlackAuthorizationFilter.AuthorizedItemKey].Should().Be(true);

        // (b) An unrelated path -- bypassed even with no team_id.
        ActionExecutingContext outOfScope = BuildJsonContext("/api/operator/diagnostics", @"{""op"":""ping""}");
        bool outOfScopeNext = false;
        await h.Filter.OnActionExecutionAsync(outOfScope, () =>
        {
            outOfScopeNext = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });
        outOfScopeNext.Should().BeTrue("paths outside the configured prefix must pass through");
        outOfScope.Result.Should().BeNull();
        h.AuditSink.Records.Should().BeEmpty("an out-of-scope path must not emit an audit record");
    }

    [Fact]
    public async Task Empty_prefix_disables_path_guard_and_enforces_on_every_action()
    {
        // A diagnostic host that only mounts Slack controllers can
        // explicitly clear the prefix to enforce on every action.
        Harness h = Harness.Build(
            allowedChannels: new[] { ChannelId },
            allowedGroups: new[] { AllowedGroup },
            options: new SlackAuthorizationOptions { Enabled = true, PathPrefix = string.Empty });

        // Even a non-/api/slack path without team_id must now be rejected.
        ActionExecutingContext ctx = BuildJsonContext("/anything", "{}");
        await h.Filter.OnActionExecutionAsync(ctx, () => Task.FromResult<ActionExecutedContext>(null!));

        AssertEphemeral200(ctx.Result, SlackAuthorizationOptions.DefaultRejectionMessage);
        h.AuditSink.Records.Should().ContainSingle()
            .Which.Reason.Should().Be(SlackAuthorizationRejectionReason.MissingTeamId,
                "an empty PathPrefix disables the bypass so non-Slack paths fall into the ACL just like inbound Slack paths");
    }

    [Fact]
    public async Task Custom_rejection_message_is_json_escaped_in_ephemeral_body()
    {
        // Defense against an operator-supplied string that contains
        // characters that would otherwise break the inline JSON
        // serialisation (quotes, newlines, control chars).
        Harness h = Harness.Build(
            allowedChannels: new[] { "C_OTHER" },
            allowedGroups: new[] { AllowedGroup },
            options: new SlackAuthorizationOptions
            {
                Enabled = true,
                RejectionMessage = "no \"slash\"\nfor you",
            });

        ActionExecutingContext ctx = BuildCommandContext(
            teamId: TeamId, channelId: ChannelId, userId: UserId,
            command: "/agent", text: "ask");

        await h.Filter.OnActionExecutionAsync(ctx, () => Task.FromResult<ActionExecutedContext>(null!));

        ContentResult result = ctx.Result.Should().BeOfType<ContentResult>().Subject;
        result.Content.Should().Contain("no \\\"slash\\\"\\nfor you",
            "the ephemeral body builder must escape quotes and newlines for JSON safety");
    }

    private static void AssertEphemeral200(IActionResult? result, string expectedText)
    {
        ContentResult content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be((int)HttpStatusCode.OK,
            "Slack endpoints must return HTTP 200 even on rejection -- non-2xx triggers Slack retries");
        content.ContentType.Should().StartWith("application/json");
        content.Content.Should().Contain("\"response_type\":\"ephemeral\"");
        content.Content.Should().Contain(expectedText);
    }

    private static ActionExecutingContext BuildCommandContext(
        string? teamId,
        string channelId,
        string userId,
        string command,
        string text)
    {
        StringBuilder body = new();
        AppendField(body, "team_id", teamId);
        AppendField(body, "channel_id", channelId);
        AppendField(body, "user_id", userId);
        AppendField(body, "command", command);
        AppendField(body, "text", text);

        return BuildFormContext(CommandsPath, body.ToString());
    }

    private static void AppendField(StringBuilder body, string name, string? value)
    {
        if (value is null)
        {
            return;
        }

        if (body.Length > 0)
        {
            body.Append('&');
        }

        body.Append(Uri.EscapeDataString(name));
        body.Append('=');
        body.Append(Uri.EscapeDataString(value));
    }

    private static ActionExecutingContext BuildJsonContext(string path, string jsonBody)
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Method = "POST";
        ctx.Request.Path = path;
        ctx.Request.ContentType = "application/json";

        byte[] bytes = Utf8NoBom.GetBytes(jsonBody);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        return BuildActionContext(ctx);
    }

    private static ActionExecutingContext BuildFormContext(string path, string formBody)
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Method = "POST";
        ctx.Request.Path = path;
        ctx.Request.ContentType = "application/x-www-form-urlencoded";

        byte[] bytes = Utf8NoBom.GetBytes(formBody);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        return BuildActionContext(ctx);
    }

    private static ActionExecutingContext BuildActionContext(HttpContext http)
    {
        ActionContext actionContext = new(http, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());
    }

    private sealed class Harness
    {
        public Harness(
            SlackAuthorizationFilter filter,
            RecordingWorkspaceStore workspaceStore,
            RecordingUserGroupClient userGroupClient,
            InMemorySlackAuthorizationAuditSink auditSink)
        {
            this.Filter = filter;
            this.WorkspaceStore = workspaceStore;
            this.UserGroupClient = userGroupClient;
            this.AuditSink = auditSink;
        }

        public SlackAuthorizationFilter Filter { get; }

        public RecordingWorkspaceStore WorkspaceStore { get; }

        public RecordingUserGroupClient UserGroupClient { get; }

        public InMemorySlackAuthorizationAuditSink AuditSink { get; }

        public static Harness Build(
            IReadOnlyCollection<string> allowedChannels,
            IReadOnlyCollection<string> allowedGroups,
            SlackAuthorizationOptions? options = null,
            bool enabled = true)
        {
            DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
            SlackWorkspaceConfig workspace = new()
            {
                TeamId = TeamId,
                WorkspaceName = "Test Workspace",
                BotTokenSecretRef = "env://BOT",
                SigningSecretRef = "env://SIGN",
                DefaultChannelId = ChannelId,
                AllowedChannelIds = allowedChannels is null ? Array.Empty<string>() : new List<string>(allowedChannels).ToArray(),
                AllowedUserGroupIds = allowedGroups is null ? Array.Empty<string>() : new List<string>(allowedGroups).ToArray(),
                Enabled = enabled,
                CreatedAt = now,
                UpdatedAt = now,
            };

            RecordingWorkspaceStore store = new(new[] { workspace });
            RecordingUserGroupClient client = new();
            InMemorySlackAuthorizationAuditSink auditSink = new();

            SlackAuthorizationOptions effectiveOptions = options ?? new SlackAuthorizationOptions();
            StaticOptionsMonitor<SlackAuthorizationOptions> optionsMonitor = new(effectiveOptions);

            FixedTimeProvider timeProvider = new(now);

            SlackMembershipResolver resolver = new(
                client,
                new StaticOptionsMonitor<AgentSwarm.Messaging.Slack.Configuration.SlackConnectorOptions>(
                    new AgentSwarm.Messaging.Slack.Configuration.SlackConnectorOptions()),
                NullLogger<SlackMembershipResolver>.Instance,
                timeProvider);

            SlackAuthorizationFilter filter = new(
                store,
                resolver,
                auditSink,
                optionsMonitor,
                NullLogger<SlackAuthorizationFilter>.Instance,
                timeProvider);

            return new Harness(filter, store, client, auditSink);
        }
    }

    internal sealed class RecordingWorkspaceStore : ISlackWorkspaceConfigStore
    {
        private readonly InMemorySlackWorkspaceConfigStore inner;
        private int lookupCount;

        public RecordingWorkspaceStore(IEnumerable<SlackWorkspaceConfig> seed)
        {
            this.inner = new InMemorySlackWorkspaceConfigStore(seed);
        }

        public int LookupCount => this.lookupCount;

        public Task<SlackWorkspaceConfig?> GetByTeamIdAsync(string? teamId, CancellationToken ct)
        {
            Interlocked.Increment(ref this.lookupCount);
            return this.inner.GetByTeamIdAsync(teamId, ct);
        }

        public Task<IReadOnlyCollection<SlackWorkspaceConfig>> GetAllEnabledAsync(CancellationToken ct)
            => this.inner.GetAllEnabledAsync(ct);
    }

    internal sealed class RecordingUserGroupClient : ISlackUserGroupClient
    {
        private readonly ConcurrentDictionary<(string Team, string Group), HashSet<string>> members = new();
        private readonly ConcurrentDictionary<string, Exception> errorsByGroup = new();
        private int callCount;

        public int CallCount => this.callCount;

        public void SetMembers(string teamId, string userGroupId, params string[] userIds)
        {
            this.members[(teamId, userGroupId)] = new HashSet<string>(userIds, StringComparer.Ordinal);
        }

        public void ThrowOn(string userGroupId, Exception ex)
        {
            this.errorsByGroup[userGroupId] = ex;
        }

        public Task<IReadOnlyCollection<string>> ListUserGroupMembersAsync(
            string teamId,
            string userGroupId,
            CancellationToken ct)
        {
            Interlocked.Increment(ref this.callCount);
            if (this.errorsByGroup.TryGetValue(userGroupId, out Exception? err))
            {
                return Task.FromException<IReadOnlyCollection<string>>(err);
            }

            if (this.members.TryGetValue((teamId, userGroupId), out HashSet<string>? set))
            {
                IReadOnlyCollection<string> snapshot = new List<string>(set);
                return Task.FromResult(snapshot);
            }

            return Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());
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
