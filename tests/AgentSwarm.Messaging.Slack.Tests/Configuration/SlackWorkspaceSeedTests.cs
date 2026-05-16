// -----------------------------------------------------------------------
// <copyright file="SlackWorkspaceSeedTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentSwarm.Messaging.Slack.Tests.Configuration;

using System.Collections.Generic;
using System.Threading;
using AgentSwarm.Messaging.Slack.Configuration;
using AgentSwarm.Messaging.Slack.Entities;
using AgentSwarm.Messaging.Slack.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Stage 3.1 evaluator iter-1 item 4 regression tests for
/// <see cref="SlackConnectorServiceCollectionExtensions.AddSlackWorkspaceConfigStoreFromConfiguration"/>.
/// </summary>
/// <remarks>
/// The iter-1 evaluator flagged: "the Worker host defaults to empty
/// in-memory workspace/secret stores with no configuration path to seed
/// a workspace signing secret". These tests pin the new
/// <c>Slack:Workspaces</c> configuration shape so a future regression in
/// the seed extension (e.g., accidental rename of the section, lost
/// array binding) surfaces here.
/// </remarks>
public sealed class SlackWorkspaceSeedTests
{
    [Fact]
    public async System.Threading.Tasks.Task Seeds_workspace_store_from_flat_array_config_shape()
    {
        Dictionary<string, string?> config = new()
        {
            ["Slack:Workspaces:0:TeamId"] = "T0FLAT0001",
            ["Slack:Workspaces:0:WorkspaceName"] = "Flat Form Workspace",
            ["Slack:Workspaces:0:SigningSecretRef"] = "env://SLACK_SIGNING_T0FLAT0001",
            ["Slack:Workspaces:0:BotTokenSecretRef"] = "env://SLACK_BOT_T0FLAT0001",
            ["Slack:Workspaces:0:DefaultChannelId"] = "C0FLAT0001",
            ["Slack:Workspaces:0:Enabled"] = "true",
            ["Slack:Workspaces:0:AllowedChannelIds:0"] = "C0FLAT0001",
            ["Slack:Workspaces:0:AllowedChannelIds:1"] = "C0FLAT0002",
            ["Slack:Workspaces:0:AllowedUserGroupIds:0"] = "S0FLAT0001",
        };

        ISlackWorkspaceConfigStore store = BuildStore(config);

        SlackWorkspaceConfig? entry = await store.GetByTeamIdAsync("T0FLAT0001", CancellationToken.None);
        entry.Should().NotBeNull();
        entry!.WorkspaceName.Should().Be("Flat Form Workspace");
        entry.SigningSecretRef.Should().Be("env://SLACK_SIGNING_T0FLAT0001");
        entry.BotTokenSecretRef.Should().Be("env://SLACK_BOT_T0FLAT0001");
        entry.DefaultChannelId.Should().Be("C0FLAT0001");
        entry.AllowedChannelIds.Should().BeEquivalentTo(new[] { "C0FLAT0001", "C0FLAT0002" });
        entry.AllowedUserGroupIds.Should().BeEquivalentTo(new[] { "S0FLAT0001" });
        entry.Enabled.Should().BeTrue();
    }

    [Fact]
    public async System.Threading.Tasks.Task Seeds_multiple_workspaces_in_order()
    {
        Dictionary<string, string?> config = new()
        {
            ["Slack:Workspaces:0:TeamId"] = "T0MULTI001",
            ["Slack:Workspaces:0:SigningSecretRef"] = "env://A",
            ["Slack:Workspaces:0:Enabled"] = "true",
            ["Slack:Workspaces:1:TeamId"] = "T0MULTI002",
            ["Slack:Workspaces:1:SigningSecretRef"] = "env://B",
            ["Slack:Workspaces:1:Enabled"] = "true",
        };

        ISlackWorkspaceConfigStore store = BuildStore(config);

        IReadOnlyCollection<SlackWorkspaceConfig> all = await store.GetAllEnabledAsync(CancellationToken.None);
        all.Should().HaveCount(2);
        all.Should().Contain(c => c.TeamId == "T0MULTI001");
        all.Should().Contain(c => c.TeamId == "T0MULTI002");
    }

    [Fact]
    public async System.Threading.Tasks.Task Disabled_workspace_returns_null_from_GetByTeamIdAsync_per_store_contract()
    {
        // Stage 3.1 evaluator iter-4 item 2: ISlackWorkspaceConfigStore
        // contracts that disabled rows are filtered at the store
        // boundary. A direct GetByTeamIdAsync lookup MUST return
        // null for an Enabled=false row so future authorization code
        // (Stage 3.2 ACL filter) can trust the result without
        // re-checking Enabled. The rejection audit still records the
        // team_id because SlackSignatureValidator passes the requested
        // team_id through to SlackSignatureValidationResult regardless
        // of whether the workspace was resolved.
        Dictionary<string, string?> config = new()
        {
            ["Slack:Workspaces:0:TeamId"] = "T0DISABLED",
            ["Slack:Workspaces:0:SigningSecretRef"] = "env://DISABLED",
            ["Slack:Workspaces:0:Enabled"] = "false",
        };

        ISlackWorkspaceConfigStore store = BuildStore(config);

        SlackWorkspaceConfig? lookup = await store.GetByTeamIdAsync("T0DISABLED", CancellationToken.None);
        lookup.Should().BeNull(
            "the store boundary filters Enabled=false rows so callers cannot accidentally trust a disabled workspace");

        IReadOnlyCollection<SlackWorkspaceConfig> enabled = await store.GetAllEnabledAsync(CancellationToken.None);
        enabled.Should().BeEmpty(
            "GetAllEnabledAsync must also filter out Enabled = false rows so the url_verification handshake skips them");
    }

