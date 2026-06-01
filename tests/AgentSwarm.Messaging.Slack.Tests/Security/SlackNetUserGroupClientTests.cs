// -----------------------------------------------------------------------
// <copyright file="SlackNetUserGroupClientTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Security;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentSwarm.Messaging.Core.Secrets;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Moq;
using SlackNet;
using SlackNet.WebApi;
using Xunit;

/// <summary>
/// Stage 3.2 unit tests for <see cref="SlackNetUserGroupClient"/> --
/// the production <see cref="ISlackUserGroupClient"/> that wires Slack's
/// <c>usergroups.users.list</c> through SlackNet. These tests pin the
/// integration contract between the per-workspace
/// <see cref="ISlackWorkspaceConfigStore"/> + <see cref="ISecretProvider"/>
/// chain and the SlackNet call shape so regressions are caught at unit
/// time rather than waiting for live Slack integration tests.
/// </summary>
/// <remarks>
/// <para>
/// Coverage targets the four contract points that broke in iteration 1's
/// review:
/// </para>
/// <list type="bullet">
///   <item><description>Bot-token resolution flow: workspace lookup ->
///   secret provider -> SlackNet client factory invocation.</description></item>
///   <item><description>Exact SlackNet call shape:
///   <c>client.UserGroupUsers.List(userGroupId, includeDisabled: false, ct)</c>
///   with <c>includeDisabled = false</c> so soft-deleted members are
///   not granted access.</description></item>
///   <item><description>The cancellation token from the resolver is
///   propagated through to the SlackNet call.</description></item>
///   <item><description>Fail-closed behaviour on misconfiguration:
///   unknown workspace, missing
///   <see cref="SlackWorkspaceConfig.BotTokenSecretRef"/>, and an
///   empty resolved secret all surface as
///   <see cref="InvalidOperationException"/> so
///   <see cref="SlackMembershipResolver"/> wraps them in a controlled
///   <see cref="SlackMembershipResolutionException"/>.</description></item>
/// </list>
/// </remarks>
public sealed class SlackNetUserGroupClientTests
{
    private const string TeamId = "T0123ABCD";
    private const string UserGroupId = "S1111GAMMA";
    private const string BotTokenSecretRef = "env://SLACK_BOT_TOKEN";
    private const string BotTokenValue = "xoxb-test-bot-token";

    [Fact]
    public async Task ListUserGroupMembersAsync_resolves_bot_token_and_invokes_usergroups_users_list()
    {
        // The full happy-path wire-up: store -> secret -> SlackNet
        // call. Verifying every link of the chain via a single fact
        // prevents the prior-iteration gap where SlackNetUserGroupClient
        // had no unit coverage at all.
        SlackWorkspaceConfig workspace = BuildWorkspace();
        InMemorySlackWorkspaceConfigStore store = new(new[] { workspace });
        InMemorySecretProvider secrets = new(new Dictionary<string, string>
        {
            [BotTokenSecretRef] = BotTokenValue,
        });

        Mock<IUserGroupUsersApi> userGroupUsersApi = new(MockBehavior.Strict);
        userGroupUsersApi
            .Setup(api => api.List(UserGroupId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)new List<string> { "U_alpha", "U_beta" });

        string? capturedToken = null;
        Mock<ISlackApiClient> slackClient = new(MockBehavior.Loose);
        slackClient.SetupGet(c => c.UserGroupUsers).Returns(userGroupUsersApi.Object);

        SlackNetUserGroupClient sut = new(store, secrets, token =>
        {
            capturedToken = token;
            return slackClient.Object;
        });

        IReadOnlyCollection<string> members = await sut
            .ListUserGroupMembersAsync(TeamId, UserGroupId, CancellationToken.None);

        capturedToken.Should().Be(BotTokenValue,
            "the workspace bot-token secret must be resolved via the secret provider before constructing the SlackNet client");
        userGroupUsersApi.Verify(
            api => api.List(UserGroupId, false, It.IsAny<CancellationToken>()),
            Times.Once,
            "SlackNet usergroups.users.list must be invoked exactly once with includeDisabled=false so soft-deleted users do not bypass the ACL");
        members.Should().BeEquivalentTo(new[] { "U_alpha", "U_beta" });
    }