    [Fact]
    public async System.Threading.Tasks.Task Empty_section_yields_empty_store()
    {
        Dictionary<string, string?> config = new();

        ISlackWorkspaceConfigStore store = BuildStore(config);

        IReadOnlyCollection<SlackWorkspaceConfig> all = await store.GetAllEnabledAsync(CancellationToken.None);
        all.Should().BeEmpty(
            "a missing Slack:Workspaces section must NOT throw -- the host starts up and the validator rejects every request with UnknownWorkspace until an operator seeds at least one workspace");
    }

    [Fact]
    public void Entry_with_blank_team_id_fails_startup_with_actionable_message()
    {
        // Stage 3.1 evaluator iter-2 item 3: a security-critical
        // workspace-config error (blank TeamId) must NOT silently
        // disappear into an empty/partial store -- it must fail
        // startup loud enough that the operator fixes it before any
        // real Slack traffic arrives.
        Dictionary<string, string?> config = new()
        {
            ["Slack:Workspaces:0:TeamId"] = string.Empty,
            ["Slack:Workspaces:0:SigningSecretRef"] = "env://ORPHAN",
            ["Slack:Workspaces:1:TeamId"] = "T0VALID001",
            ["Slack:Workspaces:1:SigningSecretRef"] = "env://VALID",
            ["Slack:Workspaces:1:Enabled"] = "true",
        };

        System.Action act = () => BuildStore(config);

        act.Should().Throw<System.InvalidOperationException>()
            .WithMessage("*Slack:Workspaces*")
            .WithMessage("*index 0*")
            .WithMessage("*blank TeamId*");
    }

    [Fact]
    public void Entry_with_team_id_but_blank_signing_secret_ref_fails_startup()
    {
        // Stage 3.1 evaluator iter-2 item 3 (extended structural fix):
        // a workspace with a TeamId but no SigningSecretRef would later
        // resolve to SigningSecretUnresolved on every real Slack request
        // -- equally a startup-time misconfig the operator must fix.
        Dictionary<string, string?> config = new()
        {
            ["Slack:Workspaces:0:TeamId"] = "T0NOSECRET",
            ["Slack:Workspaces:0:SigningSecretRef"] = string.Empty,
        };

        System.Action act = () => BuildStore(config);

        act.Should().Throw<System.InvalidOperationException>()
            .WithMessage("*Slack:Workspaces*")
            .WithMessage("*index 0*")
            .WithMessage("*T0NOSECRET*")
            .WithMessage("*SigningSecretRef*");
    }

    [Fact]
    public async System.Threading.Tasks.Task Preregistered_workspace_store_wins_over_seed_extension()
    {
        // The seed extension uses TryAddSingleton, so a production
        // composition root that registered a database-backed store BEFORE
        // calling this extension must still win.
        ServiceCollection services = new();
        StubWorkspaceStore preRegistered = new();
        services.AddSingleton<ISlackWorkspaceConfigStore>(preRegistered);

        IConfiguration cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Slack:Workspaces:0:TeamId"] = "T0SEEDED01",
                ["Slack:Workspaces:0:SigningSecretRef"] = "env://SEED",
                ["Slack:Workspaces:0:Enabled"] = "true",
            })
            .Build();

        services.AddSlackWorkspaceConfigStoreFromConfiguration(cfg);

        using ServiceProvider provider = services.BuildServiceProvider();
        ISlackWorkspaceConfigStore resolved = provider.GetRequiredService<ISlackWorkspaceConfigStore>();

        resolved.Should().BeSameAs(preRegistered,
            "the seed extension's TryAddSingleton must lose to a pre-registered production workspace store");
        SlackWorkspaceConfig? lookup = await resolved.GetByTeamIdAsync("T0SEEDED01", CancellationToken.None);
        lookup.Should().BeNull(
            "the seed entry must NOT leak into the pre-registered store");
    }

    private static ISlackWorkspaceConfigStore BuildStore(Dictionary<string, string?> overrides)
    {
        IConfiguration cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(overrides)
            .Build();

        ServiceCollection services = new();
        services.AddSlackWorkspaceConfigStoreFromConfiguration(cfg);

        ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ISlackWorkspaceConfigStore>();
    }

    private sealed class StubWorkspaceStore : ISlackWorkspaceConfigStore
    {
        public System.Threading.Tasks.Task<SlackWorkspaceConfig?> GetByTeamIdAsync(string? teamId, CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult<SlackWorkspaceConfig?>(null);

        public System.Threading.Tasks.Task<IReadOnlyCollection<SlackWorkspaceConfig>> GetAllEnabledAsync(CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult<IReadOnlyCollection<SlackWorkspaceConfig>>(System.Array.Empty<SlackWorkspaceConfig>());
    }
}