    [Fact]
    public async Task ListUserGroupMembersAsync_propagates_cancellation_token_to_slacknet()
    {
        // Stage 3.2's resolver passes the inbound request's
        // CancellationToken down. The production client must propagate
        // it to SlackNet so an aborted Slack request actually cancels
        // the outbound HTTP call.
        SlackWorkspaceConfig workspace = BuildWorkspace();
        InMemorySlackWorkspaceConfigStore store = new(new[] { workspace });
        InMemorySecretProvider secrets = new(new Dictionary<string, string>
        {
            [BotTokenSecretRef] = BotTokenValue,
        });

        using CancellationTokenSource cts = new();
        CancellationToken expected = cts.Token;
        CancellationToken seen = default;

        Mock<IUserGroupUsersApi> userGroupUsersApi = new(MockBehavior.Strict);
        userGroupUsersApi
            .Setup(api => api.List(UserGroupId, false, It.IsAny<CancellationToken>()))
            .Callback<string, bool, CancellationToken>((_, _, ct) => seen = ct)
            .ReturnsAsync((IReadOnlyList<string>)Array.Empty<string>());

        Mock<ISlackApiClient> slackClient = new(MockBehavior.Loose);
        slackClient.SetupGet(c => c.UserGroupUsers).Returns(userGroupUsersApi.Object);

        SlackNetUserGroupClient sut = new(store, secrets, _ => slackClient.Object);

        await sut.ListUserGroupMembersAsync(TeamId, UserGroupId, expected);

        seen.Should().Be(expected,
            "the resolver's CancellationToken must flow through to SlackNet so aborted Slack requests cancel the outbound usergroups.users.list call");
    }

    [Fact]
    public async Task ListUserGroupMembersAsync_returns_empty_when_slacknet_returns_null()
    {
        // SlackNet contracts return Task<IReadOnlyList<string>>; a
        // defensive null-check protects against an unexpected null
        // payload so the resolver never sees a NullReferenceException
        // and instead treats the group as "no members" (deny).
        SlackWorkspaceConfig workspace = BuildWorkspace();
        InMemorySlackWorkspaceConfigStore store = new(new[] { workspace });
        InMemorySecretProvider secrets = new(new Dictionary<string, string>
        {
            [BotTokenSecretRef] = BotTokenValue,
        });

        Mock<IUserGroupUsersApi> userGroupUsersApi = new(MockBehavior.Strict);
        userGroupUsersApi
            .Setup(api => api.List(UserGroupId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>?)null!);

        Mock<ISlackApiClient> slackClient = new(MockBehavior.Loose);
        slackClient.SetupGet(c => c.UserGroupUsers).Returns(userGroupUsersApi.Object);

        SlackNetUserGroupClient sut = new(store, secrets, _ => slackClient.Object);

        IReadOnlyCollection<string> members = await sut
            .ListUserGroupMembersAsync(TeamId, UserGroupId, CancellationToken.None);

        members.Should().NotBeNull("the client must never return null per the ISlackUserGroupClient contract");
        members.Should().BeEmpty();
    }

    [Fact]
    public async Task ListUserGroupMembersAsync_throws_for_unknown_workspace()
    {
        // Fail-closed: if the workspace store has no entry for the
        // requested team_id we surface InvalidOperationException so
        // SlackMembershipResolver wraps it in
        // SlackMembershipResolutionException and the filter rejects
        // the request with MembershipResolutionFailed.
        InMemorySlackWorkspaceConfigStore store = new();
        InMemorySecretProvider secrets = new();

        SlackNetUserGroupClient sut = new(store, secrets, _ =>
            throw new InvalidOperationException("Factory must not be invoked when workspace lookup fails."));

        Func<Task> act = () => sut.ListUserGroupMembersAsync(TeamId, UserGroupId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*workspace '{TeamId}' is not registered*");
    }

    [Fact]
    public async Task ListUserGroupMembersAsync_throws_when_workspace_has_no_bot_token_ref()
    {
        SlackWorkspaceConfig workspace = BuildWorkspace(botTokenSecretRef: string.Empty);
        InMemorySlackWorkspaceConfigStore store = new(new[] { workspace });
        InMemorySecretProvider secrets = new();

        SlackNetUserGroupClient sut = new(store, secrets, _ =>
            throw new InvalidOperationException("Factory must not be invoked when the workspace has no bot-token ref."));

        Func<Task> act = () => sut.ListUserGroupMembersAsync(TeamId, UserGroupId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no bot-token secret reference*");
    }

    [Fact]
    public async Task ListUserGroupMembersAsync_throws_when_resolved_bot_token_is_empty()
    {
        // The secret provider successfully resolves the reference but
        // the secret payload is an empty string. SlackNet would refuse
        // to authenticate; we fail-closed earlier with a controlled
        // exception so the audit trail records "membership resolution
        // failed" rather than a generic Slack auth error.
        SlackWorkspaceConfig workspace = BuildWorkspace();
        InMemorySlackWorkspaceConfigStore store = new(new[] { workspace });
        InMemorySecretProvider secrets = new(new Dictionary<string, string>
        {
            [BotTokenSecretRef] = string.Empty,
        });

        SlackNetUserGroupClient sut = new(store, secrets, _ =>
            throw new InvalidOperationException("Factory must not be invoked when the resolved secret is empty."));

        Func<Task> act = () => sut.ListUserGroupMembersAsync(TeamId, UserGroupId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*resolved to an empty string*");
    }

    [Theory]
    [InlineData(null, UserGroupId)]
    [InlineData("", UserGroupId)]
    [InlineData("   ", UserGroupId)]
    [InlineData(TeamId, null)]
    [InlineData(TeamId, "")]
    [InlineData(TeamId, "   ")]
    public async Task ListUserGroupMembersAsync_validates_required_arguments(string? teamId, string? userGroupId)
    {
        InMemorySlackWorkspaceConfigStore store = new();
        InMemorySecretProvider secrets = new();
        SlackNetUserGroupClient sut = new(store, secrets, _ =>
            throw new InvalidOperationException("Factory must not be invoked for argument-validation failures."));

        Func<Task> act = () => sut.ListUserGroupMembersAsync(teamId!, userGroupId!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_rejects_null_collaborators()
    {
        InMemorySlackWorkspaceConfigStore store = new();
        InMemorySecretProvider secrets = new();

        Action nullStore = () => _ = new SlackNetUserGroupClient(null!, secrets);
        Action nullSecrets = () => _ = new SlackNetUserGroupClient(store, null!);
        Action nullFactory = () => _ = new SlackNetUserGroupClient(store, secrets, apiClientFactory: null!);

        nullStore.Should().Throw<ArgumentNullException>().WithParameterName("workspaceStore");
        nullSecrets.Should().Throw<ArgumentNullException>().WithParameterName("secretProvider");
        nullFactory.Should().Throw<ArgumentNullException>().WithParameterName("apiClientFactory");
    }

    private static SlackWorkspaceConfig BuildWorkspace(string botTokenSecretRef = BotTokenSecretRef)
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_714_410_000);
        return new SlackWorkspaceConfig
        {
            TeamId = TeamId,
            WorkspaceName = "Test Workspace",
            BotTokenSecretRef = botTokenSecretRef,
            SigningSecretRef = "env://SIGN",
            DefaultChannelId = "C_default",
            AllowedChannelIds = new[] { "C_default" },
            AllowedUserGroupIds = new[] { UserGroupId },
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
